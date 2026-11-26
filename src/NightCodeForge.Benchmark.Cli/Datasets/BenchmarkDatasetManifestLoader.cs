using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Parses and strictly validates pinned suite selection manifests (<c>*.manifest.json</c>).
/// </summary>
public static class BenchmarkDatasetManifestLoader
{
    public static async Task<BenchmarkDatasetManifest> LoadAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new BenchmarkDatasetException("Manifest path cannot be empty.", manifestPath);
        }

        if (!File.Exists(manifestPath))
        {
            throw new BenchmarkDatasetException($"Benchmark manifest file '{manifestPath}' does not exist.", manifestPath);
        }

        var content = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BenchmarkDatasetException($"Benchmark manifest file '{manifestPath}' is empty.", manifestPath);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content, BenchmarkDatasetLoader.DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new BenchmarkDatasetException(
                $"Malformed JSON in manifest '{manifestPath}': {ex.Message}",
                ex,
                manifestPath);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new BenchmarkDatasetException(
                    $"Manifest '{manifestPath}' must contain a root JSON object.",
                    manifestPath);
            }

            var suiteName = RequireManifestString(root, BenchmarkDatasetConstants.ManifestSuiteName, manifestPath).Trim();
            var version = RequireManifestString(root, BenchmarkDatasetConstants.ManifestVersion, manifestPath).Trim();
            var description = BenchmarkDatasetLoader.GetOptionalString(root, BenchmarkDatasetConstants.ManifestDescription, manifestPath)?.Trim();

            if (!BenchmarkDatasetLoader.TryGetPropertyIgnoreCase(root, BenchmarkDatasetConstants.ManifestEntries, out var entriesElem) ||
                entriesElem.ValueKind != JsonValueKind.Array)
            {
                throw new BenchmarkDatasetException(
                    $"Manifest '{manifestPath}' is missing required array '{BenchmarkDatasetConstants.ManifestEntries}'.",
                    manifestPath);
            }

            var entries = new List<BenchmarkManifestEntry>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int index = 0;

            foreach (var item in entriesElem.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new BenchmarkDatasetException(
                        $"Manifest entry at index {index} in '{manifestPath}' must be a JSON object.",
                        manifestPath);
                }

                var rawId = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldInstanceId, manifestPath);
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    throw new BenchmarkDatasetException(
                        $"Manifest entry at index {index} in '{manifestPath}' is missing required '{BenchmarkDatasetConstants.FieldInstanceId}'.",
                        manifestPath);
                }

                var instanceId = rawId.Trim();
                if (!seenIds.Add(instanceId))
                {
                    throw new BenchmarkDatasetException(
                        $"Duplicate instance_id '{instanceId}' found in manifest '{manifestPath}'.",
                        manifestPath,
                        instanceId);
                }

                var rawLanguage = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldLanguage, manifestPath, instanceId);
                if (string.IsNullOrWhiteSpace(rawLanguage))
                {
                    throw new BenchmarkDatasetException(
                        $"Manifest entry '{instanceId}' in '{manifestPath}' is missing required '{BenchmarkDatasetConstants.FieldLanguage}'.",
                        manifestPath,
                        instanceId);
                }

                var normalizedLanguage = BenchmarkLanguages.Normalize(rawLanguage);
                if (!BenchmarkLanguages.Supported.Contains(normalizedLanguage))
                {
                    throw new BenchmarkDatasetException(
                        $"Unsupported language '{rawLanguage}' for manifest entry '{instanceId}' in '{manifestPath}'. Supported: {string.Join(", ", BenchmarkLanguages.Supported)}.",
                        manifestPath,
                        instanceId);
                }

                var rawCategory = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldCategory, manifestPath, instanceId);
                var category = BenchmarkDatasetLoader.ParseOptionalCategory(rawCategory, manifestPath, instanceId);

                var rawFormat = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldTestResultFormat, manifestPath, instanceId);
                var testResultFormat = BenchmarkDatasetLoader.ParseOptionalTestResultFormat(rawFormat, manifestPath, instanceId);

                var rawSource = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.ManifestSource, manifestPath, instanceId)?.Trim();
                string? source = null;
                if (!string.IsNullOrWhiteSpace(rawSource))
                {
                    if (!TryNormalizeManifestSource(rawSource, out var canonicalSource))
                    {
                        throw new BenchmarkDatasetException(
                            $"Unsupported manifest source '{rawSource}' for entry '{instanceId}' in '{manifestPath}'.",
                            manifestPath,
                            instanceId);
                    }

                    ValidateSourceCategoryCompatibility(canonicalSource, category, manifestPath, instanceId);
                    source = canonicalSource;
                }

                var repo = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldRepo, manifestPath, instanceId)?.Trim();
                var customImage = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldCustomContainerImage, manifestPath, instanceId)?.Trim();
                var customTestCmd = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.FieldCustomTestCommand, manifestPath, instanceId)?.Trim();
                var pipConstraints = BenchmarkDatasetLoader.ParseOptionalStringArrayOrJsonString(item, BenchmarkDatasetConstants.FieldPipConstraints, manifestPath, instanceId);
                var failToPass = BenchmarkDatasetLoader.ParseOptionalStringArrayOrJsonString(item, BenchmarkDatasetConstants.FieldFailToPass, manifestPath, instanceId);
                var passToPass = BenchmarkDatasetLoader.ParseOptionalStringArrayOrJsonString(item, BenchmarkDatasetConstants.FieldPassToPass, manifestPath, instanceId);
                var notes = BenchmarkDatasetLoader.GetOptionalString(item, BenchmarkDatasetConstants.ManifestNotes, manifestPath, instanceId)?.Trim();

                entries.Add(new BenchmarkManifestEntry
                {
                    InstanceId = instanceId,
                    Source = source,
                    Category = category,
                    Language = normalizedLanguage,
                    Repo = string.IsNullOrWhiteSpace(repo) ? null : repo,
                    CustomContainerImage = string.IsNullOrWhiteSpace(customImage) ? null : customImage,
                    CustomTestCommand = string.IsNullOrWhiteSpace(customTestCmd) ? null : customTestCmd,
                    TestResultFormat = testResultFormat,
                    PipConstraints = pipConstraints,
                    FailToPass = failToPass,
                    PassToPass = passToPass,
                    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes
                });
                index++;
            }

            if (entries.Count == 0)
            {
                throw new BenchmarkDatasetException(
                    $"Manifest '{manifestPath}' contains zero entries.",
                    manifestPath);
            }

            var implementationEntries = entries
                .Where(e => e.Category is null or BenchmarkProblemCategory.Implementation)
                .ToList();
            bool hasExplicitImplSource = implementationEntries.Any(e => !string.IsNullOrWhiteSpace(e.Source));
            bool hasMissingImplSource = implementationEntries.Any(e => string.IsNullOrWhiteSpace(e.Source));
            if (hasExplicitImplSource && hasMissingImplSource)
            {
                throw new BenchmarkDatasetException(
                    $"Manifest '{manifestPath}' mixes explicit and omitted '{BenchmarkDatasetConstants.ManifestSource}' values across Implementation entries.",
                    manifestPath);
            }

            return new BenchmarkDatasetManifest
            {
                SuiteName = suiteName,
                Version = version,
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                Entries = entries
            };
        }
    }

    internal static bool TryNormalizeManifestSource(string? raw, out string canonicalSource)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            canonicalSource = string.Empty;
            return false;
        }

        switch (BenchmarkDatasetLoader.NormalizeToken(raw))
        {
            case "swesharpbench":
                canonicalSource = BenchmarkDatasetConstants.SourceSweSharpBench;
                return true;
            case "swebenchmultilingual":
                canonicalSource = BenchmarkDatasetConstants.SourceSweBenchMultilingual;
                return true;
            case "multiswebench":
                canonicalSource = BenchmarkDatasetConstants.SourceMultiSweBench;
                return true;
            case "swebenchverified":
                canonicalSource = BenchmarkDatasetConstants.SourceSweBenchVerified;
                return true;
            case "craftedcodereview":
                canonicalSource = BenchmarkDatasetConstants.SourceCraftedCodeReview;
                return true;
            case "craftedtaskreview":
                canonicalSource = BenchmarkDatasetConstants.SourceCraftedTaskReview;
                return true;
            default:
                canonicalSource = string.Empty;
                return false;
        }
    }

    private static void ValidateSourceCategoryCompatibility(
        string canonicalSource,
        BenchmarkProblemCategory? category,
        string manifestPath,
        string instanceId)
    {
        if (!category.HasValue)
        {
            return;
        }

        var expectedCategory = canonicalSource switch
        {
            BenchmarkDatasetConstants.SourceCraftedCodeReview => BenchmarkProblemCategory.CodeReview,
            BenchmarkDatasetConstants.SourceCraftedTaskReview => BenchmarkProblemCategory.TaskReview,
            _ => BenchmarkProblemCategory.Implementation
        };

        if (category.Value != expectedCategory)
        {
            throw new BenchmarkDatasetException(
                $"Manifest entry '{instanceId}' in '{manifestPath}' has incompatible source '{canonicalSource}' and category '{category.Value}' (expected '{expectedCategory}').",
                manifestPath,
                instanceId);
        }
    }

    private static string RequireManifestString(JsonElement obj, string propertyName, string manifestPath)
    {
        var value = BenchmarkDatasetLoader.GetOptionalString(obj, propertyName, manifestPath);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BenchmarkDatasetException(
                $"Manifest '{manifestPath}' is missing required '{propertyName}'.",
                manifestPath);
        }

        return value;
    }
}
