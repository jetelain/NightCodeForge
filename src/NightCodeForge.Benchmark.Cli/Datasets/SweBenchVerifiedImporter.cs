using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Normalizes <c>SWE-bench Verified</c> Python exports into <see cref="BenchmarkInstance"/> records
/// with JUnit XML test reporting.
/// </summary>
public sealed class SweBenchVerifiedImporter : ExternalBenchmarkDatasetImporterBase
{
    public override BenchmarkDatasetSource Source => BenchmarkDatasetSource.SweBenchVerified;
    public override string SourceKey => BenchmarkDatasetConstants.SourceSweBenchVerified;
    protected override string DefaultLanguage => BenchmarkLanguages.Python;
    protected override TestResultFormat? DefaultTestResultFormat => TestResultFormat.JUnitXml;
    protected override string DefaultTestCommand => BenchmarkDatasetConstants.DefaultPythonJUnitTestCommand;
}
