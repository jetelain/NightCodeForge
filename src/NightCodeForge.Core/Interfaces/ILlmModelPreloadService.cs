namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Preload configuration options including context intent and target placement.
/// </summary>
public record ModelPreloadOptions(
    int? KeepAliveSeconds = null,
    int? ContextIntentTokens = null,
    string? Target = null,
    int? TaskId = null,
    string? StageId = null);

/// <summary>
/// Candidate target placement evaluation from OllamaRouter.
/// </summary>
public record ModelPreloadCandidate(
    string Target,
    bool Eligible,
    int InFlightRequests,
    bool IsResident,
    int ContextCeiling,
    bool ServerClass,
    bool Busy,
    string? SkipReason);

/// <summary>
/// Detailed result of a model preload invocation.
/// </summary>
public record ModelPreloadResult(
    bool Success,
    string Model,
    string? SelectedTarget = null,
    int? KeepAliveSeconds = null,
    int? NumCtx = null,
    int? ContextIntentTokens = null,
    bool? LoadAccepted = null,
    bool? ResidencyConfirmed = null,
    long? PreloadDurationMs = null,
    IReadOnlyList<ModelPreloadCandidate>? Candidates = null,
    string? Reason = null)
{
    public IReadOnlyList<ModelPreloadCandidate> Candidates { get; init; } = Candidates ?? Array.Empty<ModelPreloadCandidate>();
}

/// <summary>
/// Service for JIT model preload / warmup triggers to hide model swapping latency.
/// </summary>
public interface ILlmModelPreloadService
{
    Task<ModelPreloadResult> PreloadModelAsync(
        string modelName,
        ModelPreloadOptions? options = null,
        CancellationToken cancellationToken = default);
}
