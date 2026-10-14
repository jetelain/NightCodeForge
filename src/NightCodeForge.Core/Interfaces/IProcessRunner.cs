namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Stream kind for captured process output.
/// </summary>
public enum ProcessStreamKind
{
    StandardOutput,
    StandardError,
    Exit
}

/// <summary>
/// A real-time chunk of process output or terminal exit notification.
/// </summary>
/// <param name="Kind">Type of stream event (stdout, stderr, exit).</param>
/// <param name="Text">Text content for stdout/stderr chunks.</param>
/// <param name="ExitCode">Process exit code if Kind is Exit or on timeout.</param>
public record ProcessStreamChunk(
    ProcessStreamKind Kind,
    string Text,
    int? ExitCode = null);

/// <summary>
/// Result of executing a system process.
/// </summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Standard output string.</param>
/// <param name="StandardError">Standard error string.</param>
public record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Specification for starting a system process with either arguments string or argument list,
/// optional streaming callbacks, environment variables, working directory, and timeout.
/// </summary>
public class ProcessStartSpec
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Command-line arguments string (mutually exclusive with ArgumentList).
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Command-line arguments list passed directly to ProcessStartInfo.ArgumentList, avoiding shell escaping issues.
    /// </summary>
    public IEnumerable<string>? ArgumentList { get; set; }

    public string? WorkingDirectory { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Optional standard input text to write to the process.
    /// </summary>
    public string? StandardInput { get; set; }

    /// <summary>
    /// Optional streaming callback invoked for each line of standard output in real time.
    /// </summary>
    public Action<string>? OnStandardOutput { get; set; }

    /// <summary>
    /// Optional streaming callback invoked for each line of standard error in real time.
    /// </summary>
    public Action<string>? OnStandardError { get; set; }
}

/// <summary>
/// Active interactive or streaming process session for tools and agent harnesses.
/// Supports two-way communication (stdin writing and stdout/stderr reading/streaming).
/// </summary>
public interface IProcessStreamSession : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Exit code of the process, or null if still running.
    /// </summary>
    int? ExitCode { get; }

    /// <summary>
    /// Whether the process has terminated.
    /// </summary>
    bool HasExited { get; }

    /// <summary>
    /// Standard input stream writer for interactive communication.
    /// </summary>
    StreamWriter StandardInput { get; }

    /// <summary>
    /// Standard output stream reader.
    /// </summary>
    StreamReader StandardOutput { get; }

    /// <summary>
    /// Standard error stream reader.
    /// </summary>
    StreamReader StandardError { get; }

    /// <summary>
    /// Asynchronously streams interleaved stdout and stderr chunks in real time as they arrive.
    /// Yields a final chunk with Kind == ProcessStreamKind.Exit when the process terminates.
    /// </summary>
    IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits asynchronously for the process to exit or times out.
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forcibly kills the process tree.
    /// </summary>
    void Kill();
}

/// <summary>
/// Abstraction for executing system processes with standard stream redirection, ArgumentList support,
/// real-time streaming, and timeouts. Enables deterministic mocking in unit tests.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Executes a system process synchronously using a structured specification.
    /// </summary>
    ProcessExecutionResult Run(ProcessStartSpec spec);

    /// <summary>
    /// Executes a system process asynchronously using a structured specification, supporting streaming callbacks.
    /// </summary>
    Task<ProcessExecutionResult> RunAsync(
        ProcessStartSpec spec,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams process standard output and standard error chunks asynchronously using a structured specification.
    /// </summary>
    IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(
        ProcessStartSpec spec,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an interactive streaming session supporting live stdin/stdout/stderr streaming for tools and agent harnesses.
    /// </summary>
    IProcessStreamSession StartSession(ProcessStartSpec spec);
}
