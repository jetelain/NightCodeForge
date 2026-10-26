using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using System.Diagnostics.CodeAnalysis;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Helper utilities to determine whether an activity event, task ID, or summary represents
/// a genuine autonomous task execution as opposed to an interactive discovery or placeholder session (e.g. Ideation chat).
/// </summary>
public static class AutonomousTaskClassifier
{
    /// <summary>
    /// Determines whether the specified task ID and stage ID represent an autonomous task execution
    /// rather than an interactive/placeholder session (such as Ideation chat with task 0).
    /// </summary>
    public static bool IsAutonomousTask(int? taskId, string? stageId) =>
        taskId.HasValue && taskId.Value > 0 && !string.Equals(stageId, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether the activity event represents an autonomous task execution.
    /// </summary>
    public static bool IsAutonomousTask([NotNullWhen(true)] AgentActivityEvent? evt) =>
        evt != null && IsAutonomousTask(evt.TaskId, evt.StageId);

    /// <summary>
    /// Determines whether the active task summary represents a valid autonomous task execution.
    /// </summary>
    public static bool IsAutonomousTask([NotNullWhen(true)] ActiveTaskSummary? summary) =>
        summary != null && IsAutonomousTask(summary.TaskId, summary.CurrentStageId);
}
