namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Normalizes <c>SWE-bench Multilingual</c> exports (TypeScript, JavaScript, Rust, etc.)
/// into <see cref="Models.BenchmarkInstance"/> records with pinned stack test metadata.
/// </summary>
public sealed class SweBenchMultilingualImporter : ExternalBenchmarkDatasetImporterBase
{
    public override BenchmarkDatasetSource Source => BenchmarkDatasetSource.SweBenchMultilingual;
    public override string SourceKey => BenchmarkDatasetConstants.SourceSweBenchMultilingual;
}
