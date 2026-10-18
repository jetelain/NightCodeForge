namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Record of an executed command and its output within a deterministic stage or sub-stage.
/// </summary>
public record StageCommandExecution(
    string Command,
    string Output,
    string StageName,
    bool IsSuccess,
    long DurationMs = 0);
