namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Resolves cross-platform paths between the Forge host (Windows, Linux, macOS)
/// and the Unix agent execution environment (WSL2 / ext4 / container sandbox).
/// </summary>
public interface IWorkspacePathResolver
{
    /// <summary>
    /// Gets the canonical path to the agent's isolated Git working clone (~/.nightcodeforge/wks/{workspaceId}).
    /// </summary>
    string GetAgentWorkingClonePath(int workspaceId);

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
    /// Gets the targeted WSL2 distribution name.
    /// </summary>
    string GetWslDistributionName();

    /// <summary>
    /// Gets the targeted WSL2 user name.
    /// </summary>
    string GetWslUserName();
}
