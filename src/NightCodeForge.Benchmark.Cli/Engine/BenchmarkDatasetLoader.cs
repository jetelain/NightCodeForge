using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Parses and strictly validates `.jsonl` and `.json` benchmark datasets across Implementation,
/// Code Review, and ForgeTask Review categories.
/// </summary>
public sealed class BenchmarkDatasetLoader : IBenchmarkDatasetLoader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
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

        if (string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
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
        else if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
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
        var instanceId = RequireString(element, "instance_id", datasetPath, location).Trim();
        var repo = RequireString(element, "repo", datasetPath, location, instanceId).Trim();
        var baseCommit = RequireString(element, "base_commit", datasetPath, location, instanceId).Trim();
        var problemStatement = RequireString(element, "problem_statement", datasetPath, location, instanceId);

        var rawCategory = GetOptionalString(element, "category", datasetPath, instanceId);
        var category = ParseOptionalCategory(rawCategory, datasetPath, instanceId);
        var effectiveCategory = categoryOverride ?? category ?? BenchmarkProblemCategory.Implementation;

        var rawLanguage = GetOptionalString(element, "language", datasetPath, instanceId);
        var language = BenchmarkLanguages.Normalize(rawLanguage);
        if (!BenchmarkLanguages.Supported.Contains(language))
        {
            throw new BenchmarkDatasetException(
                $"Unsupported language '{rawLanguage}' for instance '{instanceId}' in '{datasetPath}'. Supported: {string.Join(", ", BenchmarkLanguages.Supported)}.",
                datasetPath,
                instanceId);
        }

        var rawFormat = GetOptionalString(element, "test_result_format", datasetPath, instanceId);
        var testResultFormat = ParseOptionalTestResultFormat(rawFormat, datasetPath, instanceId);

        var hintsText = GetOptionalString(element, "hints_text", datasetPath, instanceId);
        var customImage = GetOptionalString(element, "custom_container_image", datasetPath, instanceId)?.Trim();
        var customTestCmd = GetOptionalString(element, "custom_test_command", datasetPath, instanceId)?.Trim();
        var goldPatch = GetOptionalString(element, "patch", datasetPath, instanceId)
            ?? GetOptionalString(element, "gold_patch", datasetPath, instanceId);
        var testPatch = GetOptionalString(element, "test_patch", datasetPath, instanceId);
        var failToPass = ParseStringArrayOrJsonString(element, "fail_to_pass", datasetPath, instanceId);
        var passToPass = ParseStringArrayOrJsonString(element, "pass_to_pass", datasetPath, instanceId);
        var candidatePatch = GetOptionalString(element, "candidate_patch", datasetPath, instanceId);
        var taskSpecification = GetOptionalString(element, "task_specification", datasetPath, instanceId);
        var expectedVerdict = GetOptionalString(element, "expected_verdict", datasetPath, instanceId)?.Trim();
        var expectedDifficulty = GetOptionalString(element, "expected_difficulty", datasetPath, instanceId)?.Trim();
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

    private static BenchmarkProblemCategory? ParseOptionalCategory(string? raw, string datasetPath, string instanceId)
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

    private static TestResultFormat? ParseOptionalTestResultFormat(string? raw, string datasetPath, string instanceId)
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

    private static string? GetOptionalString(
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

    private static IReadOnlyList<string> ParseStringArrayOrJsonString(
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
        if (!TryGetPropertyIgnoreCase(obj, "expected_findings", out var prop) || prop.ValueKind == JsonValueKind.Null)
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

            var filePath = GetOptionalString(item, "file_path", datasetPath, instanceId)
                ?? GetOptionalString(item, "file", datasetPath, instanceId);
            var symbolHint = GetOptionalString(item, "symbol_or_line_hint", datasetPath, instanceId)
                ?? GetOptionalString(item, "symbol", datasetPath, instanceId)
                ?? GetOptionalString(item, "line_hint", datasetPath, instanceId);
            var severity = GetOptionalString(item, "severity", datasetPath, instanceId);
            var summary = GetOptionalString(item, "summary", datasetPath, instanceId);
            var keywords = ParseStringArrayOrJsonString(item, "keywords", datasetPath, instanceId);

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

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
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

    private static string NormalizeToken(string value) =>
        value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
