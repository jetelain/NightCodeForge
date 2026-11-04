using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Factory for creating and managing container sandbox execution environments for workspaces and tasks.
/// </summary>
public interface IContainerEnvironmentFactory
{
    /// <summary>
    /// Gets the default WSL distribution configured in <see cref="ExecutionEnvironmentProviderOptions"/>.
    /// </summary>
    string? DefaultWslDistribution { get; }

    /// <summary>
    /// Creates a new <see cref="NightCodeForgeContainerExecutor"/> configured for the specified workspace, security tier, and optional task worktree.
    /// </summary>
    IExecutionEnvironment CreateContainerEnvironment(
        Workspace workspace,
        SandboxSecurityTier securityTier,
        int? effectiveTaskId);

    /// <summary>
    /// Removes the container associated with a specific task in the workspace.
    /// </summary>
    Task RemoveTaskContainerAsync(
        Workspace workspace,
        int taskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the base workspace container.
    /// </summary>
    Task RemoveBaseContainerAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the task IDs for all existing task containers belonging to the given workspace.
    /// </summary>
    Task<IReadOnlyList<int>> ListTaskContainerIdsAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default);
}

