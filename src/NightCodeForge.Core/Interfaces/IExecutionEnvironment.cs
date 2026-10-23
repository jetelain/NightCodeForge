namespace NightCodeForge.Core.Interfaces;

public enum ExecutionTier
{
    BareHost = 0,
    WslDirect = 1,
    ContainerSandbox = 2
}

public record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool Truncated = false)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Execution environment abstraction supporting BareHost, WslDirect, and ContainerSandbox.
/// </summary>
public interface IExecutionEnvironment
{
    ExecutionTier Tier { get; }

    Task<CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider factory resolving the appropriate execution environment for a workspace.
/// </summary>
public interface IExecutionEnvironmentProvider
{
    IExecutionEnvironment GetEnvironment(
        NightCodeForge.Core.Domain.Workspace workspace,
        NightCodeForge.Core.Domain.SandboxSecurityTier securityTier = NightCodeForge.Core.Domain.SandboxSecurityTier.Standard,
        int? taskId = null);

    /// <summary>
    /// Removes the per-task sandbox container of a task (no-op when the workspace is not container-sandboxed
    /// or the container does not exist). Never throws on Docker/WSL failures: failures are logged.
    /// </summary>
    Task CleanupTaskContainerAsync(NightCodeForge.Core.Domain.Workspace workspace, int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all per-task sandbox containers belonging to the workspace.
    /// </summary>
    Task CleanupWorkspaceContainersAsync(NightCodeForge.Core.Domain.Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the ids of tasks that currently own a (running or stopped) sandbox container in the workspace.
    /// </summary>
    Task<IReadOnlyList<int>> ListTaskContainerIdsAsync(NightCodeForge.Core.Domain.Workspace workspace, CancellationToken cancellationToken = default);
}

