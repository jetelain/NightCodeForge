using System.Text.Json.Serialization;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Benchmark.Cli.Models;

/// <summary>
/// Structured defect or specification finding anchor used in Code Review and ForgeTask Review benchmarks.
/// </summary>
public sealed record BenchmarkExpectedFinding(
    string? FilePath = null,
    string? SymbolOrLineHint = null,
    string? Severity = null,
    string? Summary = null,
    IReadOnlyList<string>? Keywords = null)
{
    public IReadOnlyList<string> Keywords { get; init; } = Keywords ?? Array.Empty<string>();
}

/// <summary>
/// Provenance metadata captured on every benchmark run so cross-commit and cross-branch regression comparisons remain reproducible.
/// </summary>
public sealed record BenchmarkRunProvenance(
    string NcfVersion,
    string GitCommit,
    string GitBranch,
    DateTimeOffset RunTimestampUtc,
    LlmProviderType ProviderType,
    string Endpoint,
    bool EnableStackSkills,
    LocalOptimizationFlags? OptimizationFlags,
    IReadOnlyDictionary<string, string>? DetectedToolVersions = null)
{
    public IReadOnlyDictionary<string, string> DetectedToolVersions { get; init; } =
        DetectedToolVersions ?? new Dictionary<string, string>();
}

/// <summary>
/// Canonical benchmark instance loaded from a dataset file across Implementation, CodeReview, or TaskReview categories.
/// </summary>
public sealed record BenchmarkInstance
{
    public required string InstanceId { get; init; }
    public BenchmarkProblemCategory? Category { get; init; }
    public string Language { get; init; } = BenchmarkLanguages.CSharp;
    public required string Repo { get; init; }
    public required string BaseCommit { get; init; }
    public required string ProblemStatement { get; init; }
    public string? HintsText { get; init; }
    public string? CustomContainerImage { get; init; }
    public string? CustomTestCommand { get; init; }
    public TestResultFormat? TestResultFormat { get; init; }

    // Implementation fields
    public string? GoldPatch { get; init; }
    public string? TestPatch { get; init; }
    public IReadOnlyList<string> FailToPass { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PassToPass { get; init; } = Array.Empty<string>();

    // Code Review fields
    public string? CandidatePatch { get; init; }

    // Shared Code Review & Task Review fields
    public string? TaskSpecification { get; init; }
    public string? ExpectedVerdict { get; init; }
    public string? ExpectedDifficulty { get; init; }
    public IReadOnlyList<BenchmarkExpectedFinding> ExpectedFindings { get; init; } = Array.Empty<BenchmarkExpectedFinding>();

    public BenchmarkProblemCategory ResolveEffectiveCategory(BenchmarkProblemCategory? categoryOverride) =>
        categoryOverride ?? Category ?? BenchmarkProblemCategory.Implementation;

    public TestResultFormat ResolveEffectiveTestResultFormat() =>
        TestResultFormat ?? BenchmarkLanguages.GetDefaultTestResultFormat(Language);
}

/// <summary>
/// Model candidate and inference configuration evaluated across benchmark instances.
/// </summary>
public sealed record BenchmarkModelCandidate
{
    public required string CandidateId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required string ModelName { get; init; }
    public ThinkingEffort ThinkingEffort { get; init; } = ThinkingEffort.Medium;
    public int? ThinkingTokenBudget { get; init; }
    public int? ContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public double? Temperature { get; init; }
    public int MaxTurns { get; init; } = BenchmarkDefaults.DefaultMaxTurns;
    public int InstanceTimeoutSeconds { get; init; } = BenchmarkDefaults.DefaultInstanceTimeoutSeconds;
    public LocalOptimizationFlags? OptimizationFlags { get; init; }
    public bool EnableStackSkills { get; init; } = true;
    public OllamaRoutingTarget RoutingTarget { get; init; } = OllamaRoutingTarget.Auto;

    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? ModelName : DisplayName;
}

/// <summary>
/// Optional LLM-as-judge configuration for qualitative scoring of Code Review and ForgeTask Review outputs.
/// </summary>
public sealed record BenchmarkJudgeConfig
{
    public required string ModelName { get; init; }
    public LlmProviderType ProviderType { get; init; } = LlmProviderType.Ollama;
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public double Temperature { get; init; } = BenchmarkDefaults.DefaultJudgeTemperature;
    public int TimeoutSeconds { get; init; } = BenchmarkDefaults.DefaultJudgeTimeoutSeconds;
}

/// <summary>
/// Request specifying the dataset, model candidates, execution options, and verification policies for a benchmark run.
/// </summary>
public sealed record BenchmarkRunRequest
{
    public required string SuiteName { get; init; }
    public required string DatasetPath { get; init; }
    public required string OutputDirectory { get; init; }
    public BenchmarkProblemCategory? CategoryOverride { get; init; }
    public IReadOnlyList<string>? InstanceIdFilter { get; init; }
    public int? MaxInstances { get; init; }
    public IReadOnlyList<BenchmarkModelCandidate> Candidates { get; init; } = Array.Empty<BenchmarkModelCandidate>();
    public bool IncludeHints { get; init; }
    public bool VerifyWithTests { get; init; } = true;
    public ExecutionMode ExecutionMode { get; init; } = ExecutionMode.ContainerSandbox;
    public int Concurrency { get; init; } = 1;
    public BenchmarkJudgeConfig? JudgeConfig { get; init; }
    public string? ResumeFromDir { get; init; }
    public LlmProviderType ProviderType { get; init; } = LlmProviderType.Ollama;
    public string Endpoint { get; init; } = string.Empty;
    public Action<string>? ProgressCallback { get; init; }
}

/// <summary>
/// Per-instance tool execution, repair, and companion tool telemetry captured directly from tool dispatch and bash audits.
/// </summary>
public sealed record ToolCallTelemetryBreakdown
{
    public int TotalToolCalls { get; init; }
    public int ReadCalls { get; init; }
    public int EditCalls { get; init; }
    public int WriteCalls { get; init; }
    public int BashCalls { get; init; }
    public int LoadSkillCalls { get; init; }
    public int AskCalls { get; init; }
    public int RequestChangesCalls { get; init; }
    public int EditFailures { get; init; }
    public int DispatcherRepairs { get; init; }
    public int ToolAliasesResolved { get; init; }
    public int ParameterAliasesResolved { get; init; }
    public int NamespacesStripped { get; init; }
    public int CasingNormalized { get; init; }
    public int EnumValuesSnapped { get; init; }
    public int StringifiedJsonRepaired { get; init; }
    public int OtherDispatcherRepairs { get; init; }
    public int CsNavBashCalls { get; init; }
    public int CodeCondenserBashCalls { get; init; }

    [JsonIgnore]
    public double EditFailureRate => EditCalls > 0 ? (double)EditFailures / EditCalls : 0.0;
}

/// <summary>
/// Qualitative score and rationale produced by <see cref="IBenchmarkLlmJudge"/>.
/// </summary>
public sealed record BenchmarkJudgeResult(double Score, string Rationale);

/// <summary>
/// Verification outcome produced by <see cref="IBenchmarkVerifier"/>.
/// </summary>
public sealed record BenchmarkVerificationResult
{
    public bool Passed { get; init; }
    public string? ActualVerdict { get; init; }
    public double DeterministicScore { get; init; }
    public int FailToPassPassed { get; init; }
    public int FailToPassTotal { get; init; }
    public int PassToPassPassed { get; init; }
    public int PassToPassTotal { get; init; }
    public IReadOnlyList<string> FailedTests { get; init; } = Array.Empty<string>();
    public string? TaskDifficultyResult { get; init; }
    public double? JudgeScore { get; init; }
    public string? JudgeRationale { get; init; }
    public string? VerificationLog { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Detailed execution and verification result for one (candidate, instance) evaluation.
/// </summary>
public sealed record BenchmarkInstanceResult
{
    public required string InstanceId { get; init; }
    public required string CandidateId { get; init; }
    public required string ModelName { get; init; }
    public BenchmarkProblemCategory Category { get; init; }
    public string Language { get; init; } = BenchmarkLanguages.CSharp;
    public BenchmarkInstanceOutcome Outcome { get; init; }
    public long DurationMs { get; init; }
    public int Turns { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public long TotalTokens { get; init; }
    public double TokensPerSecond { get; init; }
    public string? ModelPatch { get; init; }
    public string? TranscriptPath { get; init; }
    public string? ActualVerdict { get; init; }
    public double DeterministicScore { get; init; }
    public double? JudgeScore { get; init; }
    public string? JudgeRationale { get; init; }
    public string? TaskDifficultyResult { get; init; }
    public int FailToPassPassed { get; init; }
    public int FailToPassTotal { get; init; }
    public int PassToPassPassed { get; init; }
    public int PassToPassTotal { get; init; }
    public ToolCallTelemetryBreakdown Telemetry { get; init; } = new();
    public string? ErrorMessage { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Per-language aggregated scorecard metrics for a candidate.
/// </summary>
public sealed record BenchmarkLanguageBreakdown
{
    public required string Language { get; init; }
    public int TotalInstances { get; init; }
    public int PassedInstances { get; init; }
    public double PassRate { get; init; }
    public double AverageDeterministicScore { get; init; }
    public double? AverageJudgeScore { get; init; }
    public double AverageDurationMs { get; init; }
}

/// <summary>
/// Aggregated scorecard metrics for one model candidate across all evaluated instances.
/// </summary>
public sealed record BenchmarkCandidateSummary
{
    public required string CandidateId { get; init; }
    public required string DisplayName { get; init; }
    public required string ModelName { get; init; }
    public int TotalInstances { get; init; }
    public int PassedInstances { get; init; }
    public int FailedInstances { get; init; }
    public int ErrorInstances { get; init; }
    public int TimeoutInstances { get; init; }
    public double PassRate { get; init; }
    public double PatchOrVerdictRate { get; init; }
    public double AverageDeterministicScore { get; init; }
    public double? AverageJudgeScore { get; init; }
    public double AverageDurationMs { get; init; }
    public double AverageTurns { get; init; }
    public double AverageTokens { get; init; }
    public double AverageTokensPerSecond { get; init; }
    public double EditFailureRate { get; init; }
    public int TotalCsNavBashCalls { get; init; }
    public int TotalCodeCondenserBashCalls { get; init; }
    public int TotalDispatcherRepairs { get; init; }
    public IReadOnlyList<BenchmarkLanguageBreakdown> ByLanguage { get; init; } = Array.Empty<BenchmarkLanguageBreakdown>();
}

/// <summary>
/// Complete persisted benchmark run report aggregating provenance, candidate summaries, and per-instance results.
/// </summary>
public sealed record BenchmarkRunReport
{
    public required string SuiteName { get; init; }
    public required string DatasetPath { get; init; }
    public required BenchmarkRunProvenance Provenance { get; init; }
    public IReadOnlyList<BenchmarkCandidateSummary> Summaries { get; init; } = Array.Empty<BenchmarkCandidateSummary>();
    public IReadOnlyList<BenchmarkInstanceResult> Results { get; init; } = Array.Empty<BenchmarkInstanceResult>();
}

/// <summary>
/// Ephemeral workspace handle created by <see cref="IBenchmarkWorkspaceManager"/> for a single benchmark evaluation.
/// </summary>
public sealed record BenchmarkWorkspaceHandle
{
    public required string InstanceId { get; init; }
    public BenchmarkProblemCategory Category { get; init; }
    public required string WorkspacePath { get; init; }
    public required string BaseCommit { get; init; }
    public string BaseBranch { get; init; } = BenchmarkBranchNames.BaseBranch;
    public string? FeatureBranch { get; init; }
    public string? CandidateCommit { get; init; }
    public string? TaskMarkdownPath { get; init; }
    public string? TaskMarkdownInitialContent { get; init; }
    public string? ReviewDiff { get; init; }
    public string? ReviewDiffStat { get; init; }
    public string? ContainerName { get; init; }
}

/// <summary>
/// Captured agent outputs passed into <see cref="IBenchmarkVerifier"/>.
/// </summary>
public sealed record BenchmarkAgentOutcome
{
    public AgentRunStatus RunStatus { get; init; }
    public string? ModelPatch { get; init; }
    public string? Summary { get; init; }
    public string? ReviewerFeedback { get; init; }
    public string? TargetRouting { get; init; }
    public IReadOnlyList<Question> PendingQuestions { get; init; } = Array.Empty<Question>();
    public string? UpdatedTaskMarkdown { get; init; }
    public string? TaskMarkdownBefore { get; init; }
    public IReadOnlyList<string> ModifiedFilesOutsideDocsTasks { get; init; } = Array.Empty<string>();
}
