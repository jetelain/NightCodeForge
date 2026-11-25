using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Supported external implementation dataset formats normalized into <see cref="BenchmarkInstance"/> JSONL files.
/// </summary>
public enum BenchmarkDatasetSource
{
    SweSharpBench = 0,
    SweBenchMultilingual = 1,
    MultiSweBench = 2,
    SweBenchVerified = 3
}

/// <summary>
/// Constants for benchmark dataset manifests, external dataset sources, JSONL fields, and defaults.
/// </summary>
public static class BenchmarkDatasetConstants
{
    public const string SourceSweSharpBench = "swe-sharp-bench";
    public const string SourceSweBenchMultilingual = "swebench-multilingual";
    public const string SourceMultiSweBench = "multi-swe-bench";
    public const string SourceSweBenchVerified = "swebench-verified";
    public const string SourceCraftedCodeReview = "crafted-code-review";
    public const string SourceCraftedTaskReview = "crafted-task-review";

    public const string DefaultPythonJUnitTestCommand =
        "pytest --junitxml=" + BenchmarkPaths.ContainerResultsDir + "/" + BenchmarkPaths.JUnitFileName;

    public const string FieldInstanceId = "instance_id";
    public const string FieldCategory = "category";
    public const string FieldLanguage = "language";
    public const string FieldRepo = "repo";
    public const string FieldBaseCommit = "base_commit";
    public const string FieldProblemStatement = "problem_statement";
    public const string FieldHintsText = "hints_text";
    public const string FieldCustomContainerImage = "custom_container_image";
    public const string FieldCustomTestCommand = "custom_test_command";
    public const string FieldTestResultFormat = "test_result_format";
    public const string FieldPatch = "patch";
    public const string FieldGoldPatch = "gold_patch";
    public const string FieldFixPatch = "fix_patch";
    public const string FieldTestPatch = "test_patch";
    public const string FieldFailToPass = "fail_to_pass";
    public const string FieldF2pTests = "f2p_tests";
    public const string FieldPassToPass = "pass_to_pass";
    public const string FieldP2pTests = "p2p_tests";
    public const string FieldCandidatePatch = "candidate_patch";
    public const string FieldTaskSpecification = "task_specification";
    public const string FieldExpectedVerdict = "expected_verdict";
    public const string FieldExpectedDifficulty = "expected_difficulty";
    public const string FieldExpectedFindings = "expected_findings";
    public const string FieldFilePath = "file_path";
    public const string FieldFile = "file";
    public const string FieldSymbolOrLineHint = "symbol_or_line_hint";
    public const string FieldSymbol = "symbol";
    public const string FieldLineHint = "line_hint";
    public const string FieldSeverity = "severity";
    public const string FieldSummary = "summary";
    public const string FieldKeywords = "keywords";
    public const string FieldOrg = "org";
    public const string FieldBase = "base";
    public const string FieldSha = "sha";
    public const string FieldResolvedIssues = "resolved_issues";
    public const string FieldTitle = "title";
    public const string FieldBody = "body";
    public const string FieldNumber = "number";

    public const string JsonExtension = ".json";
    public const string JsonlExtension = ".jsonl";
    public const string ManifestFileSuffix = ".manifest.json";
    public const string CraftedCodeReviewFileName = "v1-code-review.jsonl";
    public const string CraftedTaskReviewFileName = "v1-task-review.jsonl";

    public const string StatusPass = "PASS";
    public const string StatusFail = "FAIL";

    public const string ManifestSuiteName = "suite_name";
    public const string ManifestVersion = "version";
    public const string ManifestDescription = "description";
    public const string ManifestEntries = "entries";
    public const string ManifestSource = "source";
    public const string ManifestNotes = "notes";
}

/// <summary>
/// Pinned selection entry inside a benchmark suite manifest (<c>v1-smoke.manifest.json</c> / <c>v1-full.manifest.json</c>).
/// </summary>
public sealed record BenchmarkManifestEntry
{
    public required string InstanceId { get; init; }
    public string? Source { get; init; }
    public BenchmarkProblemCategory? Category { get; init; }
    public required string Language { get; init; }
    public string? Repo { get; init; }
    public string? CustomContainerImage { get; init; }
    public string? CustomTestCommand { get; init; }
    public TestResultFormat? TestResultFormat { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Versioned benchmark suite selection manifest defining curated stack mix and per-instance execution overrides.
/// </summary>
public sealed record BenchmarkDatasetManifest
{
    public required string SuiteName { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<BenchmarkManifestEntry> Entries { get; init; } = Array.Empty<BenchmarkManifestEntry>();
}

/// <summary>
/// Pre-flight validation result for a single dataset instance.
/// </summary>
public sealed record BenchmarkInstanceValidationResult
{
    public required string InstanceId { get; init; }
    public BenchmarkProblemCategory Category { get; init; }
    public required string Language { get; init; }
    public bool Passed { get; init; }
    public string? Summary { get; init; }
    public string? ErrorMessage { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>
/// Aggregated pre-flight validation report produced by <see cref="IBenchmarkDatasetValidator"/>.
/// </summary>
public sealed record BenchmarkDatasetValidationReport
{
    public required string DatasetPath { get; init; }
    public IReadOnlyList<BenchmarkInstanceValidationResult> Results { get; init; } = Array.Empty<BenchmarkInstanceValidationResult>();

    public int TotalCount => Results.Count;
    public int PassedCount => Results.Count(r => r.Passed);
    public int FailedCount => Results.Count(r => !r.Passed);
    public bool AllPassed => Results.Count > 0 && Results.All(r => r.Passed);
}
