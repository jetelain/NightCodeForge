namespace NightCodeForge.Core.Domain;

/// <summary>
/// Defines the ingestion source or authoring channel of a task.
/// </summary>
public enum TaskOrigin
{
    Manual = 0,
    Ideation = 1,
    UntrustedIntake = 2,
    Import = 3,
    GitHubIssue = 4
}
