using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Normalizes an external benchmark dataset export (.json or .jsonl) into NightCodeForge's
/// canonical <see cref="BenchmarkInstance"/> schema using a pinned suite manifest.
/// </summary>
public interface IBenchmarkDatasetImporter
{
    BenchmarkDatasetSource Source { get; }
    string SourceKey { get; }

    Task<IReadOnlyList<BenchmarkInstance>> ImportAsync(
        string inputPath,
        BenchmarkDatasetManifest manifest,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves <see cref="IBenchmarkDatasetImporter"/> implementations by <see cref="BenchmarkDatasetSource"/>.
/// </summary>
public interface IBenchmarkDatasetImporterRegistry
{
    IBenchmarkDatasetImporter GetImporter(BenchmarkDatasetSource source);
}

/// <summary>
/// Executes deterministic pre-flight validation across Implementation, Code Review, and ForgeTask Review datasets.
/// </summary>
public interface IBenchmarkDatasetValidator
{
    Task<BenchmarkDatasetValidationReport> ValidateAsync(
        string datasetPath,
        BenchmarkProblemCategory? categoryOverride,
        IReadOnlyList<string>? instanceIdFilter,
        int? limit,
        bool verifyWithTests,
        Action<string>? progressCallback,
        string? manifestPath = null,
        int concurrency = 1,
        CancellationToken cancellationToken = default);
}
