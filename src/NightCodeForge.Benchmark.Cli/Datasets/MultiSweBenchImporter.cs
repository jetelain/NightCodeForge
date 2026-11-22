using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Normalizes <c>Multi-SWE-bench</c> exports supporting both standard SWE-bench fields and
/// Multi-SWE-bench structured fields (<c>org</c>+<c>repo</c>, <c>base.sha</c>, <c>resolved_issues</c>, <c>fix_patch</c>).
/// </summary>
public sealed class MultiSweBenchImporter : ExternalBenchmarkDatasetImporterBase
{
    public override BenchmarkDatasetSource Source => BenchmarkDatasetSource.MultiSweBench;
    public override string SourceKey => BenchmarkDatasetConstants.SourceMultiSweBench;

    protected override string ExtractRawInstanceId(JsonElement obj, string inputPath, string location)
    {
        var id = BenchmarkDatasetLoader.GetOptionalString(obj, BenchmarkDatasetConstants.FieldInstanceId, inputPath);
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        var org = BenchmarkDatasetLoader.GetOptionalString(obj, BenchmarkDatasetConstants.FieldOrg, inputPath)?.Trim().Trim('/');
        var repo = BenchmarkDatasetLoader.GetOptionalString(obj, BenchmarkDatasetConstants.FieldRepo, inputPath)?.Trim().Trim('/');
        var number = ExtractOptionalNumberToken(obj, inputPath);

        if (!string.IsNullOrWhiteSpace(org) &&
            !string.IsNullOrWhiteSpace(repo) &&
            !string.IsNullOrWhiteSpace(number))
        {
            return $"{org}__{repo}-{number}";
        }

        throw new BenchmarkDatasetException(
            $"Missing required '{BenchmarkDatasetConstants.FieldInstanceId}' at {location} in '{inputPath}'.",
            inputPath);
    }

    protected override string ExtractRepo(JsonElement raw, BenchmarkManifestEntry manifestEntry, string inputPath)
    {
        var repo = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldRepo, inputPath, manifestEntry.InstanceId)?.Trim().Trim('/');
        var org = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldOrg, inputPath, manifestEntry.InstanceId)?.Trim().Trim('/');

        if (!string.IsNullOrWhiteSpace(repo))
        {
            if (!repo.Contains('/', StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(org))
            {
                return $"{org}/{repo}";
            }

            return repo;
        }

        throw new BenchmarkDatasetException(
            $"Missing required '{BenchmarkDatasetConstants.FieldRepo}' for instance '{manifestEntry.InstanceId}' in '{inputPath}'.",
            inputPath,
            manifestEntry.InstanceId);
    }

    protected override string ExtractBaseCommit(JsonElement raw, string inputPath, string instanceId)
    {
        var direct = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldBaseCommit, inputPath, instanceId)?.Trim();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (BenchmarkDatasetLoader.TryGetPropertyIgnoreCase(raw, BenchmarkDatasetConstants.FieldBase, out var baseElem) &&
            baseElem.ValueKind == JsonValueKind.Object)
        {
            var sha = BenchmarkDatasetLoader.GetOptionalString(baseElem, BenchmarkDatasetConstants.FieldSha, inputPath, instanceId)?.Trim();
            if (!string.IsNullOrWhiteSpace(sha))
            {
                return sha;
            }
        }

        throw new BenchmarkDatasetException(
            $"Empty or missing '{BenchmarkDatasetConstants.FieldBaseCommit}' for instance '{instanceId}' in '{inputPath}'.",
            inputPath,
            instanceId);
    }

    protected override string ExtractProblemStatement(JsonElement raw, string inputPath, string instanceId)
    {
        var direct = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldProblemStatement, inputPath, instanceId);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (BenchmarkDatasetLoader.TryGetPropertyIgnoreCase(raw, BenchmarkDatasetConstants.FieldResolvedIssues, out var issuesElem) &&
            issuesElem.ValueKind == JsonValueKind.Array)
        {
            var formattedIssues = new List<string>();
            foreach (var issue in issuesElem.EnumerateArray())
            {
                if (issue.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = BenchmarkDatasetLoader.GetOptionalString(issue, BenchmarkDatasetConstants.FieldTitle, inputPath, instanceId)?.Trim();
                var body = BenchmarkDatasetLoader.GetOptionalString(issue, BenchmarkDatasetConstants.FieldBody, inputPath, instanceId)?.Trim();
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(body))
                {
                    formattedIssues.Add($"{title}\n\n{body}");
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    formattedIssues.Add(title);
                }
                else if (!string.IsNullOrWhiteSpace(body))
                {
                    formattedIssues.Add(body);
                }
            }

            if (formattedIssues.Count > 0)
            {
                return string.Join("\n\n---\n\n", formattedIssues);
            }
        }

        throw new BenchmarkDatasetException(
            $"Empty or missing '{BenchmarkDatasetConstants.FieldProblemStatement}' for instance '{instanceId}' in '{inputPath}'.",
            inputPath,
            instanceId);
    }

    protected override string? ExtractGoldPatch(JsonElement raw, string inputPath, string instanceId) =>
        base.ExtractGoldPatch(raw, inputPath, instanceId)
        ?? NullIfWhiteSpacePreserve(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldFixPatch, inputPath, instanceId));

    private static string? ExtractOptionalNumberToken(JsonElement obj, string inputPath)
    {
        if (!BenchmarkDatasetLoader.TryGetPropertyIgnoreCase(obj, BenchmarkDatasetConstants.FieldNumber, out var prop) ||
            prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString()?.Trim(),
            JsonValueKind.Number => prop.GetRawText().Trim(),
            _ => throw new BenchmarkDatasetException(
                $"Field '{BenchmarkDatasetConstants.FieldNumber}' must be a string or number, got {prop.ValueKind}.",
                inputPath)
        };
    }
}
