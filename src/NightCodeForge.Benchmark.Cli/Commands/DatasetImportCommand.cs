using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Datasets;

namespace NightCodeForge.Benchmark.Cli.Commands;

/// <summary>
/// Executes <c>ncf-bench dataset import</c>, normalizing external dataset exports against a pinned
/// manifest and writing canonical <see cref="Models.BenchmarkInstance"/> JSONL output.
/// </summary>
public sealed class DatasetImportCommand
{
    private readonly IBenchmarkDatasetImporterRegistry _importerRegistry;

    public DatasetImportCommand(IBenchmarkDatasetImporterRegistry importerRegistry)
    {
        _importerRegistry = importerRegistry ?? throw new ArgumentNullException(nameof(importerRegistry));
    }

    public async Task<int> ExecuteAsync(
        DatasetImportCommandOptions options,
        Action<string> writeLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeLine);

        var manifest = await BenchmarkDatasetManifestLoader.LoadAsync(options.ManifestPath, cancellationToken).ConfigureAwait(false);
        var importer = _importerRegistry.GetImporter(options.Source);
        var instances = await importer.ImportAsync(options.InputPath, manifest, cancellationToken).ConfigureAwait(false);

        await BenchmarkDatasetJsonlWriter.WriteAsync(options.OutputPath, instances, cancellationToken).ConfigureAwait(false);

        writeLine($"[{BenchmarkCliConstants.ToolName}] Imported {instances.Count} instance(s) from '{importer.SourceKey}' using manifest '{manifest.SuiteName}' -> {Path.GetFullPath(options.OutputPath)}");
        return BenchmarkCliConstants.ExitSuccess;
    }
}
