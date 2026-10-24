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
    /// All priority levels from Critical (1) to Trivial (5).
    /// </summary>
    public static readonly int[] All = [Critical, High, Normal, Low, Trivial];

    /// <summary>
    /// Returns the descriptive meaning and scheduling rule for a priority level.
    /// </summary>
    public static string GetDescription(int priority) => priority switch
    {
        Critical => "Immediate blocker or critical bug fix. Dispatched to llm-urgent queue.",
        High => "Core architectural prerequisite or time-sensitive feature milestone.",
        Normal => "Standard implementation task. Default for newly authored tasks.",
        Low => "Minor refactoring, cosmetic polish, or secondary documentation.",
        Trivial => "Non-essential cleanup, minor typos, or background experiments.",
        _ => $"Priority {priority}"
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

    /// <summary>
    /// Attempts to parse a priority value from an object (e.g. int, "P1"-"P5", "Priority 1", "Critical", "Urgent", "Critical / Urgent", "High", "Normal", "Low", "Trivial").
    /// </summary>
    public static bool TryParse(object? value, out int priority)
    {
        if (value == null)
        {
            priority = default;
            return false;
        }

        if (value is int intVal)
        {
            if (intVal >= Critical && intVal <= Trivial)
            {
                priority = intVal;
                return true;
            }
            priority = default;
            return false;
        }

        if (value is System.Text.Json.JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number && jsonElement.TryGetInt32(out var jInt))
            {
                if (jInt >= Critical && jInt <= Trivial)
                {
                    priority = jInt;
                    return true;
                }
                priority = default;
                return false;
            }

            if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return TryParse(jsonElement.GetString(), out priority);
            }

            priority = default;
            return false;
        }

        var str = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(str))
        {
            priority = default;
            return false;
        }

        if (int.TryParse(str, out var parsedInt))
        {
            if (parsedInt >= Critical && parsedInt <= Trivial)
            {
                priority = parsedInt;
                return true;
            }
            priority = default;
            return false;
        }

        if (str.StartsWith("Priority", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = str["Priority".Length..].Trim();
            if (!string.IsNullOrEmpty(remainder))
            {
                return TryParse(remainder, out priority);
            }
        }

        if (str.StartsWith("P", StringComparison.OrdinalIgnoreCase) && int.TryParse(str[1..].Trim(), out var pCode))
        {
            if (pCode >= Critical && pCode <= Trivial)
            {
                priority = pCode;
                return true;
            }
            priority = default;
            return false;
        }

        if (string.Equals(str, "Critical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Urgent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Critical / Urgent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Critical/Urgent", StringComparison.OrdinalIgnoreCase))
        {
            priority = Critical;
            return true;
        }

        if (string.Equals(str, "High", StringComparison.OrdinalIgnoreCase))
        {
            priority = High;
            return true;
        }

        if (string.Equals(str, "Normal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Medium", StringComparison.OrdinalIgnoreCase))
        {
            priority = Normal;
            return true;
        }

        if (string.Equals(str, "Low", StringComparison.OrdinalIgnoreCase))
        {
            priority = Low;
            return true;
        }

        if (string.Equals(str, "Lowest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Trivial", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Lowest / Trivial", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(str, "Lowest/Trivial", StringComparison.OrdinalIgnoreCase))
        {
            priority = Trivial;
            return true;
        }

        priority = default;
        return false;
    }

    /// <summary>
    /// Parses a priority from an object value, falling back to a default priority if unrecognized or null.
    /// </summary>
    public static int ParseOrDefault(object? value, int defaultPriority = Normal)
    {
        return TryParse(value, out var parsed) ? parsed : defaultPriority;
    }
}
