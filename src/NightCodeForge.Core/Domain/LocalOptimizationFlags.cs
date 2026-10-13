namespace NightCodeForge.Core.Domain;

/// <summary>
/// Strongly-typed feature flags controlling local LLM optimizations.
/// </summary>
public class LocalOptimizationFlags
{
    /// <summary>
    /// Strip or condense &lt;think&gt;...&lt;/think&gt; monologue blocks from historical assistant turns in conversation memory.
    /// Default: true. Opt-out retains verbatim thought traces across all multi-turn context windows.
    /// </summary>
    public bool ThinkingTraceCompaction { get; set; } = true;

    /// <summary>
    /// Monitor token stream for degenerate n-gram repetition loops and intercept duplicate tool invocations.
    /// Default: true. Opt-out allows unrestrained model execution without early abort.
    /// </summary>
    public bool LoopCircuitBreaker { get; set; } = true;

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
    /// Parse compiler errors (CS\d+) and test assertion failures into structured summaries instead of raw dumps.
    /// Default: true. Opt-out passes raw MSBuild and test runner stdout directly to the agent.
    /// </summary>
    public bool StructuredDiagnosticExtraction { get; set; } = true;

    /// <summary>
    /// Strictly enforce 4-layer byte-identical prompt prefixes to maximize KV cache hit rates.
    /// Default: true. Opt-out allows arbitrary dynamic metadata in top-level system prompts.
    /// </summary>
    public bool PrefixStabilization { get; set; } = true;

    /// <summary>
    /// Asynchronously pre-load next stage's model weights in OllamaRouter during CPU-bound deterministic stages.
    /// Default: true. Opt-out enforces strictly sequential stage-by-stage model loading.
    /// </summary>
    public bool JitModelPreload { get; set; } = true;
}
