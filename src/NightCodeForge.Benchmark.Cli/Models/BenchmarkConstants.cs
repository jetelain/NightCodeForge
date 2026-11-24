using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Models;

/// <summary>
/// Supported programming languages in benchmark datasets.
/// </summary>
public static class BenchmarkLanguages
{
    public const string CSharp = "csharp";
    public const string TypeScript = "typescript";
    public const string JavaScript = "javascript";
    public const string Python = "python";
    public const string Rust = "rust";

    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CSharp,
        TypeScript,
        JavaScript,
        Python,
        Rust
    };

    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return CSharp;
        }

        return language.Trim().ToLowerInvariant() switch
        {
            "c#" or "cs" or "dotnet" or ".net" => CSharp,
            "ts" => TypeScript,
            "js" or "node" => JavaScript,
            "py" => Python,
            "rs" => Rust,
            var other => other
        };
    }

    public static bool IsCSharp(string? language) =>
        string.Equals(Normalize(language), CSharp, StringComparison.OrdinalIgnoreCase);

    public static TestResultFormat GetDefaultTestResultFormat(string? language) => Normalize(language) switch
    {
        CSharp => TestResultFormat.Trx,
        Python or TypeScript or JavaScript => TestResultFormat.JUnitXml,
        Rust => TestResultFormat.CargoTest,
        _ => TestResultFormat.ExitCode
    };
}

/// <summary>
/// Ephemeral branch names created during leak-free benchmark workspace materialization.
/// </summary>
public static class BenchmarkBranchNames
{
    public const string BaseBranch = "ncf-bench-base";
    public const string CandidateBranch = "ncf-bench-candidate";
    public const string TaskReviewBranch = "ncf-bench-task-review";
}

/// <summary>
/// Skill identifiers referenced by benchmark skill gating policy.
/// </summary>
public static class BenchmarkSkillNames
{
    public const string CSharpNav = "csharp-nav";
    public const string CodeCondenser = "codecondenser";
    public const string MdFetch = "mdfetch";
}

/// <summary>
/// Directory, file, and container constants for benchmark cache, workspaces, and outputs.
/// </summary>
public static class BenchmarkPaths
{
    public const string DefaultRootSubdir = "benchmarks";
    public const string DatasetsSubdir = "datasets";
    public const string ReposCacheSubdir = "repos";
    public const string WorkspacesSubdir = "workspaces";
    public const string TranscriptsSubdir = "transcripts";
    public const string PredictionsFileName = "predictions.jsonl";
    public const string ResultsFileName = "results.json";
    public const string SummaryFileName = "summary.md";
    public const string DocsTasksPrefix = "docs/tasks/";
    public const string ContainerResultsDir = "/workspace/.ncf-bench-results";
    public const string TrxFileName = "results.trx";
    public const string JUnitFileName = "junit.xml";
    public const string BenchmarkResultDirRelative = ".ncf-bench-results";
    public const string GitEmail = "benchmark@nightcodeforge.local";
    public const string GitUserName = "NightCodeForge Benchmark";
}

/// <summary>
/// Shared JSON property names and default limits for benchmark execution and verification.
/// </summary>
public static class BenchmarkDefaults
{
    public const int DefaultMaxTurns = 100;
    public const int DefaultInstanceTimeoutSeconds = 600;
    public const int DefaultJudgeTimeoutSeconds = 120;
    public const int DefaultVerificationTimeoutSeconds = 600;
    public const double DefaultJudgeTemperature = 0.0;
    public const int MinimumEnrichedBodyDeltaChars = 40;
    public const int MinimumReadyBodyChars = 80;
    public const string DefaultTaskDifficulty = "standard";
    public const string DotNetRollForwardEnvVar = "DOTNET_ROLL_FORWARD";
    public const string DotNetRollForwardLatestMajor = "LatestMajor";
    public const string DotNetSdkRollForwardLatestMajor = "latestMajor";

    public static readonly IReadOnlyList<string> BlockedAntiCheatHosts =
    [
        "github.com",
        "www.github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "codeload.github.com",
        "objects.githubusercontent.com",
        "gist.github.com",
        "gitlab.com",
        "bitbucket.org",
        "huggingface.co",
        "datasets-server.huggingface.co"
    ];

    public static readonly IReadOnlyList<string> ImplementationPermittedTools =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Edit,
        CanonicalToolNames.Write,
        CanonicalToolNames.Bash
    ];

    public static readonly IReadOnlyList<string> CodeReviewPermittedTools =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Edit,
        CanonicalToolNames.Write,
        CanonicalToolNames.Bash,
        CanonicalToolNames.RequestChanges
    ];

    public static readonly IReadOnlyList<string> TaskReviewPermittedTools =
    [
        CanonicalToolNames.Read,
        CanonicalToolNames.Edit,
        CanonicalToolNames.Write,
        CanonicalToolNames.Bash,
        CanonicalToolNames.Ask
    ];

    public static readonly IReadOnlyList<string> ExcludedPatchPathSpecs =
    [
        ":(exclude)bin",
        ":(exclude)obj",
        ":(exclude)TestResults",
        ":(exclude)*.trx",
        ":(exclude)docs/transcripts",
        ":(exclude).ncf-bench-results",
        ":(exclude)node_modules",
        ":(exclude).venv",
        ":(exclude)__pycache__",
        ":(exclude).pytest_cache",
        ":(glob,exclude)**/bin/**",
        ":(glob,exclude)**/obj/**",
        ":(glob,exclude)**/TestResults/**",
        ":(glob,exclude)**/*.trx",
        ":(glob,exclude)**/node_modules/**",
        ":(glob,exclude)**/.venv/**",
        ":(glob,exclude)**/__pycache__/**",
        ":(glob,exclude)**/.pytest_cache/**"
    ];
}
