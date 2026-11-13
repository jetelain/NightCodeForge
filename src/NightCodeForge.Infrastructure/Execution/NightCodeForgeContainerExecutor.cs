using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Represents a Docker volume bind mount mapping a host directory to a container path.
/// </summary>
public class ContainerVolumeMount
{
    public string HostPath { get; set; } = string.Empty;
    public string ContainerPath { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }

    public ContainerVolumeMount() { }

    public ContainerVolumeMount(string hostPath, string containerPath, bool isReadOnly = false)
    {
        HostPath = hostPath;
        ContainerPath = containerPath;
        IsReadOnly = isReadOnly;
    }

    public string ToDockerArgument() =>
        IsReadOnly ? $"-v \"{HostPath}:{ContainerPath}:ro\"" : $"-v \"{HostPath}:{ContainerPath}\"";

    public override string ToString() =>
        IsReadOnly ? $"{HostPath}:{ContainerPath}:ro" : $"{HostPath}:{ContainerPath}";
}

public class ContainerExecutorOptions
{
    public const string DefaultAgentBaseImage = "ncf-agent-base:v4";
    public const string AlternateAgentBaseImage = "nightcodeforge-agent:v4";
    public const string ContainerHomePath = "/ncf-home";
    public const string HostDotnetToolsContainerPath = "/opt/ncf-dotnet-tools";

    public const string DefaultAgentBaseDockerfile = """
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ripgrep \
    curl \
    ca-certificates \
    jq \
    procps \
    build-essential \
    unzip \
    tar \
    gzip \
    python3 \
    python3-venv \
    python3-pip \
    nodejs \
    npm \
    locales \
    libicu-dev \
    libunwind8 \
    fontconfig \
    fonts-liberation \
    fonts-dejavu-core \
    && locale-gen en_US.UTF-8 fr_FR.UTF-8 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /cache/nuget /cache/npm /cache/pnpm /cache/uv /cache/m2 /cache/ms-playwright /ncf-home/.dotnet /ncf-home/.local/bin /opt/ncf-dotnet-tools \
    && chmod -R 777 /cache /ncf-home

ENV HOME=/ncf-home
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV LANG=en_US.UTF-8
ENV LANGUAGE=en_US:en
ENV LC_ALL=en_US.UTF-8
ENV NUGET_PACKAGES=/cache/nuget
ENV npm_config_cache=/cache/npm
ENV UV_CACHE_DIR=/cache/uv
ENV PLAYWRIGHT_BROWSERS_PATH=/cache/ms-playwright
""";

    public const string DefaultSharedMemorySize = "2g";
    public static readonly IReadOnlyList<string> DefaultProcessKillWhitelist = ["csnav"];
    public const int DefaultPidsLimit = 4096;
    public const int DefaultIdleContainerTimeoutMinutes = 30;
    public const string RestrictedReadOnlyUserUidGid = "1000:1000";

    public string ContainerNameOrId { get; set; } = "ncf-sandbox";
    public string? ContainerImage { get; set; } = DefaultAgentBaseImage;
    public string ShellExecutable { get; set; } = "docker";
    public string DefaultWorkingDirectory { get; set; } = "/workspace";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int MaxBufferBytes { get; set; } = HeadTailBuffer.DefaultMaxBytes;
    public bool IsRestrictedReadOnly { get; set; } = false;
    public string? SharedMemorySize { get; set; } = DefaultSharedMemorySize;
    public Func<Process>? CustomProcessFactory { get; set; }
    public IList<string> ProcessKillWhitelist { get; set; } = new List<string>(DefaultProcessKillWhitelist);
    public int PidsLimit { get; set; } = DefaultPidsLimit;
    public int IdleContainerTimeoutMinutes { get; set; } = DefaultIdleContainerTimeoutMinutes;

    /// <summary>
    /// Grace window in milliseconds to wait for sentinel output after sending <c>\x03</c> (<c>SIGINT</c>) on cancellation.
    /// </summary>
    public int SigIntDrainTimeoutMs { get; set; } = 200;

    /// <summary>
    /// Optional declarative sandbox scope descriptor backing this container environment.
    /// </summary>
    public SandboxScopeDescriptor? Scope { get; set; }

    /// <summary>
    /// Optional declarative mount table specifying virtual subpaths and their backing host/WSL directories.
    /// </summary>
    public SandboxMountTable? MountTable { get; set; }

    /// <summary>
    /// Target container user identity formatted as 'uid:gid' (e.g. '1000:1000').
    /// When null or empty, NightCodeForgeContainerExecutor discovers and captures the host/WSL identity once.
    /// </summary>
    public string? UserUidGid { get; set; }

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

    /// <summary>
    /// Optional targeted WSL2 user name (used for path expansion of ~ in WSL2).
    /// </summary>
    public string? WslUserName { get; set; }

    /// <summary>
    /// Host path to the workspace clone (~/.nightcodeforge/wks/{workspaceId}) mapped to /workspace.
    /// </summary>
    public string? HostWorkspacePath { get; set; }

    /// <summary>
    /// Host path to the canonical clone (~/.nightcodeforge/wks/{workspaceId}/clone) mapped to /clone.
    /// </summary>
    public string? CanonicalClonePath { get; set; }

    /// <summary>
    /// Virtual container path where the workspace is mounted (defaults to "/workspace").
    /// </summary>
    public string ContainerWorkspacePath { get; set; } = "/workspace";

    /// <summary>
    /// If true, host package manager caches (NuGet, npm, pnpm, uv, Maven) are bind-mounted into the container.
    /// </summary>
    public bool MountPackageCaches { get; set; } = true;

    /// <summary>
    /// Environment variables pointing toolchains at the neutral cache mount points (/cache/*).
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> PackageCacheEnvironment =
    [
        new("NUGET_PACKAGES", "/cache/nuget"),
        new("npm_config_cache", "/cache/npm"),
        new("npm_config_store_dir", "/cache/pnpm"),
        new("UV_CACHE_DIR", "/cache/uv"),
        new("MAVEN_OPTS", "-Dmaven.repo.local=/cache/m2"),
        new("PLAYWRIGHT_BROWSERS_PATH", "/cache/ms-playwright"),
    ];

    public string? NugetPackageCachePath { get; set; } = "~/.nuget/packages";
    public string? NpmCachePath { get; set; } = "~/.npm";
    public string? PnpmCachePath { get; set; } = "~/.local/share/pnpm/store";
    public string? UvCachePath { get; set; } = "~/.cache/uv";
    public string? MavenCachePath { get; set; } = "~/.m2/repository";
    public string? PlaywrightCachePath { get; set; } = "~/.cache/ms-playwright";

    public bool MountHostSdks { get; set; } = true;

    public static readonly IReadOnlyList<string> DefaultSdkMountPaths = new[]
    {
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/jvm",
        "/opt/android-sdk",
        "/etc/ssl/certs",
        "/etc/fonts",
        "/usr/share/fonts"
    };

    public IList<string> SdkMountPaths { get; set; } = new List<string>(DefaultSdkMountPaths);
    public IList<ContainerVolumeMount> AdditionalVolumeMounts { get; set; } = new List<ContainerVolumeMount>();
    public IList<string> ExtraPathDirectories { get; set; } = new List<string>();
    public bool ExpandTildeInMounts { get; set; } = false;
    public bool AutoStartContainer { get; set; } = false;
    public bool ProtectCloneWorkingTree { get; set; } = true;
}

/// <summary>
/// Persistent REPL execution environment inside a container or interactive shell using cryptographic sentinels.
/// </summary>
public class NightCodeForgeContainerExecutor : IExecutionEnvironment, IAsyncDisposable, IDisposable
{
    public const int TimeoutExitCode = 124;
    private const string ProbeSentinel = "__NCF_PROBE_OK__";
    private const int LivenessProbeTimeoutSeconds = 5;
    private const int ProcessReaperTimeoutSeconds = 10;
    private const int ContainerRemoveTimeoutSeconds = 10;
    private const int DockerInfoTimeoutSeconds = 10;
    private const int WslShutdownTimeoutSeconds = 120;
    private const int DockerDaemonStartTimeoutSeconds = 15;
    private const int WslRecoveryProbeTimeoutSeconds = 30;

    private readonly ContainerExecutorOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<NightCodeForgeContainerExecutor> _logger;
    private readonly IHostUserIdentityResolver _userIdentityResolver;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly PersistentContainerShellSession _shellSession;
    private readonly SemaphoreSlim _execLock = new(1, 1);
    private bool _isExecuting;
    private bool _isDisposed;

    public ContainerExecutorOptions Options => _options;
    public bool IsExecuting => _isExecuting;
    public DateTime LastExecutionTimestampUtc { get; private set; } = DateTime.UtcNow;
    public PersistentContainerShellSession ShellSession => _shellSession;

    public ExecutionTier Tier => ExecutionTier.ContainerSandbox;

    [ActivatorUtilitiesConstructor]
    public NightCodeForgeContainerExecutor(
        IOptions<ContainerExecutorOptions> options,
        IProcessRunner processRunner,
        ILogger<NightCodeForgeContainerExecutor> logger,
        IHostUserIdentityResolver userIdentityResolver,
        IOperationsTelemetryService telemetryService)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), processRunner, logger, userIdentityResolver, telemetryService)
    {
    }

    public NightCodeForgeContainerExecutor(
        ContainerExecutorOptions options,
        IProcessRunner processRunner,
        ILogger<NightCodeForgeContainerExecutor> logger,
        IHostUserIdentityResolver userIdentityResolver,
        IOperationsTelemetryService telemetryService)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userIdentityResolver = userIdentityResolver ?? throw new ArgumentNullException(nameof(userIdentityResolver));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _shellSession = new PersistentContainerShellSession(_options, _processRunner, _telemetryService, _logger);

        if (string.IsNullOrWhiteSpace(_options.UserUidGid))
        {
            _options.UserUidGid = _userIdentityResolver.ResolveUserIdentity(_options);
        }
    }

    public async Task<CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        await _execLock.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        ShellCommandRecord? runningRecord = null;
        try
        {
            _isExecuting = true;
            var buffer = new HeadTailBuffer(_options.MaxBufferBytes);
            EnsureShellProcessRunning();

            var timeout = timeoutSeconds ?? _options.DefaultTimeoutSeconds;
            var sentinel = $"__NCF_SENTINEL_{Guid.NewGuid():N}__";
            var workDir = ResolveContainerWorkingDirectory(workingDirectory, _options);

            await _shellSession.WriteEnvironmentPreambleAsync(environmentVariables);

            var cleanCommand = command.Replace("\r", "").Trim();
            var commandId = Guid.NewGuid().ToString("N");
            runningRecord = new ShellCommandRecord(
                CommandId: commandId,
                CommandLine: cleanCommand,
                WorkingDirectory: workDir,
                StartTime: DateTime.UtcNow,
                DurationMs: 0,
                ExitCode: null,
                Status: ShellCommandStatus.Running,
                OutputPreview: null,
                ErrorPreview: null);

            _telemetryService.RecordShellCommandStarted(runningRecord);

            try
            {
                var exitCode = await _shellSession.ExecuteFramedCommandAsync(
                    workDir,
                    cleanCommand,
                    sentinel,
                    timeout,
                    commandId,
                    buffer,
                    stopwatch,
                    cancellationToken);

                stopwatch.Stop();
                var completedRecord = runningRecord with
                {
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    ExitCode = exitCode,
                    OutputPreview = buffer.GetFormattedString(),
                    Status = exitCode == 0 ? ShellCommandStatus.Completed : ShellCommandStatus.Failed
                };
                _telemetryService.RecordShellCommandCompleted(completedRecord);
                return new CommandResult(exitCode, buffer.GetFormattedString(), string.Empty, stopwatch.Elapsed, buffer.IsTruncated);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Command execution timed out after {Timeout}s. Terminating shell session and executing escalation ladder.", timeout);
                _shellSession.Terminate();

                var stage = await RunTimeoutEscalationLadderAsync();
                stopwatch.Stop();

                var errorMessage = stage switch
                {
                    ContainerRecoveryStage.ProcessSweepSucceeded => $"Command timed out after {timeout}s (in-container processes reaped).",
                    ContainerRecoveryStage.ContainerRecreated => $"Command timed out after {timeout}s (unresponsive container was recycled).",
                    ContainerRecoveryStage.WslShutdownRecovered => $"Command timed out after {timeout}s (WSL2 environment was restarted).",
                    _ => $"Command timed out after {timeout}s and recovery failed."
                };

                var timeoutRecord = runningRecord with
                {
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    ExitCode = TimeoutExitCode,
                    OutputPreview = buffer.GetFormattedString(),
                    ErrorPreview = errorMessage,
                    Status = ShellCommandStatus.Failed,
                    RecoveryStage = stage
                };
                _telemetryService.RecordShellCommandCompleted(timeoutRecord);
                return new CommandResult(TimeoutExitCode, buffer.GetFormattedString(), errorMessage, stopwatch.Elapsed, buffer.IsTruncated);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (_shellSession.WasDrainedOnLastCancel)
                {
                    _logger.LogInformation("Command execution was canceled by caller and cleanly drained via SIGINT.");
                }
                else
                {
                    _logger.LogInformation("Command execution was canceled by caller while in-flight. Terminating shell session and sweeping processes.");
                    _shellSession.Terminate();

                    try
                    {
                        await RunInContainerProcessSweepAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "In-container process sweep on cancellation encountered an exception");
                    }
                }

                stopwatch.Stop();
                var cancelledRecord = runningRecord with
                {
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    ExitCode = _shellSession.WasDrainedOnLastCancel ? PersistentContainerShellSession.SigIntExitCode : -1,
                    OutputPreview = buffer.GetFormattedString(),
                    ErrorPreview = "Command execution was canceled by caller.",
                    Status = ShellCommandStatus.Failed
                };
                _telemetryService.RecordShellCommandCompleted(cancelledRecord);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Container shell session encountered an error during command execution; terminating session so next command starts cleanly.");
                _shellSession.Terminate();
                stopwatch.Stop();
                var failedRecord = runningRecord with
                {
                    DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    ExitCode = -1,
                    OutputPreview = buffer.GetFormattedString(),
                    ErrorPreview = ex.Message,
                    Status = ShellCommandStatus.Failed
                };
                _telemetryService.RecordShellCommandCompleted(failedRecord);
                throw;
            }
        }
        catch (Exception ex) when (runningRecord == null)
        {
            _shellSession.Terminate();
            stopwatch.Stop();
            _logger.LogError(ex, "Failed before command could be sent to container shell process");
            throw;
        }
        finally
        {
            _isExecuting = false;
            LastExecutionTimestampUtc = DateTime.UtcNow;
            _execLock.Release();
        }
    }

    private async Task RunInContainerProcessSweepAsync()
    {
        try
        {
            var reaperScript = BuildInContainerReaperScript(_options.ProcessKillWhitelist);
            var effectiveUser = _options.IsRestrictedReadOnly ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid : _options.UserUidGid;
            var dockerArgs = new List<string> { "exec" };
            if (!string.IsNullOrWhiteSpace(effectiveUser))
            {
                dockerArgs.AddRange(["--user", effectiveUser]);
            }
            dockerArgs.AddRange([_options.ContainerNameOrId, "bash", "-c", reaperScript]);
            var reaperSpec = BuildDockerProcessSpec(_options, dockerArgs, timeoutSeconds: ProcessReaperTimeoutSeconds);
            await _processRunner.RunAsync(reaperSpec, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "In-container process reaper encountered an exception for container {Container}", _options.ContainerNameOrId);
        }
    }

    private async Task<bool> RunLivenessProbeAsync(int timeoutSeconds)
    {
        try
        {
            var effectiveUser = _options.IsRestrictedReadOnly ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid : _options.UserUidGid;
            var dockerArgs = new List<string> { "exec" };
            if (!string.IsNullOrWhiteSpace(effectiveUser))
            {
                dockerArgs.AddRange(["--user", effectiveUser]);
            }
            dockerArgs.AddRange([_options.ContainerNameOrId, "echo", ProbeSentinel]);
            var probeSpec = BuildDockerProcessSpec(_options, dockerArgs, timeoutSeconds: timeoutSeconds);
            var res = await _processRunner.RunAsync(probeSpec, CancellationToken.None);
            return res.ExitCode == 0 && res.StandardOutput.Contains(ProbeSentinel);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container liveness probe failed for container {Container}", _options.ContainerNameOrId);
            return false;
        }
    }

    private void ForceRecycleContainer()
    {
        if (_options.CustomProcessFactory != null)
        {
            return;
        }

        _logger.LogWarning("Container {Container} is unresponsive after timeout and is being recycled.", _options.ContainerNameOrId);
        try
        {
            var rmSpec = BuildDockerProcessSpec(_options, ["rm", "-f", _options.ContainerNameOrId], timeoutSeconds: ContainerRemoveTimeoutSeconds);
            _processRunner.Run(rmSpec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove wedged container {Container}", _options.ContainerNameOrId);
        }

        var prevAutoStart = _options.AutoStartContainer;
        try
        {
            _options.AutoStartContainer = true;
            EnsureContainerRunning();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recreate container {Container} during recovery", _options.ContainerNameOrId);
        }
        finally
        {
            _options.AutoStartContainer = prevAutoStart;
        }
    }

    private async Task<ContainerRecoveryStage> ExecuteWslRecoveryAsync()
    {
        _logger.LogWarning("Container probe and recycling failed. Triggering full WSL2 reset for container {Container}", _options.ContainerNameOrId);
        try
        {
            var wslShutdownSpec = new ProcessStartSpec
            {
                FileName = _options.WslExecutable,
                ArgumentList = ["--shutdown"],
                TimeoutSeconds = WslShutdownTimeoutSeconds
            };
            await _processRunner.RunAsync(wslShutdownSpec, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "wsl --shutdown failed during Step 3 recovery");
        }

        try
        {
            var dockerInfoSpec = BuildDockerProcessSpec(_options, ["info"], timeoutSeconds: DockerInfoTimeoutSeconds);
            var dockerInfoResult = await _processRunner.RunAsync(dockerInfoSpec, CancellationToken.None);
            if (dockerInfoResult.ExitCode != 0)
            {
                var daemonStartArgs = new List<string>();
                if (!string.IsNullOrWhiteSpace(_options.WslDistribution))
                {
                    daemonStartArgs.AddRange(["-d", _options.WslDistribution.Trim()]);
                }
                daemonStartArgs.AddRange(["-u", "root", "--", "sh", "-c", "service docker start || systemctl start docker || true"]);
                var daemonStartSpec = new ProcessStartSpec
                {
                    FileName = _options.WslExecutable,
                    ArgumentList = daemonStartArgs,
                    TimeoutSeconds = DockerDaemonStartTimeoutSeconds
                };
                await _processRunner.RunAsync(daemonStartSpec, CancellationToken.None);
            }

            ForceRecycleContainer();

            var probe30 = await RunLivenessProbeAsync(timeoutSeconds: WslRecoveryProbeTimeoutSeconds);
            if (probe30 && _shellSession.TryStart())
            {
                return ContainerRecoveryStage.WslShutdownRecovered;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Step 3 WSL recovery failed for container {Container}", _options.ContainerNameOrId);
        }

        return ContainerRecoveryStage.RecoveryFailed;
    }

    private async Task<ContainerRecoveryStage> RunTimeoutEscalationLadderAsync()
    {
        await RunInContainerProcessSweepAsync();

        if (await RunLivenessProbeAsync(timeoutSeconds: LivenessProbeTimeoutSeconds) && _shellSession.TryStart())
        {
            return ContainerRecoveryStage.ProcessSweepSucceeded;
        }

        ForceRecycleContainer();

        if (await RunLivenessProbeAsync(timeoutSeconds: LivenessProbeTimeoutSeconds) && _shellSession.TryStart())
        {
            return ContainerRecoveryStage.ContainerRecreated;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _options.UseWsl)
        {
            return await ExecuteWslRecoveryAsync();
        }

        _logger.LogError("Critical recovery failure: container {Container} remains unresponsive.", _options.ContainerNameOrId);
        return ContainerRecoveryStage.RecoveryFailed;
    }

    private void EnsureShellProcessRunning()
    {
        if (_shellSession.IsRunning)
        {
            return;
        }

        EnsureContainerRunning();
        _shellSession.Start();
    }

    /// <summary>
    /// Checks if the container is running and spawns/starts it if necessary using configured volume mappings.
    /// </summary>
    public void EnsureContainerRunning()
    {
        if (!_options.AutoStartContainer || _options.CustomProcessFactory != null)
        {
            return;
        }

        try
        {
            EnsureImageReady();

            var inspectSpec = BuildDockerProcessSpec(
                _options,
                ["inspect", "-f", "{{.State.Running}}", _options.ContainerNameOrId],
                timeoutSeconds: 5);

            var inspectResult = _processRunner.Run(inspectSpec);
            var stdout = inspectResult.StandardOutput.Trim();
            if (inspectResult.ExitCode == 0)
            {
                var isRunning = stdout.Equals("true", StringComparison.OrdinalIgnoreCase);

                var drift = DetectContainerDrift();
                if (drift != null)
                {
                    if (drift.StartsWith("Missing/Mismatched mounts:", StringComparison.OrdinalIgnoreCase))
                    {
                        var missingMounts = drift["Missing/Mismatched mounts:".Length..].Trim();
                        _logger.LogWarning("Recreating container {Container} due to mount drift. Missing/Mismatched: {MissingMounts}",
                            _options.ContainerNameOrId, missingMounts);
                    }
                    else if (drift.StartsWith("Image mismatch:", StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatch = drift["Image mismatch:".Length..].Trim();
                        _logger.LogWarning("Recreating container {Container} due to image drift. Mismatch: {Mismatch}",
                            _options.ContainerNameOrId, mismatch);
                    }
                    else if (drift.StartsWith("User mismatch:", StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatch = drift["User mismatch:".Length..].Trim();
                        _logger.LogWarning("Recreating container {Container} due to user drift. Mismatch: {Mismatch}",
                            _options.ContainerNameOrId, mismatch);
                    }
                    else
                    {
                        _logger.LogWarning("Recreating container {Container} due to configuration drift. {Drift}",
                            _options.ContainerNameOrId, drift);
                    }

                    _shellSession.Terminate();

                    var rmSpec = BuildDockerProcessSpec(_options, ["rm", "-f", _options.ContainerNameOrId], timeoutSeconds: 10);
                    _processRunner.Run(rmSpec);
                }
                else if (isRunning)
                {
                    return;
                }
                else
                {
                    _logger.LogInformation("Starting stopped container {Container}", _options.ContainerNameOrId);
                    var startSpec = BuildDockerProcessSpec(_options, ["start", _options.ContainerNameOrId], timeoutSeconds: 15);
                    var startResult = _processRunner.Run(startSpec);
                    if (startResult.ExitCode != 0)
                    {
                        _logger.LogError("Failed to start stopped container {Container} (exit code {ExitCode}): {Error}",
                            _options.ContainerNameOrId, startResult.ExitCode, startResult.StandardError);
                        throw new InvalidOperationException($"Failed to start container '{_options.ContainerNameOrId}': {startResult.StandardError}");
                    }
                    return;
                }
            }

            _logger.LogInformation("Spawning container {Container} with volume mounts", _options.ContainerNameOrId);
            var runSpec = BuildDockerRunProcessSpec(_options);
            var runResult = _processRunner.Run(runSpec);
            if (runResult.ExitCode != 0)
            {
                if (runResult.ExitCode == 125 ||
                    (runResult.StandardError?.Contains("Conflict", StringComparison.OrdinalIgnoreCase) == true &&
                     runResult.StandardError?.Contains("already in use", StringComparison.OrdinalIgnoreCase) == true))
                {
                    _logger.LogWarning("Container {Container} name conflict detected during spawn; removing stale container and retrying...", _options.ContainerNameOrId);
                    var rmSpec = BuildDockerProcessSpec(_options, ["rm", "-f", _options.ContainerNameOrId], timeoutSeconds: 10);
                    _processRunner.Run(rmSpec);
                    runResult = _processRunner.Run(runSpec);
                }

                if (runResult.ExitCode != 0)
                {
                    _logger.LogError("Failed to spawn container {Container} (exit code {ExitCode}): {Error}",
                        _options.ContainerNameOrId, runResult.ExitCode, runResult.StandardError);
                    throw new InvalidOperationException(
                        $"Failed to spawn container '{_options.ContainerNameOrId}' (exit code {runResult.ExitCode}): {runResult.StandardError}");
                }
            }
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement Run - ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure container {Container} is running.", _options.ContainerNameOrId);
            throw;
        }
    }

    public string? DetectContainerDrift() =>
        ContainerMountAndDriftBuilder.DetectContainerDrift(_processRunner, _options);

    public Task<string?> DetectContainerDriftAsync(CancellationToken cancellationToken = default) =>
        ContainerMountAndDriftBuilder.DetectContainerDriftAsync(_processRunner, _options, cancellationToken);

    public void EnsureImageReady() =>
        ContainerMountAndDriftBuilder.EnsureImageReady(_processRunner, _options, _logger);

    public static bool IsImageFresh(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        TimeSpan maxAge) =>
        ContainerMountAndDriftBuilder.IsImageFresh(processRunner, imageName, options, maxAge);

    public static void BuildLocalAgentBaseImage(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        ILogger logger) =>
        ContainerMountAndDriftBuilder.BuildLocalAgentBaseImage(processRunner, imageName, options, logger);

    public static string BuildInContainerReaperScript(IEnumerable<string>? processWhitelist = null) =>
        PersistentContainerShellSession.BuildInContainerReaperScript(processWhitelist);

    public static ProcessStartSpec BuildProcessStartSpec(ContainerExecutorOptions options) =>
        PersistentContainerShellSession.BuildProcessStartSpec(options);

    public static ProcessStartInfo BuildProcessStartInfo(ContainerExecutorOptions options) =>
        PersistentContainerShellSession.BuildProcessStartInfo(options);

    public static ProcessStartSpec BuildDockerProcessSpec(
        ContainerExecutorOptions options,
        IEnumerable<string> dockerArgs,
        int timeoutSeconds = 10,
        string? workingDirectory = null) =>
        ContainerMountAndDriftBuilder.BuildDockerProcessSpec(options, dockerArgs, timeoutSeconds, workingDirectory);

    public static (string Executable, string Arguments) BuildDockerCommand(
        string dockerCommand,
        bool useWsl = false,
        string? wslDistribution = null,
        string shellExecutable = "docker",
        string wslExecutable = "wsl.exe") =>
        ContainerMountAndDriftBuilder.BuildDockerCommand(dockerCommand, useWsl, wslDistribution, shellExecutable, wslExecutable);

    public static IReadOnlyList<ContainerVolumeMount> BuildVolumeMounts(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

    public static string ResolveHostPath(string path, ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.ResolveHostPath(path, options);

    public static string ResolveContainerWorkingDirectory(string? workingDirectory, ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.ResolveContainerWorkingDirectory(workingDirectory, options);

    public static string ComputePathEnvironmentVariable(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options);

    public static List<string> BuildDockerRunArgs(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);

    public static ProcessStartSpec BuildDockerRunProcessSpec(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.BuildDockerRunProcessSpec(options);

    public static (string Executable, string Arguments) BuildDockerRunCommand(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.BuildDockerRunCommand(options);

    public static ProcessStartInfo BuildDockerRunProcessStartInfo(ContainerExecutorOptions options) =>
        ContainerMountAndDriftBuilder.BuildDockerRunProcessStartInfo(options);

    public void ForceRestartShellProcess() => _shellSession.ForceRestart();

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _shellSession.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception while disposing container executor");
        }
        finally
        {
            _execLock.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
