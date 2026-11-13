using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Default implementation of <see cref="IContainerEnvironmentFactory"/> encapsulating Docker/WSL container sandbox creation and teardown.
/// </summary>
public class ContainerEnvironmentFactory : IContainerEnvironmentFactory
{
    private const string TaskContainerInfix = "-task-";
    private const string DockerNoSuchContainerMessage = "No such container";

    private readonly IProcessRunner _processRunner;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IHostSdkDiscoveryService _sdkDiscoveryService;
    private readonly IHostUserIdentityResolver _userIdentityResolver;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ICompanionToolInstaller _companionToolInstaller;
    private readonly ILogger<NightCodeForgeContainerExecutor> _containerLogger;
    private readonly ConcurrentDictionary<(string Stack, bool UseWsl, string Distro), bool> _verifiedCompanionToolStacks = new();
    private readonly SemaphoreSlim _companionToolLock = new(1, 1);

    public ContainerEnvironmentFactory(
        IProcessRunner processRunner,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        IWorkspacePathResolver pathResolver,
        IHostSdkDiscoveryService sdkDiscoveryService,
        IHostUserIdentityResolver userIdentityResolver,
        IOperationsTelemetryService telemetryService,
        ICompanionToolInstaller companionToolInstaller,
        ILogger<NightCodeForgeContainerExecutor> containerLogger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value ?? new ExecutionEnvironmentProviderOptions();
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _sdkDiscoveryService = sdkDiscoveryService ?? throw new ArgumentNullException(nameof(sdkDiscoveryService));
        _userIdentityResolver = userIdentityResolver ?? throw new ArgumentNullException(nameof(userIdentityResolver));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _companionToolInstaller = companionToolInstaller ?? throw new ArgumentNullException(nameof(companionToolInstaller));
        _containerLogger = containerLogger ?? throw new ArgumentNullException(nameof(containerLogger));
    }

    public string? DefaultWslDistribution => _options.DefaultWslDistribution;

    public SandboxScopeDescriptor BuildWorkspaceScope(
        Workspace workspace,
        SandboxSecurityTier securityTier = SandboxSecurityTier.Standard,
        int? effectiveTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var normalizedTaskId = effectiveTaskId is > 0 ? effectiveTaskId : null;
        var isReadOnly = securityTier == SandboxSecurityTier.RestrictedReadOnly;
        var (rawPath, hostWorkspacePath, canonicalClonePath) = ResolveWorkspaceHostPaths(workspace);
        var hostSkillsDir = EnsureHostSkillsDirectory();

        string? rawTmpDir = null;
        if (!isReadOnly && !string.IsNullOrWhiteSpace(hostWorkspacePath))
        {
            rawTmpDir = EnsureHostTmpDirectory(hostWorkspacePath);
        }

        string? rawTaskWorktreePath = null;
        if (normalizedTaskId.HasValue && !string.IsNullOrWhiteSpace(hostWorkspacePath))
        {
            rawTaskWorktreePath = $"{hostWorkspacePath.TrimEnd('/', '\\')}/task-{normalizedTaskId.Value}";
        }

        var rawRoot = !string.IsNullOrWhiteSpace(hostWorkspacePath)
            ? hostWorkspacePath
            : (rawPath ?? string.Empty);

        var rawWorkingDir = normalizedTaskId.HasValue && !string.IsNullOrWhiteSpace(rawTaskWorktreePath)
            ? rawTaskWorktreePath
            : (!string.IsNullOrWhiteSpace(canonicalClonePath) ? canonicalClonePath : rawRoot);

        var additionalEntries = new List<VirtualMountEntry>();
        if (!string.IsNullOrWhiteSpace(hostSkillsDir))
        {
            additionalEntries.Add(new VirtualMountEntry(
                virtualSubPath: "skills",
                virtualMountPath: $"{SandboxMountTable.WorkspaceVirtualRoot}/skills",
                hostDirectoryPath: ResolveHostAccessiblePath(hostSkillsDir),
                wslTargetDirectoryPath: Git.WslPathConverter.ToWslPath(hostSkillsDir),
                isReadOnly: true));
        }

        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: ResolveHostAccessiblePath(rawWorkingDir),
            wslWorkingDirectory: Git.WslPathConverter.ToWslPath(rawWorkingDir),
            isReadOnly: isReadOnly,
            taskId: normalizedTaskId,
            hostCloneDirectory: !string.IsNullOrWhiteSpace(canonicalClonePath) ? ResolveHostAccessiblePath(canonicalClonePath) : null,
            wslCloneDirectory: !string.IsNullOrWhiteSpace(canonicalClonePath) ? Git.WslPathConverter.ToWslPath(canonicalClonePath) : null,
            hostTmpDirectory: !string.IsNullOrWhiteSpace(rawTmpDir) ? ResolveHostAccessiblePath(rawTmpDir) : null,
            wslTmpDirectory: !string.IsNullOrWhiteSpace(rawTmpDir) ? Git.WslPathConverter.ToWslPath(rawTmpDir) : null,
            additionalEntries: additionalEntries);

        var defaultWorkDir = normalizedTaskId.HasValue
            ? $"{SandboxMountTable.WorkspaceVirtualRoot}/task-{normalizedTaskId.Value}"
            : (!string.IsNullOrWhiteSpace(canonicalClonePath)
                ? $"{SandboxMountTable.WorkspaceVirtualRoot}/clone"
                : SandboxMountTable.WorkspaceVirtualRoot);

        var scope = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            securityTier,
            normalizedTaskId,
            mountTable,
            defaultWorkingDirectory: defaultWorkDir,
            hostWorkspacePath: hostWorkspacePath,
            canonicalClonePath: canonicalClonePath);

        return scope with { ContainerName = ResolveBaseContainerName(workspace) };
    }

    public async Task<IExecutionEnvironment> CreateContainerEnvironmentAsync(
        SandboxScopeDescriptor scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var useWsl = ShouldUseWslForScope(scope);
        var targetDistro = scope.WslDistribution ?? _options.DefaultWslDistribution;
        var containerName = ContainerMountAndDriftBuilder.ResolveContainerName(scope);
        var sdkMountPaths = _sdkDiscoveryService.DiscoverSdkMountPaths(useWsl ? targetDistro : null);

        var rawPath = !string.IsNullOrWhiteSpace(scope.Ext4MirrorPath)
            ? scope.Ext4MirrorPath
            : scope.RepositoryPath;
        var detectedStack = !string.IsNullOrWhiteSpace(rawPath)
            ? CodeStackPresets.Detect(rawPath)?.Name ?? CodeStack.DotNet
            : CodeStack.DotNet;

        await EnsureCompanionToolsOnceAsync(detectedStack, useWsl, targetDistro, cancellationToken);

        var additionalMounts = new List<ContainerVolumeMount>();

        if (scope.ScopeKind != SandboxScopeKind.SystemNightwatch)
        {
            var hostSkillsDir = EnsureHostSkillsDirectory();
            additionalMounts.Add(new(
                hostPath: hostSkillsDir,
                containerPath: "/workspace/skills",
                isReadOnly: true));
        }

        var hostToolsSource = useWsl
            ? "~/.dotnet/tools"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        if (useWsl || Directory.Exists(hostToolsSource))
        {
            additionalMounts.Add(new(
                hostPath: hostToolsSource,
                containerPath: ContainerExecutorOptions.HostDotnetToolsContainerPath,
                isReadOnly: true));
        }

        if (scope.SecurityTier != SandboxSecurityTier.RestrictedReadOnly &&
            scope.MountTable.SandboxTmpMount != null)
        {
            var tmpMount = scope.MountTable.SandboxTmpMount;
            var tmpSource = !string.IsNullOrWhiteSpace(tmpMount.WslTargetDirectoryPath)
                ? tmpMount.WslTargetDirectoryPath
                : tmpMount.HostDirectoryPath;
            if (!string.IsNullOrWhiteSpace(tmpSource))
            {
                additionalMounts.Add(new(
                    hostPath: tmpSource,
                    containerPath: "/tmp",
                    isReadOnly: false));
            }
        }

        var options = new ContainerExecutorOptions
        {
            Scope = scope,
            MountTable = scope.MountTable,
            ContainerNameOrId = containerName,
            ContainerImage = ResolveContainerImageName(scope.ContainerImage),
            HostWorkspacePath = scope.HostWorkspacePath,
            CanonicalClonePath = scope.CanonicalClonePath,
            ContainerWorkspacePath = scope.VirtualRootPath,
            DefaultWorkingDirectory = scope.DefaultWorkingDirectory,
            MountPackageCaches = true,
            MountHostSdks = true,
            SdkMountPaths = sdkMountPaths?.ToList() ?? new List<string>(),
            AdditionalVolumeMounts = additionalMounts,
            AutoStartContainer = true,
            IsRestrictedReadOnly = scope.SecurityTier == SandboxSecurityTier.RestrictedReadOnly,
            UseWsl = useWsl,
            WslDistribution = targetDistro,
            WslExecutable = _options.WslExecutable,
            WslUserName = _pathResolver.GetWslUserName(),
            ExpandTildeInMounts = true,
            ProtectCloneWorkingTree = scope.ScopeKind != SandboxScopeKind.SystemNightwatch,
            UserUidGid = _userIdentityResolver.ResolveUserIdentity(useWsl, targetDistro, _options.WslExecutable)
        };

        return new NightCodeForgeContainerExecutor(options, _processRunner, _containerLogger, _userIdentityResolver, _telemetryService);
    }

    public Task<IExecutionEnvironment> CreateContainerEnvironmentAsync(
        Workspace workspace,
        SandboxSecurityTier securityTier,
        int? effectiveTaskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var scope = BuildWorkspaceScope(workspace, securityTier, effectiveTaskId);
        return CreateContainerEnvironmentAsync(scope, cancellationToken);
    }

    public async Task RemoveContainerAsync(SandboxScopeDescriptor scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return;
        }

        var containerName = ContainerMountAndDriftBuilder.ResolveContainerName(scope);
        var targetOptions = new ContainerExecutorOptions
        {
            UseWsl = ShouldUseWslForScope(scope),
            WslDistribution = scope.WslDistribution ?? _options.DefaultWslDistribution,
            WslExecutable = _options.WslExecutable
        };

        await RemoveContainerByTargetOptionsAsync(targetOptions, containerName, cancellationToken);
    }

    public async Task RemoveTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox || taskId <= 0)
        {
            return;
        }

        var containerName = TaskContainerName(ResolveBaseContainerName(workspace), taskId);
        var roContainerName = ContainerMountAndDriftBuilder.ApplySecurityTierSuffix(containerName, SandboxSecurityTier.RestrictedReadOnly);
        await RemoveContainerAsync(workspace, containerName, cancellationToken);
        await RemoveContainerAsync(workspace, roContainerName, cancellationToken);
    }

    public async Task RemoveBaseContainerAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return;
        }

        var baseContainerName = ResolveBaseContainerName(workspace);
        var roBaseContainerName = ContainerMountAndDriftBuilder.ApplySecurityTierSuffix(baseContainerName, SandboxSecurityTier.RestrictedReadOnly);
        await RemoveContainerAsync(workspace, baseContainerName, cancellationToken);
        await RemoveContainerAsync(workspace, roBaseContainerName, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> ListTaskContainerIdsAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (workspace.ExecutionMode != ExecutionMode.ContainerSandbox)
        {
            return [];
        }

        var prefix = ResolveBaseContainerName(workspace) + TaskContainerInfix;
        var spec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
            BuildDockerTargetOptions(workspace),
            ["ps", "-a", "--filter", $"name={prefix}", "--format", "{{.Names}}"],
            timeoutSeconds: 10);

        ProcessExecutionResult result;
        try
        {
            result = await _processRunner.RunAsync(spec, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _containerLogger.LogWarning(ex, "Failed to list task containers with prefix '{Prefix}' for Workspace #{WorkspaceId}", prefix, workspace.Id);
            return [];
        }

        if (result.ExitCode != 0)
        {
            _containerLogger.LogWarning("Listing task containers with prefix '{Prefix}' failed (exit code {ExitCode}): {Error}",
                prefix, result.ExitCode, result.StandardError);
            return [];
        }

        var ids = new HashSet<int>();
        foreach (var rawName in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = rawName.TrimStart('/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = name[prefix.Length..];
            if (suffix.EndsWith(ContainerMountAndDriftBuilder.ReadOnlyContainerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                suffix = suffix[..^ContainerMountAndDriftBuilder.ReadOnlyContainerSuffix.Length];
            }

            if (int.TryParse(suffix, out var taskId) && taskId > 0)
            {
                ids.Add(taskId);
            }
        }

        return ids.ToList();
    }

    private async Task EnsureCompanionToolsOnceAsync(
        string detectedStack,
        bool useWsl,
        string? targetDistro,
        CancellationToken cancellationToken)
    {
        var key = (detectedStack.ToLowerInvariant(), useWsl, (targetDistro ?? string.Empty).ToLowerInvariant());
        if (_verifiedCompanionToolStacks.ContainsKey(key))
        {
            return;
        }

        await _companionToolLock.WaitAsync(cancellationToken);
        try
        {
            if (_verifiedCompanionToolStacks.ContainsKey(key))
            {
                return;
            }

            try
            {
                await _companionToolInstaller.EnsureToolsInstalledAsync(detectedStack, useWsl, targetDistro, cancellationToken);
                _verifiedCompanionToolStacks[key] = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _containerLogger.LogWarning(ex, "Failed to auto-install companion tools for stack {Stack}", detectedStack);
            }
        }
        finally
        {
            _companionToolLock.Release();
        }
    }

    private (string? RawPath, string? HostWorkspacePath, string? CanonicalClonePath) ResolveWorkspaceHostPaths(Workspace workspace)
    {
        var rawPath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
            ? workspace.Ext4MirrorPath
            : (!string.IsNullOrWhiteSpace(workspace.RepositoryPath)
                ? workspace.RepositoryPath
                : _pathResolver.GetAgentWorkspaceRootPath(workspace.Id));

        if (string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && workspace.Id > 0)
        {
            var resolvedClone = _pathResolver.ResolveCruciblePath(workspace.Id, string.Empty);
            if (!string.IsNullOrWhiteSpace(resolvedClone) && Directory.Exists(resolvedClone))
            {
                rawPath = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
            }
        }

        string? hostWorkspacePath = null;
        string? canonicalClonePath = null;

        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            var normRaw = rawPath.TrimEnd('/', '\\');
            var isUnixOrWsl = normRaw.StartsWith('/') || normRaw.StartsWith('~') || normRaw.StartsWith('\\');
            var dirName = Path.GetFileName(normRaw);
            string parentDir;
            string? cloneDir;

            if (dirName.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals("mirror", StringComparison.OrdinalIgnoreCase))
            {
                cloneDir = normRaw.Replace('\\', '/');
                parentDir = (Path.GetDirectoryName(normRaw) ?? normRaw).Replace('\\', '/');
            }
            else if (Directory.Exists(Path.Combine(normRaw, "clone")))
            {
                parentDir = normRaw.Replace('\\', '/');
                cloneDir = $"{normRaw.Replace('\\', '/')}/clone";
            }
            else
            {
                parentDir = normRaw.Replace('\\', '/');
                cloneDir = null;
            }

            if (isUnixOrWsl)
            {
                if (normRaw.StartsWith('/') && !parentDir.StartsWith('/')) parentDir = "/" + parentDir;
                if (cloneDir != null && normRaw.StartsWith('/') && !cloneDir.StartsWith('/')) cloneDir = "/" + cloneDir;
            }

            hostWorkspacePath = parentDir;
            canonicalClonePath = cloneDir;
        }

        return (rawPath, hostWorkspacePath, canonicalClonePath);
    }

    private string ResolveHostAccessiblePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var resolved = _pathResolver.ToHostPath(path);
        return !string.IsNullOrWhiteSpace(resolved) ? resolved : path;
    }

    private string EnsureHostSkillsDirectory()
    {
        var hostSkillsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightcodeforge", "skills");
        if (!Directory.Exists(hostSkillsDir))
        {
            try
            {
                Directory.CreateDirectory(hostSkillsDir);
            }
            catch (Exception ex)
            {
                _containerLogger.LogWarning(ex, "Failed to create host skills directory at {HostSkillsDir}", hostSkillsDir);
            }
        }

        return hostSkillsDir;
    }

    private string EnsureHostTmpDirectory(string hostWorkspacePath)
    {
        var hostTmpDir = $"{hostWorkspacePath.TrimEnd('/', '\\').Replace('\\', '/')}/tmp";
        var hostAccessibleWorkspaceDir = _pathResolver.ToHostPath(hostWorkspacePath);
        if (!string.IsNullOrWhiteSpace(hostAccessibleWorkspaceDir) && Directory.Exists(hostAccessibleWorkspaceDir))
        {
            var hostAccessibleTmpDir = _pathResolver.ToHostPath(hostTmpDir);
            if (!string.IsNullOrWhiteSpace(hostAccessibleTmpDir) && !Directory.Exists(hostAccessibleTmpDir))
            {
                try
                {
                    Directory.CreateDirectory(hostAccessibleTmpDir);
                }
                catch (Exception ex)
                {
                    _containerLogger.LogWarning(ex, "Failed to create host tmp directory at {HostTmpDir}", hostAccessibleTmpDir);
                }
            }
        }

        return hostTmpDir;
    }

    private bool ShouldUseWslForContainers(Workspace workspace) =>
        workspace.UseWslForContainers ||
        !string.IsNullOrWhiteSpace(workspace.WslDistribution) ||
        _options.UseWslForContainers ||
        (_options.AutoDetectWsl && ShouldAutoUseWslForContainers(workspace.Ext4MirrorPath));

    private bool ShouldUseWslForScope(SandboxScopeDescriptor scope) =>
        scope.UseWslForContainers ||
        !string.IsNullOrWhiteSpace(scope.WslDistribution) ||
        _options.UseWslForContainers ||
        (_options.AutoDetectWsl && ShouldAutoUseWslForContainers(scope.Ext4MirrorPath));

    private static string ResolveBaseContainerName(Workspace workspace) =>
        !string.IsNullOrWhiteSpace(workspace.ContainerName)
            ? workspace.ContainerName
            : (!string.IsNullOrWhiteSpace(workspace.ContainerImage)
                ? (workspace.ContainerImage.Contains(':') || workspace.ContainerImage.Contains('/')
                    ? $"ncf-sandbox-{workspace.Id}"
                    : workspace.ContainerImage)
                : "ncf-sandbox");

    private static string ResolveContainerImage(Workspace workspace) =>
        ResolveContainerImageName(workspace.ContainerImage);

    private static string ResolveContainerImageName(string? containerImage)
    {
        if (string.IsNullOrWhiteSpace(containerImage))
        {
            return ContainerExecutorOptions.DefaultAgentBaseImage;
        }

        var trimmed = containerImage.Trim();
        if (trimmed.Equals("ncf-agent-base", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ncf-agent-base:latest", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ncf-agent-base:v1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ncf-agent-base:v2", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ncf-agent-base:v3", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("nightcodeforge-agent:latest", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("nightcodeforge-agent:v1", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("nightcodeforge-agent:v2", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("nightcodeforge-agent:v3", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerExecutorOptions.DefaultAgentBaseImage;
        }

        return trimmed;
    }

    private static string TaskContainerName(string baseContainerName, int taskId) =>
        $"{baseContainerName}{TaskContainerInfix}{taskId}";

    private ContainerExecutorOptions BuildDockerTargetOptions(Workspace workspace) => new()
    {
        UseWsl = ShouldUseWslForContainers(workspace),
        WslDistribution = workspace.WslDistribution ?? _options.DefaultWslDistribution,
        WslExecutable = _options.WslExecutable
    };

    private Task RemoveContainerAsync(Workspace workspace, string containerName, CancellationToken cancellationToken) =>
        RemoveContainerByTargetOptionsAsync(BuildDockerTargetOptions(workspace), containerName, cancellationToken);

    private async Task RemoveContainerByTargetOptionsAsync(
        ContainerExecutorOptions targetOptions,
        string containerName,
        CancellationToken cancellationToken)
    {
        var spec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
            targetOptions,
            ["rm", "-f", containerName],
            timeoutSeconds: 10);

        try
        {
            var result = await _processRunner.RunAsync(spec, cancellationToken);
            var combinedError = (result.StandardError ?? string.Empty) + " " + (result.StandardOutput ?? string.Empty);
            if (result.ExitCode != 0 &&
                !combinedError.Contains(DockerNoSuchContainerMessage, StringComparison.OrdinalIgnoreCase))
            {
                _containerLogger.LogWarning("Failed to remove container {Container} (exit code {ExitCode}): {Error}",
                    containerName, result.ExitCode, result.StandardError);
            }
            else if (result.ExitCode == 0)
            {
                _containerLogger.LogInformation("Removed container {Container}", containerName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _containerLogger.LogWarning(ex, "Failed to remove container {Container}", containerName);
        }
    }

    private static bool ShouldAutoUseWslForContainers(string? ext4MirrorPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        var envVar = Environment.GetEnvironmentVariable("NCF_USE_WSL_DOCKER") ??
                     Environment.GetEnvironmentVariable("NCF_USE_WSL");
        if (string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(envVar, "1", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ext4MirrorPath))
        {
            return true;
        }

        if (!IsExecutableOnWindowsPath("docker.exe") && !IsExecutableOnWindowsPath("docker") &&
            (IsExecutableOnWindowsPath("wsl.exe") || File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"))))
        {
            return true;
        }

        return false;
    }

    private static bool IsExecutableOnWindowsPath(string executable)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv)) return false;

            var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                var full = Path.Combine(dir.Trim(), executable);
                if (File.Exists(full)) return true;
            }
        }
        catch (Exception)
        {
            // Expected when environment variables or PATH entries contain invalid characters or restricted permissions
        }

        return false;
    }
}
