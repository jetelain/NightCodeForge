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

    /// <summary>
    /// Compact older tool results in multi-turn conversation memory, replacing bulky historical outputs
    /// outside the active window with concise summary stubs to minimize quadratic context inflation.
    /// Default: true. Opt-out retains verbatim tool outputs across all prior turns.
    /// </summary>
    public bool CompactOlderToolResults { get; set; } = true;

    /// <summary>
    /// Alias for <see cref="CompactOlderToolResults"/> to align with ToolResultCompaction terminology.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ToolResultCompaction
    {
        get => CompactOlderToolResults;
        set => CompactOlderToolResults = value;
    }

    /// <summary>
    /// Number of recent assistant turns where &lt;think&gt;...&lt;/think&gt; traces are kept verbatim before older turns are compacted.
    /// Default: 100.
    /// </summary>
    public int ThinkingKeepVerbatimCount { get; set; } = 100;

    /// <summary>
    /// Number of recent tool results kept verbatim before older outputs are eligible for truncation/compaction.
    /// Default: 100.
    /// </summary>
    public int ToolKeepVerbatimCount { get; set; } = 100;

    /// <summary>
    /// Character threshold length above which older tool results are eligible for truncation/compaction.
    /// Default: 2000 characters.
    /// </summary>
    public int ToolMaxResultLength { get; set; } = 2000;

    /// <summary>
    /// Number of characters to retain in the summary prefix when truncating bulky tool results.
    /// Default: 1750 characters.
    /// </summary>
    public int ToolTruncateToLength { get; set; } = 1750;

    /// <summary>
    /// Creates a deep memberwise clone of this configuration.
    /// </summary>
    public LocalOptimizationFlags Clone() => new()
    {
        ThinkingTraceCompaction = ThinkingTraceCompaction,
        LoopCircuitBreaker = LoopCircuitBreaker,
        GuidedGrammarDecoding = GuidedGrammarDecoding,
        DeterministicSymbolMap = DeterministicSymbolMap,
        StructuredDiagnosticExtraction = StructuredDiagnosticExtraction,
        PrefixStabilization = PrefixStabilization,
        JitModelPreload = JitModelPreload,
        CompactOlderToolResults = CompactOlderToolResults,
        ThinkingKeepVerbatimCount = ThinkingKeepVerbatimCount,
        ToolKeepVerbatimCount = ToolKeepVerbatimCount,
        ToolMaxResultLength = ToolMaxResultLength,
        ToolTruncateToLength = ToolTruncateToLength
    };
}

