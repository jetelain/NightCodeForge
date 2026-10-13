using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

public class ContainerExecutorOptions
{
    public string ContainerNameOrId { get; set; } = "ncf-sandbox";
    public string ShellExecutable { get; set; } = "docker";
    public string DefaultWorkingDirectory { get; set; } = "/workspace";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int MaxBufferBytes { get; set; } = HeadTailBuffer.DefaultMaxBytes;
    public bool IsRestrictedReadOnly { get; set; } = false;
    public Func<Process>? CustomProcessFactory { get; set; }
}

/// <summary>
/// Persistent REPL execution environment inside a container or interactive shell using cryptographic sentinels.
/// </summary>
public partial class NightCodeForgeContainerExecutor : IExecutionEnvironment, IAsyncDisposable, IDisposable
{
    [GeneratedRegex(@"^\s*(-?\d+)")]
    private static partial Regex SentinelExitCodeRegex();

    private readonly ContainerExecutorOptions _options;
    private readonly ILogger<NightCodeForgeContainerExecutor> _logger;
    private readonly SemaphoreSlim _execLock = new(1, 1);

    private Process? _shellProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _isDisposed;

    public ExecutionTier Tier => ExecutionTier.ContainerSandbox;

    public NightCodeForgeContainerExecutor(
        ContainerExecutorOptions? options = null,
        ILogger<NightCodeForgeContainerExecutor>? logger = null)
    {
        _options = options ?? new ContainerExecutorOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForgeContainerExecutor>.Instance;
    }

    public async Task<CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        await _execLock.WaitAsync(cancellationToken);
        try
        {
            EnsureShellProcessRunning();

            var timeout = timeoutSeconds ?? _options.DefaultTimeoutSeconds;
            var sentinel = $"__NCF_SENTINEL_{Guid.NewGuid():N}__";
            var workDir = workingDirectory ?? _options.DefaultWorkingDirectory;
            var stopwatch = Stopwatch.StartNew();

            // Set environment variables if any
            if (environmentVariables != null && environmentVariables.Count > 0)
            {
                foreach (var (k, v) in environmentVariables)
                {
                    await _stdin!.WriteLineAsync($"export {k}=\"{v.Replace("\"", "\\\"")}\"");
                }
            }

            await _stdin!.WriteLineAsync($"cd \"{workDir}\" && {command}");
            await _stdin.WriteLineAsync($"echo \"{sentinel} $?\"");
            await _stdin.FlushAsync();

            try
            {
                var (output, exitCode) = await ReadUntilSentinelAsync(sentinel, timeout, cancellationToken);
                stopwatch.Stop();
                return new CommandResult(exitCode, output.GetFormattedString(), string.Empty, stopwatch.Elapsed, output.IsTruncated);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Command execution timed out after {Timeout}s. Sending SIGINT (\\x03)", timeout);
                
                // 1. Send SIGINT (Ctrl+C)
                await _stdin!.WriteAsync('\x03');
                await _stdin.FlushAsync();

                try
                {
                    // 2. Wait 2-second grace period for shell to settle and emit sentinel
                    var (graceOutput, exitCode) = await ReadUntilSentinelAsync(sentinel, 2, cancellationToken);
                    stopwatch.Stop();
                    return new CommandResult(exitCode, graceOutput.GetFormattedString(), "Command timed out and was aborted with SIGINT.", stopwatch.Elapsed, graceOutput.IsTruncated);
                }
                catch (Exception)
                {
                    // 3. Unresponsive: forcefully restart shell process
                    _logger.LogError("Container shell process unresponsive after SIGINT grace period. Forcefully restarting shell.");
                    ForceRestartShellProcess();
                    stopwatch.Stop();
                    throw new TimeoutException($"Command timed out after {timeout}s and the container shell was forcefully restarted.");
                }
            }
        }
        finally
        {
            _execLock.Release();
        }
    }

    private async Task<(HeadTailBuffer Output, int ExitCode)> ReadUntilSentinelAsync(
        string sentinel,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var buffer = new HeadTailBuffer(_options.MaxBufferBytes);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        while (!cts.IsCancellationRequested)
        {
            var readLineTask = _stdout!.ReadLineAsync(cts.Token).AsTask();
            var line = await readLineTask;

            if (line == null)
            {
                throw new InvalidOperationException("Container shell process terminated unexpectedly (stdout closed).");
            }

            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith(sentinel, StringComparison.Ordinal))
            {
                var suffix = trimmedLine[sentinel.Length..];
                var match = SentinelExitCodeRegex().Match(suffix);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var exitCode))
                {
                    return (buffer, exitCode);
                }
            }

            buffer.Append(line + "\n");
        }

        throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}'.");
    }

    private void EnsureShellProcessRunning()
    {
        if (_shellProcess != null && !_shellProcess.HasExited)
        {
            return;
        }

        StartShellProcess();
    }

    private void StartShellProcess()
    {
        if (_options.CustomProcessFactory != null)
        {
            _shellProcess = _options.CustomProcessFactory();
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.ShellExecutable,
                Arguments = $"exec -i {_options.ContainerNameOrId} bash --noprofile --norc",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _shellProcess = new Process { StartInfo = psi };
        }

        _shellProcess.Start();
        _stdin = new StreamWriter(_shellProcess.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        _stdout = _shellProcess.StandardOutput;
    }

    public void ForceRestartShellProcess()
    {
        try
        {
            if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while killing unresponsive shell process");
        }
        finally
        {
            _shellProcess?.Dispose();
            _shellProcess = null;
            _stdin?.Dispose();
            _stdin = null;
        }

        StartShellProcess();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _stdin?.WriteLine("exit");
                _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch { }

        _shellProcess?.Dispose();
        _stdin?.Dispose();
        _execLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
