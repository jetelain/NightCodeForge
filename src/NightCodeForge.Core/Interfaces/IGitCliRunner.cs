namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Execution result of a native Git CLI command.
/// </summary>
/// <param name="ExitCode">Process exit code (0 for success).</param>
/// <param name="StandardOutput">Captured standard output stream.</param>
/// <param name="StandardError">Captured standard error stream.</param>
/// <param name="Duration">Execution duration.</param>
public record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

/// <summary>
/// Exception thrown when a Git CLI command exits with a non-zero exit code.
/// </summary>
public class GitCommandException : Exception
{
    public string WorkingDirectory { get; }
    public string Arguments { get; }
    public int ExitCode { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }

    public GitCommandException(
        string workingDirectory,
        string arguments,
        int exitCode,
        string standardOutput,
        string standardError)
        : base($"Git command 'git {arguments}' failed with exit code {exitCode} in '{workingDirectory}'.\n{standardError}")
    {
        WorkingDirectory = workingDirectory;
        Arguments = arguments;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }
}

/// <summary>
/// Native Git CLI command runner abstraction.
/// </summary>
public interface IGitCliRunner
{
    /// <summary>
    /// Executes a native Git CLI command asynchronously.
    /// </summary>
    /// <param name="workingDirectory">Repository working directory.</param>
    /// <param name="arguments">Git CLI arguments (without leading 'git').</param>
    /// <param name="timeoutSeconds">Optional command timeout in seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result containing exit code, stdout, stderr, and duration.</returns>
    /// <exception cref="GitCommandException">Thrown if Git exits with a non-zero exit code.</exception>
    Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default);
}
