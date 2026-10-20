namespace NightCodeForge.Core.Domain;

/// <summary>
/// Constants for bidirectional task markdown synchronization and workspace hygiene.
/// </summary>
public static class TaskSyncConstants
{
    public const string DocsSegment = "docs";
    public const string TasksSegment = "tasks";
    public const string MarkdownPattern = "*.md";
    public const string MarkdownExtension = ".md";
    public const string ArchiveSegment = "archive";
    public const int DefaultPriority = TaskPriority.Normal;

    public const string InitialTaskCreation = "Initial task creation";
    public const string TaskCreatedFromDisk = "Task created from disk markdown";
    public const string DiskModificationsSync = "Synchronized disk modifications";
    public const string TaskReviewSpecificationUpdate = "Task review stage specification update";
    public const string PreserveEditsBeforeResumeReason = "Preserve agent edits before resumed stage pre-sync";

    public static readonly IReadOnlyList<string> RequiredGitIgnoreEntries = new[]
    {
        "bin/",
        "obj/",
        ".vs/",
        "TestResults/",
        "*.trx",
        "docs/tasks/",
        "docs/transcripts/",
        ".nightcodeforge/transcripts/",
        ".nightcodeforge/worktrees/",
        ".nightcodeforge/logs/",
        ".nightcodeforge/audit-report.md"
    };
}
