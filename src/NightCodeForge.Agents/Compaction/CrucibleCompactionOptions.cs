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

    /// <summary>
    /// Whether automatic context compaction is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Total context window in tokens (e.g. 32768 for small budgets, 225280 for full production models).
    /// </summary>
    public int ContextWindowTokens { get; set; } = 32768;

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
    /// Maximum character length per tool result serialized for summarization.
    /// Content exceeding this limit is safely truncated with a marker.
    /// </summary>
    public int ToolResultMaxChars { get; set; } = DefaultToolResultMaxChars;

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
