using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Execution;

/// <summary>
/// System process runner executing native host executables with stream capture, ArgumentList support,
/// real-time streaming, null character sanitization, and timeouts.
/// </summary>
public class SystemProcessRunner : IProcessRunner
{
    private readonly ILogger _logger;

    public SystemProcessRunner()
        : this(NullLogger<SystemProcessRunner>.Instance)
    {
    }

    public SystemProcessRunner(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }


    public ProcessExecutionResult Run(ProcessStartSpec spec)
    {
        var psi = CreateStartInfo(spec);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            if (!string.IsNullOrEmpty(spec.StandardInput))
            {
                process.StandardInput.Write(spec.StandardInput);
                process.StandardInput.Flush();
            }
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var timeoutMs = Math.Max(1, spec.TimeoutSeconds) * 1000;
            if (!process.WaitForExit(timeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill timed out process '{FileName}'", spec.FileName);
                }

                return new ProcessExecutionResult(124, string.Empty, $"Process timed out after {spec.TimeoutSeconds}s.");
            }

            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5));

            var stdout = Sanitize(stdoutTask.Result);
            var stderr = Sanitize(stderrTask.Result);

            return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute process '{FileName}'", spec.FileName);
            return new ProcessExecutionResult(-1, string.Empty, ex.Message);
        }
    }


    public async Task<ProcessExecutionResult> RunAsync(
        ProcessStartSpec spec,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var psi = CreateStartInfo(spec);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            if (!string.IsNullOrEmpty(spec.StandardInput))
            {
                await process.StandardInput.WriteAsync(spec.StandardInput).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            process.StandardInput.Close();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var stdoutTask = spec.OnStandardOutput == null
                ? process.StandardOutput.ReadToEndAsync(linkedCts.Token)
                : ReadStreamWithCallbacksAsync(process.StandardOutput, spec.OnStandardOutput, linkedCts.Token);

            var stderrTask = spec.OnStandardError == null
                ? process.StandardError.ReadToEndAsync(linkedCts.Token)
                : ReadStreamWithCallbacksAsync(process.StandardError, spec.OnStandardError, linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to kill timed-out process '{FileName}'", spec.FileName);
                }

                return new ProcessExecutionResult(124, string.Empty, $"Process timed out after {spec.TimeoutSeconds}s.");
            }

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            var stdout = Sanitize(await stdoutTask);
            var stderr = Sanitize(await stderrTask);

            return new ProcessExecutionResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute async process '{FileName}'", spec.FileName);
            return new ProcessExecutionResult(-1, string.Empty, ex.Message);
        }
    }


    public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(
        ProcessStartSpec spec,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IProcessStreamSession? session = null;
        ProcessStreamChunk? errorChunk = null;
        try
        {
            session = StartSession(spec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start streaming process '{FileName}'", spec.FileName);
            errorChunk = new ProcessStreamChunk(ProcessStreamKind.StandardError, ex.Message, -1);
        }

        if (session == null || errorChunk != null)
        {
            if (errorChunk != null)
            {
                yield return errorChunk;
                yield return new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, -1);
            }
            yield break;
        }

        await using (session.ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(spec.StandardInput))
            {
                try
                {
                    await session.StandardInput.WriteAsync(spec.StandardInput).ConfigureAwait(false);
                    await session.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    session.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to write StandardInput to process '{FileName}'", spec.FileName);
                }
            }

            await foreach (var chunk in session.ReadAllAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }
    }

    public IProcessStreamSession StartSession(ProcessStartSpec spec)
    {
        var psi = CreateStartInfo(spec);
        var process = new Process { StartInfo = psi };
        process.Start();
        return new SystemProcessStreamSession(process, spec.TimeoutSeconds, _logger);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessStartSpec spec)
    {
        var workDir = !string.IsNullOrWhiteSpace(spec.WorkingDirectory) && Directory.Exists(spec.WorkingDirectory)
            ? spec.WorkingDirectory
            : AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (spec.ArgumentList != null)
        {
            foreach (var arg in spec.ArgumentList)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else if (!string.IsNullOrEmpty(spec.Arguments))
        {
            psi.Arguments = spec.Arguments;
        }

        if (spec.EnvironmentVariables != null)
        {
            foreach (var (k, v) in spec.EnvironmentVariables)
            {
                psi.Environment[k] = v;
            }
        }

        return psi;
    }

    private static async Task<string> ReadStreamWithCallbacksAsync(
        StreamReader reader,
        Action<string>? callback,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null) break;

                sb.AppendLine(line);
                callback?.Invoke(line);
            }
        }
        catch (OperationCanceledException) { }

        return sb.ToString();
    }

    private static string Sanitize(string? output)
    {
        if (string.IsNullOrEmpty(output)) return string.Empty;
        if (output.Contains('\0'))
        {
            return new string(output.Where(c => c != '\0').ToArray());
        }
        return output;
    }

    private sealed class SystemProcessStreamSession : IProcessStreamSession
    {
        private readonly Process _process;
        private readonly int _timeoutSeconds;
        private readonly ILogger _logger;
        private readonly Channel<ProcessStreamChunk> _channel;
        private readonly CancellationTokenSource _cts = new();
        private Task? _stdoutTask;
        private Task? _stderrTask;
        private Task? _exitMonitorTask;
        private int _startedPumps;
        private bool _isDisposed;

        public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
        public bool HasExited => _process.HasExited;
        public StreamWriter StandardInput => _process.StandardInput;
        public StreamReader StandardOutput => _process.StandardOutput;
        public StreamReader StandardError => _process.StandardError;

        public SystemProcessStreamSession(Process process, int timeoutSeconds, ILogger logger)
        {
            _process = process;
            _timeoutSeconds = Math.Max(1, timeoutSeconds);
            _logger = logger;
            _channel = Channel.CreateUnbounded<ProcessStreamChunk>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = false
            });
        }

        public void StartPumps()
        {
            if (Interlocked.Exchange(ref _startedPumps, 1) == 1) return;

            var token = _cts.Token;
            _stdoutTask = PumpStreamAsync(_process.StandardOutput, ProcessStreamKind.StandardOutput, token);
            _stderrTask = PumpStreamAsync(_process.StandardError, ProcessStreamKind.StandardError, token);
            _exitMonitorTask = MonitorExitAsync(token);
        }

        private async Task PumpStreamAsync(StreamReader reader, ProcessStreamKind kind, CancellationToken token)
        {
            var buffer = new char[2048];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;

                    var text = Sanitize(new string(buffer, 0, read));
                    if (!string.IsNullOrEmpty(text))
                    {
                        await _channel.Writer.WriteAsync(new ProcessStreamChunk(kind, text), token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception while pumping process stream {Kind}", kind);
            }
        }

        private async Task MonitorExitAsync(CancellationToken token)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

            try
            {
                await _process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                if (_stdoutTask != null && _stderrTask != null)
                {
                    await Task.WhenAll(_stdoutTask, _stderrTask).ConfigureAwait(false);
                }
                await _channel.Writer.WriteAsync(new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, _process.ExitCode), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                Kill();
                await _channel.Writer.WriteAsync(new ProcessStreamChunk(ProcessStreamKind.StandardError, $"Process timed out after {_timeoutSeconds}s.", 124), CancellationToken.None).ConfigureAwait(false);
                await _channel.Writer.WriteAsync(new ProcessStreamChunk(ProcessStreamKind.Exit, string.Empty, 124), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception while monitoring process exit");
            }
            finally
            {
                _channel.Writer.TryComplete();
            }
        }

        public async IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StartPumps();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            while (true)
            {
                bool hasMore;
                try
                {
                    hasMore = await _channel.Reader.WaitToReadAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (!hasMore) break;

                while (_channel.Reader.TryRead(out var chunk))
                {
                    yield return chunk;
                }
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _cts.Token);

            try
            {
                await _process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                return _process.ExitCode;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Kill();
                return 124;
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already terminated
            }
            catch (Exception)
            {
                // Best-effort process kill during session disposal
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            Kill();
            _cts.Dispose();
            _process.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
