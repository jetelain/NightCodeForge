using System.Globalization;
using System.Text;
using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Parses and strictly validates `.jsonl` and `.json` benchmark datasets across Implementation,
/// Code Review, and ForgeTask Review categories.
/// </summary>
public sealed class BenchmarkDatasetLoader : IBenchmarkDatasetLoader
{
    internal static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public async Task<IReadOnlyList<BenchmarkInstance>> LoadAsync(
        string datasetPath,
        BenchmarkProblemCategory? categoryOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(datasetPath))
        {
            throw new BenchmarkDatasetException("Dataset path cannot be empty.", datasetPath);
        }

        if (!File.Exists(datasetPath))
        {
            throw new BenchmarkDatasetException($"Benchmark dataset file '{datasetPath}' does not exist.", datasetPath);
        }

        var extension = Path.GetExtension(datasetPath);
        var records = new List<BenchmarkInstance>();

        if (string.Equals(extension, BenchmarkDatasetConstants.JsonlExtension, StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(datasetPath);
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
                    using var doc = JsonDocument.Parse(line, DocumentOptions);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new BenchmarkDatasetException(
                            $"JSONL line {lineNumber} in '{datasetPath}' must be a JSON object.",
                            datasetPath);
                    }

                    records.Add(ParseInstance(doc.RootElement, datasetPath, $"line {lineNumber}", categoryOverride));
                }
                catch (JsonException ex)
                {
                    throw new BenchmarkDatasetException(
                        $"Malformed JSON on line {lineNumber} of '{datasetPath}': {ex.Message}",
                        ex,
                        datasetPath);
                }
            }
        }
        else if (string.Equals(extension, BenchmarkDatasetConstants.JsonExtension, StringComparison.OrdinalIgnoreCase))
        {
            var content = await File.ReadAllTextAsync(datasetPath, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new BenchmarkDatasetException($"Benchmark dataset file '{datasetPath}' is empty.", datasetPath);
            }

            try
            {
                using var doc = JsonDocument.Parse(content, DocumentOptions);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (elem.ValueKind != JsonValueKind.Object)
                        {
                            throw new BenchmarkDatasetException(
                                $"Dataset element at index {index} in '{datasetPath}' must be a JSON object.",
                                datasetPath);
                        }

                        records.Add(ParseInstance(elem, datasetPath, $"index {index}", categoryOverride));
                        index++;
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    records.Add(ParseInstance(doc.RootElement, datasetPath, "root", categoryOverride));
                }
                else
                {
                    throw new BenchmarkDatasetException(
                        $"JSON dataset '{datasetPath}' must be a JSON array or object.",
                        datasetPath);
                }
            }
            catch (JsonException ex)
            {
                throw new BenchmarkDatasetException(
                    $"Malformed JSON in '{datasetPath}': {ex.Message}",
                    ex,
                    datasetPath);
            }
        }
        else
        {
            throw new BenchmarkDatasetException(
                $"Unsupported dataset extension '{extension}' for '{datasetPath}'. Expected '.jsonl' or '.json'.",
                datasetPath);
        }

        if (records.Count == 0)
        {
            throw new BenchmarkDatasetException($"Benchmark dataset '{datasetPath}' contains zero records.", datasetPath);
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!seenIds.Add(record.InstanceId))
            {
                throw new BenchmarkDatasetException(
                    $"Duplicate instance_id '{record.InstanceId}' found in dataset '{datasetPath}'.",
                    datasetPath,
                    record.InstanceId);
            }
        }

        return records;
    }

    private static BenchmarkInstance ParseInstance(
        JsonElement element,
        string datasetPath,
        string location,
        BenchmarkProblemCategory? categoryOverride)
    {
        var instanceId = RequireString(element, BenchmarkDatasetConstants.FieldInstanceId, datasetPath, location).Trim();
        var repo = RequireString(element, BenchmarkDatasetConstants.FieldRepo, datasetPath, location, instanceId).Trim();
        var baseCommit = RequireString(element, BenchmarkDatasetConstants.FieldBaseCommit, datasetPath, location, instanceId).Trim();
        var problemStatement = RequireString(element, BenchmarkDatasetConstants.FieldProblemStatement, datasetPath, location, instanceId);

        var rawCategory = GetOptionalString(element, BenchmarkDatasetConstants.FieldCategory, datasetPath, instanceId);
        var category = ParseOptionalCategory(rawCategory, datasetPath, instanceId);
        var effectiveCategory = categoryOverride ?? category ?? BenchmarkProblemCategory.Implementation;

        var rawLanguage = GetOptionalString(element, BenchmarkDatasetConstants.FieldLanguage, datasetPath, instanceId);
        var language = BenchmarkLanguages.Normalize(rawLanguage);
        if (!BenchmarkLanguages.Supported.Contains(language))
        {
            throw new BenchmarkDatasetException(
                $"Unsupported language '{rawLanguage}' for instance '{instanceId}' in '{datasetPath}'. Supported: {string.Join(", ", BenchmarkLanguages.Supported)}.",
                datasetPath,
                instanceId);
        }

        var rawFormat = GetOptionalString(element, BenchmarkDatasetConstants.FieldTestResultFormat, datasetPath, instanceId);
        var testResultFormat = ParseOptionalTestResultFormat(rawFormat, datasetPath, instanceId);

        var hintsText = GetOptionalString(element, BenchmarkDatasetConstants.FieldHintsText, datasetPath, instanceId);
        var customImage = GetOptionalString(element, BenchmarkDatasetConstants.FieldCustomContainerImage, datasetPath, instanceId)?.Trim();
        var customTestCmd = GetOptionalString(element, BenchmarkDatasetConstants.FieldCustomTestCommand, datasetPath, instanceId)?.Trim();
        var goldPatch = GetOptionalString(element, BenchmarkDatasetConstants.FieldPatch, datasetPath, instanceId)
            ?? GetOptionalString(element, BenchmarkDatasetConstants.FieldGoldPatch, datasetPath, instanceId);
        var testPatch = GetOptionalString(element, BenchmarkDatasetConstants.FieldTestPatch, datasetPath, instanceId);
        var failToPass = ParseStringArrayOrJsonString(element, BenchmarkDatasetConstants.FieldFailToPass, datasetPath, instanceId);
        var passToPass = ParseStringArrayOrJsonString(element, BenchmarkDatasetConstants.FieldPassToPass, datasetPath, instanceId);
        var candidatePatch = GetOptionalString(element, BenchmarkDatasetConstants.FieldCandidatePatch, datasetPath, instanceId);
        var taskSpecification = GetOptionalString(element, BenchmarkDatasetConstants.FieldTaskSpecification, datasetPath, instanceId);
        var expectedVerdict = GetOptionalString(element, BenchmarkDatasetConstants.FieldExpectedVerdict, datasetPath, instanceId)?.Trim();
        var expectedDifficulty = GetOptionalString(element, BenchmarkDatasetConstants.FieldExpectedDifficulty, datasetPath, instanceId)?.Trim();
        var expectedFindings = ParseExpectedFindings(element, datasetPath, instanceId);

        ValidateCategoryRequirements(
            effectiveCategory,
            datasetPath,
            instanceId,
            candidatePatch,
            taskSpecification,
            expectedVerdict,
            expectedDifficulty);

        return new BenchmarkInstance
        {
            InstanceId = instanceId,
            Category = category,
            Language = language,
            Repo = repo,
            BaseCommit = baseCommit,
            ProblemStatement = problemStatement,
            HintsText = hintsText,
            CustomContainerImage = customImage,
            CustomTestCommand = customTestCmd,
            TestResultFormat = testResultFormat,
            GoldPatch = goldPatch,
            TestPatch = testPatch,
            FailToPass = failToPass,
            PassToPass = passToPass,
            CandidatePatch = candidatePatch,
            TaskSpecification = taskSpecification,
            ExpectedVerdict = expectedVerdict,
            ExpectedDifficulty = expectedDifficulty,
            ExpectedFindings = expectedFindings
        };
    }

    private static void ValidateCategoryRequirements(
        BenchmarkProblemCategory effectiveCategory,
        string datasetPath,
        string instanceId,
        string? candidatePatch,
        string? taskSpecification,
        string? expectedVerdict,
        string? expectedDifficulty)
    {
        if (effectiveCategory == BenchmarkProblemCategory.CodeReview)
        {
            if (string.IsNullOrWhiteSpace(candidatePatch))
            {
                throw new BenchmarkDatasetException(
                    $"CodeReview instance '{instanceId}' in '{datasetPath}' is missing required 'candidate_patch'.",
                    datasetPath,
                    instanceId);
            }

            if (string.IsNullOrWhiteSpace(expectedVerdict) || !TryParseCodeReviewVerdict(expectedVerdict, out _))
            {
                throw new BenchmarkDatasetException(
                    $"CodeReview instance '{instanceId}' in '{datasetPath}' has missing or invalid 'expected_verdict' ('{expectedVerdict ?? "<null>"}'). Expected Approve, MinorFix, or RequestChanges.",
                    datasetPath,
                    instanceId);
            }
        }
        else if (effectiveCategory == BenchmarkProblemCategory.TaskReview)
        {
            if (string.IsNullOrWhiteSpace(taskSpecification))
            {
                throw new BenchmarkDatasetException(
                    $"TaskReview instance '{instanceId}' in '{datasetPath}' is missing required 'task_specification'.",
                    datasetPath,
                    instanceId);
            }

            if (string.IsNullOrWhiteSpace(expectedVerdict) || !TryParseTaskReviewVerdict(expectedVerdict, out _))
            {
                throw new BenchmarkDatasetException(
                    $"TaskReview instance '{instanceId}' in '{datasetPath}' has missing or invalid 'expected_verdict' ('{expectedVerdict ?? "<null>"}'). Expected Ready or NeedsClarification.",
                    datasetPath,
                    instanceId);
            }

            if (!string.IsNullOrWhiteSpace(expectedDifficulty) &&
                !IsValidDifficulty(expectedDifficulty))
            {
                throw new BenchmarkDatasetException(
                    $"TaskReview instance '{instanceId}' in '{datasetPath}' has invalid 'expected_difficulty' ('{expectedDifficulty}'). Expected standard, difficult, or expert.",
                    datasetPath,
                    instanceId);
            }
        }
    }

    public static bool TryParseCodeReviewVerdict(string? value, out CodeReviewVerdict verdict)
    {
        verdict = CodeReviewVerdict.Approve;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (NormalizeToken(value))
        {
            case "approve" or "approved":
                verdict = CodeReviewVerdict.Approve;
                return true;
            case "minorfix" or "minorpolish":
                verdict = CodeReviewVerdict.MinorFix;
                return true;
            case "requestchanges" or "changesrequested" or "reject":
                verdict = CodeReviewVerdict.RequestChanges;
                return true;
            default:
                return false;
        }
    }

    public static bool TryParseTaskReviewVerdict(string? value, out TaskReviewVerdict verdict)
    {
        verdict = TaskReviewVerdict.Ready;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (NormalizeToken(value))
        {
            case "ready":
                verdict = TaskReviewVerdict.Ready;
                return true;
            case "needsclarification" or "waitingforhuman" or "ask":
                verdict = TaskReviewVerdict.NeedsClarification;
                return true;
            default:
                return false;
        }
    }

    private static bool IsValidDifficulty(string value) =>
        Enum.TryParse<TaskDifficulty>(value.Trim(), ignoreCase: true, out _);

    internal static BenchmarkProblemCategory? ParseOptionalCategory(string? raw, string datasetPath, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return NormalizeToken(raw) switch
        {
            "implementation" or "impl" => BenchmarkProblemCategory.Implementation,
            "codereview" => BenchmarkProblemCategory.CodeReview,
            "taskreview" or "forgetaskreview" => BenchmarkProblemCategory.TaskReview,
            _ => throw new BenchmarkDatasetException(
                $"Invalid category '{raw}' for instance '{instanceId}' in '{datasetPath}'.",
                datasetPath,
                instanceId)
        };
    }

    internal static TestResultFormat? ParseOptionalTestResultFormat(string? raw, string datasetPath, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return NormalizeToken(raw) switch
        {
            "trx" => TestResultFormat.Trx,
            "junit" or "junitxml" => TestResultFormat.JUnitXml,
            "cargotest" or "cargo" => TestResultFormat.CargoTest,
            "exitcode" => TestResultFormat.ExitCode,
            _ => throw new BenchmarkDatasetException(
                $"Invalid test_result_format '{raw}' for instance '{instanceId}' in '{datasetPath}'.",
                datasetPath,
                instanceId)
        };
    }

    private static string RequireString(
        JsonElement obj,
        string propertyName,
        string datasetPath,
        string location,
        string? instanceId = null)
    {
        var value = GetOptionalString(obj, propertyName, datasetPath, instanceId);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BenchmarkDatasetException(
                $"Missing required field '{propertyName}' at {location} in '{datasetPath}'.",
                datasetPath,
                instanceId);
        }

        return value;
    }

    internal static string? GetOptionalString(
        JsonElement obj,
        string propertyName,
        string? datasetPath = null,
        string? instanceId = null)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            _ => throw new BenchmarkDatasetException(
                $"Field '{propertyName}' must be a string, got {prop.ValueKind}.",
                datasetPath,
                instanceId)
        };
    }

    internal static IReadOnlyList<string> ParseStringArrayOrJsonString(
        JsonElement obj,
        string propertyName,
        string datasetPath,
        string instanceId)
    {
        if (!TryGetPropertyIgnoreCase(obj, propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (prop.ValueKind == JsonValueKind.Array)
        {
            return ExtractStringArray(prop, propertyName, datasetPath, instanceId);
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<string>();
            }

            try
            {
                using var innerDoc = JsonDocument.Parse(raw, DocumentOptions);
                if (innerDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new BenchmarkDatasetException(
                        $"Field '{propertyName}' string value for instance '{instanceId}' in '{datasetPath}' must decode to a JSON array.",
                        datasetPath,
                        instanceId);
                }

                return ExtractStringArray(innerDoc.RootElement, propertyName, datasetPath, instanceId);
            }
            catch (JsonException ex)
            {
                if (TryParsePythonStringList(raw, out var pythonItems))
                {
                    return pythonItems;
                }

                throw new BenchmarkDatasetException(
                    $"Field '{propertyName}' string value for instance '{instanceId}' in '{datasetPath}' is not valid JSON: {ex.Message}",
                    ex,
                    datasetPath,
                    instanceId);
            }
        }

        throw new BenchmarkDatasetException(
            $"Field '{propertyName}' for instance '{instanceId}' in '{datasetPath}' must be a JSON array or JSON-encoded string array, got {prop.ValueKind}.",
            datasetPath,
            instanceId);
    }

    internal static bool TryParsePythonStringList(string raw, out IReadOnlyList<string> items)
    {
        items = Array.Empty<string>();
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
        {
            return false;
        }

        var result = new List<string>();
        int index = 1;
        int end = trimmed.Length - 1;

        while (index < end)
        {
            while (index < end && char.IsWhiteSpace(trimmed[index]))
            {
                index++;
            }

            if (index >= end)
            {
                break;
            }

            var quoteChar = trimmed[index];
            if (quoteChar is not '\'' and not '"')
            {
                return false;
            }

            index++;
            if (!TryReadPythonQuotedString(trimmed, ref index, end, quoteChar, out var item))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(item))
            {
                result.Add(item);
            }

            while (index < end && char.IsWhiteSpace(trimmed[index]))
            {
                index++;
            }

            if (index >= end)
            {
                break;
            }

            if (trimmed[index] != ',')
            {
                return false;
            }

            index++;
        }

        items = result;
        return true;
    }

    private static bool TryReadPythonQuotedString(
        ReadOnlySpan<char> source,
        ref int index,
        int end,
        char quoteChar,
        out string value)
    {
        var sb = new StringBuilder();
        while (index < end)
        {
            var ch = source[index++];
            if (ch == quoteChar)
            {
                value = sb.ToString();
                return true;
            }

            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (index >= end)
            {
                value = string.Empty;
                return false;
            }

            var escaped = source[index++];
            switch (escaped)
            {
                case '\\': sb.Append('\\'); break;
                case '\'': sb.Append('\''); break;
                case '"': sb.Append('"'); break;
                case '/': sb.Append('/'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (index + 4 > end ||
                        !ushort.TryParse(source.Slice(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
                    {
                        value = string.Empty;
                        return false;
                    }
                    sb.Append((char)codeUnit);
                    index += 4;
                    break;
                default:
                    value = string.Empty;
                    return false;
            }
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> ExtractStringArray(
        JsonElement arrayElement,
        string propertyName,
        string datasetPath,
        string instanceId)
    {
        var result = new List<string>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new BenchmarkDatasetException(
                    $"Field '{propertyName}' for instance '{instanceId}' in '{datasetPath}' contains a non-string element ({item.ValueKind}).",
                    datasetPath,
                    instanceId);
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static IReadOnlyList<BenchmarkExpectedFinding> ParseExpectedFindings(
        JsonElement obj,
        string datasetPath,
        string instanceId)
    {
        if (!TryGetPropertyIgnoreCase(obj, BenchmarkDatasetConstants.FieldExpectedFindings, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<BenchmarkExpectedFinding>();
        }

        if (prop.ValueKind != JsonValueKind.Array)
        {
            throw new BenchmarkDatasetException(
                $"Field 'expected_findings' for instance '{instanceId}' in '{datasetPath}' must be a JSON array.",
                datasetPath,
                instanceId);
        }

        var findings = new List<BenchmarkExpectedFinding>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new BenchmarkDatasetException(
                    $"Each item in 'expected_findings' for instance '{instanceId}' in '{datasetPath}' must be a JSON object.",
                    datasetPath,
                    instanceId);
            }

            var filePath = GetOptionalString(item, BenchmarkDatasetConstants.FieldFilePath, datasetPath, instanceId)
                ?? GetOptionalString(item, BenchmarkDatasetConstants.FieldFile, datasetPath, instanceId);
            var symbolHint = GetOptionalString(item, BenchmarkDatasetConstants.FieldSymbolOrLineHint, datasetPath, instanceId)
                ?? GetOptionalString(item, BenchmarkDatasetConstants.FieldSymbol, datasetPath, instanceId)
                ?? GetOptionalString(item, BenchmarkDatasetConstants.FieldLineHint, datasetPath, instanceId);
            var severity = GetOptionalString(item, BenchmarkDatasetConstants.FieldSeverity, datasetPath, instanceId);
            var summary = GetOptionalString(item, BenchmarkDatasetConstants.FieldSummary, datasetPath, instanceId);
            var keywords = ParseStringArrayOrJsonString(item, BenchmarkDatasetConstants.FieldKeywords, datasetPath, instanceId);

            if (string.IsNullOrWhiteSpace(filePath) &&
                string.IsNullOrWhiteSpace(symbolHint) &&
                string.IsNullOrWhiteSpace(summary) &&
                keywords.Count == 0)
            {
                throw new BenchmarkDatasetException(
                    $"Expected finding entry for instance '{instanceId}' in '{datasetPath}' must specify at least one of file_path, symbol_or_line_hint, summary, or keywords.",
                    datasetPath,
                    instanceId);
            }

            findings.Add(new BenchmarkExpectedFinding(filePath, symbolHint, severity, summary, keywords));
        }

        return findings;
    }

    internal static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        var normalizedTarget = NormalizeToken(propertyName);
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeToken(prop.Name), normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static string NormalizeToken(string value) =>
        value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
