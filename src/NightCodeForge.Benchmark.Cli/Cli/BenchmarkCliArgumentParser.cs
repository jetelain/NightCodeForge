using System.Globalization;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Cli;

/// <summary>
/// Parses and validates command-line arguments for <c>ncf-bench run</c>, <c>ncf-bench report</c>,
/// <c>ncf-bench dataset import</c>, and <c>ncf-bench dataset validate</c>.
/// </summary>
public static class BenchmarkCliArgumentParser
{
    public static RunCommandOptions? ParseRunOptions(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var options = new RunCommandOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i].Trim();
            if (string.IsNullOrEmpty(rawArg))
            {
                continue;
            }

            if (TryMatchBooleanFlag(rawArg, BenchmarkCliConstants.OptionVerify, BenchmarkCliConstants.OptionNoVerify, out var verifyVal))
            {
                options.VerifyWithTests = verifyVal;
                continue;
            }

            if (TryMatchBooleanFlag(rawArg, BenchmarkCliConstants.OptionSkills, BenchmarkCliConstants.OptionNoSkills, out var skillsVal))
            {
                options.EnableStackSkills = skillsVal;
                continue;
            }

            if (TryMatchBooleanFlag(rawArg, BenchmarkCliConstants.OptionHints, BenchmarkCliConstants.OptionNoHints, out var hintsVal))
            {
                options.IncludeHints = hintsVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionDataset, out var datasetVal, out error))
            {
                if (error != null) return null;
                options.DatasetPath = datasetVal!;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionManifest, out var manifestVal, out error))
            {
                if (error != null) return null;
                options.ManifestPath = manifestVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionSuite, out var suiteVal, out error))
            {
                if (error != null) return null;
                options.SuiteName = suiteVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionModels, out var modelsVal, out error))
            {
                if (error != null) return null;
                options.ModelsCsv = modelsVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionConfig, out var configVal, out error))
            {
                if (error != null) return null;
                options.ConfigPath = configVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionProvider, out var providerVal, out error))
            {
                if (error != null) return null;
                if (!TryParseProvider(providerVal!, out var parsedProvider))
                {
                    error = $"Invalid value '{providerVal}' for '{BenchmarkCliConstants.OptionProvider}'. Expected one of: {BenchmarkCliConstants.ProviderOllama}, {BenchmarkCliConstants.ProviderOllamaRouter}, {BenchmarkCliConstants.ProviderOpenAi}.";
                    return null;
                }
                options.Provider = parsedProvider;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionEndpoint, out var endpointVal, out error))
            {
                if (error != null) return null;
                options.Endpoint = endpointVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionCategory, out var categoryVal, out error))
            {
                if (error != null) return null;
                if (!TryParseCategory(categoryVal!, out var parsedCategory))
                {
                    error = $"Invalid value '{categoryVal}' for '{BenchmarkCliConstants.OptionCategory}'. Expected one of: {BenchmarkCliConstants.CategoryImplementation}, {BenchmarkCliConstants.CategoryCodeReview}, {BenchmarkCliConstants.CategoryTaskReview}.";
                    return null;
                }
                options.Category = parsedCategory;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionFilter, out var filterVal, out error))
            {
                if (error != null) return null;
                if (!TryAppendFilterIds(filterVal!, options.FilterInstanceIds, out error))
                {
                    return null;
                }
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionLimit, out var limitVal, out error))
            {
                if (error != null) return null;
                if (!TryParsePositiveInt(limitVal!, BenchmarkCliConstants.OptionLimit, out var limit, out error))
                {
                    return null;
                }
                options.Limit = limit;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionTimeout, out var timeoutVal, out error))
            {
                if (error != null) return null;
                if (!TryParsePositiveInt(timeoutVal!, BenchmarkCliConstants.OptionTimeout, out var timeout, out error))
                {
                    return null;
                }
                options.TimeoutSeconds = timeout;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionConcurrency, out var concurrencyVal, out error))
            {
                if (error != null) return null;
                if (!TryParsePositiveInt(concurrencyVal!, BenchmarkCliConstants.OptionConcurrency, out var concurrency, out error))
                {
                    return null;
                }
                options.Concurrency = concurrency;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionResume, out var resumeVal, out error))
            {
                if (error != null) return null;
                options.ResumeFromDir = resumeVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionJudgeModel, out var judgeModelVal, out error))
            {
                if (error != null) return null;
                options.JudgeModel = judgeModelVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionJudgeProvider, out var judgeProviderVal, out error))
            {
                if (error != null) return null;
                if (!TryParseProvider(judgeProviderVal!, out var parsedJudgeProvider))
                {
                    error = $"Invalid value '{judgeProviderVal}' for '{BenchmarkCliConstants.OptionJudgeProvider}'. Expected one of: {BenchmarkCliConstants.ProviderOllama}, {BenchmarkCliConstants.ProviderOllamaRouter}, {BenchmarkCliConstants.ProviderOpenAi}.";
                    return null;
                }
                options.JudgeProvider = parsedJudgeProvider;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionJudgeEndpoint, out var judgeEndpointVal, out error))
            {
                if (error != null) return null;
                options.JudgeEndpoint = judgeEndpointVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionOut, out var outVal, out error))
            {
                if (error != null) return null;
                options.OutputDirectory = outVal;
                continue;
            }

            error = rawArg.StartsWith('-')
                ? $"Unrecognized option '{rawArg}'."
                : $"Unexpected positional argument '{rawArg}'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.DatasetPath))
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionDataset} <path>'.";
            return null;
        }

        bool hasModels = !string.IsNullOrWhiteSpace(options.ModelsCsv);
        bool hasConfig = !string.IsNullOrWhiteSpace(options.ConfigPath);
        if (hasModels == hasConfig)
        {
            error = hasModels
                ? $"Options '{BenchmarkCliConstants.OptionModels}' and '{BenchmarkCliConstants.OptionConfig}' are mutually exclusive; specify only one."
                : $"Either '{BenchmarkCliConstants.OptionModels} <m1,m2>' or '{BenchmarkCliConstants.OptionConfig} <candidates.json>' must be specified.";
            return null;
        }

        if (hasModels &&
            options.ModelsCsv!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length == 0)
        {
            error = $"Option '{BenchmarkCliConstants.OptionModels}' must contain at least one model name.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.Endpoint) && !IsValidHttpEndpoint(options.Endpoint))
        {
            error = $"Invalid URL '{options.Endpoint}' for '{BenchmarkCliConstants.OptionEndpoint}'. Expected an absolute http:// or https:// URL.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.JudgeEndpoint) && !IsValidHttpEndpoint(options.JudgeEndpoint))
        {
            error = $"Invalid URL '{options.JudgeEndpoint}' for '{BenchmarkCliConstants.OptionJudgeEndpoint}'. Expected an absolute http:// or https:// URL.";
            return null;
        }

        if ((options.JudgeProvider.HasValue || !string.IsNullOrWhiteSpace(options.JudgeEndpoint)) &&
            string.IsNullOrWhiteSpace(options.JudgeModel))
        {
            error = $"'{BenchmarkCliConstants.OptionJudgeProvider}' and '{BenchmarkCliConstants.OptionJudgeEndpoint}' require '{BenchmarkCliConstants.OptionJudgeModel} <model>'.";
            return null;
        }

        return options;
    }

    public static ReportCommandOptions? ParseReportOptions(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var options = new ReportCommandOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i].Trim();
            if (string.IsNullOrEmpty(rawArg))
            {
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionBaseline, out var baselineVal, out error))
            {
                if (error != null) return null;
                options.BaselineDirectory = baselineVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionFormat, out var formatVal, out error))
            {
                if (error != null) return null;
                if (!TryParseReportFormat(formatVal!, out var parsedFormat))
                {
                    error = $"Invalid value '{formatVal}' for '{BenchmarkCliConstants.OptionFormat}'. Expected one of: {BenchmarkCliConstants.FormatMarkdown}, {BenchmarkCliConstants.FormatAscii}, {BenchmarkCliConstants.FormatJson}.";
                    return null;
                }
                options.Format = parsedFormat;
                continue;
            }

            if (rawArg.StartsWith('-'))
            {
                error = $"Unrecognized option '{rawArg}'.";
                return null;
            }

            options.ResultDirectories.Add(rawArg);
        }

        if (options.ResultDirectories.Count == 0)
        {
            error = "At least one results directory (<resultsDir1>) must be specified.";
            return null;
        }

        return options;
    }

    public static DatasetImportCommandOptions? ParseDatasetImportOptions(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var options = new DatasetImportCommandOptions();
        bool hasSource = false;

        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i].Trim();
            if (string.IsNullOrEmpty(rawArg))
            {
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionSource, out var sourceVal, out error))
            {
                if (error != null) return null;
                if (!TryParseDatasetSource(sourceVal!, out var parsedSource))
                {
                    error = $"Invalid value '{sourceVal}' for '{BenchmarkCliConstants.OptionSource}'. Expected one of: {BenchmarkDatasetConstants.SourceSweSharpBench}, {BenchmarkDatasetConstants.SourceSweBenchMultilingual}, {BenchmarkDatasetConstants.SourceMultiSweBench}, {BenchmarkDatasetConstants.SourceSweBenchVerified}.";
                    return null;
                }

                options.Source = parsedSource;
                hasSource = true;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionInput, out var inputVal, out error))
            {
                if (error != null) return null;
                options.InputPath = inputVal!;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionManifest, out var manifestVal, out error))
            {
                if (error != null) return null;
                options.ManifestPath = manifestVal!;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionOut, out var outVal, out error))
            {
                if (error != null) return null;
                options.OutputPath = outVal!;
                continue;
            }

            error = rawArg.StartsWith('-')
                ? $"Unrecognized option '{rawArg}'."
                : $"Unexpected positional argument '{rawArg}'.";
            return null;
        }

        if (!hasSource)
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionSource} <source>'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.InputPath))
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionInput} <path>'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionManifest} <manifest.json>'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionOut} <path.jsonl>'.";
            return null;
        }

        return options;
    }

    public static DatasetValidateCommandOptions? ParseDatasetValidateOptions(string[] args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        error = null;
        var options = new DatasetValidateCommandOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var rawArg = args[i].Trim();
            if (string.IsNullOrEmpty(rawArg))
            {
                continue;
            }

            if (TryMatchBooleanFlag(rawArg, BenchmarkCliConstants.OptionVerify, BenchmarkCliConstants.OptionNoVerify, out var verifyVal))
            {
                options.VerifyWithTests = verifyVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionDataset, out var datasetVal, out error))
            {
                if (error != null) return null;
                options.DatasetPath = datasetVal!;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionManifest, out var manifestVal, out error))
            {
                if (error != null) return null;
                options.ManifestPath = manifestVal;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionCategory, out var categoryVal, out error))
            {
                if (error != null) return null;
                if (!TryParseCategory(categoryVal!, out var parsedCategory))
                {
                    error = $"Invalid value '{categoryVal}' for '{BenchmarkCliConstants.OptionCategory}'. Expected one of: {BenchmarkCliConstants.CategoryImplementation}, {BenchmarkCliConstants.CategoryCodeReview}, {BenchmarkCliConstants.CategoryTaskReview}.";
                    return null;
                }
                options.Category = parsedCategory;
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionFilter, out var filterVal, out error))
            {
                if (error != null) return null;
                if (!TryAppendFilterIds(filterVal!, options.FilterInstanceIds, out error))
                {
                    return null;
                }
                continue;
            }

            if (TryReadOptionValue(args, ref i, BenchmarkCliConstants.OptionLimit, out var limitVal, out error))
            {
                if (error != null) return null;
                if (!TryParsePositiveInt(limitVal!, BenchmarkCliConstants.OptionLimit, out var limit, out error))
                {
                    return null;
                }
                options.Limit = limit;
                continue;
            }

            error = rawArg.StartsWith('-')
                ? $"Unrecognized option '{rawArg}'."
                : $"Unexpected positional argument '{rawArg}'.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.DatasetPath))
        {
            error = $"Missing required option '{BenchmarkCliConstants.OptionDataset} <path>'.";
            return null;
        }

        return options;
    }

    public static bool TryParseDatasetSource(string raw, out BenchmarkDatasetSource source)
    {
        if (!BenchmarkDatasetManifestLoader.TryNormalizeManifestSource(raw, out var canonical))
        {
            source = default;
            return false;
        }

        switch (canonical)
        {
            case BenchmarkDatasetConstants.SourceSweSharpBench:
                source = BenchmarkDatasetSource.SweSharpBench;
                return true;
            case BenchmarkDatasetConstants.SourceSweBenchMultilingual:
                source = BenchmarkDatasetSource.SweBenchMultilingual;
                return true;
            case BenchmarkDatasetConstants.SourceMultiSweBench:
                source = BenchmarkDatasetSource.MultiSweBench;
                return true;
            case BenchmarkDatasetConstants.SourceSweBenchVerified:
                source = BenchmarkDatasetSource.SweBenchVerified;
                return true;
            default:
                source = default;
                return false;
        }
    }

    private static bool TryAppendFilterIds(string rawFilter, List<string> target, out string? error)
    {
        var ids = rawFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0)
        {
            error = $"Option '{BenchmarkCliConstants.OptionFilter}' must contain at least one instance ID.";
            return false;
        }

        foreach (var id in ids)
        {
            target.Add(id);
        }

        error = null;
        return true;
    }

    public static bool TryParseProvider(string raw, out LlmProviderType provider)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case BenchmarkCliConstants.ProviderOllama:
                provider = LlmProviderType.Ollama;
                return true;
            case BenchmarkCliConstants.ProviderOllamaRouter:
            case "ollamarouter":
                provider = LlmProviderType.OllamaRouter;
                return true;
            case BenchmarkCliConstants.ProviderOpenAi:
            case BenchmarkCliConstants.ProviderOpenAiCompatible:
            case "openaicompatible":
                provider = LlmProviderType.OpenAiCompatible;
                return true;
            default:
                provider = default;
                return false;
        }
    }

    public static bool TryParseCategory(string raw, out BenchmarkProblemCategory category)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case BenchmarkCliConstants.CategoryImplementation:
            case "impl":
                category = BenchmarkProblemCategory.Implementation;
                return true;
            case BenchmarkCliConstants.CategoryCodeReview:
            case "codereview":
            case "code_review":
                category = BenchmarkProblemCategory.CodeReview;
                return true;
            case BenchmarkCliConstants.CategoryTaskReview:
            case "taskreview":
            case "task_review":
            case "forgetask-review":
                category = BenchmarkProblemCategory.TaskReview;
                return true;
            default:
                category = default;
                return false;
        }
    }

    public static bool TryParseReportFormat(string raw, out ReportOutputFormat format)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case BenchmarkCliConstants.FormatMarkdown:
            case "md":
                format = ReportOutputFormat.Markdown;
                return true;
            case BenchmarkCliConstants.FormatAscii:
            case "text":
            case "table":
                format = ReportOutputFormat.Ascii;
                return true;
            case BenchmarkCliConstants.FormatJson:
                format = ReportOutputFormat.Json;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static bool IsValidHttpEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) &&
        (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool TryMatchBooleanFlag(string rawArg, string positiveFlag, string negativeFlag, out bool value)
    {
        if (rawArg.Equals(positiveFlag, StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (rawArg.Equals(negativeFlag, StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryReadOptionValue(string[] args, ref int index, string optionName, out string? value, out string? error)
    {
        error = null;
        value = null;
        var current = args[index].Trim();

        if (current.Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= args.Length)
            {
                error = $"Missing value for option '{optionName}'.";
                return true;
            }

            var next = args[index + 1].Trim();
            if (string.IsNullOrEmpty(next))
            {
                error = $"Value for option '{optionName}' cannot be empty.";
                return true;
            }

            if (next.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Missing value for option '{optionName}'.";
                return true;
            }

            index++;
            value = next;
            return true;
        }

        var prefix = optionName + "=";
        if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var inline = current[prefix.Length..].Trim();
            if (string.IsNullOrEmpty(inline))
            {
                error = $"Value for option '{optionName}' cannot be empty.";
                return true;
            }

            value = inline;
            return true;
        }

        return false;
    }

    private static bool TryParsePositiveInt(string rawValue, string optionName, out int parsed, out string? error)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed <= 0)
        {
            error = $"Invalid value '{rawValue}' for '{optionName}'. Expected a positive integer.";
            return false;
        }

        error = null;
        return true;
    }
}
