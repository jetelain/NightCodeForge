using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Transparent decorator around IProcessRunner capturing CLI start, real-time output preview,
/// execution duration, exit codes, and failure states across all process invocations in the system.
/// </summary>
public class TelemetryProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _innerRunner;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger<TelemetryProcessRunner> _logger;

    public TelemetryProcessRunner(
        IProcessRunner innerRunner,
        IOperationsTelemetryService telemetryService,
        ILogger<TelemetryProcessRunner> logger)
    {
        _innerRunner = innerRunner ?? throw new ArgumentNullException(nameof(innerRunner));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProcessExecutionResult Run(ProcessStartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var commandId = Guid.NewGuid().ToString("N");
        var cmdLine = FormatCommandLine(spec);
        var workingDir = ResolveWorkingDirectory(spec);
        var startTime = DateTime.UtcNow;

        var runningRecord = new ShellCommandRecord(
            CommandId: commandId,
            CommandLine: cmdLine,
            WorkingDirectory: workingDir,
            StartTime: startTime,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        _telemetryService.RecordShellCommandStarted(runningRecord);
        var sw = Stopwatch.StartNew();

        var originalStdOut = spec.OnStandardOutput;
        var originalStdErr = spec.OnStandardError;

        var outBuilder = new StringBuilder();
        var errBuilder = new StringBuilder();

        spec.OnStandardOutput = line =>
        {
            if (outBuilder.Length < 10_000)
            {
                outBuilder.AppendLine(line);
            }
            _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", false);
            originalStdOut?.Invoke(line);
        };

        spec.OnStandardError = line =>
        {
            if (errBuilder.Length < 10_000)
            {
                errBuilder.AppendLine(line);
            }
            _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", true);
            originalStdErr?.Invoke(line);
        };

        try
        {
            var result = _innerRunner.Run(spec);
            sw.Stop();

            var outPreview = !string.IsNullOrEmpty(result.StandardOutput)
                ? Truncate(result.StandardOutput, 2_000)
                : Truncate(outBuilder.ToString(), 2_000);

            var errPreview = !string.IsNullOrEmpty(result.StandardError)
                ? Truncate(result.StandardError, 2_000)
                : Truncate(errBuilder.ToString(), 2_000);

            var completedRecord = runningRecord with
            {
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ExitCode = result.ExitCode,
                OutputPreview = outPreview,
                ErrorPreview = errPreview,
                Status = result.ExitCode == 0 ? ShellCommandStatus.Completed : ShellCommandStatus.Failed
            };

            _telemetryService.RecordShellCommandCompleted(completedRecord);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failedRecord = runningRecord with
            {
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ExitCode = -1,
                ErrorPreview = ex.Message,
                Status = ShellCommandStatus.Failed
            };

            _telemetryService.RecordShellCommandCompleted(failedRecord);
            _logger.LogDebug(ex, "Intercepted failure running shell command {CommandId}: {Cmd}", commandId, cmdLine);
            throw;
        }
        finally
        {
            spec.OnStandardOutput = originalStdOut;
            spec.OnStandardError = originalStdErr;
        }
    }

    public async Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var commandId = Guid.NewGuid().ToString("N");
        var cmdLine = FormatCommandLine(spec);
        var workingDir = ResolveWorkingDirectory(spec);
        var startTime = DateTime.UtcNow;

        var runningRecord = new ShellCommandRecord(
            CommandId: commandId,
            CommandLine: cmdLine,
            WorkingDirectory: workingDir,
            StartTime: startTime,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        _telemetryService.RecordShellCommandStarted(runningRecord);
        var sw = Stopwatch.StartNew();

        var originalStdOut = spec.OnStandardOutput;
        var originalStdErr = spec.OnStandardError;

        var outBuilder = new StringBuilder();
        var errBuilder = new StringBuilder();

        spec.OnStandardOutput = line =>
        {
            if (outBuilder.Length < 10_000)
            {
                outBuilder.AppendLine(line);
            }
            _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", false);
            originalStdOut?.Invoke(line);
        };

        spec.OnStandardError = line =>
        {
            if (errBuilder.Length < 10_000)
            {
                errBuilder.AppendLine(line);
            }
            _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", true);
            originalStdErr?.Invoke(line);
        };

        try
        {
            var result = await _innerRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            var outPreview = !string.IsNullOrEmpty(result.StandardOutput)
                ? Truncate(result.StandardOutput, 2_000)
                : Truncate(outBuilder.ToString(), 2_000);

            var errPreview = !string.IsNullOrEmpty(result.StandardError)
                ? Truncate(result.StandardError, 2_000)
                : Truncate(errBuilder.ToString(), 2_000);

            var completedRecord = runningRecord with
            {
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ExitCode = result.ExitCode,
                OutputPreview = outPreview,
                ErrorPreview = errPreview,
                Status = result.ExitCode == 0 ? ShellCommandStatus.Completed : ShellCommandStatus.Failed
            };

            _telemetryService.RecordShellCommandCompleted(completedRecord);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failedRecord = runningRecord with
            {
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ExitCode = -1,
                ErrorPreview = ex.Message,
                Status = ShellCommandStatus.Failed
            };

            _telemetryService.RecordShellCommandCompleted(failedRecord);
            _logger.LogDebug(ex, "Intercepted failure running async shell command {CommandId}: {Cmd}", commandId, cmdLine);
            throw;
        }
        finally
        {
            spec.OnStandardOutput = originalStdOut;
            spec.OnStandardError = originalStdErr;
        }
    }

    public async IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(
        ProcessStartSpec spec,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var commandId = Guid.NewGuid().ToString("N");
        var cmdLine = FormatCommandLine(spec);
        var workingDir = ResolveWorkingDirectory(spec);
        var startTime = DateTime.UtcNow;

        var runningRecord = new ShellCommandRecord(
            CommandId: commandId,
            CommandLine: cmdLine,
            WorkingDirectory: workingDir,
            StartTime: startTime,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        _telemetryService.RecordShellCommandStarted(runningRecord);
        var sw = Stopwatch.StartNew();

        var outBuilder = new StringBuilder();
        var errBuilder = new StringBuilder();
        int? finalExitCode = null;
        bool completionRecorded = false;
        bool naturalCompletion = false;

        IAsyncEnumerator<ProcessStreamChunk>? enumerator = null;
        try
        {
            enumerator = _innerRunner.RunStreamingAsync(spec, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var failedRecord = runningRecord with
            {
                DurationMs = sw.Elapsed.TotalMilliseconds,
                ExitCode = -1,
                ErrorPreview = ex.Message,
                Status = ShellCommandStatus.Failed
            };
            _telemetryService.RecordShellCommandCompleted(failedRecord);
            throw;
        }

        try
        {
            while (true)
            {
                ProcessStreamChunk chunk;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }
                    chunk = enumerator.Current;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    var failedRecord = runningRecord with
                    {
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                        ExitCode = finalExitCode ?? -1,
                        OutputPreview = Truncate(outBuilder.ToString(), 2_000),
                        ErrorPreview = ex.Message,
                        Status = ShellCommandStatus.Failed
                    };
                    completionRecorded = true;
                    _telemetryService.RecordShellCommandCompleted(failedRecord);
                    throw;
                }

                if (chunk.Kind == ProcessStreamKind.StandardOutput)
                {
                    if (outBuilder.Length < 10_000)
                    {
                        outBuilder.Append(chunk.Text);
                    }
                    _telemetryService.UpdateShellCommandOutput(commandId, chunk.Text, false);
                }
                else if (chunk.Kind == ProcessStreamKind.StandardError)
                {
                    if (errBuilder.Length < 10_000)
                    {
                        errBuilder.Append(chunk.Text);
                    }
                    _telemetryService.UpdateShellCommandOutput(commandId, chunk.Text, true);
                }
                else if (chunk.Kind == ProcessStreamKind.Exit)
                {
                    finalExitCode = chunk.ExitCode;
                }

                yield return chunk;
            }

            naturalCompletion = true;
        }
        finally
        {
            if (enumerator != null)
            {
                await enumerator.DisposeAsync();
            }

            if (!completionRecorded)
            {
                sw.Stop();
                var exitCode = finalExitCode ?? (naturalCompletion ? 0 : -1);
                var status = exitCode == 0 ? ShellCommandStatus.Completed : ShellCommandStatus.Failed;

                var completedRecord = runningRecord with
                {
                    DurationMs = sw.Elapsed.TotalMilliseconds,
                    ExitCode = exitCode,
                    OutputPreview = Truncate(outBuilder.ToString(), 2_000),
                    ErrorPreview = Truncate(errBuilder.ToString(), 2_000),
                    Status = status
                };

                _telemetryService.RecordShellCommandCompleted(completedRecord);
            }
        }
    }

    public IProcessStreamSession StartSession(ProcessStartSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (IsPersistentInteractiveShellSession(spec))
        {
            // Suppress persistent interactive wrapper sessions (e.g. container REPL bash sessions)
            // from telemetry to keep the Active Operations console transparent to real developer commands.
            return _innerRunner.StartSession(spec);
        }

        var commandId = Guid.NewGuid().ToString("N");
        var cmdLine = FormatCommandLine(spec);
        var workingDir = ResolveWorkingDirectory(spec);
        var startTime = DateTime.UtcNow;

        var runningRecord = new ShellCommandRecord(
            CommandId: commandId,
            CommandLine: cmdLine,
            WorkingDirectory: workingDir,
            StartTime: startTime,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        _telemetryService.RecordShellCommandStarted(runningRecord);
        var sw = Stopwatch.StartNew();

        var innerSession = _innerRunner.StartSession(spec);
        return new TelemetryProcessStreamSession(innerSession, runningRecord, _telemetryService, sw);
    }

    private static bool IsPersistentInteractiveShellSession(ProcessStartSpec spec)
    {
        if (spec.ArgumentList != null)
        {
            var args = spec.ArgumentList.ToList();
            bool hasBash = args.Any(a => string.Equals(a, "bash", StringComparison.OrdinalIgnoreCase) || a.EndsWith("/bash", StringComparison.OrdinalIgnoreCase));
            bool hasNoProfile = args.Any(a => string.Equals(a, "--noprofile", StringComparison.OrdinalIgnoreCase));
            bool hasNoRc = args.Any(a => string.Equals(a, "--norc", StringComparison.OrdinalIgnoreCase));
            if (hasBash && hasNoProfile && hasNoRc)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.Arguments))
        {
            var tokens = spec.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool hasBash = tokens.Any(a => string.Equals(a, "bash", StringComparison.OrdinalIgnoreCase) || a.EndsWith("/bash", StringComparison.OrdinalIgnoreCase));
            bool hasNoProfile = tokens.Any(a => string.Equals(a, "--noprofile", StringComparison.OrdinalIgnoreCase));
            bool hasNoRc = tokens.Any(a => string.Equals(a, "--norc", StringComparison.OrdinalIgnoreCase));
            if (hasBash && hasNoProfile && hasNoRc)
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatCommandLine(ProcessStartSpec spec)
    {
        if (spec.ArgumentList != null && spec.ArgumentList.Any())
        {
            return $"{spec.FileName} {string.Join(" ", spec.ArgumentList)}".Trim();
        }
        if (!string.IsNullOrWhiteSpace(spec.Arguments))
        {
            return $"{spec.FileName} {spec.Arguments}".Trim();
        }
        return spec.FileName;
    }

    private static string ResolveWorkingDirectory(ProcessStartSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.WorkingDirectory))
        {
            return spec.WorkingDirectory;
        }
        return Environment.CurrentDirectory;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private sealed class TelemetryProcessStreamSession : IProcessStreamSession
    {
        private readonly IProcessStreamSession _innerSession;
        private readonly ShellCommandRecord _record;
        private readonly IOperationsTelemetryService _telemetryService;
        private readonly Stopwatch _sw;
        private readonly StringBuilder _outBuilder = new();
        private readonly StringBuilder _errBuilder = new();
        private bool _completed;
        private bool _wasKilled;

        public TelemetryProcessStreamSession(
            IProcessStreamSession innerSession,
            ShellCommandRecord record,
            IOperationsTelemetryService telemetryService,
            Stopwatch sw)
        {
            _innerSession = innerSession;
            _record = record;
            _telemetryService = telemetryService;
            _sw = sw;
        }

        public int? ExitCode => _innerSession.ExitCode;
        public bool HasExited => _innerSession.HasExited;
        public StreamWriter StandardInput => _innerSession.StandardInput;
        public StreamReader StandardOutput => _innerSession.StandardOutput;
        public StreamReader StandardError => _innerSession.StandardError;

        public async IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var chunk in _innerSession.ReadAllAsync(cancellationToken))
                {
                    if (chunk.Kind == ProcessStreamKind.StandardOutput)
                    {
                        if (_outBuilder.Length < 10_000) _outBuilder.Append(chunk.Text);
                        _telemetryService.UpdateShellCommandOutput(_record.CommandId, chunk.Text, false);
                    }
                    else if (chunk.Kind == ProcessStreamKind.StandardError)
                    {
                        if (_errBuilder.Length < 10_000) _errBuilder.Append(chunk.Text);
                        _telemetryService.UpdateShellCommandOutput(_record.CommandId, chunk.Text, true);
                    }

                    yield return chunk;
                }
            }
            finally
            {
                FinishTelemetry();
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _innerSession.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                FinishTelemetry();
            }
        }

        public void Kill()
        {
            _wasKilled = true;
            try
            {
                _innerSession.Kill();
            }
            finally
            {
                FinishTelemetry();
            }
        }

        public void Dispose()
        {
            try
            {
                _innerSession.Dispose();
            }
            finally
            {
                FinishTelemetry();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _innerSession.DisposeAsync();
            }
            finally
            {
                FinishTelemetry();
            }
        }

        private void FinishTelemetry()
        {
            if (_completed) return;
            _completed = true;
            _sw.Stop();

            var innerCode = _innerSession.ExitCode;
            int exitCode;
            ShellCommandStatus status;

            if (_wasKilled)
            {
                exitCode = innerCode ?? -1;
                status = ShellCommandStatus.Failed;
            }
            else if (innerCode.HasValue)
            {
                exitCode = innerCode.Value;
                status = exitCode == 0 ? ShellCommandStatus.Completed : ShellCommandStatus.Failed;
            }
            else
            {
                // Process did not exit normally (abrupt close / disposed)
                exitCode = -1;
                status = ShellCommandStatus.Failed;
            }

            var completedRecord = _record with
            {
                DurationMs = _sw.Elapsed.TotalMilliseconds,
                ExitCode = exitCode,
                OutputPreview = Truncate(_outBuilder.ToString(), 2_000),
                ErrorPreview = Truncate(_errBuilder.ToString(), 2_000),
                Status = status
            };

            _telemetryService.RecordShellCommandCompleted(completedRecord);
        }
    }
}
