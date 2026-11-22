using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Agents.Benchmarking;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Benchmark.Cli.Cli;

/// <summary>
/// Loads and validates <see cref="BenchmarkModelCandidate"/> lists from <c>--models</c> CSV
/// or <c>--config</c> JSON manifests, applying explicit CLI flag overrides.
/// </summary>
public static class BenchmarkCandidateConfigLoader
{
    private const double MinValidTemperature = 0.0;
    private const double MaxValidTemperature = 2.0;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };

    private sealed class CandidateManifestEnvelope
    {
        public List<CandidateJsonEntry?>? Candidates { get; set; }
    }

    private sealed class CandidateJsonEntry
    {
        public string? CandidateId { get; set; }
        public string? DisplayName { get; set; }
        public string? ModelName { get; set; }
        public ThinkingEffort? ThinkingEffort { get; set; }
        public int? ThinkingTokenBudget { get; set; }
        public int? ContextWindowTokens { get; set; }
        public int? MaxOutputTokens { get; set; }
        public double? Temperature { get; set; }
        public int? MaxTurns { get; set; }
        public int? InstanceTimeoutSeconds { get; set; }
        public LocalOptimizationFlags? OptimizationFlags { get; set; }
        public bool? EnableStackSkills { get; set; }
        public OllamaRoutingTarget? RoutingTarget { get; set; }
    }

    public static IReadOnlyList<BenchmarkModelCandidate> LoadCandidates(RunCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ModelsCsv))
        {
            return LoadFromModelsCsv(options.ModelsCsv, options.EnableStackSkills, options.TimeoutSeconds);
        }

        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            return LoadFromConfigFile(options.ConfigPath, options.EnableStackSkills, options.TimeoutSeconds);
        }

        throw new BenchmarkException($"Either '{BenchmarkCliConstants.OptionModels}' or '{BenchmarkCliConstants.OptionConfig}' must be provided to select model candidates.");
    }

    public static IReadOnlyList<BenchmarkModelCandidate> LoadFromModelsCsv(
        string modelsCsv,
        bool? enableStackSkillsOverride,
        int? timeoutSecondsOverride)
    {
        if (string.IsNullOrWhiteSpace(modelsCsv))
        {
            throw new BenchmarkException($"Model list '{BenchmarkCliConstants.OptionModels}' cannot be empty.");
        }

        if (timeoutSecondsOverride is <= 0)
        {
            throw new BenchmarkException($"Timeout override must be positive (got {timeoutSecondsOverride}).");
        }

        var rawModels = modelsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToList();

        if (rawModels.Count == 0)
        {
            throw new BenchmarkException($"Model list '{BenchmarkCliConstants.OptionModels}' did not contain any valid model names.");
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<BenchmarkModelCandidate>(rawModels.Count);

        foreach (var model in rawModels)
        {
            var baseSlug = SanitizeDerivedCandidateSlug(model);
            var candidateId = EnsureUniqueCandidateId(baseSlug, usedIds);

            candidates.Add(new BenchmarkModelCandidate
            {
                CandidateId = candidateId,
                DisplayName = model,
                ModelName = model,
                EnableStackSkills = enableStackSkillsOverride ?? true,
                InstanceTimeoutSeconds = timeoutSecondsOverride ?? BenchmarkDefaults.DefaultInstanceTimeoutSeconds
            });
        }

        return candidates;
    }

    public static IReadOnlyList<BenchmarkModelCandidate> LoadFromConfigFile(
        string configPath,
        bool? enableStackSkillsOverride,
        int? timeoutSecondsOverride)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new BenchmarkException($"Candidate config path '{BenchmarkCliConstants.OptionConfig}' cannot be empty.");
        }

        if (timeoutSecondsOverride is <= 0)
        {
            throw new BenchmarkException($"Timeout override must be positive (got {timeoutSecondsOverride}).");
        }

        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            throw new BenchmarkException($"Candidate config file was not found at '{fullPath}'.");
        }

        var json = File.ReadAllText(fullPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BenchmarkException($"Candidate config file '{fullPath}' is empty.");
        }

        List<CandidateJsonEntry?> rawEntries = ParseEntries(json, fullPath);
        if (rawEntries.Count == 0)
        {
            throw new BenchmarkException($"Candidate config file '{fullPath}' contains zero candidates.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<BenchmarkModelCandidate>(rawEntries.Count);

        for (int i = 0; i < rawEntries.Count; i++)
        {
            var entry = rawEntries[i];
            if (entry == null)
            {
                throw new BenchmarkException($"Candidate at index {i} in '{fullPath}' cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(entry.ModelName))
            {
                throw new BenchmarkException($"Candidate at index {i} in '{fullPath}' is missing required 'ModelName'.");
            }

            var modelName = entry.ModelName.Trim();
            string candidateId;
            if (string.IsNullOrWhiteSpace(entry.CandidateId))
            {
                candidateId = SanitizeDerivedCandidateSlug(modelName);
            }
            else
            {
                candidateId = entry.CandidateId.Trim();
                if (candidateId is "." or ".." ||
                    candidateId.Contains('/') ||
                    candidateId.Contains('\\') ||
                    candidateId.Contains("..", StringComparison.Ordinal))
                {
                    throw new BenchmarkException($"CandidateId '{candidateId}' in '{fullPath}' contains invalid path characters.");
                }
            }

            if (!seenIds.Add(candidateId))
            {
                throw new BenchmarkException($"Duplicate CandidateId '{candidateId}' in candidate config '{fullPath}'.");
            }

            var slug = SanitizeDerivedCandidateSlug(candidateId);
            if (!seenSlugs.Add(slug))
            {
                throw new BenchmarkException($"CandidateId '{candidateId}' in '{fullPath}' collides with another candidate's sanitized artifact folder name '{slug}'.");
            }

            if (entry.ThinkingEffort.HasValue && !Enum.IsDefined(entry.ThinkingEffort.Value))
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid ThinkingEffort ({entry.ThinkingEffort.Value}).");
            }

            if (entry.RoutingTarget.HasValue && !Enum.IsDefined(entry.RoutingTarget.Value))
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid RoutingTarget ({entry.RoutingTarget.Value}).");
            }

            if (entry.ThinkingTokenBudget is <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid ThinkingTokenBudget ({entry.ThinkingTokenBudget}).");
            }

            if (entry.ContextWindowTokens is <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid ContextWindowTokens ({entry.ContextWindowTokens}).");
            }

            if (entry.MaxOutputTokens is <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid MaxOutputTokens ({entry.MaxOutputTokens}).");
            }

            if (entry.Temperature.HasValue &&
                (double.IsNaN(entry.Temperature.Value) ||
                 double.IsInfinity(entry.Temperature.Value) ||
                 entry.Temperature.Value < MinValidTemperature ||
                 entry.Temperature.Value > MaxValidTemperature))
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid Temperature ({entry.Temperature.Value}). Expected a value between {MinValidTemperature} and {MaxValidTemperature}.");
            }

            var maxTurns = entry.MaxTurns ?? BenchmarkDefaults.DefaultMaxTurns;
            if (maxTurns <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid MaxTurns ({maxTurns}).");
            }

            if (entry.InstanceTimeoutSeconds is <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid InstanceTimeoutSeconds ({entry.InstanceTimeoutSeconds}).");
            }

            var timeoutSeconds = timeoutSecondsOverride ?? entry.InstanceTimeoutSeconds ?? BenchmarkDefaults.DefaultInstanceTimeoutSeconds;
            if (timeoutSeconds <= 0)
            {
                throw new BenchmarkException($"Candidate '{candidateId}' in '{fullPath}' has invalid InstanceTimeoutSeconds ({timeoutSeconds}).");
            }

            candidates.Add(new BenchmarkModelCandidate
            {
                CandidateId = candidateId,
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? modelName : entry.DisplayName.Trim(),
                ModelName = modelName,
                ThinkingEffort = entry.ThinkingEffort ?? ThinkingEffort.Medium,
                ThinkingTokenBudget = entry.ThinkingTokenBudget,
                ContextWindowTokens = entry.ContextWindowTokens,
                MaxOutputTokens = entry.MaxOutputTokens,
                Temperature = entry.Temperature,
                MaxTurns = maxTurns,
                InstanceTimeoutSeconds = timeoutSeconds,
                OptimizationFlags = entry.OptimizationFlags?.Clone(),
                EnableStackSkills = enableStackSkillsOverride ?? entry.EnableStackSkills ?? true,
                RoutingTarget = entry.RoutingTarget ?? OllamaRoutingTarget.Auto
            });
        }

        return candidates;
    }

    private static List<CandidateJsonEntry?> ParseEntries(string json, string fullPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<CandidateJsonEntry?>>(json, ConfigJsonOptions) ?? [];
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var envelope = JsonSerializer.Deserialize<CandidateManifestEnvelope>(json, ConfigJsonOptions);
                if (envelope?.Candidates != null)
                {
                    return envelope.Candidates;
                }

                throw new BenchmarkException($"Candidate config object '{fullPath}' must contain a 'candidates' array property.");
            }

            throw new BenchmarkException($"Candidate config '{fullPath}' must be a JSON array or an object with a 'candidates' array.");
        }
        catch (JsonException ex)
        {
            throw new BenchmarkException($"Invalid JSON in candidate config '{fullPath}': {ex.Message}", ex);
        }
    }

    private static string SanitizeDerivedCandidateSlug(string raw)
    {
        var slug = BenchmarkWorkspaceManager.SanitizePathSegment(raw);
        if (string.IsNullOrWhiteSpace(slug) || slug.All(ch => ch == '.'))
        {
            return BenchmarkCliConstants.DefaultCandidateSlug;
        }

        return slug;
    }

    private static string EnsureUniqueCandidateId(string baseSlug, HashSet<string> usedIds)
    {
        if (usedIds.Add(baseSlug))
        {
            return baseSlug;
        }

        int suffix = 2;
        while (!usedIds.Add($"{baseSlug}-{suffix}"))
        {
            suffix++;
        }

        return $"{baseSlug}-{suffix}";
    }
}
