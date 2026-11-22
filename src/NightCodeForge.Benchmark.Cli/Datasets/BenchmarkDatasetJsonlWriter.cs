using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Writes canonical <see cref="BenchmarkInstance"/> collections as snake_case JSONL datasets
/// compatible with <see cref="Engine.BenchmarkDatasetLoader"/>.
/// </summary>
public static class BenchmarkDatasetJsonlWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions CompactSnakeCaseOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync(
        string outputPath,
        IReadOnlyList<BenchmarkInstance> instances,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new BenchmarkDatasetException("Output dataset path cannot be empty.", outputPath);
        }

        ArgumentNullException.ThrowIfNull(instances);
        if (instances.Count == 0)
        {
            throw new BenchmarkDatasetException("Cannot write an empty benchmark dataset.", outputPath);
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i]
                ?? throw new BenchmarkDatasetException($"Dataset instance at index {i} cannot be null.", outputPath);
            if (string.IsNullOrWhiteSpace(instance.InstanceId))
            {
                throw new BenchmarkDatasetException($"Dataset instance at index {i} cannot have an empty instance_id.", outputPath);
            }

            if (!seenIds.Add(instance.InstanceId.Trim()))
            {
                throw new BenchmarkDatasetException(
                    $"Duplicate instance_id '{instance.InstanceId}' cannot be written to '{outputPath}'.",
                    outputPath,
                    instance.InstanceId);
            }
        }

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, Utf8NoBom))
            {
                foreach (var instance in instances)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dto = ToSerializableRecord(instance);
                    var line = JsonSerializer.Serialize(dto, CompactSnakeCaseOptions);
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Failed to delete temporary dataset file '{tempPath}': {ex.Message}");
        }
    }

    private static SerializableBenchmarkRecord ToSerializableRecord(BenchmarkInstance instance) => new()
    {
        InstanceId = instance.InstanceId,
        Category = instance.Category?.ToString(),
        Language = instance.Language,
        Repo = instance.Repo,
        BaseCommit = instance.BaseCommit,
        ProblemStatement = instance.ProblemStatement,
        HintsText = instance.HintsText,
        CustomContainerImage = instance.CustomContainerImage,
        CustomTestCommand = instance.CustomTestCommand,
        TestResultFormat = instance.TestResultFormat?.ToString(),
        Patch = instance.GoldPatch,
        TestPatch = instance.TestPatch,
        FailToPass = instance.FailToPass is { Count: > 0 } ? instance.FailToPass : null,
        PassToPass = instance.PassToPass is { Count: > 0 } ? instance.PassToPass : null,
        CandidatePatch = instance.CandidatePatch,
        TaskSpecification = instance.TaskSpecification,
        ExpectedVerdict = instance.ExpectedVerdict,
        ExpectedDifficulty = instance.ExpectedDifficulty,
        ExpectedFindings = instance.ExpectedFindings is { Count: > 0 }
            ? instance.ExpectedFindings.Select(f => new SerializableExpectedFinding
            {
                FilePath = f.FilePath,
                SymbolOrLineHint = f.SymbolOrLineHint,
                Severity = f.Severity,
                Summary = f.Summary,
                Keywords = f.Keywords is { Count: > 0 } ? f.Keywords : null
            }).ToList()
            : null
    };

    private sealed class SerializableBenchmarkRecord
    {
        [JsonPropertyName(BenchmarkDatasetConstants.FieldInstanceId)]
        public required string InstanceId { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldCategory)]
        public string? Category { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldLanguage)]
        public required string Language { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldRepo)]
        public required string Repo { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldBaseCommit)]
        public required string BaseCommit { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldProblemStatement)]
        public required string ProblemStatement { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldHintsText)]
        public string? HintsText { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldCustomContainerImage)]
        public string? CustomContainerImage { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldCustomTestCommand)]
        public string? CustomTestCommand { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldTestResultFormat)]
        public string? TestResultFormat { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldPatch)]
        public string? Patch { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldTestPatch)]
        public string? TestPatch { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldFailToPass)]
        public IReadOnlyList<string>? FailToPass { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldPassToPass)]
        public IReadOnlyList<string>? PassToPass { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldCandidatePatch)]
        public string? CandidatePatch { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldTaskSpecification)]
        public string? TaskSpecification { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldExpectedVerdict)]
        public string? ExpectedVerdict { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldExpectedDifficulty)]
        public string? ExpectedDifficulty { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldExpectedFindings)]
        public IReadOnlyList<SerializableExpectedFinding>? ExpectedFindings { get; init; }
    }

    private sealed class SerializableExpectedFinding
    {
        [JsonPropertyName(BenchmarkDatasetConstants.FieldFilePath)]
        public string? FilePath { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldSymbolOrLineHint)]
        public string? SymbolOrLineHint { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldSeverity)]
        public string? Severity { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldSummary)]
        public string? Summary { get; init; }

        [JsonPropertyName(BenchmarkDatasetConstants.FieldKeywords)]
        public IReadOnlyList<string>? Keywords { get; init; }
    }
}
