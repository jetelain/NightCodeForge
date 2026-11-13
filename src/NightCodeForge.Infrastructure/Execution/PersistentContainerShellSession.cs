using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Manages a persistent interactive <c>docker exec -i</c> bash stream session,
/// sentinel command framing, <see cref="HeadTailBuffer"/> output capture, and
/// <c>\x03</c> (<c>SIGINT</c>) cancellation sentinel drain recovery.
/// </summary>
public sealed partial class PersistentContainerShellSession : IDisposable
{
    [GeneratedRegex(@"^\s*(-?\d+)")]
    private static partial Regex SentinelExitCodeRegex();

    [GeneratedRegex(@"^(?:\x03|\^C)?\s*__NCF_SENTINEL_[0-9a-fA-F]{32}__\s+-?\d+\s*$")]
    private static partial Regex StaleSentinelLineRegex();

    public const int SigIntExitCode = 130;

    private readonly ContainerExecutorOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger _logger;

    private IProcessStreamSession? _shellSession;
    private Process? _shellProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public PersistentContainerShellSession(
        ContainerExecutorOptions options,
        IProcessRunner processRunner,
        IOperationsTelemetryService telemetryService,
        ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Indicates whether an underlying shell process or stream session is currently active.
    /// </summary>
    public bool IsRunning =>
        (_shellSession != null && !_shellSession.HasExited) ||
        (_shellProcess != null && !_shellProcess.HasExited);

    /// <summary>
    /// Indicates whether the most recent cancellation cleanly drained output up to the sentinel via <c>SIGINT</c>.
    /// </summary>
    public bool WasDrainedOnLastCancel { get; private set; }

    /// <summary>
    /// Holds the partial command result (exit code 130) captured when a command was cancelled via <c>SIGINT</c>.
    /// </summary>
    public CommandResult? LastCancelledCommandResult { get; private set; }

    public void Start()
    {
        if (_options.CustomProcessFactory != null)
        {
            _shellProcess = _options.CustomProcessFactory();
            _shellProcess.Start();
            _stdin = new StreamWriter(_shellProcess.StandardInput.BaseStream, new System.Text.UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _stdout = _shellProcess.StandardOutput;
            return;
        }

        var spec = BuildProcessStartSpec(_options);
        _shellSession = _processRunner.StartSession(spec);
        _stdin = _shellSession.StandardInput;
        _stdin.NewLine = "\n";
        _stdout = _shellSession.StandardOutput;
    }

    public bool TryStart()
    {
        try
        {
            Start();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start interactive shell session for container {Container}", _options.ContainerNameOrId);
            return false;
        }
    }

    public void ForceRestart()
    {
        Terminate();
        Start();
    }

    public void Terminate()
    {
        try
        {
            if (_shellSession != null && !_shellSession.HasExited)
            {
                _shellSession.Kill();
            }
            else if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while terminating shell session");
        }
        finally
        {
            _shellSession?.Dispose();
            _shellSession = null;
            _shellProcess?.Dispose();
            _shellProcess = null;
            _stdin = null;
            _stdout = null;
        }
    }

    public void SendExitAndTerminate()
    {
        try
        {
            _stdin?.WriteLine("exit");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort exit command failed while closing shell session");
        }

        Terminate();
    }

    public async Task WriteEnvironmentPreambleAsync(IReadOnlyDictionary<string, string>? environmentVariables)
    {
        if (_stdin == null)
        {
            throw new InvalidOperationException("Container shell process stdin is not available.");
        }

        var computedPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(_options);
        var hasCustomPath = environmentVariables != null && environmentVariables.ContainsKey("PATH");
        if (!hasCustomPath)
        {
            await _stdin.WriteLineAsync($"export PATH=\"{computedPath}\"");
        }

        await _stdin.WriteLineAsync("export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0");
        await _stdin.WriteLineAsync("export LANG=en_US.UTF-8");
        await _stdin.WriteLineAsync("export LC_ALL=en_US.UTF-8");
        await _stdin.WriteLineAsync($"mkdir -p {ContainerExecutorOptions.ContainerHomePath}/.dotnet 2>/dev/null || true");
        await _stdin.WriteLineAsync($"if [ ! -w {ContainerExecutorOptions.ContainerHomePath} ] || [ ! -w {ContainerExecutorOptions.ContainerHomePath}/.dotnet ]; then export HOME=\"/tmp/ncf-home-$(id -u)\"; mkdir -p \"$HOME/.dotnet\" 2>/dev/null || true; else export HOME=\"{ContainerExecutorOptions.ContainerHomePath}\"; fi");
        await _stdin.WriteLineAsync("git config --global --add safe.directory '*' 2>/dev/null || true");

        if (_options.MountPackageCaches)
        {
            foreach (var (name, value) in ContainerExecutorOptions.PackageCacheEnvironment)
            {
                await _stdin.WriteLineAsync($"export {name}=\"{value}\"");
            }
        }

        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            foreach (var (k, v) in environmentVariables)
            {
                if (!ShellQuoting.IsValidVariableName(k))
                {
                    _logger.LogWarning("Skipping environment variable with invalid name '{Key}'", k);
                    continue;
                }

                if (_options.IsRestrictedReadOnly && IsSensitiveEnvVar(k))
                {
                    continue;
                }

                await _stdin.WriteLineAsync($"export {k}={ShellQuoting.QuotePosix(v)}");
            }
        }

        if (_options.IsRestrictedReadOnly)
        {
            await _stdin.WriteLineAsync("export HTTP_PROXY=\"\" HTTPS_PROXY=\"\" ALL_PROXY=\"\"");
        }
    }

    public async Task<int> ExecuteFramedCommandAsync(
        string workDir,
        string cleanCommand,
        string sentinel,
        int timeoutSeconds,
        string commandId,
        HeadTailBuffer buffer,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (_stdin == null)
        {
            throw new InvalidOperationException("Container shell process stdin is not available.");
        }

        WasDrainedOnLastCancel = false;
        LastCancelledCommandResult = null;

        await _stdin.WriteLineAsync($"cd \"{workDir}\"; ( {cleanCommand}\n) < /dev/null 2>&1");
        await _stdin.WriteLineAsync($"echo \"{sentinel} $?\"");
        await _stdin.FlushAsync();

        return await ReadUntilSentinelAsync(sentinel, timeoutSeconds, commandId, buffer, stopwatch, cancellationToken);
    }

    public async Task<int> ReadUntilSentinelAsync(
        string sentinel,
        int timeoutSeconds,
        string commandId,
        HeadTailBuffer buffer,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (_stdout == null)
        {
            throw new InvalidOperationException("Container shell process stdout is not available.");
        }

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<string?>? pendingReadTask = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                WasDrainedOnLastCancel = await TryDrainOnSigIntAsync(sentinel, commandId, buffer, pendingReadTask);
                LastCancelledCommandResult = new CommandResult(
                    SigIntExitCode,
                    buffer.GetFormattedString(),
                    "Command interrupted via SIGINT.",
                    stopwatch.Elapsed,
                    buffer.IsTruncated);
                throw new OperationCanceledException(cancellationToken);
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}' after {timeoutSeconds}s.");
            }

            pendingReadTask ??= _stdout.ReadLineAsync(CancellationToken.None).AsTask();
            var delayTask = Task.Delay(remaining, linkedCts.Token);

            var completedTask = await Task.WhenAny(pendingReadTask, delayTask);
            if (completedTask == delayTask)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    WasDrainedOnLastCancel = await TryDrainOnSigIntAsync(sentinel, commandId, buffer, pendingReadTask);
                    LastCancelledCommandResult = new CommandResult(
                        SigIntExitCode,
                        buffer.GetFormattedString(),
                        "Command interrupted via SIGINT.",
                        stopwatch.Elapsed,
                        buffer.IsTruncated);
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}' after {timeoutSeconds}s.");
            }

            var line = await pendingReadTask;
            pendingReadTask = null;

            if (line == null)
            {
                throw new InvalidOperationException("Container shell process terminated unexpectedly (stdout closed).");
            }

            if (TryParseSentinelLine(line, sentinel, commandId, buffer, out var exitCode))
            {
                linkedCts.Cancel();
                return exitCode;
            }

            if (StaleSentinelLineRegex().IsMatch(line))
            {
                continue;
            }

            _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", false);
            buffer.Append(line + "\n");
        }
    }

    private async Task<bool> TryDrainOnSigIntAsync(
        string sentinel,
        string commandId,
        HeadTailBuffer buffer,
        Task<string?>? pendingReadTask)
    {
        if (_stdin == null || _stdout == null)
        {
            return false;
        }

        try
        {
            await _stdin.WriteAsync($"\x03\necho \"{sentinel} {SigIntExitCode}\"\n");
            await _stdin.FlushAsync();

            var drainTimeoutMs = Math.Max(50, _options.SigIntDrainTimeoutMs);
            var drainDeadline = DateTime.UtcNow.AddMilliseconds(drainTimeoutMs);

            while (DateTime.UtcNow < drainDeadline)
            {
                var remaining = drainDeadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                pendingReadTask ??= _stdout.ReadLineAsync(CancellationToken.None).AsTask();
                var delayTask = Task.Delay(remaining);
                var completed = await Task.WhenAny(pendingReadTask, delayTask);
                if (completed != pendingReadTask)
                {
                    return false;
                }

                var line = await pendingReadTask;
                pendingReadTask = null;

                if (line == null)
                {
                    return false;
                }

                if (TryParseSentinelLine(line, sentinel, commandId, buffer, out _))
                {
                    return true;
                }

                if (StaleSentinelLineRegex().IsMatch(line))
                {
                    continue;
                }

                _telemetryService.UpdateShellCommandOutput(commandId, line + "\n", false);
                buffer.Append(line + "\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SIGINT drain did not complete cleanly for container {Container}", _options.ContainerNameOrId);
        }

        return false;
    }

    private bool TryParseSentinelLine(
        string line,
        string sentinel,
        string commandId,
        HeadTailBuffer buffer,
        out int exitCode)
    {
        exitCode = 0;
        var sentinelIdx = line.IndexOf(sentinel, StringComparison.Ordinal);
        if (sentinelIdx < 0)
        {
            return false;
        }

        var suffix = line[(sentinelIdx + sentinel.Length)..];
        var match = SentinelExitCodeRegex().Match(suffix);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out exitCode))
        {
            return false;
        }

        var prefix = line[..sentinelIdx];
        if (!string.IsNullOrEmpty(prefix))
        {
            _telemetryService.UpdateShellCommandOutput(commandId, prefix + "\n", false);
            buffer.Append(prefix + "\n");
        }

        return true;
    }

    /// <summary>
    /// Builds the structured ProcessStartSpec with ArgumentList avoiding escaping pitfalls for container or WSL execution.
    /// </summary>
    public static ProcessStartSpec BuildProcessStartSpec(ContainerExecutorOptions options)
    {
        var dockerArgs = new List<string> { "exec", "-i" };
        var effectiveUser = options.IsRestrictedReadOnly
            ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
            : options.UserUidGid;

        if (!string.IsNullOrWhiteSpace(effectiveUser))
        {
            dockerArgs.AddRange(["--user", effectiveUser]);
        }

        dockerArgs.Add(options.ContainerNameOrId);
        dockerArgs.AddRange(["bash", "--noprofile", "--norc"]);

        if (options.UseWsl)
        {
            var wslArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.AddRange(["-d", options.WslDistribution]);
            }

            wslArgs.AddRange(["--", options.ShellExecutable]);
            wslArgs.AddRange(dockerArgs);

            return new ProcessStartSpec
            {
                FileName = options.WslExecutable,
                ArgumentList = wslArgs,
                TimeoutSeconds = options.DefaultTimeoutSeconds
            };
        }

        return new ProcessStartSpec
        {
            FileName = options.ShellExecutable,
            ArgumentList = dockerArgs,
            TimeoutSeconds = options.DefaultTimeoutSeconds
        };
    }

    public static ProcessStartInfo BuildProcessStartInfo(ContainerExecutorOptions options)
    {
        var effectiveUser = options.IsRestrictedReadOnly
            ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
            : options.UserUidGid;
        var userArg = !string.IsNullOrWhiteSpace(effectiveUser) ? $"--user {effectiveUser} " : string.Empty;
        var dockerArgs = $"exec -i {userArg}{options.ContainerNameOrId} bash --noprofile --norc";

        if (options.UseWsl)
        {
            var wslArgs = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.Append($"-d {options.WslDistribution.Trim()} ");
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

    public static string BuildInContainerReaperScript(IEnumerable<string>? processWhitelist = null)
    {
        var whitelistTokens = (processWhitelist ?? ContainerExecutorOptions.DefaultProcessKillWhitelist)
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => ShellQuoting.QuotePosix(w.Trim()))
            .Distinct()
            .ToList();

        var whitelistCheck = whitelistTokens.Count > 0
            ? $$"""
                for w in {{string.Join(" ", whitelistTokens)}}; do
                  if [[ "$comm" == *"$w"* ]] || [[ "$cmdline" == *"$w"* ]]; then
                    whitelisted=1
                    break
                  fi
                done
            """
            : string.Empty;

        return $$"""
get_targets() {
  local targets=()
  for p in /proc/[0-9]*; do
    [ -d "$p" ] || continue
    local pid="${p##*/}"
    [ "$pid" = "1" ] && continue
    [ "$pid" = "$$" ] && continue
    [ "$pid" = "$BASHPID" ] && continue
    [ "$pid" = "$PPID" ] && continue

    local stat_line state
    if read -r stat_line < "$p/stat" 2>/dev/null; then
      state="${stat_line##*) }"
      state="${state%% *}"
      if [ "$state" = "Z" ] || [ "$state" = "X" ]; then
        continue
      fi
    fi

    local comm cmdline
    comm=$(cat "$p/comm" 2>/dev/null || true)
    cmdline=$(tr '\0' ' ' < "$p/cmdline" 2>/dev/null || true)

    if [[ "$comm" == "tail" ]] && [[ "$cmdline" == *"tail"*"/dev/null"* ]]; then
      continue
    fi

    local whitelisted=0
    {{whitelistCheck}}
    [ $whitelisted -eq 1 ] && continue

    targets+=("$pid")
  done
  echo "${targets[@]}"
}

targets=($(get_targets))
if [ ${#targets[@]} -gt 0 ]; then
  kill -15 "${targets[@]}" 2>/dev/null || true
  for i in {1..25}; do
    alive=0
    for pid in "${targets[@]}"; do
      if kill -0 "$pid" 2>/dev/null; then
        if read -r stat_line < "/proc/$pid/stat" 2>/dev/null; then
          state="${stat_line##*) }"
          state="${state%% *}"
          if [ "$state" = "Z" ] || [ "$state" = "X" ]; then
            continue
          fi
        fi
        alive=1
        break
      fi
    done
    [ $alive -eq 0 ] && break
    sleep 0.2
  done

  survivors=($(get_targets))
  if [ ${#survivors[@]} -gt 0 ]; then
    kill -9 "${survivors[@]}" 2>/dev/null || true
  fi
fi
""";
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

    public void Dispose()
    {
        SendExitAndTerminate();
    }
}

