using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Reporting;

/// <summary>
/// Loaded benchmark report paired with its source directory and disambiguated provenance label.
/// </summary>
public sealed record LoadedBenchmarkRun(
    string SourceDirectory,
    string ResultsFilePath,
    BenchmarkRunReport Report,
    string DisplayTag);

/// <summary>
/// Loads persisted <see cref="BenchmarkRunReport"/> JSON artifacts from benchmark result directories.
/// </summary>
public static class BenchmarkReportLoader
{
    private const int ShortCommitLength = 7;

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: true) }
    };

    public static async Task<LoadedBenchmarkRun> LoadAsync(
        string directoryOrFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(directoryOrFilePath))
        {
            throw new BenchmarkException("Benchmark results path cannot be empty.");
        }

        var fullInputPath = Path.GetFullPath(directoryOrFilePath.Trim());
        string resultsFilePath;
        string sourceDirectory;

        if (File.Exists(fullInputPath))
        {
            resultsFilePath = fullInputPath;
            sourceDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullInputPath) ?? fullInputPath);
        }
        else if (Directory.Exists(fullInputPath))
        {
            sourceDirectory = Path.TrimEndingDirectorySeparator(fullInputPath);
            resultsFilePath = Path.Combine(sourceDirectory, BenchmarkPaths.ResultsFileName);
        }
        else
        {
            throw new BenchmarkException($"Benchmark results path '{fullInputPath}' does not exist.");
        }

        if (!File.Exists(resultsFilePath))
        {
            throw new BenchmarkException($"Benchmark results file '{BenchmarkPaths.ResultsFileName}' was not found in '{sourceDirectory}'.");
        }

        var json = await File.ReadAllTextAsync(resultsFilePath, cancellationToken).ConfigureAwait(false);
        BenchmarkRunReport? report;
        try
        {
            report = JsonSerializer.Deserialize<BenchmarkRunReport>(json, ReportJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BenchmarkException($"Failed to parse benchmark report '{resultsFilePath}': {ex.Message}", ex);
        }

        if (report == null)
        {
            throw new BenchmarkException($"Benchmark report '{resultsFilePath}' deserialized to null.");
        }

        if (report.Provenance == null)
        {
            throw new BenchmarkException($"Benchmark report '{resultsFilePath}' is missing required 'provenance' metadata.");
        }

        if (report.Summaries == null)
        {
            throw new BenchmarkException($"Benchmark report '{resultsFilePath}' is missing required 'summaries' collection.");
        }

        var tag = FormatProvenanceTag(report.Provenance, sourceDirectory);
        return new LoadedBenchmarkRun(sourceDirectory, resultsFilePath, report, tag);
    }

    public static string FormatProvenanceTag(BenchmarkRunProvenance provenance, string? fallbackDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        var branch = string.IsNullOrWhiteSpace(provenance.GitBranch)
            ? BenchmarkCliConstants.UnknownProvenanceValue
            : provenance.GitBranch.Trim();
        var commit = string.IsNullOrWhiteSpace(provenance.GitCommit)
            ? BenchmarkCliConstants.UnknownProvenanceValue
            : provenance.GitCommit.Trim();
        if (commit.Length > ShortCommitLength &&
            !string.Equals(commit, BenchmarkCliConstants.UnknownProvenanceValue, StringComparison.OrdinalIgnoreCase))
        {
            commit = commit[..ShortCommitLength];
        }

        var version = string.IsNullOrWhiteSpace(provenance.NcfVersion)
            ? BenchmarkCliConstants.UnknownProvenanceValue
            : provenance.NcfVersion.Trim();
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        if (string.Equals(branch, BenchmarkCliConstants.UnknownProvenanceValue, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(commit, BenchmarkCliConstants.UnknownProvenanceValue, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(version, BenchmarkCliConstants.UnknownProvenanceValue, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var leaf = GetDirectoryLeafName(fallbackDirectory.Trim());
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                return leaf;
            }
        }

        return $"{branch} @ {commit} (v{version})";
    }

    public static IReadOnlyList<LoadedBenchmarkRun> DisambiguateTags(IReadOnlyList<LoadedBenchmarkRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        var baseTags = runs.Select(r => r.DisplayTag).ToArray();
        var duplicateBaseTags = FindDuplicateTags(baseTags);
        if (duplicateBaseTags.Count == 0)
        {
            return runs;
        }

        // First pass: append leaf folder name
        var current = runs
            .Select((r, i) => duplicateBaseTags.Contains(baseTags[i])
                ? r with { DisplayTag = $"{baseTags[i]} [{GetDirectoryLeafName(r.SourceDirectory)}]" }
                : r)
            .ToList();

        // Second pass: if leaf folders also collide (e.g. exp1/results vs exp2/results), include parent/leaf
        var remainingDupes = FindDuplicateTags(current.Select(r => r.DisplayTag));
        if (remainingDupes.Count > 0)
        {
            for (int i = 0; i < current.Count; i++)
            {
                if (!remainingDupes.Contains(current[i].DisplayTag))
                {
                    continue;
                }

                var parentAndLeaf = GetParentAndLeafName(current[i].SourceDirectory);
                current[i] = current[i] with { DisplayTag = $"{baseTags[i]} [{parentAndLeaf}]" };
            }
        }

        // Third pass: if parent/leaf also collide across distinct directories, fall back to full directory path
        remainingDupes = FindDuplicateTags(current.Select(r => r.DisplayTag));
        if (remainingDupes.Count > 0)
        {
            for (int i = 0; i < current.Count; i++)
            {
                if (!remainingDupes.Contains(current[i].DisplayTag))
                {
                    continue;
                }

                current[i] = current[i] with { DisplayTag = $"{baseTags[i]} [{current[i].SourceDirectory}]" };
            }
        }

        // Final pass: if exact same directory was supplied multiple times, append 1-based ordinal
        remainingDupes = FindDuplicateTags(current.Select(r => r.DisplayTag));
        if (remainingDupes.Count > 0)
        {
            for (int i = 0; i < current.Count; i++)
            {
                if (!remainingDupes.Contains(current[i].DisplayTag))
                {
                    continue;
                }

                current[i] = current[i] with { DisplayTag = $"{current[i].DisplayTag} #{i + 1}" };
            }
        }

        return current;
    }

    private static HashSet<string> FindDuplicateTags(IEnumerable<string> tags) =>
        tags
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string GetDirectoryLeafName(string directoryPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directoryPath);
        var leaf = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(leaf) ? directoryPath : leaf;
    }

    private static string GetParentAndLeafName(string directoryPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(directoryPath);
        var leaf = Path.GetFileName(trimmed);
        var parentDir = Path.GetDirectoryName(trimmed);
        var parentLeaf = !string.IsNullOrWhiteSpace(parentDir)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(parentDir))
            : null;

        if (!string.IsNullOrWhiteSpace(parentLeaf) && !string.IsNullOrWhiteSpace(leaf))
        {
            return $"{parentLeaf}/{leaf}";
        }

        return directoryPath;
    }
}
