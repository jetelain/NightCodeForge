using System.Globalization;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Reporting;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Commands;

/// <summary>
/// Executes <c>ncf-bench run</c>, invoking <see cref="ICrucibleBenchmarkRunner"/> and writing
/// <c>results.json</c>, <c>summary.md</c>, transcripts, and <c>predictions.jsonl</c>.
/// </summary>
public sealed class RunBenchmarkCommand
{
    private readonly ICrucibleBenchmarkRunner _runner;

    public RunBenchmarkCommand(ICrucibleBenchmarkRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public async Task<int> ExecuteAsync(
        RunCommandOptions options,
        IReadOnlyList<BenchmarkModelCandidate> candidates,
        Action<string> writeLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(writeLine);

        var suiteName = ResolveSuiteName(options);
        var endpoint = ResolveEndpoint(options.Provider, options.Endpoint);
        var outputDir = ResolveOutputDirectory(options, suiteName);
        var judgeConfig = BuildJudgeConfig(options, endpoint);

        var request = new BenchmarkRunRequest
        {
            SuiteName = suiteName,
            DatasetPath = Path.GetFullPath(options.DatasetPath),
            ManifestPath = string.IsNullOrWhiteSpace(options.ManifestPath) ? null : Path.GetFullPath(options.ManifestPath),
            OutputDirectory = outputDir,
            CategoryOverride = options.Category,
            InstanceIdFilter = options.FilterInstanceIds.Count > 0 ? options.FilterInstanceIds.ToList() : null,
            MaxInstances = options.Limit,
            Candidates = candidates,
            IncludeHints = options.IncludeHints,
            VerifyWithTests = options.VerifyWithTests,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            Concurrency = options.Concurrency,
            JudgeConfig = judgeConfig,
            ResumeFromDir = string.IsNullOrWhiteSpace(options.ResumeFromDir) ? null : Path.GetFullPath(options.ResumeFromDir),
            ProviderType = options.Provider,
            Endpoint = endpoint,
            ProgressCallback = writeLine
        };

        writeLine($"[{BenchmarkCliConstants.ToolName}] Running suite '{suiteName}' against {candidates.Count} candidate(s) using {options.Provider} ({endpoint})...");

        var report = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(outputDir);
        var summaryMarkdown = BenchmarkReportFormatter.FormatSingleRun(report, ReportOutputFormat.Markdown);
        var summaryPath = Path.Combine(outputDir, BenchmarkPaths.SummaryFileName);
        await File.WriteAllTextAsync(summaryPath, summaryMarkdown + Environment.NewLine, cancellationToken).ConfigureAwait(false);

        writeLine(string.Empty);
        writeLine(summaryMarkdown);
        writeLine(string.Empty);
        writeLine($"[{BenchmarkCliConstants.ToolName}] Results written to: {Path.Combine(outputDir, BenchmarkPaths.ResultsFileName)}");
        writeLine($"[{BenchmarkCliConstants.ToolName}] Summary written to: {summaryPath}");

        return BenchmarkCliConstants.ExitSuccess;
    }

    public static string ResolveSuiteName(RunCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.SuiteName))
        {
            return options.SuiteName.Trim();
        }

        var stem = Path.GetFileNameWithoutExtension(options.DatasetPath);
        return string.IsNullOrWhiteSpace(stem) ? BenchmarkCliConstants.DefaultSuiteName : stem.Trim();
    }

    public static string ResolveEndpoint(LlmProviderType providerType, string? explicitEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(explicitEndpoint))
        {
            return explicitEndpoint.Trim();
        }

        return providerType == LlmProviderType.OpenAiCompatible
            ? BenchmarkCliConstants.DefaultOpenAiEndpoint
            : BenchmarkCliConstants.DefaultOllamaEndpoint;
    }

    public static string ResolveOutputDirectory(RunCommandOptions options, string suiteName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return Path.GetFullPath(options.OutputDirectory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(options.ResumeFromDir))
        {
            return Path.GetFullPath(options.ResumeFromDir.Trim());
        }

        var slug = BenchmarkWorkspaceManager.SanitizePathSegment(suiteName);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(
            Directory.GetCurrentDirectory(),
            BenchmarkCliConstants.DefaultResultsDirectoryName,
            $"{slug}-{stamp}");
    }

    public static string? ResolveApiKeyFromEnvironment() =>
        ResolveApiKeyFromEnvironment(Environment.GetEnvironmentVariable);

    internal static string? ResolveApiKeyFromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var benchKey = getEnvironmentVariable(BenchmarkCliConstants.BenchmarkApiKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(benchKey))
        {
            return benchKey.Trim();
        }

        var openAiKey = getEnvironmentVariable(BenchmarkCliConstants.OpenAiApiKeyEnvVar);
        return string.IsNullOrWhiteSpace(openAiKey) ? null : openAiKey.Trim();
    }

    private static BenchmarkJudgeConfig? BuildJudgeConfig(RunCommandOptions options, string mainEndpoint)
    {
        if (string.IsNullOrWhiteSpace(options.JudgeModel))
        {
            return null;
        }

        var judgeProvider = options.JudgeProvider ?? options.Provider;
        var judgeEndpoint = !string.IsNullOrWhiteSpace(options.JudgeEndpoint)
            ? options.JudgeEndpoint.Trim()
            : (judgeProvider == options.Provider ? mainEndpoint : ResolveEndpoint(judgeProvider, null));

        return new BenchmarkJudgeConfig
        {
            ModelName = options.JudgeModel.Trim(),
            ProviderType = judgeProvider,
            Endpoint = judgeEndpoint,
            ApiKey = ResolveApiKeyFromEnvironment()
        };
    }
}
