namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Resolves cross-platform paths between the Forge host (Windows, Linux, macOS)
/// and the Unix agent execution environment (WSL2 / ext4 / container sandbox).
/// </summary>
public interface IWorkspacePathResolver
{
    /// <summary>
    /// Gets the base path to the agent's workspace directory (~/.nightcodeforge/wks/{workspaceId}).
    /// </summary>
    string GetAgentWorkspaceRootPath(int workspaceId) => $"~/.nightcodeforge/wks/{workspaceId}";

    /// <summary>
    /// Gets the canonical path to the agent's isolated Git working clone (~/.nightcodeforge/wks/{workspaceId}/clone).
    /// </summary>
    string GetAgentWorkingClonePath(int workspaceId) => $"{GetAgentWorkspaceRootPath(workspaceId)}/clone";

    /// <summary>
    /// Gets the path to an isolated Git worktree for a specific task (~/.nightcodeforge/wks/{workspaceId}/task-{taskId}).
    /// </summary>
    string GetAgentWorktreePath(int workspaceId, int taskId) =>
        $"{GetAgentWorkspaceRootPath(workspaceId)}/task-{taskId}";

    /// <summary>
    /// Gets the Git remote URL for host-origin (converts Windows path to /mnt/... mount path on Windows, raw path on Unix).
    /// </summary>
    string GetHostRemoteUrl(string repositoryPath);

    /// <summary>
    /// Resolves Crucible file tool paths (read/write/edit), stripping any /workspace/ virtual container prefix
    /// and mapping to the Windows UNC share (\\wsl.localhost\{distrib}\home\{wslUser}\.nightcodeforge\wks\{id}\...)
    /// on Windows, or the user home directory on Unix.
    /// </summary>
    string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath);

    /// <summary>
    /// Resolves Crucible file tool paths (read/write/edit), optionally targeting a specific task worktree.
    /// If taskId is null, resolves to the canonical repository clone (used for ideation and triage on main).
    /// </summary>
    string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath, int? taskId) =>
        ResolveCruciblePath(workspaceId, relativeOrContainerPath);

    /// <summary>
    /// Resolves Crucible file tool paths (read/write/edit) given a workspace root directory on the host.
    /// Remaps /workspace virtual container paths to the host filesystem, taking into account
    /// /workspace/clone and /workspace/task-{id} worktrees.
    /// </summary>
    string ResolveCruciblePath(string workspaceRoot, string relativeOrContainerPath, int? taskId = null) =>
        ResolveCruciblePath(0, relativeOrContainerPath, taskId);

    /// <summary>
    /// Gets the targeted WSL2 distribution name.
    /// </summary>
    string GetWslDistributionName();

    /// <summary>
    /// Gets the targeted WSL2 user name.
    /// </summary>
    string GetWslUserName();
}
