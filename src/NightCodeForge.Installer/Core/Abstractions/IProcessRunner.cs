namespace NightCodeForge.Installer.Core.Abstractions;

public sealed record ProcessRunRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? RawArguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool UseShellExecute { get; init; }
    public bool RunAsAdmin { get; init; }
    public TimeSpan? Timeout { get; init; }
    public bool Detached { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
}

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Abstraction for spawning external processes, elevated helpers, and detached background commands.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
