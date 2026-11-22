using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Resolves registered <see cref="IBenchmarkDatasetImporter"/> implementations by source.
/// </summary>
public sealed class BenchmarkDatasetImporterRegistry : IBenchmarkDatasetImporterRegistry
{
    private readonly IReadOnlyDictionary<BenchmarkDatasetSource, IBenchmarkDatasetImporter> _importers;

    public BenchmarkDatasetImporterRegistry(IEnumerable<IBenchmarkDatasetImporter> importers)
    {
        ArgumentNullException.ThrowIfNull(importers);

        var map = new Dictionary<BenchmarkDatasetSource, IBenchmarkDatasetImporter>();
        foreach (var importer in importers)
        {
            if (importer is null)
            {
                throw new ArgumentException("Importers collection cannot contain null elements.", nameof(importers));
            }

            if (!map.TryAdd(importer.Source, importer))
            {
                throw new BenchmarkDatasetException($"Duplicate benchmark dataset importer registered for source '{importer.Source}'.");
            }
        }

        _importers = map;
    }

    public IBenchmarkDatasetImporter GetImporter(BenchmarkDatasetSource source)
    {
        if (_importers.TryGetValue(source, out var importer))
        {
            return importer;
        }

        throw new BenchmarkDatasetException($"No benchmark dataset importer is registered for source '{source}'.");
    }
}
