namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Telemetry record capturing the high-level context of an actively executing task.
/// </summary>
public record ActiveTaskSummary(
    int TaskId,
    string TaskTitle,
    string CurrentStageId,
    int Priority,
    string QueueType,
    DateTime StartedAt,
    string HardwareTarget = "Local GPU");
