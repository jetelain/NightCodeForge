using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation formatting helpers for <see cref="TaskDifficulty"/> badges, selectors, and escalation telemetry.
/// Keeps Razor UI components free of domain styling rules and string-formatting branches.
/// </summary>
public static class TaskDifficultyPresentationHelper
{
    public static IReadOnlyList<TaskDifficulty> AllDifficulties { get; } =
    [
        TaskDifficulty.Standard,
        TaskDifficulty.Difficult,
        TaskDifficulty.Expert
    ];

    public static string GetLabel(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "Standard",
        TaskDifficulty.Difficult => "Difficult",
        TaskDifficulty.Expert => "Expert",
        _ => difficulty.ToString()
    };

    public static string GetDisplayLabel(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "Standard",
        TaskDifficulty.Difficult => "🔥 Difficult",
        TaskDifficulty.Expert => "⚡ Expert",
        _ => difficulty.ToString()
    };

    public static string GetDescription(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "Standard: baseline local models and latency.",
        TaskDifficulty.Difficult => "Difficult: deeper reasoning / stronger architecture & coding tier.",
        TaskDifficulty.Expert => "Expert: maximum capability tier or cloud ACP frontier escalation.",
        _ => string.Empty
    };

    public static string GetShortDescription(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "Baseline tier (base agent profiles)",
        TaskDifficulty.Difficult => "Escalated reasoning / stronger local or architect tier",
        TaskDifficulty.Expert => "Maximum capability / frontier cloud ACP tier",
        _ => string.Empty
    };

    public static string GetBadgeCssClass(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "badge-gray",
        TaskDifficulty.Difficult => "badge-amber",
        TaskDifficulty.Expert => "badge-purple",
        _ => "badge-gray"
    };

    public static string GetBadgeStyle(TaskDifficulty difficulty) => difficulty switch
    {
        TaskDifficulty.Standard => "background: rgba(148, 163, 184, 0.14); color: #cbd5e1; border: 1px solid rgba(148, 163, 184, 0.28);",
        TaskDifficulty.Difficult => "background: rgba(245, 158, 11, 0.16); color: #fbbf24; border: 1px solid rgba(245, 158, 11, 0.35);",
        TaskDifficulty.Expert => "background: rgba(168, 85, 247, 0.18); color: #c084fc; border: 1px solid rgba(168, 85, 247, 0.38);",
        _ => "background: rgba(148, 163, 184, 0.14); color: #cbd5e1; border: 1px solid rgba(148, 163, 184, 0.28);"
    };

    public static string GetChipStyle(TaskDifficulty difficulty, bool isSelected)
    {
        if (!isSelected)
        {
            return "background: var(--bg-surface); color: var(--text-secondary); border: 1px solid var(--border-subtle);";
        }

        return difficulty switch
        {
            TaskDifficulty.Standard => "background: rgba(99, 102, 241, 0.18); color: #a5b4fc; border: 1px solid rgba(99, 102, 241, 0.45); font-weight: 600;",
            TaskDifficulty.Difficult => "background: rgba(245, 158, 11, 0.20); color: #fbbf24; border: 1px solid rgba(245, 158, 11, 0.50); font-weight: 600;",
            TaskDifficulty.Expert => "background: rgba(168, 85, 247, 0.22); color: #d8b4fe; border: 1px solid rgba(168, 85, 247, 0.55); font-weight: 600;",
            _ => "background: var(--bg-surface); color: var(--text-primary); border: 1px solid var(--border-default);"
        };
    }

    public const string DeterministicComputeLabel = "Deterministic CPU";
    public const string AgenticGpuComputeLabel = "Agentic GPU";
    public const string AntigravityAcpComputeLabel = "Antigravity ACP";

    public static string FormatStageComputeLabel(WorkflowStageKind stageKind, string? harnessId) =>
        stageKind == WorkflowStageKind.Deterministic
            ? DeterministicComputeLabel
            : (string.Equals(harnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)
                ? AntigravityAcpComputeLabel
                : AgenticGpuComputeLabel);

    public static string FormatHarnessShortLabel(string? harnessId) =>
        string.Equals(harnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase)
            ? AntigravityAcpComputeLabel
            : "Crucible";

    public static string FormatEscalationBadge(TaskDifficulty difficulty, string? harnessId, string? modelName)
    {
        var difficultyLabel = GetLabel(difficulty);
        var harnessLabel = FormatHarnessShortLabel(harnessId);
        var effectiveModel = string.IsNullOrWhiteSpace(modelName) ? "default" : modelName.Trim();
        return $"⚡ Escalated ({difficultyLabel}: {harnessLabel} / {effectiveModel})";
    }
}
