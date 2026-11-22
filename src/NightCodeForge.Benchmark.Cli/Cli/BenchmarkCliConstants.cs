using NightCodeForge.Core.Security;

namespace NightCodeForge.Benchmark.Cli.Cli;

/// <summary>
/// Shared CLI command names, option keys, provider aliases, environment variable names, exit codes, and defaults.
/// </summary>
public static class BenchmarkCliConstants
{
    public const string ToolName = "ncf-bench";
    public const string ToolDisplayName = "NightCodeForge Benchmark CLI";
    public const string DefaultVersion = "1.0.0";
    public const string DefaultSuiteName = "benchmark-suite";
    public const string DefaultCandidateSlug = "candidate";
    public const string UnknownProvenanceValue = "unknown";

    public const int ExitSuccess = 0;
    public const int ExitFailure = 1;
    public const int ExitUsageError = 2;

    public const string CommandRun = "run";
    public const string CommandReport = "report";
    public const string CommandDataset = "dataset";
    public const string CommandHelp = "help";
    public const string CommandVersion = "version";

    public const string SubCommandImport = "import";
    public const string SubCommandValidate = "validate";

    public const string FlagHelp = "--help";
    public const string FlagHelpShort = "-h";
    public const string FlagVersion = "--version";
    public const string FlagVersionShort = "-v";

    public const string OptionDataset = "--dataset";
    public const string OptionSuite = "--suite";
    public const string OptionModels = "--models";
    public const string OptionConfig = "--config";
    public const string OptionProvider = "--provider";
    public const string OptionEndpoint = "--endpoint";
    public const string OptionCategory = "--category";
    public const string OptionFilter = "--filter";
    public const string OptionLimit = "--limit";
    public const string OptionVerify = "--verify";
    public const string OptionNoVerify = "--no-verify";
    public const string OptionSkills = "--skills";
    public const string OptionNoSkills = "--no-skills";
    public const string OptionHints = "--hints";
    public const string OptionNoHints = "--no-hints";
    public const string OptionTimeout = "--timeout";
    public const string OptionConcurrency = "--concurrency";
    public const string OptionResume = "--resume";
    public const string OptionJudgeModel = "--judge-model";
    public const string OptionJudgeProvider = "--judge-provider";
    public const string OptionJudgeEndpoint = "--judge-endpoint";
    public const string OptionOut = "--out";
    public const string OptionBaseline = "--baseline";
    public const string OptionFormat = "--format";
    public const string OptionSource = "--source";
    public const string OptionInput = "--input";
    public const string OptionManifest = "--manifest";

    public const string BenchmarkApiKeyEnvVar = "NCF_BENCHMARK_API_KEY";
    public const string OpenAiApiKeyEnvVar = ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable;

    public const string DefaultOllamaEndpoint = "http://localhost:11434";
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";
    public const string DefaultResultsDirectoryName = "benchmark-results";

    public const string ProviderOllama = "ollama";
    public const string ProviderOllamaRouter = "ollama-router";
    public const string ProviderOpenAi = "openai";
    public const string ProviderOpenAiCompatible = "openai-compatible";

    public const string CategoryImplementation = "implementation";
    public const string CategoryCodeReview = "code-review";
    public const string CategoryTaskReview = "task-review";

    public const string FormatMarkdown = "markdown";
    public const string FormatAscii = "ascii";
    public const string FormatJson = "json";
}
