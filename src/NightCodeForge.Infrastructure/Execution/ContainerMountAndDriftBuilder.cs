using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Builds Docker volume bind mounts, container run arguments, synthesized PATH values, and container drift inspections
/// from a <see cref="SandboxScopeDescriptor"/> / <see cref="SandboxMountTable"/> and <see cref="ContainerExecutorOptions"/>.
/// </summary>
public class ContainerMountAndDriftBuilder
{
    public const string ReadOnlyContainerSuffix = SandboxScopeDescriptor.ReadOnlyContainerSuffix;
    private const string MountInspectFormat = "{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}";
    private const string ImageInspectFormat = "{{.Config.Image}}";
    private const string UserInspectFormat = "{{.Config.User}}";
    private const string RunningInspectFormat = "{{.State.Running}}";
    private const int InspectTimeoutSeconds = 5;
    private const string NoSuchContainerMessage = "No such container";
    private const string NoSuchObjectMessage = "No such object";
    private const string NetworkModeInspectFormat = "{{.HostConfig.NetworkMode}}";
    private const string ExtraHostsInspectFormat = "{{range .HostConfig.ExtraHosts}}{{.}};{{end}}";
    private const string TmpfsInspectFormat = "{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}";
    private const string EnvInspectFormat = "{{range .Config.Env}}{{.}}\n{{end}}";
    private const string NoneNetworkMode = "none";
    private const string ReadOnlyTmpfsOption = "ro";
    private const string RustupHomeEnvVarName = "RUSTUP_HOME";
    private const string BlockedHostLoopbackIp = "127.0.0.1";
    private const string DotnetDirectorySuffix = "/.dotnet";
    private const string DotnetToolsDirectorySuffix = "/.dotnet/tools";

    /// <summary>
    /// Appends the '-ro' suffix to a container name when the security tier is <see cref="SandboxSecurityTier.RestrictedReadOnly"/>.
    /// </summary>
    public static string ApplySecurityTierSuffix(string containerName, SandboxSecurityTier securityTier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        var trimmed = containerName.Trim();
        if (securityTier == SandboxSecurityTier.RestrictedReadOnly &&
            !trimmed.EndsWith(ReadOnlyContainerSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}{ReadOnlyContainerSuffix}";
        }

        return trimmed;
    }

    /// <summary>
    /// Resolves the canonical container name for a <see cref="SandboxScopeDescriptor"/>, enforcing '-ro' isolation for RestrictedReadOnly.
    /// </summary>
    public static string ResolveContainerName(SandboxScopeDescriptor scope, string? fallbackBaseName = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.ScopeKind == SandboxScopeKind.SystemNightwatch)
        {
            var nwBase = !string.IsNullOrWhiteSpace(scope.ContainerName)
                ? scope.ContainerName
                : SandboxScopeDescriptor.NightwatchContainerBaseName;
            return ApplySecurityTierSuffix(nwBase, scope.SecurityTier);
        }

        var baseName = !string.IsNullOrWhiteSpace(scope.ContainerName)
            ? scope.ContainerName
            : (!string.IsNullOrWhiteSpace(fallbackBaseName)
                ? fallbackBaseName
                : (scope.WorkspaceId is > 0 ? $"ncf-sandbox-{scope.WorkspaceId.Value}" : "ncf-sandbox"));

        var taskSuffix = scope.TaskId is > 0 ? $"-task-{scope.TaskId.Value}" : string.Empty;
        var nameWithTask = scope.TaskId is > 0 && !baseName.Contains(taskSuffix, StringComparison.OrdinalIgnoreCase)
            ? $"{baseName}{taskSuffix}"
            : baseName;

        return ApplySecurityTierSuffix(nameWithTask, scope.SecurityTier);
    }

    /// <summary>
    /// Computes <c>entry.IsReadOnly || (isRestrictedReadOnly &amp;&amp; !entry.AllowWriteInReadOnlyScope)</c>.
    /// </summary>
    public static bool ResolveEffectiveReadOnly(VirtualMountEntry entry, bool isRestrictedReadOnly)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsReadOnly || (isRestrictedReadOnly && !entry.AllowWriteInReadOnlyScope);
    }

    /// <summary>
    /// Builds the list of Docker volume bind mounts according to <see cref="ContainerExecutorOptions"/> and any attached <see cref="SandboxMountTable"/>.
    /// </summary>
    public static IReadOnlyList<ContainerVolumeMount> BuildVolumeMounts(ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mounts = new List<ContainerVolumeMount>();
        var mountedContainerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddMount(string hostPath, string containerPath, bool isReadOnly)
        {
            var normContainer = NormalizeContainerPath(containerPath);
            if (string.IsNullOrEmpty(normContainer) || !mountedContainerPaths.Add(normContainer))
            {
                return;
            }
            mounts.Add(new ContainerVolumeMount(hostPath, containerPath, isReadOnly));
        }

        // 1A. Declarative SandboxMountTable mounts (e.g. /nightcodeforge/wks, /nightcodeforge/ncf-logs, or explicit mount table entries)
        if (options.MountTable != null &&
            (string.IsNullOrWhiteSpace(options.HostWorkspacePath) ||
             string.Equals(options.MountTable.VirtualRootPath, SandboxMountTable.NightwatchVirtualRoot, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var entry in options.MountTable.Entries)
            {
                var isAttachmentsEntry = string.Equals(
                    NormalizeContainerPath(entry.VirtualMountPath),
                    AttachmentConstants.VirtualAttachmentsMountPath,
                    StringComparison.OrdinalIgnoreCase);

                if (isAttachmentsEntry && !string.IsNullOrWhiteSpace(options.AttachmentsPath))
                {
                    continue;
                }

                var sourcePath = !string.IsNullOrWhiteSpace(entry.WslTargetDirectoryPath)
                    ? entry.WslTargetDirectoryPath
                    : entry.HostDirectoryPath;

                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                AddMount(
                    ResolveHostPath(sourcePath, options),
                    entry.VirtualMountPath,
                    isReadOnly: ResolveEffectiveReadOnly(entry, options.IsRestrictedReadOnly) || isAttachmentsEntry);
            }
        }

        // 1B. Workspace mount: HostWorkspacePath -> ContainerWorkspacePath
        if (!string.IsNullOrWhiteSpace(options.HostWorkspacePath))
        {
            var hostWks = ResolveHostPath(options.HostWorkspacePath, options);
            var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
                ? options.ContainerWorkspacePath
                : "/workspace";
            AddMount(hostWks, containerWks, isReadOnly: options.IsRestrictedReadOnly);

            var normHost = hostWks.TrimEnd('/', '\\');
            var normContainer = containerWks.TrimEnd('/', '\\');

            if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath))
            {
                var normClone = ResolveHostPath(options.CanonicalClonePath, options).TrimEnd('/', '\\');
                if (!normClone.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase) &&
                    !normClone.Equals(normHost, StringComparison.OrdinalIgnoreCase))
                {
                    AddMount(normClone, "/clone", isReadOnly: true);
                    AddMount($"{normClone}/.git", "/clone/.git", isReadOnly: options.IsRestrictedReadOnly);
                }
            }
            else if (options.ProtectCloneWorkingTree && !options.IsRestrictedReadOnly)
            {
                AddMount($"{normHost}/clone", $"{normContainer}/clone", isReadOnly: true);
                AddMount($"{normHost}/clone/.git", $"{normContainer}/clone/.git", isReadOnly: false);
            }

            // 1C. External or read-only sub-mounts declared in SandboxMountTable alongside HostWorkspacePath
            if (options.MountTable != null &&
                !string.Equals(options.MountTable.VirtualRootPath, SandboxMountTable.NightwatchVirtualRoot, StringComparison.OrdinalIgnoreCase))
            {
                var normCloneResolved = !string.IsNullOrWhiteSpace(options.CanonicalClonePath)
                    ? ResolveHostPath(options.CanonicalClonePath, options).TrimEnd('/', '\\')
                    : null;

                foreach (var entry in options.MountTable.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.VirtualSubPath) ||
                        string.Equals(entry.VirtualSubPath, "clone", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourcePath = !string.IsNullOrWhiteSpace(entry.WslTargetDirectoryPath)
                        ? entry.WslTargetDirectoryPath
                        : entry.HostDirectoryPath;

                    if (string.IsNullOrWhiteSpace(sourcePath))
                    {
                        continue;
                    }

                    var resolvedSource = ResolveHostPath(sourcePath, options);
                    var normSource = resolvedSource.TrimEnd('/', '\\');
                    var isAttachmentsMount = string.Equals(
                        NormalizeContainerPath(entry.VirtualMountPath),
                        AttachmentConstants.VirtualAttachmentsMountPath,
                        StringComparison.OrdinalIgnoreCase);

                    if (isAttachmentsMount && !string.IsNullOrWhiteSpace(options.AttachmentsPath))
                    {
                        continue;
                    }

                    var isMemoryMount = string.Equals(
                        NormalizeContainerPath(entry.VirtualMountPath),
                        NormalizeContainerPath(AgentMemoryConstants.VirtualLocalMemoryPath),
                        StringComparison.OrdinalIgnoreCase);
                    var effectiveReadOnly = ResolveEffectiveReadOnly(entry, options.IsRestrictedReadOnly) || isAttachmentsMount;

                    var isInsideHostWorkspace =
                        normSource.Equals(normHost, StringComparison.OrdinalIgnoreCase) ||
                        normSource.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase);

                    var isCanonicalClone =
                        !string.IsNullOrEmpty(normCloneResolved) &&
                        normSource.Equals(normCloneResolved, StringComparison.OrdinalIgnoreCase);

                    // Skip subpaths already exposed identically through the parent /workspace bind mount
                    // (but always keep explicit read-only /workspace/attachments mount even in RestrictedReadOnly mode).
                    if (!isAttachmentsMount &&
                        !(isMemoryMount && options.IsRestrictedReadOnly) &&
                        (isInsideHostWorkspace || isCanonicalClone) &&
                        effectiveReadOnly == options.IsRestrictedReadOnly)
                    {
                        continue;
                    }

                    AddMount(resolvedSource, entry.VirtualMountPath, isReadOnly: effectiveReadOnly);
                }
            }
        }

        // 1D. Explicit workspace attachments directory bind-mounted strictly read-only at /workspace/attachments
        if (!string.IsNullOrWhiteSpace(options.AttachmentsPath))
        {
            AddMount(
                ResolveHostPath(options.AttachmentsPath, options),
                AttachmentConstants.VirtualAttachmentsMountPath,
                isReadOnly: true);
        }

        // 2. Package caches (NuGet, npm, pnpm, uv, Maven, Playwright)
        if (options.MountPackageCaches)
        {
            if (!string.IsNullOrWhiteSpace(options.NugetPackageCachePath))
            {
                AddMount(ResolveHostPath(options.NugetPackageCachePath, options), "/cache/nuget", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.NpmCachePath) &&
                !options.IsRestrictedReadOnly &&
                !options.DisableNetwork)
            {
                AddMount(ResolveHostPath(options.NpmCachePath, options), ContainerExecutorOptions.ContainerNpmCachePath, isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.PnpmCachePath))
            {
                AddMount(ResolveHostPath(options.PnpmCachePath, options), "/cache/pnpm", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.UvCachePath))
            {
                AddMount(ResolveHostPath(options.UvCachePath, options), "/cache/uv", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.MavenCachePath))
            {
                AddMount(ResolveHostPath(options.MavenCachePath, options), "/cache/m2", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.PlaywrightCachePath))
            {
                AddMount(ResolveHostPath(options.PlaywrightCachePath, options), "/cache/ms-playwright", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.CargoCachePath))
            {
                AddMount(
                    ResolveHostPath(options.CargoCachePath, options),
                    ContainerExecutorOptions.ContainerCargoCachePath,
                    isReadOnly: options.IsRestrictedReadOnly);
            }
        }

        // 3. Heavy SDK mounts
        if (options.MountHostSdks && options.SdkMountPaths != null)
        {
            var isManagedBaseImage = IsManagedBaseImage(options.ContainerImage);
            var containerHomeDotnet = $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet";
            var hasMappedHomeDotnet = false;
            var userDotnetRoots = options.SdkMountPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && IsNonRootUserDotnetRoot(p.TrimEnd('/')))
                .Select(p => p.TrimEnd('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rustupHomes = options.SdkMountPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => NormalizeContainerPath(ResolveHostPath(p, options)))
                .Where(IsRustupHomePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sdkPath in options.SdkMountPaths)
            {
                if (string.IsNullOrWhiteSpace(sdkPath))
                {
                    continue;
                }

                var trimmedSdk = sdkPath.TrimEnd('/');
                var normSdk = NormalizeContainerPath(sdkPath);
                if (!isManagedBaseImage && IsHostMultiarchSharedLibraryPath(normSdk))
                {
                    continue;
                }

                if (trimmedSdk.EndsWith("/.dotnet/tools", StringComparison.OrdinalIgnoreCase))
                {
                    if (!options.MountHostDotnetTools)
                    {
                        continue;
                    }

                    var parentDotnet = trimmedSdk[..^"/tools".Length];
                    if (userDotnetRoots.Contains(parentDotnet))
                    {
                        continue;
                    }
                }

                var resolvedHostSdk = ResolveHostPath(sdkPath, options);
                var normResolvedHostSdk = NormalizeContainerPath(resolvedHostSdk);
                if (!IsRustupHomePath(normResolvedHostSdk) &&
                    rustupHomes.Any(home => normResolvedHostSdk.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var isUserDotnetRoot = userDotnetRoots.Contains(trimmedSdk);

                if (isUserDotnetRoot)
                {
                    if (!options.IsRestrictedReadOnly && !hasMappedHomeDotnet)
                    {
                        AddMount(resolvedHostSdk, containerHomeDotnet, isReadOnly: false);
                        hasMappedHomeDotnet = true;
                    }
                    continue;
                }

                AddMount(resolvedHostSdk, sdkPath, isReadOnly: true);
            }
        }

        // 4. Additional custom volume mounts
        if (options.AdditionalVolumeMounts != null)
        {
            foreach (var customMount in options.AdditionalVolumeMounts)
            {
                if (!options.MountHostDotnetTools &&
                    string.Equals(
                        NormalizeContainerPath(customMount.ContainerPath),
                        ContainerExecutorOptions.HostDotnetToolsContainerPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddMount(
                    ResolveHostPath(customMount.HostPath, options),
                    customMount.ContainerPath,
                    isReadOnly: customMount.IsReadOnly);
            }
        }

        return mounts;
    }

    /// <summary>
    /// Compares the existing container's mounts, tmpfs masks, image, user, network isolation, and environment with the expected configuration asynchronously.
    /// Returns a description of the drift, or null when the container can be safely reused.
    /// </summary>
    public static async Task<string?> DetectContainerDriftAsync(
        IProcessRunner processRunner,
        ContainerExecutorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(options);

        return await DetectContainerDriftCoreAsync(
            spec => new ValueTask<ProcessExecutionResult>(processRunner.RunAsync(spec, cancellationToken)),
            options).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes whether the configured container is currently running via <c>docker inspect -f {{.State.Running}}</c>.
    /// Returns <see langword="true"/> if running, <see langword="false"/> if stopped or absent, or <see langword="null"/> when status cannot be determined.
    /// </summary>
    public static async Task<bool?> IsContainerRunningAsync(
        IProcessRunner processRunner,
        ContainerExecutorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(options);

        if (options.CustomProcessFactory != null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.ContainerNameOrId))
        {
            return false;
        }

        var result = await processRunner.RunAsync(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", RunningInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: InspectTimeoutSeconds), cancellationToken);

        if (result.ExitCode == 0)
        {
            return result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        if (IsMissingContainerInspectMessage(result.StandardError))
        {
            return false;
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a Docker inspect diagnostic message indicates that the container does not exist yet.
    /// </summary>
    public static bool IsMissingContainerInspectMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        (message.Contains(NoSuchContainerMessage, StringComparison.OrdinalIgnoreCase) ||
         message.Contains(NoSuchObjectMessage, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compares the existing container's mounts, tmpfs masks, image, user, network isolation, and environment with the expected configuration synchronously.
    /// Returns a description of the drift, or null when the container can be safely reused.
    /// </summary>
    public static string? DetectContainerDrift(IProcessRunner processRunner, ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(options);

        var vt = DetectContainerDriftCoreAsync(
            spec => ValueTask.FromResult(processRunner.Run(spec)),
            options);
        return vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
    }

    private static async ValueTask<string?> DetectContainerDriftCoreAsync(
        Func<ProcessStartSpec, ValueTask<ProcessExecutionResult>> runInspect,
        ContainerExecutorOptions options)
    {
        var mountResult = await runInspect(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", MountInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
        if (mountResult.ExitCode != 0)
        {
            return $"Failed to inspect mounts of container (exit code {mountResult.ExitCode}): {mountResult.StandardError}";
        }

        var driftFromMounts = EvaluateMountDrift(mountResult.StandardOutput, options, out var expectedMounts);
        if (driftFromMounts != null)
        {
            return driftFromMounts;
        }

        var maskedDotnetRoots = GetDotnetRootsRequiringToolsMask(expectedMounts, options);
        if (maskedDotnetRoots.Count > 0)
        {
            var tmpfsResult = await runInspect(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", TmpfsInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
            if (tmpfsResult.ExitCode != 0)
            {
                return $"Failed to inspect tmpfs mounts of container (exit code {tmpfsResult.ExitCode}): {tmpfsResult.StandardError}";
            }

            var tmpfsDrift = EvaluateDotnetToolsTmpfsMaskDrift(tmpfsResult.StandardOutput, maskedDotnetRoots);
            if (tmpfsDrift != null)
            {
                return tmpfsDrift;
            }
        }

        var expectedImage = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;
        var imageResult = await runInspect(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", ImageInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
        if (imageResult.ExitCode != 0)
        {
            return $"Failed to inspect image of container (exit code {imageResult.ExitCode}): {imageResult.StandardError}";
        }

        var actualImage = imageResult.StandardOutput.Trim();
        if (!IsMatchingImage(actualImage, expectedImage))
        {
            return $"Image mismatch: '{actualImage}' vs '{expectedImage}'";
        }

        var expectedUser = options.IsRestrictedReadOnly ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid : options.UserUidGid;
        if (!string.IsNullOrWhiteSpace(expectedUser))
        {
            var userResult = await runInspect(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", UserInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
            if (userResult.ExitCode != 0)
            {
                return $"Failed to inspect user of container (exit code {userResult.ExitCode}): {userResult.StandardError}";
            }

            var actualUser = userResult.StandardOutput.Trim();
            if (!string.Equals(actualUser, expectedUser, StringComparison.Ordinal))
            {
                return $"User mismatch: '{actualUser}' vs '{expectedUser}'";
            }
        }

        var networkExpectedNone = options.IsRestrictedReadOnly || options.DisableNetwork;
        if (networkExpectedNone)
        {
            var networkResult = await runInspect(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", NetworkModeInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
            if (networkResult == null || networkResult.ExitCode != 0)
            {
                return $"Failed to inspect network mode of container (exit code {networkResult?.ExitCode ?? -1}): {networkResult?.StandardError}";
            }

            var networkDrift = EvaluateNetworkModeDrift(networkResult.StandardOutput);
            if (networkDrift != null)
            {
                return networkDrift;
            }
        }
        else
        {
            var blockedHosts = NormalizeBlockedHosts(options.BlockedHosts);
            if (blockedHosts.Count > 0)
            {
                var extraHostsResult = await runInspect(BuildDockerProcessSpec(
                    options,
                    ["inspect", "-f", ExtraHostsInspectFormat, options.ContainerNameOrId],
                    timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
                if (extraHostsResult.ExitCode != 0)
                {
                    return $"Failed to inspect extra hosts of container (exit code {extraHostsResult.ExitCode}): {extraHostsResult.StandardError}";
                }

                var hostsDrift = EvaluateBlockedHostsDrift(extraHostsResult.StandardOutput, blockedHosts);
                if (hostsDrift != null)
                {
                    return hostsDrift;
                }
            }
        }

        var expectedRustupHome = ResolveRustupHomePath(options);
        var expectCargoHome = options.MountPackageCaches && !string.IsNullOrWhiteSpace(options.CargoCachePath);
        var expectOfflineNpmEnv = networkExpectedNone;
        var expectFallbackNpmCacheEnv = networkExpectedNone && options.MountPackageCaches;
        if (!string.IsNullOrWhiteSpace(expectedRustupHome) || expectCargoHome || expectOfflineNpmEnv || expectFallbackNpmCacheEnv)
        {
            var envResult = await runInspect(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", EnvInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: InspectTimeoutSeconds)).ConfigureAwait(false);
            if (envResult == null || envResult.ExitCode != 0)
            {
                return $"Failed to inspect environment of container (exit code {envResult?.ExitCode ?? -1}): {envResult?.StandardError}";
            }

            if (!string.IsNullOrWhiteSpace(expectedRustupHome))
            {
                var rustupDrift = EvaluateRequiredEnvVarDrift(envResult.StandardOutput, RustupHomeEnvVarName, expectedRustupHome);
                if (rustupDrift != null)
                {
                    return rustupDrift;
                }
            }

            if (expectCargoHome)
            {
                var cargoDrift = EvaluateRequiredEnvVarDrift(
                    envResult.StandardOutput,
                    ContainerExecutorOptions.CargoHomeEnvVarName,
                    ContainerExecutorOptions.ContainerCargoCachePath);
                if (cargoDrift != null)
                {
                    return cargoDrift;
                }
            }

            if (expectOfflineNpmEnv)
            {
                var offlineLowerDrift = EvaluateRequiredEnvVarDrift(
                    envResult.StandardOutput,
                    ContainerExecutorOptions.NpmConfigOfflineEnvVarName,
                    ContainerExecutorOptions.OfflineTrueValue);
                if (offlineLowerDrift != null)
                {
                    return offlineLowerDrift;
                }

                var offlineUpperDrift = EvaluateRequiredEnvVarDrift(
                    envResult.StandardOutput,
                    ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName,
                    ContainerExecutorOptions.OfflineTrueValue);
                if (offlineUpperDrift != null)
                {
                    return offlineUpperDrift;
                }
            }

            if (expectFallbackNpmCacheEnv)
            {
                var npmCacheDrift = EvaluateRequiredEnvVarDrift(
                    envResult.StandardOutput,
                    ContainerExecutorOptions.NpmCacheEnvVarName,
                    ContainerExecutorOptions.FallbackNpmCachePath);
                if (npmCacheDrift != null)
                {
                    return npmCacheDrift;
                }
            }
        }

        return null;
    }

    private static string? EvaluateMountDrift(
        string mountInspectOutput,
        ContainerExecutorOptions options,
        out IReadOnlyList<ContainerVolumeMount> expectedMounts)
    {
        var actual = new Dictionary<string, (string Source, bool IsRw)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (mountInspectOutput ?? string.Empty).Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Trim().Split('=');
            if (parts.Length < 3) continue;
            var isRw = bool.TryParse(parts[^1].Trim(), out var rw) && rw;
            var destination = NormalizeContainerPath(parts[0]);
            var source = string.Join('=', parts[1..^1]).Trim();
            actual[destination] = (NormalizeMountSourcePath(source), isRw);
        }

        expectedMounts = BuildVolumeMounts(options);
        var problems = new List<string>();
        foreach (var expected in expectedMounts)
        {
            var expectedDest = NormalizeContainerPath(expected.ContainerPath);
            if (!actual.TryGetValue(expectedDest, out var current))
            {
                problems.Add($"{expected.ContainerPath} (missing)");
                continue;
            }

            var expectedRw = !expected.IsReadOnly;
            if (current.IsRw != expectedRw)
            {
                problems.Add($"{expected.ContainerPath} (expected {(expected.IsReadOnly ? "ro" : "rw")})");
            }
        }

        var normClone = NormalizeContainerPath("/clone");
        var expectedHasClone = expectedMounts.Any(m => string.Equals(NormalizeContainerPath(m.ContainerPath), normClone, StringComparison.OrdinalIgnoreCase));
        if (!expectedHasClone && (actual.ContainsKey(normClone) || actual.ContainsKey(NormalizeContainerPath("/clone/.git"))))
        {
            problems.Add("/clone (expected absent)");
        }

        var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? options.ContainerWorkspacePath
            : "/workspace";
        var normWks = NormalizeContainerPath(containerWks);

        if (!string.IsNullOrWhiteSpace(options.HostWorkspacePath) &&
            actual.TryGetValue(normWks, out var wks))
        {
            var expectedSource = NormalizeMountSourcePath(ResolveHostPath(options.HostWorkspacePath, options));
            if (!string.Equals(wks.Source, expectedSource, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{containerWks} (source '{wks.Source}' vs '{expectedSource}')");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath) &&
            actual.TryGetValue(normClone, out var cloneMount))
        {
            var expectedCloneSource = NormalizeMountSourcePath(ResolveHostPath(options.CanonicalClonePath, options));
            if (!string.Equals(cloneMount.Source, expectedCloneSource, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"/clone (source '{cloneMount.Source}' vs '{expectedCloneSource}')");
            }
        }

        var normAttachments = NormalizeContainerPath(AttachmentConstants.VirtualAttachmentsMountPath);
        var attachmentsSourceValidated = false;
        if (!string.IsNullOrWhiteSpace(options.AttachmentsPath) &&
            actual.TryGetValue(normAttachments, out var attachmentsMount))
        {
            var expectedAttachmentsSource = NormalizeMountSourcePath(ResolveHostPath(options.AttachmentsPath, options));
            if (!string.Equals(attachmentsMount.Source, expectedAttachmentsSource, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{AttachmentConstants.VirtualAttachmentsMountPath} (source '{attachmentsMount.Source}' vs '{expectedAttachmentsSource}')");
            }
            attachmentsSourceValidated = true;
        }

        if (options.MountTable != null)
        {
            var inspectAllMountTableEntries =
                string.IsNullOrWhiteSpace(options.HostWorkspacePath) ||
                string.Equals(options.MountTable.VirtualRootPath, SandboxMountTable.NightwatchVirtualRoot, StringComparison.OrdinalIgnoreCase);

            var expectedDestSet = expectedMounts
                .Select(m => NormalizeContainerPath(m.ContainerPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in options.MountTable.Entries)
            {
                if (!inspectAllMountTableEntries &&
                    (string.IsNullOrWhiteSpace(entry.VirtualSubPath) ||
                     string.Equals(entry.VirtualSubPath, "clone", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var normDest = NormalizeContainerPath(entry.VirtualMountPath);
                if (!expectedDestSet.Contains(normDest))
                {
                    continue;
                }

                if (attachmentsSourceValidated && string.Equals(normDest, normAttachments, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var backing = !string.IsNullOrWhiteSpace(entry.WslTargetDirectoryPath)
                    ? entry.WslTargetDirectoryPath
                    : entry.HostDirectoryPath;

                if (!string.IsNullOrWhiteSpace(backing) && actual.TryGetValue(normDest, out var actualMount))
                {
                    var expectedSource = NormalizeMountSourcePath(ResolveHostPath(backing, options));
                    if (!string.Equals(actualMount.Source, expectedSource, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{entry.VirtualMountPath} (source '{actualMount.Source}' vs '{expectedSource}')");
                    }
                }
            }
        }

        if (!options.MountHostDotnetTools)
        {
            var hostToolsDest = NormalizeContainerPath(ContainerExecutorOptions.HostDotnetToolsContainerPath);
            if (actual.ContainsKey(hostToolsDest))
            {
                problems.Add($"{ContainerExecutorOptions.HostDotnetToolsContainerPath} (expected absent)");
            }

            foreach (var (dest, info) in actual)
            {
                if (dest.EndsWith(DotnetToolsDirectorySuffix, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(info.Source))
                {
                    problems.Add($"{dest} (expected absent)");
                }
            }
        }

        if (options.IsRestrictedReadOnly || options.DisableNetwork)
        {
            var npmCacheDest = NormalizeContainerPath(ContainerExecutorOptions.ContainerNpmCachePath);
            if (actual.TryGetValue(npmCacheDest, out var npmMount) && !string.IsNullOrEmpty(npmMount.Source))
            {
                problems.Add($"{ContainerExecutorOptions.ContainerNpmCachePath} (expected absent)");
            }
        }

        if (!IsManagedBaseImage(options.ContainerImage))
        {
            var expectedDestSet = expectedMounts
                .Select(m => NormalizeContainerPath(m.ContainerPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (dest, info) in actual)
            {
                if (IsHostMultiarchSharedLibraryPath(dest) &&
                    !string.IsNullOrEmpty(info.Source) &&
                    !expectedDestSet.Contains(dest))
                {
                    problems.Add($"{dest} (expected absent)");
                }
            }
        }

        if (problems.Count > 0)
        {
            return $"Missing/Mismatched mounts: {string.Join(", ", problems)}";
        }

        return null;
    }

    private static string? EvaluateDotnetToolsTmpfsMaskDrift(
        string tmpfsInspectOutput,
        IReadOnlyList<string> maskedDotnetRoots)
    {
        var actualTmpfs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (tmpfsInspectOutput ?? string.Empty).Trim().Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var destination = NormalizeContainerPath(entry[..separatorIndex]);
            var optionsText = entry[(separatorIndex + 1)..].Trim();
            actualTmpfs[destination] = optionsText;
        }

        var problems = new List<string>();
        foreach (var dotnetRoot in maskedDotnetRoots)
        {
            var toolsDest = $"{dotnetRoot}/tools";
            if (!actualTmpfs.TryGetValue(toolsDest, out var mountOpts) ||
                !HasTmpfsOption(mountOpts, ReadOnlyTmpfsOption))
            {
                problems.Add($"{toolsDest} (missing ro tmpfs mask)");
            }
        }

        return problems.Count > 0
            ? $"Missing/Mismatched mounts: {string.Join(", ", problems)}"
            : null;
    }

    private static bool HasTmpfsOption(string mountOptions, string expectedOption)
    {
        foreach (var part in mountOptions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, expectedOption, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? EvaluateNetworkModeDrift(string networkInspectOutput)
    {
        var actualMode = (networkInspectOutput ?? string.Empty).Trim();
        return string.Equals(actualMode, NoneNetworkMode, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Network mode mismatch: '{actualMode}' vs '{NoneNetworkMode}'";
    }

    private static string? EvaluateBlockedHostsDrift(string extraHostsInspectOutput, IReadOnlyList<string> expectedBlockedHosts)
    {
        var actualHosts = new HashSet<string>(
            (extraHostsInspectOutput ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var host in expectedBlockedHosts)
        {
            var mapping = $"{host}:{BlockedHostLoopbackIp}";
            if (!actualHosts.Contains(mapping))
            {
                missing.Add(mapping);
            }
        }

        return missing.Count > 0
            ? $"Blocked hosts mismatch: missing {string.Join(", ", missing)}"
            : null;
    }

    private static string? EvaluateRequiredEnvVarDrift(string envInspectOutput, string envVarName, string expectedValue)
    {
        var expectedEntry = $"{envVarName}={expectedValue}";
        var envEntries = (envInspectOutput ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var entry in envEntries)
        {
            if (string.Equals(entry.Trim(), expectedEntry, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return $"Environment mismatch: missing {envVarName}='{expectedValue}'";
    }

    private static List<string> NormalizeBlockedHosts(IEnumerable<string>? blockedHosts)
    {
        var result = new List<string>();
        if (blockedHosts == null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawHost in blockedHosts)
        {
            if (string.IsNullOrWhiteSpace(rawHost))
            {
                continue;
            }

            var host = rawHost.Trim();
            if (host.StartsWith('-') ||
                host.Contains(':') ||
                host.Contains('/') ||
                host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                throw new ArgumentException($"Invalid blocked host entry '{host}'.", nameof(blockedHosts));
            }

            if (seen.Add(host))
            {
                result.Add(host);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> GetDotnetRootsRequiringToolsMask(
        IReadOnlyList<ContainerVolumeMount> mounts,
        ContainerExecutorOptions options)
    {
        if (options.MountHostDotnetTools)
        {
            return Array.Empty<string>();
        }

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts)
        {
            var normPath = NormalizeContainerPath(mount.ContainerPath);
            if (normPath.EndsWith(DotnetDirectorySuffix, StringComparison.OrdinalIgnoreCase) && seen.Add(normPath))
            {
                roots.Add(normPath);
            }
        }

        return roots;
    }

    public static bool IsNonRootUserDotnetRoot(string trimmedPath)
    {
        if (trimmedPath.Equals("~/.dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmedPath.StartsWith("/home/", StringComparison.OrdinalIgnoreCase) &&
               trimmedPath.EndsWith("/.dotnet", StringComparison.OrdinalIgnoreCase) &&
               !trimmedPath.Equals($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveHostPath(string path, ContainerExecutorOptions options)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (options.UseWsl)
        {
            if (Git.WslPathConverter.TryExtractWslUnc(path, out _, out var wslLinuxPath))
            {
                return wslLinuxPath;
            }

            if (path.Contains(":\\") || path.Contains(":/"))
            {
                return Git.WslPathConverter.ToWslPath(path);
            }

            if (path.StartsWith('\\') || path.Contains('\\'))
            {
                var norm = path.Replace('\\', '/');
                if (path.StartsWith('\\') && !norm.StartsWith('/'))
                {
                    norm = "/" + norm;
                }
                return norm;
            }
        }

        if (options.ExpandTildeInMounts && (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\")))
        {
            string homeDir;
            if (options.UseWsl && !string.IsNullOrWhiteSpace(options.WslUserName))
            {
                homeDir = $"/home/{options.WslUserName}";
            }
            else
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                homeDir = !string.IsNullOrWhiteSpace(profile) ? profile.Replace('\\', '/') : "~";
            }

            var subPath = path.Length > 1 ? path[1..].TrimStart('/', '\\') : string.Empty;
            var resolved = string.IsNullOrEmpty(subPath) ? homeDir : $"{homeDir}/{subPath}";
            if (options.UseWsl && (resolved.Contains(":\\") || resolved.Contains(":/")))
            {
                return Git.WslPathConverter.ToWslPath(resolved);
            }
            return resolved;
        }

        return path;
    }

    public static string ResolveContainerWorkingDirectory(string? workingDirectory, ContainerExecutorOptions options)
    {
        var defaultDir = !string.IsNullOrWhiteSpace(options.DefaultWorkingDirectory)
            ? options.DefaultWorkingDirectory
            : (!string.IsNullOrWhiteSpace(options.ContainerWorkspacePath) ? options.ContainerWorkspacePath : "/workspace");

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return defaultDir;
        }

        var targetDir = workingDirectory.Trim();
        if (targetDir == "/workspace" ||
            targetDir == SandboxMountTable.NightwatchVirtualRoot ||
            string.Equals(targetDir, options.ContainerWorkspacePath, StringComparison.OrdinalIgnoreCase))
        {
            return defaultDir;
        }

        if (targetDir == "." || targetDir == "./")
        {
            return defaultDir;
        }

        if (targetDir.Equals("/clone", StringComparison.OrdinalIgnoreCase))
        {
            return defaultDir;
        }

        if (targetDir.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = targetDir[7..].TrimStart('/');
            return string.IsNullOrEmpty(rel) ? defaultDir : $"{defaultDir}/{rel}";
        }

        if (targetDir.StartsWith("/workspace/", StringComparison.Ordinal) ||
            targetDir.StartsWith("/nightcodeforge/", StringComparison.Ordinal))
        {
            return targetDir;
        }

        if (!Path.IsPathRooted(targetDir) && !targetDir.StartsWith('/') && !targetDir.StartsWith('\\') && !targetDir.Contains(":\\") && !targetDir.Contains(":/"))
        {
            var cleanRel = targetDir.Replace('\\', '/').TrimStart('.', '/');
            return string.IsNullOrEmpty(cleanRel) ? defaultDir : $"{defaultDir}/{cleanRel}";
        }

        var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? options.ContainerWorkspacePath
            : "/workspace";

        var normTarget = targetDir.Replace('\\', '/').TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(options.AttachmentsPath) &&
            TryMatchHostOrWslPrefix(normTarget, options.AttachmentsPath, out var attachRel))
        {
            return string.IsNullOrEmpty(attachRel)
                ? AttachmentConstants.VirtualAttachmentsMountPath
                : $"{AttachmentConstants.VirtualAttachmentsMountPath}/{attachRel}";
        }

        if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath) &&
            TryMatchHostOrWslPrefix(normTarget, options.CanonicalClonePath, out var cloneRelFromHost))
        {
            return string.IsNullOrEmpty(cloneRelFromHost)
                ? defaultDir
                : $"{defaultDir}/{cloneRelFromHost}";
        }

        var hostWks = options.HostWorkspacePath;
        if (!string.IsNullOrWhiteSpace(hostWks) &&
            TryMatchHostOrWslPrefix(normTarget, hostWks, out var wksRel))
        {
            return string.IsNullOrEmpty(wksRel)
                ? defaultDir
                : $"{containerWks}/{wksRel}";
        }

        if (targetDir.Contains(":\\") || targetDir.Contains(":/") || targetDir.StartsWith("/mnt/") || targetDir.StartsWith("/home/"))
        {
            return defaultDir;
        }

        return targetDir;
    }

    public static string ComputePathEnvironmentVariable(ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pathEntries = new List<string>();

        if (options.ExtraPathDirectories != null)
        {
            pathEntries.AddRange(options.ExtraPathDirectories.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        if (options.MountHostSdks && options.SdkMountPaths != null)
        {
            var containerHomeDotnet = $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet";
            var userDotnetRoots = options.SdkMountPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && IsNonRootUserDotnetRoot(p.Trim().TrimEnd('/')))
                .Select(p => p.Trim().TrimEnd('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sdk in options.SdkMountPaths)
            {
                if (string.IsNullOrWhiteSpace(sdk)) continue;
                var trimmedSdk = sdk.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(trimmedSdk)) continue;

                if (trimmedSdk.StartsWith("/etc/", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.Equals("/etc", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.StartsWith("/usr/share/fonts", StringComparison.OrdinalIgnoreCase) ||
                    IsHostMultiarchSharedLibraryPath(trimmedSdk) ||
                    trimmedSdk.Equals("/usr/lib/snapd", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.StartsWith("/snap", StringComparison.OrdinalIgnoreCase) ||
                    IsRustupHomePath(trimmedSdk))
                {
                    continue;
                }

                if (trimmedSdk.EndsWith("/.dotnet/tools", StringComparison.OrdinalIgnoreCase))
                {
                    if (!options.MountHostDotnetTools)
                    {
                        continue;
                    }

                    var parentDotnet = trimmedSdk[..^"/tools".Length];
                    if (userDotnetRoots.Contains(parentDotnet))
                    {
                        if (!options.IsRestrictedReadOnly)
                        {
                            pathEntries.Add($"{containerHomeDotnet}/tools");
                        }
                        continue;
                    }
                }

                if (trimmedSdk.EndsWith("/.dotnet", StringComparison.OrdinalIgnoreCase))
                {
                    if (userDotnetRoots.Contains(trimmedSdk))
                    {
                        if (!options.IsRestrictedReadOnly)
                        {
                            if (options.MountHostDotnetTools)
                            {
                                pathEntries.Add($"{containerHomeDotnet}/tools");
                            }
                            pathEntries.Add(containerHomeDotnet);
                        }
                    }
                    else
                    {
                        if (options.MountHostDotnetTools)
                        {
                            pathEntries.Add($"{trimmedSdk}/tools");
                        }
                        pathEntries.Add(trimmedSdk);
                    }
                }
                else if (trimmedSdk.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase) || trimmedSdk == "/usr/share/dotnet")
                {
                    pathEntries.Add(trimmedSdk);
                    if (options.MountHostDotnetTools)
                    {
                        pathEntries.Add("/root/.dotnet/tools");
                    }
                }
                else if (trimmedSdk.Contains("jvm", StringComparison.OrdinalIgnoreCase))
                {
                    if (trimmedSdk.Equals("/usr/lib/jvm", StringComparison.OrdinalIgnoreCase))
                    {
                        pathEntries.Add("/usr/lib/jvm/default-java/bin");
                    }
                    else
                    {
                        pathEntries.Add($"{trimmedSdk}/bin");
                    }
                }
                else if (trimmedSdk.Contains("android", StringComparison.OrdinalIgnoreCase))
                {
                    if (trimmedSdk.EndsWith("/cmdline-tools/latest", StringComparison.OrdinalIgnoreCase))
                    {
                        pathEntries.Add($"{trimmedSdk}/bin");
                    }
                    else if (trimmedSdk.EndsWith("/platform-tools", StringComparison.OrdinalIgnoreCase))
                    {
                        pathEntries.Add(trimmedSdk);
                    }
                    else
                    {
                        pathEntries.Add($"{trimmedSdk}/platform-tools");
                        pathEntries.Add($"{trimmedSdk}/cmdline-tools/latest/bin");
                    }
                }
                else if (trimmedSdk.Contains("/toolchains/", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add(trimmedSdk.EndsWith("/bin", StringComparison.OrdinalIgnoreCase)
                        ? trimmedSdk
                        : $"{trimmedSdk}/bin");
                }
                else if (trimmedSdk.EndsWith("/bin", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add(trimmedSdk);
                }
                else if (trimmedSdk.Contains("cargo", StringComparison.OrdinalIgnoreCase) ||
                         trimmedSdk.Contains("go", StringComparison.OrdinalIgnoreCase) ||
                         trimmedSdk.Contains("node", StringComparison.OrdinalIgnoreCase) ||
                         trimmedSdk.Contains("nvm", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add($"{trimmedSdk}/bin");
                    pathEntries.Add(trimmedSdk);
                }
                else
                {
                    pathEntries.Add(trimmedSdk);
                }
            }
        }
        else
        {
            pathEntries.Add("/usr/share/dotnet");
            if (options.MountHostDotnetTools)
            {
                pathEntries.Add("/root/.dotnet/tools");
            }
        }

        if (options.MountHostDotnetTools)
        {
            pathEntries.Add(ContainerExecutorOptions.HostDotnetToolsContainerPath);
            pathEntries.Add($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools");
        }
        pathEntries.Add($"{ContainerExecutorOptions.ContainerHomePath}/.local/bin");
        pathEntries.Add($"{ContainerExecutorOptions.ContainerHomePath}/.cargo/bin");
        if (options.MountHostDotnetTools)
        {
            pathEntries.Add("/root/.dotnet/tools");
        }
        pathEntries.Add("/root/.local/bin");
        pathEntries.Add("/root/.cargo/bin");
        pathEntries.Add("/root/.npm-global/bin");

        pathEntries.Add("/usr/local/sbin");
        pathEntries.Add("/usr/local/bin");
        pathEntries.Add("/usr/sbin");
        pathEntries.Add("/usr/bin");
        pathEntries.Add("/sbin");
        pathEntries.Add("/bin");

        var distinct = new List<string>();
        foreach (var entry in pathEntries)
        {
            var trimmed = entry.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !distinct.Contains(trimmed, StringComparer.Ordinal))
            {
                distinct.Add(trimmed);
            }
        }

        return string.Join(":", distinct);
    }

    public static string? ResolveRustupHomePath(ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.MountHostSdks || options.SdkMountPaths == null)
        {
            return null;
        }

        foreach (var sdk in options.SdkMountPaths)
        {
            if (string.IsNullOrWhiteSpace(sdk))
            {
                continue;
            }

            var trimmed = sdk.Trim().TrimEnd('/');
            if (IsRustupHomePath(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> is a host OS multiarch shared-library directory
    /// (such as <c>/usr/lib/x86_64-linux-gnu</c>, <c>/usr/lib/aarch64-linux-gnu</c>, or <c>/lib/*-linux-gnu*</c>).
    /// </summary>
    public static bool IsHostMultiarchSharedLibraryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var norm = NormalizeContainerPath(path);
        string? remainder = null;
        if (norm.StartsWith("/usr/lib/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm["/usr/lib/".Length..];
        }
        else if (norm.StartsWith("/lib/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = norm["/lib/".Length..];
        }

        if (string.IsNullOrEmpty(remainder))
        {
            return false;
        }

        var slashIndex = remainder.IndexOf('/');
        var firstSegment = slashIndex >= 0 ? remainder[..slashIndex] : remainder;
        return firstSegment.Contains("-linux-gnu", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRustupHomePath(string trimmedPath) =>
        trimmedPath.EndsWith("/.rustup", StringComparison.OrdinalIgnoreCase) ||
        trimmedPath.EndsWith("/rustup", StringComparison.OrdinalIgnoreCase);

    public static List<string> BuildDockerRunArgs(ContainerExecutorOptions options)
    {
        var args = new List<string> { "run", "-d" };

        if (!string.IsNullOrWhiteSpace(options.ContainerNameOrId))
        {
            args.AddRange(["--name", options.ContainerNameOrId]);
        }

        var isManagedBaseImage = IsManagedBaseImage(options.ContainerImage);
        var containerHome = isManagedBaseImage
            ? ContainerExecutorOptions.ContainerHomePath
            : ContainerExecutorOptions.CustomImageDefaultHomePath;
        var containerLocale = isManagedBaseImage
            ? ContainerExecutorOptions.ManagedBaseImageLocale
            : ContainerExecutorOptions.CustomImageFallbackLocale;

        var computedPath = ComputePathEnvironmentVariable(options);
        args.AddRange(["-e", $"PATH={computedPath}"]);
        args.AddRange(["-e", $"HOME={containerHome}"]);
        args.AddRange(["-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0"]);
        args.AddRange(["-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"]);
        args.AddRange(["-e", $"{ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}={ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}"]);
        args.AddRange(["-e", $"LANG={containerLocale}"]);
        args.AddRange(["-e", $"LC_ALL={containerLocale}"]);

        var dotnetSdkPath = options.SdkMountPaths?
            .FirstOrDefault(p => p.Equals("/usr/lib/dotnet", StringComparison.OrdinalIgnoreCase))
            ?? options.SdkMountPaths?
                .FirstOrDefault(p => p.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase) || p == "/usr/share/dotnet")
            ?? (options.SdkMountPaths != null && !options.IsRestrictedReadOnly && options.SdkMountPaths.Any(p => !string.IsNullOrWhiteSpace(p) && IsNonRootUserDotnetRoot(p.Trim().TrimEnd('/')))
                ? $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet"
                : null)
            ?? "/usr/share/dotnet";
        args.AddRange(["-e", $"DOTNET_ROOT={dotnetSdkPath}"]);

        var rustupHome = ResolveRustupHomePath(options);
        if (!string.IsNullOrWhiteSpace(rustupHome))
        {
            args.AddRange(["-e", $"RUSTUP_HOME={rustupHome}"]);
        }

        if (options.MountPackageCaches)
        {
            var isOffline = options.IsRestrictedReadOnly || options.DisableNetwork;
            foreach (var (name, value) in ContainerExecutorOptions.PackageCacheEnvironment)
            {
                if (string.Equals(name, ContainerExecutorOptions.CargoHomeEnvVarName, StringComparison.Ordinal) &&
                    string.IsNullOrWhiteSpace(options.CargoCachePath))
                {
                    continue;
                }

                var effectiveValue = isOffline && string.Equals(name, ContainerExecutorOptions.NpmCacheEnvVarName, StringComparison.Ordinal)
                    ? ContainerExecutorOptions.FallbackNpmCachePath
                    : value;

                args.AddRange(["-e", $"{name}={effectiveValue}"]);
            }
        }

        var mounts = BuildVolumeMounts(options);
        foreach (var mount in mounts)
        {
            args.Add("-v");
            args.Add(mount.ToString());
        }

        foreach (var dotnetRoot in GetDotnetRootsRequiringToolsMask(mounts, options))
        {
            args.AddRange(["--tmpfs", $"{dotnetRoot}/tools:ro,noexec,nosuid,size=1m"]);
        }

        var workDir = !string.IsNullOrWhiteSpace(options.DefaultWorkingDirectory)
            ? options.DefaultWorkingDirectory
            : options.ContainerWorkspacePath;
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            args.AddRange(["-w", workDir]);
        }

        var effectiveUser = options.IsRestrictedReadOnly
            ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
            : options.UserUidGid;

        if (!string.IsNullOrWhiteSpace(effectiveUser))
        {
            args.AddRange(["--user", effectiveUser]);
        }

        if (!string.IsNullOrWhiteSpace(options.SharedMemorySize))
        {
            args.AddRange(["--shm-size", options.SharedMemorySize]);
        }

        args.AddRange(["--ulimit", "core=0"]);
        args.Add("--init");

        if (options.PidsLimit > 0)
        {
            args.AddRange(["--pids-limit", options.PidsLimit.ToString()]);
        }

        if (options.IsRestrictedReadOnly || options.DisableNetwork)
        {
            args.AddRange(["--network", "none"]);
            args.AddRange(["-e", $"{ContainerExecutorOptions.NpmConfigOfflineEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}"]);
            args.AddRange(["-e", $"{ContainerExecutorOptions.NpmConfigOfflineUpperEnvVarName}={ContainerExecutorOptions.OfflineTrueValue}"]);
        }
        else if (options.BlockedHosts is { Count: > 0 })
        {
            foreach (var host in NormalizeBlockedHosts(options.BlockedHosts))
            {
                args.AddRange(["--add-host", $"{host}:{BlockedHostLoopbackIp}"]);
            }
        }

        if (options.IsRestrictedReadOnly)
        {
            args.AddRange(["--read-only"]);
            if (!mounts.Any(m => string.Equals(NormalizeContainerPath(m.ContainerPath), "/tmp", StringComparison.OrdinalIgnoreCase)))
            {
                args.AddRange(["--tmpfs", "/tmp:rw,noexec,nosuid,size=32m"]);
            }
            args.AddRange(["--tmpfs", $"{ContainerExecutorOptions.ContainerHomePath}:rw,noexec,nosuid,size=16m"]);
            args.AddRange(["--cap-drop=ALL"]);
            args.AddRange(["--security-opt", "no-new-privileges:true"]);
        }

        var image = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;
        args.Add(image);

        args.AddRange(["tail", "-f", "/dev/null"]);
        return args;
    }

    public static ProcessStartSpec BuildDockerProcessSpec(
        ContainerExecutorOptions options,
        IEnumerable<string> dockerArgs,
        int timeoutSeconds = 10,
        string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dockerArgs);

        var argsList = dockerArgs.ToList();

        if (options.UseWsl)
        {
            var wslArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.AddRange(["-d", options.WslDistribution.Trim()]);
            }
            wslArgs.AddRange(["--", options.ShellExecutable]);
            wslArgs.AddRange(argsList);

            return new ProcessStartSpec
            {
                FileName = options.WslExecutable,
                ArgumentList = wslArgs,
                WorkingDirectory = workingDirectory,
                TimeoutSeconds = timeoutSeconds
            };
        }

        return new ProcessStartSpec
        {
            FileName = options.ShellExecutable,
            ArgumentList = argsList,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = timeoutSeconds
        };
    }

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
                ? $"-d {wslDistribution.Trim()} "
                : string.Empty;
            return (wslExecutable, $"{distroArg}-- {shellExecutable} {dockerCommand}".Trim());
        }

        return (shellExecutable, dockerCommand);
    }

    public static ProcessStartSpec BuildDockerRunProcessSpec(ContainerExecutorOptions options)
    {
        var runArgs = BuildDockerRunArgs(options);

        if (options.UseWsl)
        {
            var wslArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.AddRange(["-d", options.WslDistribution]);
            }
            wslArgs.AddRange(["--", options.ShellExecutable]);
            wslArgs.AddRange(runArgs);

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
            ArgumentList = runArgs,
            TimeoutSeconds = options.DefaultTimeoutSeconds
        };
    }

    public static (string Executable, string Arguments) BuildDockerRunCommand(ContainerExecutorOptions options)
    {
        var runArgs = BuildDockerRunArgs(options);
        var formattedArgs = string.Join(" ", runArgs.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
        return BuildDockerCommand(formattedArgs, options.UseWsl, options.WslDistribution, options.ShellExecutable, options.WslExecutable);
    }

    public static ProcessStartInfo BuildDockerRunProcessStartInfo(ContainerExecutorOptions options)
    {
        var (exe, args) = BuildDockerRunCommand(options);
        return new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    /// Returns <see langword="true"/> when the configured image is a managed NightCodeForge base image (<c>ncf-agent-base*</c> or <c>nightcodeforge-agent*</c>).
    /// </summary>
    public static bool IsManagedBaseImage(string? containerImage)
    {
        var image = !string.IsNullOrWhiteSpace(containerImage)
            ? containerImage.Trim()
            : ContainerExecutorOptions.DefaultAgentBaseImage;

        return image.StartsWith("ncf-agent-base", StringComparison.OrdinalIgnoreCase) ||
               image.StartsWith("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureImageReady(
        IProcessRunner processRunner,
        ContainerExecutorOptions options,
        ILogger logger)
    {
        var image = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;

        if (!IsManagedBaseImage(image))
        {
            return;
        }

        try
        {
            if (IsImageFresh(processRunner, image, options, TimeSpan.FromDays(30), logger))
            {
                return;
            }

            var altImage = image.StartsWith("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase)
                ? ContainerExecutorOptions.DefaultAgentBaseImage
                : ContainerExecutorOptions.AlternateAgentBaseImage;

            if (IsImageFresh(processRunner, altImage, options, TimeSpan.FromDays(30), logger))
            {
                var tagSpec = BuildDockerProcessSpec(options, ["tag", altImage, image], timeoutSeconds: 10);
                var tagResult = processRunner.Run(tagSpec);
                if (tagResult.ExitCode == 0)
                {
                    logger.LogInformation("Tagged existing image {AltImage} as {Image}", altImage, image);
                    return;
                }
            }

            BuildLocalAgentBaseImage(processRunner, image, options, logger);
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement Run - ignore
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ensure container image {Image} is ready; attempting to proceed", image);
        }
    }

    public static bool IsImageFresh(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        TimeSpan maxAge,
        ILogger? logger = null)
    {
        try
        {
            var inspectSpec = BuildDockerProcessSpec(
                options,
                ["image", "inspect", "-f", "{{.Created}}", imageName],
                timeoutSeconds: 5);

            var inspectResult = processRunner.Run(inspectSpec);
            if (inspectResult.ExitCode == 0)
            {
                var createdStr = inspectResult.StandardOutput.Trim();
                if (DateTime.TryParse(createdStr, null, DateTimeStyles.RoundtripKind, out var createdDate))
                {
                    var age = DateTime.UtcNow - createdDate.ToUniversalTime();
                    return age >= TimeSpan.Zero && age < maxAge;
                }
            }
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement synchronous Run; treat image as not verified fresh.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Docker image freshness inspection failed for {ImageName}", imageName);
        }

        return false;
    }

    public static void BuildLocalAgentBaseImage(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        ILogger logger)
    {
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), $"ncf-build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var dockerfilePath = Path.Combine(tempDir, "Dockerfile");
            File.WriteAllText(dockerfilePath, ContainerExecutorOptions.DefaultAgentBaseDockerfile.Replace("\r\n", "\n"));

            var buildContext = options.UseWsl
                ? Git.WslPathConverter.ToWslPath(tempDir)
                : tempDir;

            var buildSpec = BuildDockerProcessSpec(
                options,
                ["build", "-t", imageName, buildContext],
                timeoutSeconds: 600);

            logger.LogInformation("Building local base container image {Image} with git, ripgrep, python3, and nodejs...", imageName);
            var buildResult = processRunner.Run(buildSpec);

            if (buildResult.ExitCode != 0)
            {
                logger.LogWarning("Failed to build local base image {Image} (exit code {ExitCode}): {Error}",
                    imageName, buildResult.ExitCode, buildResult.StandardError);
            }
            else
            {
                logger.LogInformation("Successfully built local base image {Image}.", imageName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while building local base container image {Image}", imageName);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception deleteEx)
                {
                    logger.LogWarning(deleteEx, "Failed to clean up temporary container build directory '{TempDir}'", tempDir);
                }
            }
        }
    }

    private static string NormalizeContainerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim().Replace('\\', '/');
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static bool IsMatchingImage(string actual, string expected)
    {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (expected.IndexOf(':') < 0 && string.Equals(actual, expected + ":latest", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (actual.IndexOf(':') < 0 && string.Equals(actual + ":latest", expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string NormalizeMountSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (norm.StartsWith("/host_mnt/", StringComparison.OrdinalIgnoreCase))
        {
            norm = "/mnt/" + norm["/host_mnt/".Length..];
        }
        else if (norm.StartsWith("/run/desktop/mnt/host/", StringComparison.OrdinalIgnoreCase))
        {
            norm = "/mnt/" + norm["/run/desktop/mnt/host/".Length..];
        }
        return Git.WslPathConverter.ToWslPath(norm).TrimEnd('/');
    }

    private static bool TryMatchHostOrWslPrefix(string normTarget, string basePath, out string relativeSubPath)
    {
        var normBase = basePath.Replace('\\', '/').TrimEnd('/');
        var normBaseWsl = Git.WslPathConverter.ToWslPath(basePath).Replace('\\', '/').TrimEnd('/');
        var normTargetWsl = Git.WslPathConverter.ToWslPath(normTarget).Replace('\\', '/').TrimEnd('/');

        if (normTarget.Equals(normBase, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(normBaseWsl) &&
             (normTarget.Equals(normBaseWsl, StringComparison.OrdinalIgnoreCase) ||
              normTargetWsl.Equals(normBaseWsl, StringComparison.OrdinalIgnoreCase))))
        {
            relativeSubPath = string.Empty;
            return true;
        }

        if (normTarget.StartsWith(normBase + "/", StringComparison.OrdinalIgnoreCase))
        {
            relativeSubPath = normTarget[(normBase.Length + 1)..];
            return true;
        }

        if (!string.IsNullOrEmpty(normBaseWsl))
        {
            if (normTarget.StartsWith(normBaseWsl + "/", StringComparison.OrdinalIgnoreCase))
            {
                relativeSubPath = normTarget[(normBaseWsl.Length + 1)..];
                return true;
            }

            if (normTargetWsl.StartsWith(normBaseWsl + "/", StringComparison.OrdinalIgnoreCase))
            {
                relativeSubPath = normTargetWsl[(normBaseWsl.Length + 1)..];
                return true;
            }
        }

        relativeSubPath = string.Empty;
        return false;
    }
}

