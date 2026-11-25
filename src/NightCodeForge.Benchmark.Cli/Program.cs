using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Commands;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Hosting;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return await ExecuteAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    internal static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0)
        {
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitSuccess;
        }

        var firstArg = args[0].Trim();

        if (IsTopLevelHelpToken(firstArg))
        {
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitSuccess;
        }

        if (IsTopLevelVersionToken(firstArg))
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? BenchmarkCliConstants.DefaultVersion;
            stdout.WriteLine($"{BenchmarkCliConstants.ToolName} version {version} ({BenchmarkCliConstants.ToolDisplayName})");
            return BenchmarkCliConstants.ExitSuccess;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (firstArg.Equals(BenchmarkCliConstants.CommandRun, StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteRunCommandAsync(args.Skip(1).ToArray(), stdout, stderr, cancellationToken).ConfigureAwait(false);
            }

            if (firstArg.Equals(BenchmarkCliConstants.CommandReport, StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteReportCommandAsync(args.Skip(1).ToArray(), stdout, stderr, cancellationToken).ConfigureAwait(false);
            }

            if (firstArg.Equals(BenchmarkCliConstants.CommandDataset, StringComparison.OrdinalIgnoreCase))
            {
                return await ExecuteDatasetCommandAsync(args.Skip(1).ToArray(), stdout, stderr, cancellationToken).ConfigureAwait(false);
            }

            stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Unknown command: '{firstArg}'");
            stderr.WriteLine();
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitUsageError;
        }
        catch (BenchmarkException ex)
        {
            stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Benchmark error: {ex.Message}");
            return BenchmarkCliConstants.ExitFailure;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Cancelled.");
            return BenchmarkCliConstants.ExitFailure;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Fatal error: {ex.Message}");
            stderr.WriteLine(ex.StackTrace);
            return BenchmarkCliConstants.ExitFailure;
        }
    }

    private static async Task<int> ExecuteRunCommandAsync(
        string[] remainingArgs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (remainingArgs.Any(IsHelpFlag))
        {
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitSuccess;
        }

        var options = BenchmarkCliArgumentParser.ParseRunOptions(remainingArgs, out var parseError);
        if (options == null)
        {
            return PrintUsageError(stdout, stderr, parseError!);
        }

        var candidates = BenchmarkCandidateConfigLoader.LoadCandidates(options);
        var endpoint = RunBenchmarkCommand.ResolveEndpoint(options.Provider, options.Endpoint);
        var apiKey = RunBenchmarkCommand.ResolveApiKeyFromEnvironment();

        var hostConfig = new BenchmarkCliHostConfig(
            ProviderType: options.Provider,
            Endpoint: endpoint,
            ApiKey: apiKey,
            AllowedOpenAiModels: candidates.Select(c => c.ModelName).ToList(),
            BenchmarkRootDirectory: null);

        var services = new ServiceCollection();
        services.AddNightCodeForgeBenchmarkCliServices(hostConfig);

        await using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ICrucibleBenchmarkRunner>();
        var command = new RunBenchmarkCommand(runner);

        return await command.ExecuteAsync(options, candidates, stdout.WriteLine, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteReportCommandAsync(
        string[] remainingArgs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (remainingArgs.Any(IsHelpFlag))
        {
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitSuccess;
        }

        var options = BenchmarkCliArgumentParser.ParseReportOptions(remainingArgs, out var parseError);
        if (options == null)
        {
            return PrintUsageError(stdout, stderr, parseError!);
        }

        var command = new ReportBenchmarkCommand();
        return await command.ExecuteAsync(options, stdout.WriteLine, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteDatasetCommandAsync(
        string[] remainingArgs,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (remainingArgs.Length == 0 ||
            IsTopLevelHelpToken(remainingArgs[0].Trim()) ||
            remainingArgs.Any(IsHelpFlag))
        {
            PrintUsage(stdout);
            return BenchmarkCliConstants.ExitSuccess;
        }

        var subCommand = remainingArgs[0].Trim();
        var subArgs = remainingArgs.Skip(1).ToArray();

        if (subCommand.Equals(BenchmarkCliConstants.SubCommandImport, StringComparison.OrdinalIgnoreCase))
        {
            var options = BenchmarkCliArgumentParser.ParseDatasetImportOptions(subArgs, out var parseError);
            if (options == null)
            {
                return PrintUsageError(stdout, stderr, parseError!);
            }

            var services = new ServiceCollection();
            services.AddNightCodeForgeBenchmarkDatasetImporters();

            await using var serviceProvider = services.BuildServiceProvider();
            var registry = serviceProvider.GetRequiredService<IBenchmarkDatasetImporterRegistry>();
            var command = new DatasetImportCommand(registry);
            return await command.ExecuteAsync(options, stdout.WriteLine, cancellationToken).ConfigureAwait(false);
        }

        if (subCommand.Equals(BenchmarkCliConstants.SubCommandValidate, StringComparison.OrdinalIgnoreCase))
        {
            var options = BenchmarkCliArgumentParser.ParseDatasetValidateOptions(subArgs, out var parseError);
            if (options == null)
            {
                return PrintUsageError(stdout, stderr, parseError!);
            }

            var hostConfig = new BenchmarkCliHostConfig(
                ProviderType: LlmProviderType.Ollama,
                Endpoint: BenchmarkCliConstants.DefaultOllamaEndpoint,
                ApiKey: null,
                AllowedOpenAiModels: Array.Empty<string>(),
                BenchmarkRootDirectory: null);

            var services = new ServiceCollection();
            services.AddNightCodeForgeBenchmarkCliServices(hostConfig);

            await using var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var validator = scope.ServiceProvider.GetRequiredService<IBenchmarkDatasetValidator>();
            var command = new DatasetValidateCommand(validator);
            return await command.ExecuteAsync(options, stdout.WriteLine, cancellationToken).ConfigureAwait(false);
        }

        stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Unknown dataset subcommand: '{subCommand}'");
        stderr.WriteLine();
        PrintUsage(stdout);
        return BenchmarkCliConstants.ExitUsageError;
    }

    private static int PrintUsageError(TextWriter stdout, TextWriter stderr, string parseError)
    {
        stderr.WriteLine($"[{BenchmarkCliConstants.ToolName}] Error: {parseError}");
        stderr.WriteLine();
        PrintUsage(stdout);
        return BenchmarkCliConstants.ExitUsageError;
    }

    private static bool IsTopLevelHelpToken(string token) =>
        token.Equals(BenchmarkCliConstants.FlagHelp, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(BenchmarkCliConstants.FlagHelpShort, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(BenchmarkCliConstants.CommandHelp, StringComparison.OrdinalIgnoreCase);

    private static bool IsTopLevelVersionToken(string token) =>
        token.Equals(BenchmarkCliConstants.FlagVersion, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(BenchmarkCliConstants.FlagVersionShort, StringComparison.OrdinalIgnoreCase) ||
        token.Equals(BenchmarkCliConstants.CommandVersion, StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpFlag(string rawArg)
    {
        var trimmed = rawArg.Trim();
        return trimmed.Equals(BenchmarkCliConstants.FlagHelp, StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals(BenchmarkCliConstants.FlagHelpShort, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine(@"NightCodeForge Standalone Benchmark CLI (ncf-bench)

USAGE:
  ncf-bench run --dataset <path.jsonl|json> (--models <m1,m2> | --config <candidates.json>) [--manifest <manifest.json>] [options]
  ncf-bench report <resultsDir1> [<resultsDir2> ...] [--baseline <resultsDir>] [--format markdown|ascii|json]
  ncf-bench dataset import --source <swe-sharp-bench|swebench-multilingual|multi-swe-bench|swebench-verified> --input <path> --manifest <manifest.json> --out <path.jsonl>
  ncf-bench dataset validate --dataset <path.jsonl> [--manifest <manifest.json>] [--category <implementation|code-review|task-review>] [--filter <id1,id2>] [--limit <N>] [--verify|--no-verify]
  ncf-bench --version
  ncf-bench --help

RUN OPTIONS:
  --dataset <path>              Path to benchmark dataset (.jsonl or .json)
  --manifest <manifest.json>    Optional pinned manifest for metadata & crafted review overlay (auto-discovered if omitted)
  --models <m1,m2>              Comma-separated model names to evaluate
  --config <candidates.json>    JSON manifest of candidate model configurations
  --suite <name>                Optional suite name (default: dataset file stem)
  --provider <type>             ollama | ollama-router | openai (default: ollama)
  --endpoint <url>              Inference endpoint URL
  --category <name>             implementation | code-review | task-review
  --filter <id1,id2>            Comma-separated instance IDs to include
  --limit <N>                   Maximum number of dataset instances to run
  --verify / --no-verify        Enable or disable containerized verification (default: --verify)
  --skills / --no-skills        Enable or disable stack skills injection (default: --skills)
  --hints / --no-hints          Include dataset hints_text in prompt (default: --no-hints)
  --timeout <seconds>           Override per-instance timeout in seconds
  --concurrency <N>             Parallel evaluation workers (default: 1)
  --resume <resultsDir>         Skip (candidate, instance) pairs already completed in results.json
  --judge-model <model>         Optional LLM-as-judge model for review categories
  --judge-provider <type>       Judge provider override (ollama | ollama-router | openai)
  --judge-endpoint <url>        Judge endpoint URL override
  --out <dir>                   Output directory for results.json, summary.md, transcripts/, predictions.jsonl

REPORT OPTIONS:
  <resultsDir...>               One or more benchmark result directories (containing results.json)
  --baseline <resultsDir>       Baseline run directory for regression deltas (defaults to first directory in multi-run)
  --format <fmt>                markdown | ascii | json (default: markdown)

DATASET OPTIONS:
  import:
    --source <name>             swe-sharp-bench | swebench-multilingual | multi-swe-bench | swebench-verified
    --input <path>              Raw external dataset file (.jsonl or .json)
    --manifest <manifest.json>  Pinned selection manifest JSON file
    --out <path.jsonl>          Destination normalized BenchmarkInstance JSONL file
  validate:
    --dataset <path>            Path to benchmark dataset (.jsonl or .json)
    --manifest <manifest.json>  Optional pinned manifest for metadata & crafted review overlay
    --category <name>           Optional category override (implementation | code-review | task-review)
    --filter <id1,id2>          Comma-separated instance IDs to validate
    --limit <N>                 Maximum number of dataset instances to validate
    --verify / --no-verify      Run containerized test verification vs patch/frontmatter-only check (default: --verify)");
    }
}
