namespace NightCodeForge.Core.Domain;

/// <summary>
/// Defines standard priority levels and formatting helpers for tasks and queue scheduling (1 = Highest, 5 = Lowest).
/// </summary>
public static class TaskPriority
{
    /// <summary>
    /// Critical / Urgent (Priority 1): Blockers, hotfixes, critical bugs. Dispatched to llm-urgent queue.
    /// </summary>
    public const int Critical = 1;

    /// <summary>
    /// High (Priority 2): Major milestones, core architectural prerequisites.
    /// </summary>
    public const int High = 2;

    /// <summary>
    /// Normal / Medium (Priority 3): Default feature development.
    /// </summary>
    public const int Normal = 3;

    /// <summary>
    /// Low (Priority 4): Minor enhancements, non-critical improvements.
    /// </summary>
    public const int Low = 4;

    /// <summary>
    /// Lowest / Trivial (Priority 5): Cleanup, typos, cosmetic polishes.
    /// </summary>
    public const int Trivial = 5;

    /// <summary>
    /// Human-readable label for a priority level.
    /// </summary>
    public static string GetLabel(int priority) => priority switch
    {
        Critical => "Critical / Urgent",
        High => "High",
        Normal => "Normal",
        Low => "Low",
        Trivial => "Lowest / Trivial",
        _ => $"Priority {priority}"
    };

    /// <summary>
    /// Short badge code (P1-P5).
    /// </summary>
    public static string GetShortLabel(int priority) => priority switch
    {
        Critical => "P1",
        High => "P2",
        Normal => "P3",
        Low => "P4",
        Trivial => "P5",
        _ => $"P{priority}"
    };

    /// <summary>
    /// Standard theme hex color for priority badge borders and highlights.
    /// </summary>
    public static string GetBadgeColor(int priority) => priority switch
    {
        Critical => "#ef4444",
        High => "#f97316",
        Normal => "#3b82f6",
        Low => "#64748b",
        Trivial => "#475569",
        _ => "#64748b"
    };
}
