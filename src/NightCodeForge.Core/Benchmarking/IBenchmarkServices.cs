using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Benchmarking;

/// <summary>
/// Parses and validates benchmark datasets across Implementation, Code Review, and ForgeTask Review formats.
/// </summary>
public interface IBenchmarkDatasetLoader
{
    Task<IReadOnlyList<BenchmarkInstance>> LoadAsync(
        string datasetPath,
        BenchmarkProblemCategory? categoryOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provisions leak-free ephemeral git workspaces from a local repository cache and handles patch extraction/application.
/// </summary>
public interface IBenchmarkWorkspaceManager
{
    Task<string> EnsureRepositoryCachedAsync(
        string repo,
        string baseCommit,
        CancellationToken cancellationToken = default);

    Task<BenchmarkWorkspaceHandle> ProvisionAsync(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        string? instanceWorkspacePath = null,
        CancellationToken cancellationToken = default);

    Task<string> ExtractPatchAsync(
        string workspacePath,
        string baseCommitOrRef,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetModifiedFilesAsync(
        string workspacePath,
        string? baseCommitOrRef = null,
        CancellationToken cancellationToken = default);

    Task ResetToBaseAsync(
        string workspacePath,
        string baseCommitOrRef,
        CancellationToken cancellationToken = default);

    Task ApplyPatchAsync(
        string workspacePath,
        string patchContent,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(
        BenchmarkWorkspaceHandle handle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Verifies agent benchmark outcomes deterministically (and via optional LLM-as-judge for review categories).
/// </summary>
public interface IBenchmarkVerifier
{
    Task<BenchmarkVerificationResult> VerifyAsync(
        BenchmarkInstance instance,
        BenchmarkWorkspaceHandle workspace,
        BenchmarkAgentOutcome outcome,
        ExecutionMode executionMode,
        bool verifyWithTests,
        BenchmarkJudgeConfig? judgeConfig,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional qualitative LLM-as-judge evaluator for Code Review and ForgeTask Review benchmark categories.
/// </summary>
public interface IBenchmarkLlmJudge
{
    Task<BenchmarkJudgeResult> EvaluateCodeReviewAsync(
        BenchmarkInstance instance,
        BenchmarkAgentOutcome outcome,
        BenchmarkJudgeConfig judgeConfig,
        CancellationToken cancellationToken = default);

    Task<BenchmarkJudgeResult> EvaluateTaskReviewAsync(
        BenchmarkInstance instance,
        BenchmarkAgentOutcome outcome,
        BenchmarkJudgeConfig judgeConfig,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Collects provenance metadata (NCF version, git commit/branch, installed companion tool versions).
/// </summary>
public interface IBenchmarkProvenanceProvider
{
    Task<BenchmarkRunProvenance> CaptureAsync(
        LlmProviderType providerType,
        string endpoint,
        bool enableStackSkills,
        LocalOptimizationFlags? optimizationFlags,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Standalone stateless Crucible benchmark runner sweeping model candidates over dataset instances.
/// </summary>
public interface ICrucibleBenchmarkRunner
{
    Task<BenchmarkRunReport> RunAsync(
        BenchmarkRunRequest request,
        CancellationToken cancellationToken = default);
}
