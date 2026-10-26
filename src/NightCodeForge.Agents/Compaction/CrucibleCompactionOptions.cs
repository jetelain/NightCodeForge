namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// Configuration options governing Pi-style context compaction in Crucible harnesses.
/// Budget allocations ensure the model always has adequate generation and reasoning headroom
/// without dropping the critical initial task anchor.
/// </summary>
public sealed class CrucibleCompactionOptions
{
    /// <summary>Default token headroom reserved for model reasoning and generation.</summary>
    public const int DefaultReserveTokens = 8192;

    /// <summary>Default recent context budget preserved verbatim without summarization.</summary>
    public const int DefaultKeepRecentTokens = 16384;

    /// <summary>Maximum characters retained per tool result during summarization serialization.</summary>
    public const int DefaultToolResultMaxChars = 2000;

    /// <summary>Context windows at or below this size use proportional reserve/keep-recent budgets.</summary>
    public const int SmallContextWindowTokens = 32768;

    /// <summary>Minimum reserve/keep-recent budget used for small context windows.</summary>
    public const int MinSmallWindowBudgetTokens = 2048;

    /// <summary>Default combined safety margin accounting for summary budget (1,500) and headroom margin (2,500).</summary>
    public const int DefaultSafetyMarginTokens = 4000;

    /// <summary>Default floor for preserving recent turns when context budget permits.</summary>
    public const int DefaultMinRecentTokens = 4096;

    /// <summary>Default minimum turns that must elapse between successive compaction checkpoints.</summary>
    public const int DefaultDebounceTurnCooldown = 3;

    /// <summary>Default minimum newly accumulated tokens required before re-triggering compaction.</summary>
    public const int DefaultDebounceTokenDelta = 2500;

    /// <summary>Context window utilization ratio triggering emergency compaction regardless of cooldown.</summary>
    public const double EmergencyCompactionThresholdRatio = 0.95;

    /// <summary>Maximum character length for individual tool results before emergency clamping triggers.</summary>
    public const int DefaultEmergencyToolResultMaxChars = 8000;

    /// <summary>
    /// Whether automatic context compaction is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Total context window in tokens (e.g. 131072 for 128k baseline, 225280 for full production models).
    /// In a multi-node fleet or reverse-proxy cluster, this should be based on the largest capability of the fleet
    /// (the highest context ceiling across capable targets or cloud overflow) to avoid premature compaction
    /// when high-VRAM nodes can service the extended context.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 131072;

    /// <summary>
    /// Token headroom reserved for model generation and reasoning.
    /// Compaction triggers when: contextTokens &gt; ContextWindowTokens - ReserveTokens.
    /// </summary>
    public int ReserveTokens { get; set; } = DefaultReserveTokens;

    /// <summary>
    /// Budget of recent tokens to keep untouched in full conversational fidelity.
    /// </summary>
    public int KeepRecentTokens { get; set; } = DefaultKeepRecentTokens;

    /// <summary>
    /// Combined safety margin accounting for checkpoint summary budget and generation headroom.
    /// </summary>
    public int SafetyMarginTokens { get; set; } = DefaultSafetyMarginTokens;

    /// <summary>
    /// Minimum floor for preserving recent turns when context budget permits.
    /// </summary>
    public int MinRecentTokens { get; set; } = DefaultMinRecentTokens;

    /// <summary>
    /// Minimum assistant turns that must elapse before allowing another compaction pass.
    /// </summary>
    public int DebounceTurnCooldown { get; set; } = DefaultDebounceTurnCooldown;

    /// <summary>
    /// Minimum newly accumulated tokens required since last compaction before re-compaction is permitted.
    /// </summary>
    public int DebounceTokenDelta { get; set; } = DefaultDebounceTokenDelta;

    /// <summary>
    /// Emergency threshold ratio of ContextWindowTokens that bypasses debounce cooldown.
    /// </summary>
    public double EmergencyThresholdRatio { get; set; } = EmergencyCompactionThresholdRatio;

    /// <summary>
    /// Maximum characters allowed per tool result before emergency clamping prior to summarization.
    /// </summary>
    public int EmergencyToolResultMaxChars { get; set; } = DefaultEmergencyToolResultMaxChars;

    /// <summary>
    /// Maximum character length per tool result serialized for summarization.
    /// Content exceeding this limit is safely truncated with a marker.
    /// </summary>
    public int ToolResultMaxChars { get; set; } = DefaultToolResultMaxChars;

    /// <summary>
    /// Identifies how the active context window size was resolved (e.g., "AgentProfile", "LlmProviderOptions (appsettings.json)").
    /// </summary>
    public string? ResolutionSource { get; set; }

    /// <summary>
    /// Model used for LLM summarization. When null, the summarizer client default model is used.
    /// Must match the model the agent runs on when a per-profile override is in effect.
    /// </summary>
    public string? SummarizerModelId { get; set; }

    /// <summary>
    /// Calculates the trigger threshold above which compaction must execute.
    /// </summary>
    public int TriggerThresholdTokens => Math.Max(1, ContextWindowTokens - ReserveTokens);
}
