using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Datasets;

/// <summary>
/// Shared base implementation for normalizing external benchmark JSON/JSONL exports into
/// <see cref="BenchmarkInstance"/> records guided by a pinned <see cref="BenchmarkDatasetManifest"/>.
/// </summary>
public abstract class ExternalBenchmarkDatasetImporterBase : IBenchmarkDatasetImporter
{
    public abstract BenchmarkDatasetSource Source { get; }
    public abstract string SourceKey { get; }
    protected virtual string? DefaultLanguage => null;
    protected virtual TestResultFormat? DefaultTestResultFormat => null;
    protected virtual string? DefaultTestCommand => null;

    public async Task<IReadOnlyList<BenchmarkInstance>> ImportAsync(
        string inputPath,
        BenchmarkDatasetManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new BenchmarkDatasetException("External dataset input path cannot be empty.", inputPath);
        }

        if (!File.Exists(inputPath))
        {
            throw new BenchmarkDatasetException($"External dataset file '{inputPath}' does not exist.", inputPath);
        }

        ArgumentNullException.ThrowIfNull(manifest);

        var selectedEntries = SelectManifestEntries(manifest);
        var selectedIds = selectedEntries
            .Select(e => e.InstanceId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rawRecords = await LoadRawRecordsAsync(inputPath, selectedIds, cancellationToken).ConfigureAwait(false);

        var missingIds = selectedEntries
            .Select(e => e.InstanceId.Trim())
            .Where(id => !rawRecords.ContainsKey(id))
            .ToList();

        if (missingIds.Count > 0)
        {
            throw new BenchmarkDatasetException(
                $"Manifest '{manifest.SuiteName}' references {missingIds.Count} instance_id(s) not found in '{inputPath}': {string.Join(", ", missingIds)}.",
                inputPath,
                missingIds[0]);
        }

        var normalized = new List<BenchmarkInstance>(selectedEntries.Count);
        foreach (var entry in selectedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawRecord = rawRecords[entry.InstanceId.Trim()];
            normalized.Add(NormalizeRecord(rawRecord, entry, inputPath));
        }

        return normalized;
    }

    protected virtual IReadOnlyList<BenchmarkManifestEntry> SelectManifestEntries(BenchmarkDatasetManifest manifest)
    {
        if (manifest.Entries.Count == 0)
        {
            throw new BenchmarkDatasetException($"Manifest '{manifest.SuiteName}' contains zero entries.");
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.InstanceId))
            {
                throw new BenchmarkDatasetException($"Manifest '{manifest.SuiteName}' contains an entry with an empty instance_id.");
            }

            var trimmedId = entry.InstanceId.Trim();
            if (!seenIds.Add(trimmedId))
            {
                throw new BenchmarkDatasetException(
                    $"Duplicate instance_id '{trimmedId}' found in manifest '{manifest.SuiteName}'.",
                    instanceId: trimmedId);
            }

            if (entry.TestResultFormat.HasValue && !Enum.IsDefined(entry.TestResultFormat.Value))
            {
                throw new BenchmarkDatasetException(
                    $"Unsupported test_result_format '{entry.TestResultFormat.Value}' for manifest entry '{trimmedId}'.",
                    instanceId: trimmedId);
            }
        }

        var implementationEntries = manifest.Entries
            .Where(e => e.Category is null or BenchmarkProblemCategory.Implementation)
            .ToList();

        bool hasExplicitSources = implementationEntries.Any(e => !string.IsNullOrWhiteSpace(e.Source));
        bool hasMissingSources = implementationEntries.Any(e => string.IsNullOrWhiteSpace(e.Source));
        if (hasExplicitSources && hasMissingSources)
        {
            throw new BenchmarkDatasetException(
                $"Manifest '{manifest.SuiteName}' mixes explicit and omitted '{BenchmarkDatasetConstants.ManifestSource}' values among Implementation entries.");
        }

        var normalizedSourceKey = BenchmarkDatasetLoader.NormalizeToken(SourceKey);
        var selected = implementationEntries
            .Where(e =>
                !hasExplicitSources ||
                string.Equals(BenchmarkDatasetLoader.NormalizeToken(e.Source!), normalizedSourceKey, StringComparison.Ordinal))
            .ToList();

        if (selected.Count == 0)
        {
            throw new BenchmarkDatasetException(
                $"Manifest '{manifest.SuiteName}' contains zero Implementation entries matching source '{SourceKey}'.");
        }

        return selected;
    }

    protected virtual BenchmarkInstance NormalizeRecord(
        JsonElement raw,
        BenchmarkManifestEntry manifestEntry,
        string inputPath)
    {
        var instanceId = manifestEntry.InstanceId.Trim();
        var language = ResolveLanguage(raw, manifestEntry, inputPath);
        var repo = ExtractRepo(raw, manifestEntry, inputPath);
        var baseCommit = ExtractBaseCommit(raw, inputPath, instanceId);
        var problemStatement = ExtractProblemStatement(raw, inputPath, instanceId);

        var hintsText = NullIfWhiteSpace(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldHintsText, inputPath, instanceId));
        var goldPatch = ExtractGoldPatch(raw, inputPath, instanceId);
        var testPatch = NullIfWhiteSpacePreserve(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldTestPatch, inputPath, instanceId));

        var failToPass = manifestEntry.FailToPass
            ?? ExtractTestList(raw, inputPath, instanceId, BenchmarkDatasetConstants.FieldFailToPass, BenchmarkDatasetConstants.FieldF2pTests);
        var passToPass = manifestEntry.PassToPass
            ?? ExtractTestList(raw, inputPath, instanceId, BenchmarkDatasetConstants.FieldPassToPass, BenchmarkDatasetConstants.FieldP2pTests);

        var rawFormatStr = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldTestResultFormat, inputPath, instanceId);
        var rawFormat = BenchmarkDatasetLoader.ParseOptionalTestResultFormat(rawFormatStr, inputPath, instanceId);
        var effectiveFormat = manifestEntry.TestResultFormat
            ?? rawFormat
            ?? DefaultTestResultFormat
            ?? BenchmarkLanguages.GetDefaultTestResultFormat(language);

        var customImage = NullIfWhiteSpace(manifestEntry.CustomContainerImage)
            ?? NullIfWhiteSpace(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldCustomContainerImage, inputPath, instanceId));

        var customTestCmd = NullIfWhiteSpace(manifestEntry.CustomTestCommand)
            ?? NullIfWhiteSpace(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldCustomTestCommand, inputPath, instanceId))
            ?? DefaultTestCommand;

        var pipConstraints = manifestEntry.PipConstraints
            ?? BenchmarkDatasetLoader.ParseStringArrayOrJsonString(raw, BenchmarkDatasetConstants.FieldPipConstraints, inputPath, instanceId);

        return new BenchmarkInstance
        {
            InstanceId = instanceId,
            Category = BenchmarkProblemCategory.Implementation,
            Language = language,
            Repo = repo,
            BaseCommit = baseCommit,
            ProblemStatement = problemStatement,
            HintsText = hintsText,
            CustomContainerImage = customImage,
            CustomTestCommand = customTestCmd,
            TestResultFormat = effectiveFormat,
            PipConstraints = pipConstraints,
            GoldPatch = goldPatch,
            TestPatch = testPatch,
            FailToPass = failToPass,
            PassToPass = passToPass
        };
    }

    protected virtual string ResolveLanguage(JsonElement raw, BenchmarkManifestEntry manifestEntry, string inputPath)
    {
        var rawLanguage = !string.IsNullOrWhiteSpace(manifestEntry.Language)
            ? manifestEntry.Language
            : BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldLanguage, inputPath, manifestEntry.InstanceId)
              ?? DefaultLanguage;

        if (string.IsNullOrWhiteSpace(rawLanguage))
        {
            throw new BenchmarkDatasetException(
                $"Missing language metadata for instance '{manifestEntry.InstanceId}' during '{SourceKey}' import.",
                inputPath,
                manifestEntry.InstanceId);
        }

        var normalized = BenchmarkLanguages.Normalize(rawLanguage);
        if (!BenchmarkLanguages.Supported.Contains(normalized))
        {
            throw new BenchmarkDatasetException(
                $"Unsupported language '{rawLanguage}' for instance '{manifestEntry.InstanceId}' during '{SourceKey}' import. Supported: {string.Join(", ", BenchmarkLanguages.Supported)}.",
                inputPath,
                manifestEntry.InstanceId);
        }

        return normalized;
    }

    protected virtual string ExtractRepo(JsonElement raw, BenchmarkManifestEntry manifestEntry, string inputPath)
    {
        var repo = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldRepo, inputPath, manifestEntry.InstanceId)?.Trim();
        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new BenchmarkDatasetException(
                $"Missing required '{BenchmarkDatasetConstants.FieldRepo}' for instance '{manifestEntry.InstanceId}' in '{inputPath}'.",
                inputPath,
                manifestEntry.InstanceId);
        }

        return repo;
    }

    protected virtual string ExtractBaseCommit(JsonElement raw, string inputPath, string instanceId)
    {
        var baseCommit = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldBaseCommit, inputPath, instanceId)?.Trim();
        if (string.IsNullOrWhiteSpace(baseCommit))
        {
            throw new BenchmarkDatasetException(
                $"Empty or missing '{BenchmarkDatasetConstants.FieldBaseCommit}' for instance '{instanceId}' in '{inputPath}'.",
                inputPath,
                instanceId);
        }

        return baseCommit;
    }

    protected virtual string ExtractProblemStatement(JsonElement raw, string inputPath, string instanceId)
    {
        var problemStatement = BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldProblemStatement, inputPath, instanceId);
        if (string.IsNullOrWhiteSpace(problemStatement))
        {
            throw new BenchmarkDatasetException(
                $"Empty or missing '{BenchmarkDatasetConstants.FieldProblemStatement}' for instance '{instanceId}' in '{inputPath}'.",
                inputPath,
                instanceId);
        }

        return problemStatement;
    }

    protected virtual string? ExtractGoldPatch(JsonElement raw, string inputPath, string instanceId) =>
        NullIfWhiteSpacePreserve(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldPatch, inputPath, instanceId))
        ?? NullIfWhiteSpacePreserve(BenchmarkDatasetLoader.GetOptionalString(raw, BenchmarkDatasetConstants.FieldGoldPatch, inputPath, instanceId));

    protected static IReadOnlyList<string> ExtractTestList(
        JsonElement raw,
        string inputPath,
        string instanceId,
        params string[] candidatePropertyNames)
    {
        foreach (var propName in candidatePropertyNames)
        {
            if (!BenchmarkDatasetLoader.TryGetPropertyIgnoreCase(raw, propName, out var prop) ||
                prop.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            IReadOnlyList<string> extracted;
            if (prop.ValueKind == JsonValueKind.Object)
            {
                extracted = prop.EnumerateObject()
                    .Select(p => p.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
            }
            else
            {
                extracted = BenchmarkDatasetLoader.ParseStringArrayOrJsonString(raw, propName, inputPath, instanceId);
            }

            if (extracted.Count > 0)
            {
                return extracted;
            }
        }

        return Array.Empty<string>();
    }

    protected virtual string ExtractRawInstanceId(JsonElement obj, string inputPath, string location)
    {
        var id = BenchmarkDatasetLoader.GetOptionalString(obj, BenchmarkDatasetConstants.FieldInstanceId, inputPath);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new BenchmarkDatasetException(
                $"Missing required '{BenchmarkDatasetConstants.FieldInstanceId}' at {location} in '{inputPath}'.",
                inputPath);
        }

        return id.Trim();
    }

    private async Task<Dictionary<string, JsonElement>> LoadRawRecordsAsync(
        string inputPath,
        IReadOnlySet<string> selectedIds,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(inputPath);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(extension, BenchmarkDatasetConstants.JsonlExtension, StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(inputPath);
            int lineNumber = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line, BenchmarkDatasetLoader.DocumentOptions);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new BenchmarkDatasetException(
                            $"JSONL line {lineNumber} in '{inputPath}' must be a JSON object.",
                            inputPath);
                    }

                    AddRawRecord(seenIds, records, selectedIds, doc.RootElement, inputPath, $"line {lineNumber}");
                }
                catch (JsonException ex)
                {
                    throw new BenchmarkDatasetException(
                        $"Malformed JSON on line {lineNumber} of '{inputPath}': {ex.Message}",
                        ex,
                        inputPath);
                }
            }
        }
        else if (string.Equals(extension, BenchmarkDatasetConstants.JsonExtension, StringComparison.OrdinalIgnoreCase))
        {
            var content = await File.ReadAllTextAsync(inputPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new BenchmarkDatasetException($"External dataset file '{inputPath}' is empty.", inputPath);
            }

            try
            {
                using var doc = JsonDocument.Parse(content, BenchmarkDatasetLoader.DocumentOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (elem.ValueKind != JsonValueKind.Object)
                        {
                            throw new BenchmarkDatasetException(
                                $"External dataset element at index {index} in '{inputPath}' must be a JSON object.",
                                inputPath);
                        }

                        AddRawRecord(seenIds, records, selectedIds, elem, inputPath, $"index {index}");
                        index++;
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddRawRecord(seenIds, records, selectedIds, doc.RootElement, inputPath, "root");
                }
                else
                {
                    throw new BenchmarkDatasetException(
                        $"JSON dataset '{inputPath}' must be a JSON array or object.",
                        inputPath);
                }
            }
            catch (JsonException ex)
            {
                throw new BenchmarkDatasetException(
                    $"Malformed JSON in '{inputPath}': {ex.Message}",
                    ex,
                    inputPath);
            }
        }
        else
        {
            throw new BenchmarkDatasetException(
                $"Unsupported external dataset extension '{extension}' for '{inputPath}'. Expected '.jsonl' or '.json'.",
                inputPath);
        }

        if (seenIds.Count == 0)
        {
            throw new BenchmarkDatasetException($"External dataset '{inputPath}' contains zero records.", inputPath);
        }

        return records;
    }

    private void AddRawRecord(
        HashSet<string> seenIds,
        Dictionary<string, JsonElement> records,
        IReadOnlySet<string> selectedIds,
        JsonElement obj,
        string inputPath,
        string location)
    {
        var id = ExtractRawInstanceId(obj, inputPath, location);
        if (!seenIds.Add(id))
        {
            throw new BenchmarkDatasetException(
                $"Duplicate instance_id '{id}' found in external dataset '{inputPath}'.",
                inputPath,
                id);
        }

        if (selectedIds.Contains(id))
        {
            records.Add(id, obj.Clone());
        }
    }

    protected static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected static string? NullIfWhiteSpacePreserve(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
