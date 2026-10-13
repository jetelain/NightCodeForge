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
    IExecutionEnvironment GetEnvironment(NightCodeForge.Core.Domain.Workspace workspace);
}

