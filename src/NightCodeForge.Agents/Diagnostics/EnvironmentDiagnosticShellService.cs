using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Filtering;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Agents.Diagnostics;

/// <summary>
/// Diagnostic service exposing container/environment inspection, dual-pipeline shell execution
/// (<c>AgentBashTool</c> and <c>RawExecutionEnvironment</c>), telemetry-correlated cancellation output recovery,
/// Environment Doctor probing, and container recreation across workspace, task, and System Nightwatch scopes.
/// </summary>
public sealed class EnvironmentDiagnosticShellService : IEnvironmentDiagnosticShellService
{
    public const string SystemNightwatchWorkspaceName = "System Nightwatch";
    private const string CancelledErrorMessage = "Command execution was canceled by caller.";
    private const string NightwatchLogsDirectoryName = "logs";
    private const string NightwatchSkillsRelativeDir = "skills";
    private const string NightwatchConfigRootDir = ".nightcodeforge";
    private const string NightwatchWksVirtualSource = "~/.nightcodeforge/wks";

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IExecutionEnvironmentProvider _environmentProvider;
    private readonly IContainerEnvironmentFactory _containerEnvironmentFactory;
    private readonly IWorkspacePathResolver _workspacePathResolver;
    private readonly ICliOutputFilterRegistry _cliFilterRegistry;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ExecutionEnvironmentProviderOptions _providerOptions;
    private readonly ILogger<EnvironmentDiagnosticShellService> _logger;

    public EnvironmentDiagnosticShellService(
        IWorkspaceRepository workspaceRepository,
        IExecutionEnvironmentProvider environmentProvider,
        IContainerEnvironmentFactory containerEnvironmentFactory,
        IWorkspacePathResolver workspacePathResolver,
        ICliOutputFilterRegistry cliFilterRegistry,
        IOperationsTelemetryService telemetryService,
        IOptions<ExecutionEnvironmentProviderOptions> providerOptions,
        ILogger<EnvironmentDiagnosticShellService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _containerEnvironmentFactory = containerEnvironmentFactory ?? throw new ArgumentNullException(nameof(containerEnvironmentFactory));
        _workspacePathResolver = workspacePathResolver ?? throw new ArgumentNullException(nameof(workspacePathResolver));
        _cliFilterRegistry = cliFilterRegistry ?? throw new ArgumentNullException(nameof(cliFilterRegistry));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _providerOptions = (providerOptions ?? throw new ArgumentNullException(nameof(providerOptions))).Value
            ?? new ExecutionEnvironmentProviderOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<EnvironmentInspectionSnapshot> InspectEnvironmentAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (scope, workspaceName) = await ResolveTargetScopeAsync(target, cancellationToken);
        var env = await _environmentProvider.GetEnvironmentAsync(scope, cancellationToken);

        return await BuildInspectionSnapshotAsync(scope, workspaceName, env, cancellationToken);
    }

    public async Task<DiagnosticCommandResult> ExecuteCommandAsync(
        EnvironmentDiagnosticTarget target,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var (scope, _) = await ResolveTargetScopeAsync(target, cancellationToken);
        var env = await _environmentProvider.GetEnvironmentAsync(scope, cancellationToken);

        var normalizedInputCommand = command.Replace("\r", string.Empty).Trim();
        var pathResolver = new SandboxPathResolver(scope);
        var translatedCommand = target.Pipeline == DiagnosticExecutionPipeline.AgentBashTool
            ? TryTranslateCommandForCorrelation(pathResolver, command, env.Tier, normalizedInputCommand)
            : normalizedInputCommand;

        var expectedRawWorkDir = ResolveRawWorkingDirectory(target.WorkingDirectoryOverride, scope, env);

        var correlationLock = new object();
        string? trackedCommandId = null;
        ShellCommandRecord? completedRecord = null;
        var stdoutTelemetry = new StringBuilder();
        var stderrTelemetry = new StringBuilder();

        bool MatchesCommand(ShellCommandRecord record)
        {
            var recCmd = (record.CommandLine ?? string.Empty).Replace("\r", string.Empty).Trim();
            if (!string.Equals(recCmd, normalizedInputCommand, StringComparison.Ordinal) &&
                !string.Equals(recCmd, translatedCommand, StringComparison.Ordinal))
            {
                return false;
            }

            if (target.Pipeline == DiagnosticExecutionPipeline.RawExecutionEnvironment &&
                !string.IsNullOrWhiteSpace(expectedRawWorkDir) &&
                !string.IsNullOrWhiteSpace(record.WorkingDirectory) &&
                !string.Equals(record.WorkingDirectory, expectedRawWorkDir, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        void HandleShellCommandChanged(ShellCommandRecord record)
        {
            lock (correlationLock)
            {
                if (trackedCommandId == null)
                {
                    if (record.Status == ShellCommandStatus.Running && MatchesCommand(record))
                    {
                        trackedCommandId = record.CommandId;
                    }
                    return;
                }

                if (string.Equals(record.CommandId, trackedCommandId, StringComparison.Ordinal) &&
                    record.Status != ShellCommandStatus.Running)
                {
                    completedRecord = record;
                }
            }
        }

        void HandleShellCommandOutput(string commandId, string chunk, bool isError)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            lock (correlationLock)
            {
                if (trackedCommandId == null ||
                    !string.Equals(commandId, trackedCommandId, StringComparison.Ordinal))
                {
                    return;
                }

                if (isError)
                {
                    stderrTelemetry.Append(chunk);
                }
                else
                {
                    stdoutTelemetry.Append(chunk);
                }
            }
        }

        var stopwatch = Stopwatch.StartNew();
        _telemetryService.OnShellCommandChanged += HandleShellCommandChanged;
        _telemetryService.OnShellCommandOutput += HandleShellCommandOutput;

        try
        {
            if (target.Pipeline == DiagnosticExecutionPipeline.AgentBashTool)
            {
                var toolScope = new CanonicalToolScope(
                    WorkspaceRoot: scope.VirtualRootPath,
                    WorkspaceId: scope.WorkspaceId,
                    TaskId: scope.TaskId,
                    IsReadOnly: scope.IsReadOnly,
                    SandboxScope: scope,
                    SandboxPathResolver: pathResolver);

                var bashModule = new SandboxBashToolModule(
                    toolScope,
                    pathResolver,
                    env,
                    _cliFilterRegistry,
                    _logger);

                BashToolExecutionResult structured;
                try
                {
                    structured = await bashModule.ExecuteStructuredAsync(
                        command,
                        timeout: target.TimeoutSecondsOverride,
                        workingDirectory: target.WorkingDirectoryOverride,
                        disableFilter: null,
                        cancellationToken: cancellationToken);
                }
                catch (UnauthorizedAccessException ex)
                {
                    stopwatch.Stop();
                    return new DiagnosticCommandResult(
                        CommandLine: command,
                        EffectiveWorkingDirectory: !string.IsNullOrWhiteSpace(target.WorkingDirectoryOverride)
                            ? target.WorkingDirectoryOverride.Trim()
                            : scope.DefaultWorkingDirectory,
                        ExitCode: 1,
                        Output: $"Error: {ex.Message}",
                        Error: ex.Message,
                        Duration: stopwatch.Elapsed,
                        IsTruncated: false,
                        ExecutedAt: DateTimeOffset.UtcNow);
                }

                stopwatch.Stop();
                return new DiagnosticCommandResult(
                    CommandLine: command,
                    EffectiveWorkingDirectory: structured.EffectiveWorkingDirectory,
                    ExitCode: structured.ExitCode,
                    Output: structured.FormattedOutput,
                    Error: structured.Error,
                    Duration: structured.Duration > TimeSpan.Zero ? structured.Duration : stopwatch.Elapsed,
                    IsTruncated: structured.IsTruncated,
                    ExecutedAt: DateTimeOffset.UtcNow);
            }

            var rawResult = await env.ExecuteAsync(
                command,
                workingDirectory: expectedRawWorkDir,
                timeoutSeconds: target.TimeoutSecondsOverride,
                environmentVariables: null,
                cancellationToken: cancellationToken);

            stopwatch.Stop();
            return new DiagnosticCommandResult(
                CommandLine: command,
                EffectiveWorkingDirectory: expectedRawWorkDir,
                ExitCode: rawResult.ExitCode,
                Output: rawResult.StandardOutput,
                Error: rawResult.StandardError,
                Duration: rawResult.Duration > TimeSpan.Zero ? rawResult.Duration : stopwatch.Elapsed,
                IsTruncated: rawResult.Truncated,
                ExecutedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return BuildCancelledResult(
                command,
                expectedRawWorkDir,
                env,
                stopwatch.Elapsed,
                correlationLock,
                () => trackedCommandId != null,
                () => completedRecord,
                stdoutTelemetry,
                stderrTelemetry);
        }
        finally
        {
            _telemetryService.OnShellCommandChanged -= HandleShellCommandChanged;
            _telemetryService.OnShellCommandOutput -= HandleShellCommandOutput;
        }
    }

    public async Task<IReadOnlyList<EnvironmentDoctorCheckItem>> RunEnvironmentDoctorAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (scope, _) = await ResolveTargetScopeAsync(target, cancellationToken);
        var env = await _environmentProvider.GetEnvironmentAsync(scope, cancellationToken);
        var expectedMounts = ResolveConfiguredMounts(scope, env);

        var script = EnvironmentDoctorProbe.BuildScript(scope, expectedMounts);
        var probeTarget = target with
        {
            Pipeline = DiagnosticExecutionPipeline.RawExecutionEnvironment,
            WorkingDirectoryOverride = null
        };

        var result = await ExecuteCommandAsync(probeTarget, script, cancellationToken);
        return EnvironmentDoctorProbe.ParseOutput(result.Output, result.Error, result.ExitCode, scope);
    }

    public async Task RecreateEnvironmentContainerAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (scope, _) = await ResolveTargetScopeAsync(target, cancellationToken);
        await _environmentProvider.RecreateEnvironmentContainerAsync(scope, cancellationToken);
    }

    private async Task<(SandboxScopeDescriptor Scope, string WorkspaceName)> ResolveTargetScopeAsync(
        EnvironmentDiagnosticTarget target,
        CancellationToken cancellationToken)
    {
        if (target.ScopeKindOverride == SandboxScopeKind.SystemNightwatch)
        {
            return (BuildNightwatchScope(), SystemNightwatchWorkspaceName);
        }

        if (target.WorkspaceId is not > 0)
        {
            throw new ArgumentException(
                "A positive WorkspaceId is required unless ScopeKindOverride is SystemNightwatch.",
                nameof(target));
        }

        var workspace = await _workspaceRepository.GetByIdAsync(target.WorkspaceId.Value, cancellationToken)
            ?? throw new KeyNotFoundException($"Workspace #{target.WorkspaceId.Value} was not found.");

        if (target.ScopeKindOverride == SandboxScopeKind.TaskWorktree && target.TaskId is not > 0)
        {
            throw new ArgumentException(
                "A positive TaskId is required when ScopeKindOverride is TaskWorktree.",
                nameof(target));
        }

        var effectiveTaskId = target.ScopeKindOverride == SandboxScopeKind.WorkspaceClone
            ? null
            : (target.TaskId is > 0 ? target.TaskId : null);

        var scope = _containerEnvironmentFactory.BuildWorkspaceScope(workspace, target.SecurityTier, effectiveTaskId);
        return (scope, workspace.Name);
    }

    private SandboxScopeDescriptor BuildNightwatchScope()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hostNcfLogsPath = Path.Combine(AppContext.BaseDirectory, NightwatchLogsDirectoryName);
        var wslNcfLogsPath = WslPathConverter.ToWslPath(hostNcfLogsPath);

        var rawSkillsPath = Path.Combine(userProfile, NightwatchConfigRootDir, NightwatchSkillsRelativeDir);
        var hostSkillsPath = ResolveHostAccessiblePath(rawSkillsPath);
        var wslSkillsPath = WslPathConverter.ToWslPath(rawSkillsPath);

        var resolvedWksHost = _workspacePathResolver.ToHostPath(NightwatchWksVirtualSource);
        var hostWorkspacesRootPath = !string.IsNullOrWhiteSpace(resolvedWksHost) &&
                                     !string.Equals(resolvedWksHost, NightwatchWksVirtualSource, StringComparison.Ordinal)
            ? resolvedWksHost
            : Path.Combine(userProfile, NightwatchConfigRootDir, "wks");

        var useWsl = _providerOptions.UseWslForContainers ||
                     !string.IsNullOrWhiteSpace(_providerOptions.DefaultWslDistribution);

        return SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: hostNcfLogsPath,
            wslNcfLogsPath: wslNcfLogsPath,
            hostSkillsPath: hostSkillsPath,
            wslSkillsPath: wslSkillsPath,
            hostWorkspacesRootPath: hostWorkspacesRootPath,
            wslWorkspacesRootPath: NightwatchWksVirtualSource,
            executionMode: ExecutionMode.ContainerSandbox,
            securityTier: SandboxSecurityTier.RestrictedReadOnly,
            wslDistribution: _providerOptions.DefaultWslDistribution,
            useWslForContainers: useWsl);
    }

    private string ResolveHostAccessiblePath(string path)
    {
        var resolved = _workspacePathResolver.ToHostPath(path);
        return !string.IsNullOrWhiteSpace(resolved) ? resolved : path;
    }

    private async Task<EnvironmentInspectionSnapshot> BuildInspectionSnapshotAsync(
        SandboxScopeDescriptor scope,
        string workspaceName,
        IExecutionEnvironment env,
        CancellationToken cancellationToken)
    {
        if (env is NightCodeForgeContainerExecutor containerExecutor)
        {
            var opts = containerExecutor.Options;
            var isRunning = await containerExecutor.IsContainerRunningAsync(cancellationToken);

            string? drift = null;
            if (isRunning.HasValue)
            {
                try
                {
                    var rawDrift = await containerExecutor.DetectContainerDriftAsync(cancellationToken);
                    drift = ContainerMountAndDriftBuilder.IsMissingContainerInspectMessage(rawDrift)
                        ? null
                        : rawDrift;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Container drift detection failed for {Container}", opts.ContainerNameOrId);
                }
            }

            var effectiveUser = opts.IsRestrictedReadOnly
                ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
                : opts.UserUidGid;

            var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(opts)
                .Select(m => new EnvironmentMountInfo(m.HostPath, m.ContainerPath, m.IsReadOnly))
                .ToList();

            var defaultDir = !string.IsNullOrWhiteSpace(opts.DefaultWorkingDirectory)
                ? opts.DefaultWorkingDirectory
                : scope.DefaultWorkingDirectory;

            return new EnvironmentInspectionSnapshot(
                ScopeKind: scope.ScopeKind,
                WorkspaceId: scope.WorkspaceId,
                WorkspaceName: workspaceName,
                ConfiguredMode: scope.ExecutionMode,
                ResolvedTier: env.Tier,
                TaskId: scope.TaskId,
                SecurityTier: scope.SecurityTier,
                DefaultWorkingDirectory: defaultDir,
                ContainerName: opts.ContainerNameOrId,
                ContainerImage: opts.ContainerImage,
                IsContainerRunning: isRunning,
                DriftDescription: drift,
                UserUidGid: effectiveUser,
                UseWsl: opts.UseWsl,
                WslDistribution: opts.WslDistribution,
                SynthesizedPath: ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(opts),
                ConfiguredMounts: mounts);
        }

        var scopeMounts = ResolveConfiguredMounts(scope, env);

        if (env is WslExecutionEnvironment wslEnv)
        {
            return new EnvironmentInspectionSnapshot(
                ScopeKind: scope.ScopeKind,
                WorkspaceId: scope.WorkspaceId,
                WorkspaceName: workspaceName,
                ConfiguredMode: scope.ExecutionMode,
                ResolvedTier: env.Tier,
                TaskId: scope.TaskId,
                SecurityTier: scope.SecurityTier,
                DefaultWorkingDirectory: wslEnv.DefaultWorkingDirectory ?? scope.DefaultWorkingDirectory,
                ContainerName: null,
                ContainerImage: null,
                IsContainerRunning: null,
                DriftDescription: null,
                UserUidGid: null,
                UseWsl: true,
                WslDistribution: wslEnv.Distribution,
                SynthesizedPath: string.Empty,
                ConfiguredMounts: scopeMounts);
        }

        var resolvedContainerName = scope.ExecutionMode == ExecutionMode.ContainerSandbox
            ? ContainerMountAndDriftBuilder.ResolveContainerName(scope)
            : null;

        return new EnvironmentInspectionSnapshot(
            ScopeKind: scope.ScopeKind,
            WorkspaceId: scope.WorkspaceId,
            WorkspaceName: workspaceName,
            ConfiguredMode: scope.ExecutionMode,
            ResolvedTier: env.Tier,
            TaskId: scope.TaskId,
            SecurityTier: scope.SecurityTier,
            DefaultWorkingDirectory: scope.DefaultWorkingDirectory,
            ContainerName: resolvedContainerName,
            ContainerImage: scope.ExecutionMode == ExecutionMode.ContainerSandbox ? scope.ContainerImage : null,
            IsContainerRunning: null,
            DriftDescription: null,
            UserUidGid: null,
            UseWsl: scope.UseWslForContainers,
            WslDistribution: scope.WslDistribution,
            SynthesizedPath: string.Empty,
            ConfiguredMounts: scopeMounts);
    }

    private static IReadOnlyList<EnvironmentMountInfo> ResolveConfiguredMounts(
        SandboxScopeDescriptor scope,
        IExecutionEnvironment env)
    {
        if (env is NightCodeForgeContainerExecutor containerExecutor)
        {
            return ContainerMountAndDriftBuilder.BuildVolumeMounts(containerExecutor.Options)
                .Select(m => new EnvironmentMountInfo(m.HostPath, m.ContainerPath, m.IsReadOnly))
                .ToList();
        }

        var isRestricted = scope.SecurityTier == SandboxSecurityTier.RestrictedReadOnly;
        return scope.MountTable.Entries
            .Select(e => new EnvironmentMountInfo(
                HostPath: !string.IsNullOrWhiteSpace(e.WslTargetDirectoryPath) ? e.WslTargetDirectoryPath : e.HostDirectoryPath,
                ContainerPath: e.VirtualMountPath,
                IsReadOnly: ContainerMountAndDriftBuilder.ResolveEffectiveReadOnly(e, isRestricted)))
            .ToList();
    }

    private static string ResolveRawWorkingDirectory(
        string? workingDirectoryOverride,
        SandboxScopeDescriptor scope,
        IExecutionEnvironment env)
    {
        if (env is NightCodeForgeContainerExecutor containerExecutor)
        {
            return ContainerMountAndDriftBuilder.ResolveContainerWorkingDirectory(
                workingDirectoryOverride,
                containerExecutor.Options);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectoryOverride))
        {
            return workingDirectoryOverride.Trim();
        }

        if (env is WslExecutionEnvironment wslEnv && !string.IsNullOrWhiteSpace(wslEnv.DefaultWorkingDirectory))
        {
            return wslEnv.DefaultWorkingDirectory;
        }

        return scope.DefaultWorkingDirectory;
    }

    private static string TryTranslateCommandForCorrelation(
        SandboxPathResolver pathResolver,
        string command,
        ExecutionTier tier,
        string fallbackNormalizedCommand)
    {
        try
        {
            return pathResolver.TranslateBashCommand(command, tier).Replace("\r", string.Empty).Trim();
        }
        catch (UnauthorizedAccessException)
        {
            return fallbackNormalizedCommand;
        }
    }

    private static DiagnosticCommandResult BuildCancelledResult(
        string command,
        string effectiveWorkDir,
        IExecutionEnvironment env,
        TimeSpan elapsed,
        object correlationLock,
        Func<bool> hasTrackedCommandAccessor,
        Func<ShellCommandRecord?> getCompletedRecord,
        StringBuilder stdoutTelemetry,
        StringBuilder stderrTelemetry)
    {
        bool hasTrackedCommand;
        ShellCommandRecord? completedRecord;
        string streamedOut;
        string streamedErr;
        lock (correlationLock)
        {
            hasTrackedCommand = hasTrackedCommandAccessor();
            completedRecord = getCompletedRecord();
            streamedOut = stdoutTelemetry.ToString();
            streamedErr = stderrTelemetry.ToString();
        }

        var containerExecutor = env as NightCodeForgeContainerExecutor;
        var sessionCancelled = hasTrackedCommand ? containerExecutor?.ShellSession.LastCancelledCommandResult : null;
        var wasSigIntDrained = hasTrackedCommand && containerExecutor?.ShellSession.WasDrainedOnLastCancel == true;

        var output = !string.IsNullOrEmpty(streamedOut)
            ? streamedOut
            : (completedRecord?.OutputPreview ?? sessionCancelled?.StandardOutput ?? string.Empty);

        var error = !string.IsNullOrEmpty(streamedErr)
            ? streamedErr
            : (completedRecord?.ErrorPreview ?? sessionCancelled?.StandardError ?? CancelledErrorMessage);

        var exitCode = completedRecord?.ExitCode
            ?? (wasSigIntDrained ? PersistentContainerShellSession.SigIntExitCode : -1);

        var duration = completedRecord is { DurationMs: > 0 }
            ? TimeSpan.FromMilliseconds(completedRecord.DurationMs)
            : (sessionCancelled?.Duration ?? elapsed);

        var isTruncated = sessionCancelled?.Truncated ?? false;

        return new DiagnosticCommandResult(
            CommandLine: command,
            EffectiveWorkingDirectory: !string.IsNullOrWhiteSpace(completedRecord?.WorkingDirectory)
                ? completedRecord.WorkingDirectory
                : effectiveWorkDir,
            ExitCode: exitCode,
            Output: output,
            Error: error,
            Duration: duration,
            IsTruncated: isTruncated,
            ExecutedAt: DateTimeOffset.UtcNow);
    }
}
