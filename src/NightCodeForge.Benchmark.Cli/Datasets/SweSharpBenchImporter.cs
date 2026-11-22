using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Normalizes <c>microsoft/SWE-Sharp-Bench</c> C#/.NET exports into <see cref="BenchmarkInstance"/> records.
/// </summary>
public sealed class SweSharpBenchImporter : ExternalBenchmarkDatasetImporterBase
{
    public override BenchmarkDatasetSource Source => BenchmarkDatasetSource.SweSharpBench;
    public override string SourceKey => BenchmarkDatasetConstants.SourceSweSharpBench;
    protected override string DefaultLanguage => BenchmarkLanguages.CSharp;
    protected override TestResultFormat? DefaultTestResultFormat => TestResultFormat.Trx;
}
