namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Extension methods for <see cref="IProcessRunner"/> providing convenience overloads
/// for synchronous execution, asynchronous execution, callback streaming, and async enumeration.
/// </summary>
public static class ProcessRunnerExtensions
{
    /// <summary>
    /// Executes a system process synchronously with command-line arguments string and returns captured stdout and stderr.
    /// </summary>
    public static ProcessExecutionResult Run(
        this IProcessRunner runner,
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.Run(new ProcessStartSpec
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        });
    }

    /// <summary>
    /// Executes a system process synchronously with an argument list (ProcessStartInfo.ArgumentList) to avoid escaping tasks.
    /// </summary>
    public static ProcessExecutionResult Run(
        this IProcessRunner runner,
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.Run(new ProcessStartSpec
        {
            FileName = fileName,
            ArgumentList = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        });
    }

    /// <summary>
    /// Executes a system process asynchronously with command-line arguments string.
    /// </summary>
    public static Task<ProcessExecutionResult> RunAsync(
        this IProcessRunner runner,
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(new ProcessStartSpec
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a system process asynchronously with an argument list (ProcessStartInfo.ArgumentList).
    /// </summary>
    public static Task<ProcessExecutionResult> RunAsync(
        this IProcessRunner runner,
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(new ProcessStartSpec
        {
            FileName = fileName,
            ArgumentList = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a process asynchronously while streaming standard output and standard error lines to callbacks.
    /// </summary>
    public static Task<ProcessExecutionResult> RunWithCallbacksAsync(
        this IProcessRunner runner,
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onStandardOutput,
        Action<string>? onStandardError = null,
        string? workingDirectory = null,
        int timeoutSeconds = 10,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(new ProcessStartSpec
        {
            FileName = fileName,
            ArgumentList = arguments,
            OnStandardOutput = onStandardOutput,
            OnStandardError = onStandardError,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            StandardInput = standardInput
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a process asynchronously while streaming standard output and standard error lines to callbacks.
    /// </summary>
    public static Task<ProcessExecutionResult> RunWithCallbacksAsync(
        this IProcessRunner runner,
        string fileName,
        string arguments,
        Action<string>? onStandardOutput,
        Action<string>? onStandardError = null,
        string? workingDirectory = null,
        int timeoutSeconds = 10,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(new ProcessStartSpec
        {
            FileName = fileName,
            Arguments = arguments,
            OnStandardOutput = onStandardOutput,
            OnStandardError = onStandardError,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            StandardInput = standardInput
        }, cancellationToken);
    }

    /// <summary>
    /// Streams process standard output and standard error chunks asynchronously as they arrive using an argument list.
    /// </summary>
    public static IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(
        this IProcessRunner runner,
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 300,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunStreamingAsync(new ProcessStartSpec
        {
            FileName = fileName,
            ArgumentList = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            StandardInput = standardInput
        }, cancellationToken);
    }

    /// <summary>
    /// Streams process standard output and standard error chunks asynchronously as they arrive using an arguments string.
    /// </summary>
    public static IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(
        this IProcessRunner runner,
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutSeconds = 300,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunStreamingAsync(new ProcessStartSpec
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds,
            StandardInput = standardInput
        }, cancellationToken);
    }
}
