namespace NightCodeForge.Core.Domain;

/// <summary>
/// Strongly-typed feature flags controlling local LLM optimizations.
/// </summary>
public class LocalOptimizationFlags
{
    /// <summary>
    /// Enforce native JSON schema or GBNF grammar constraints at the Ollama/vLLM sampler level.
    /// Default: true. Opt-out relies purely on unconstrained sampling and LenientToolDispatcher fallback.
    /// </summary>
    public bool GuidedGrammarDecoding { get; set; } = true;

    /// <summary>
    /// Deterministically pre-index workspace symbols (Roslyn/ctags) and inject matching symbols into Stage 1 prompt.
    /// Default: true. Opt-out forces agent to discover symbols via bash exploration (find/grep).
    /// </summary>
    public bool DeterministicSymbolMap { get; set; } = true;

    /// <summary>
    /// Apply smart CLI and build/test output filtering (path relativization, SDK boilerplate pruning,
    /// warning suppression on errors) while offloading raw logs to /tmp.
    /// Default: true. Opt-out passes raw CLI stdout/stderr directly (within inline caps).
    /// </summary>
    public bool SmartCliOutputFiltering { get; set; } = true;

    /// <summary>
    /// Asynchronously pre-load next stage's model weights in OllamaRouter during CPU-bound deterministic stages.
    /// Default: true. Opt-out enforces strictly sequential stage-by-stage model loading.
    /// </summary>
    public bool JitModelPreload { get; set; } = true;

    /// <summary>
    /// Pre-seed agent ChatHistory with synthetic bash tool call/result pairs from prior deterministic stages
    /// to prevent redundant exploratory build/test tool calls.
    /// Default: true.
    /// </summary>
    public bool SyntheticConversationPriming { get; set; } = true;

    /// <summary>
    /// Creates a deep memberwise clone of this configuration.
    /// </summary>
    public LocalOptimizationFlags Clone() => new()
    {
        GuidedGrammarDecoding = GuidedGrammarDecoding,
        DeterministicSymbolMap = DeterministicSymbolMap,
        SmartCliOutputFiltering = SmartCliOutputFiltering,
        JitModelPreload = JitModelPreload,
        SyntheticConversationPriming = SyntheticConversationPriming
    };
}

