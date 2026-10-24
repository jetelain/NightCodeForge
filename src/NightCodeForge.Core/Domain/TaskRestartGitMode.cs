namespace NightCodeForge.Core.Domain;

/// <summary>
/// Defines the Git worktree and branch strategy when restarting a task pipeline.
/// </summary>
public enum TaskRestartGitMode
{
    /// <summary>
    /// Preserves existing feature branch commits and modified worktree files,
    /// restarting workflow stages from the workflow initial stage on top of existing work.
    /// </summary>
    PreserveWork = 0,

    /// <summary>
    /// Hard-resets the task's feature branch to the latest commit of its base branch (e.g. main),
    /// cleans uncommitted files, recycles any running sandbox container, clears pipeline bags,
    /// aligns remote branch if present, and starts the workflow fresh.
    /// </summary>
    HardResetToBaseBranch = 1
}
