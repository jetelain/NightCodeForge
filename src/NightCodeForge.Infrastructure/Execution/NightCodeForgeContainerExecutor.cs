using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

public class ContainerExecutorOptions
{
    public string ContainerNameOrId { get; set; } = "ncf-sandbox";
    public string? ContainerImage { get; set; }
    public string ShellExecutable { get; set; } = "docker";
    public string DefaultWorkingDirectory { get; set; } = "/workspace";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int MaxBufferBytes { get; set; } = HeadTailBuffer.DefaultMaxBytes;
    public bool IsRestrictedReadOnly { get; set; } = false;
    public Func<Process>? CustomProcessFactory { get; set; }

    /// <summary>
    /// If true, container operations (e.g. docker exec) are invoked via wsl.exe into a WSL2 environment.
    /// </summary>
    public bool UseWsl { get; set; } = false;

    /// <summary>
    /// Optional WSL2 distribution name to target (e.g. "Ubuntu", "docker-desktop"). If null, uses the default WSL distribution.
    /// </summary>
    public string? WslDistribution { get; set; }

    /// <summary>
    /// Path or name of the WSL2 executable on Windows (defaults to "wsl.exe").
    /// </summary>
    public string WslExecutable { get; set; } = "wsl.exe";
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
            if (_options.UseWsl && !string.IsNullOrWhiteSpace(workDir))
            {
                workDir = Git.WslPathConverter.ToWslPath(workDir);
            }
            var stopwatch = Stopwatch.StartNew();

            // Set environment variables if any (scrub secrets if RestrictedReadOnly)
            if (environmentVariables != null && environmentVariables.Count > 0)
            {
                foreach (var (k, v) in environmentVariables)
                {
                    if (_options.IsRestrictedReadOnly && IsSensitiveEnvVar(k))
                    {
                        continue;
                    }
                    await _stdin!.WriteLineAsync($"export {k}=\"{v.Replace("\"", "\\\"")}\"");
                }
            }

            if (_options.IsRestrictedReadOnly)
            {
                await _stdin!.WriteLineAsync("export HTTP_PROXY=\"\" HTTPS_PROXY=\"\" ALL_PROXY=\"\"");
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
            var psi = BuildProcessStartInfo(_options);
            _shellProcess = new Process { StartInfo = psi };
        }

        _shellProcess.Start();
        _stdin = new StreamWriter(_shellProcess.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        _stdout = _shellProcess.StandardOutput;
    }

    public static ProcessStartInfo BuildProcessStartInfo(ContainerExecutorOptions options)
    {
        var userArg = options.IsRestrictedReadOnly ? "--user 1000:1000 " : string.Empty;
        var dockerArgs = $"exec -i {userArg}{options.ContainerNameOrId} bash --noprofile --norc";

        if (options.UseWsl)
        {
            var wslArgs = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.Append($"-d \"{options.WslDistribution}\" ");
            }
            wslArgs.Append($"-- {options.ShellExecutable} {dockerArgs}");

            return new ProcessStartInfo
            {
                FileName = options.WslExecutable,
                Arguments = wslArgs.ToString(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = options.ShellExecutable,
            Arguments = dockerArgs,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    /// Builds executable and arguments to invoke Docker either directly on the host or inside WSL2 via wsl.exe.
    /// </summary>
    public static (string Executable, string Arguments) BuildDockerCommand(
        string dockerCommand,
        bool useWsl = false,
        string? wslDistribution = null,
        string shellExecutable = "docker",
        string wslExecutable = "wsl.exe")
    {
        if (useWsl)
        {
            var distroArg = !string.IsNullOrWhiteSpace(wslDistribution)
                ? $"-d \"{wslDistribution}\" "
                : string.Empty;
            return (wslExecutable, $"{distroArg}-- {shellExecutable} {dockerCommand}".Trim());
        }

        return (shellExecutable, dockerCommand);
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

    private static bool IsSensitiveEnvVar(string key)
    {
        var upper = key.ToUpperInvariant();
        return upper.Contains("KEY") ||
               upper.Contains("TOKEN") ||
               upper.Contains("SECRET") ||
               upper.Contains("PASSWORD") ||
               upper.Contains("AUTH") ||
               upper.Contains("CREDENTIAL");
    }
}
