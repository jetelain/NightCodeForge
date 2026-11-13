using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Factory for creating and managing container sandbox execution environments for workspaces, tasks, and declarative sandbox scopes.
/// </summary>
public interface IContainerEnvironmentFactory
{
    /// <summary>
    /// Gets the default WSL distribution configured in <see cref="ExecutionEnvironmentProviderOptions"/>.
    /// </summary>
    string? DefaultWslDistribution { get; }

    /// <summary>
    /// Builds a declarative <see cref="SandboxScopeDescriptor"/> for a workspace and optional task ID.
    /// </summary>
    SandboxScopeDescriptor BuildWorkspaceScope(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? effectiveTaskId = null);

    /// <summary>
    /// Asynchronously creates a new <see cref="NightCodeForgeContainerExecutor"/> configured for the specified declarative <see cref="SandboxScopeDescriptor"/>.
    /// </summary>
    Task<IExecutionEnvironment> CreateContainerEnvironmentAsync(
        SandboxScopeDescriptor scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously creates a new <see cref="NightCodeForgeContainerExecutor"/> configured for the specified workspace, security tier, and optional task worktree.
    /// </summary>
    Task<IExecutionEnvironment> CreateContainerEnvironmentAsync(
        Workspace workspace,
        SandboxSecurityTier securityTier,
        int? effectiveTaskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the container associated with a declarative <see cref="SandboxScopeDescriptor"/>.
    /// </summary>
    Task RemoveContainerAsync(
        SandboxScopeDescriptor scope,
        CancellationToken cancellationToken = default);

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
