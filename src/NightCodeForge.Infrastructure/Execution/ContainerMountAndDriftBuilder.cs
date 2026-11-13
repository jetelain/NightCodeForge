using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

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
                    isReadOnly: options.IsRestrictedReadOnly || entry.IsReadOnly);
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
                    var effectiveReadOnly = options.IsRestrictedReadOnly || entry.IsReadOnly;

                    var isInsideHostWorkspace =
                        normSource.Equals(normHost, StringComparison.OrdinalIgnoreCase) ||
                        normSource.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase);

                    var isCanonicalClone =
                        !string.IsNullOrEmpty(normCloneResolved) &&
                        normSource.Equals(normCloneResolved, StringComparison.OrdinalIgnoreCase);

                    // Skip subpaths already exposed identically through the parent /workspace bind mount
                    if ((isInsideHostWorkspace || isCanonicalClone) && effectiveReadOnly == options.IsRestrictedReadOnly)
                    {
                        continue;
                    }

                    AddMount(resolvedSource, entry.VirtualMountPath, isReadOnly: effectiveReadOnly);
                }
            }
        }

        // 2. Package caches (NuGet, npm, pnpm, uv, Maven, Playwright)
        if (options.MountPackageCaches)
        {
            if (!string.IsNullOrWhiteSpace(options.NugetPackageCachePath))
            {
                AddMount(ResolveHostPath(options.NugetPackageCachePath, options), "/cache/nuget", isReadOnly: false);
            }

            if (!string.IsNullOrWhiteSpace(options.NpmCachePath))
            {
                AddMount(ResolveHostPath(options.NpmCachePath, options), "/cache/npm", isReadOnly: false);
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
        }

        // 3. Heavy SDK mounts
        if (options.MountHostSdks && options.SdkMountPaths != null)
        {
            var containerHomeDotnet = $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet";
            var hasMappedHomeDotnet = false;
            var userDotnetRoots = options.SdkMountPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && IsNonRootUserDotnetRoot(p.TrimEnd('/')))
                .Select(p => p.TrimEnd('/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var sdkPath in options.SdkMountPaths)
            {
                if (string.IsNullOrWhiteSpace(sdkPath))
                {
                    continue;
                }

                var trimmedSdk = sdkPath.TrimEnd('/');
                if (trimmedSdk.EndsWith("/.dotnet/tools", StringComparison.OrdinalIgnoreCase))
                {
                    var parentDotnet = trimmedSdk[..^"/tools".Length];
                    if (userDotnetRoots.Contains(parentDotnet))
                    {
                        continue;
                    }
                }

                var resolvedHostSdk = ResolveHostPath(sdkPath, options);
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
                AddMount(
                    ResolveHostPath(customMount.HostPath, options),
                    customMount.ContainerPath,
                    isReadOnly: customMount.IsReadOnly);
            }
        }

        return mounts;
    }

    /// <summary>
    /// Compares the existing container's mounts, image, and user with the expected configuration asynchronously.
    /// Returns a description of the drift, or null when the container can be safely reused.
    /// </summary>
    public static async Task<string?> DetectContainerDriftAsync(
        IProcessRunner processRunner,
        ContainerExecutorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(options);

        var mountResult = await processRunner.RunAsync(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", MountInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: 5), cancellationToken);
        if (mountResult.ExitCode != 0)
        {
            return $"Failed to inspect mounts of container (exit code {mountResult.ExitCode}): {mountResult.StandardError}";
        }

        var driftFromMounts = EvaluateMountDrift(mountResult.StandardOutput, options);
        if (driftFromMounts != null)
        {
            return driftFromMounts;
        }

        var expectedImage = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;
        var imageResult = await processRunner.RunAsync(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", ImageInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: 5), cancellationToken);
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
            var userResult = await processRunner.RunAsync(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", UserInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: 5), cancellationToken);
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

        return null;
    }

    /// <summary>
    /// Compares the existing container's mounts, image, and user with the expected configuration synchronously.
    /// Returns a description of the drift, or null when the container can be safely reused.
    /// </summary>
    public static string? DetectContainerDrift(IProcessRunner processRunner, ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(options);

        var mountResult = processRunner.Run(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", MountInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: 5));
        if (mountResult.ExitCode != 0)
        {
            return $"Failed to inspect mounts of container (exit code {mountResult.ExitCode}): {mountResult.StandardError}";
        }

        var driftFromMounts = EvaluateMountDrift(mountResult.StandardOutput, options);
        if (driftFromMounts != null)
        {
            return driftFromMounts;
        }

        var expectedImage = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;
        var imageResult = processRunner.Run(BuildDockerProcessSpec(
            options,
            ["inspect", "-f", ImageInspectFormat, options.ContainerNameOrId],
            timeoutSeconds: 5));
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
            var userResult = processRunner.Run(BuildDockerProcessSpec(
                options,
                ["inspect", "-f", UserInspectFormat, options.ContainerNameOrId],
                timeoutSeconds: 5));
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

        return null;
    }

    private static string? EvaluateMountDrift(string mountInspectOutput, ContainerExecutorOptions options)
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

        var expectedMounts = BuildVolumeMounts(options);
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

        if (problems.Count > 0)
        {
            return $"Missing/Mismatched mounts: {string.Join(", ", problems)}";
        }

        return null;
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

        if (!Path.IsPathRooted(targetDir) && !targetDir.StartsWith('/') && !targetDir.StartsWith('\\'))
        {
            var cleanRel = targetDir.Replace('\\', '/').TrimStart('.', '/');
            return string.IsNullOrEmpty(cleanRel) ? defaultDir : $"{defaultDir}/{cleanRel}";
        }

        var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? options.ContainerWorkspacePath
            : "/workspace";

        var normTarget = targetDir.Replace('\\', '/').TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath))
        {
            var normClone = options.CanonicalClonePath.Replace('\\', '/').TrimEnd('/');
            if (normTarget.Equals(normClone, StringComparison.OrdinalIgnoreCase))
            {
                return defaultDir;
            }
            if (normTarget.StartsWith(normClone + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normClone.Length + 1)..];
                return $"{defaultDir}/{rel}";
            }
        }

        var hostWks = options.HostWorkspacePath;
        if (!string.IsNullOrWhiteSpace(hostWks))
        {
            var normHost = hostWks.Replace('\\', '/').TrimEnd('/');
            var normHostWsl = Git.WslPathConverter.ToWslPath(hostWks).Replace('\\', '/').TrimEnd('/');

            if (normTarget.Equals(normHost, StringComparison.OrdinalIgnoreCase) ||
                normTarget.Equals(normHostWsl, StringComparison.OrdinalIgnoreCase))
            {
                return defaultDir;
            }

            if (normTarget.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normHost.Length + 1)..];
                return $"{containerWks}/{rel}";
            }

            if (normTarget.StartsWith(normHostWsl + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normHostWsl.Length + 1)..];
                return $"{containerWks}/{rel}";
            }
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
                    trimmedSdk.Equals("/usr/lib/x86_64-linux-gnu", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.Equals("/usr/lib/aarch64-linux-gnu", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.Equals("/usr/lib/snapd", StringComparison.OrdinalIgnoreCase) ||
                    trimmedSdk.StartsWith("/snap", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (trimmedSdk.EndsWith("/.dotnet/tools", StringComparison.OrdinalIgnoreCase))
                {
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
                            pathEntries.Add($"{containerHomeDotnet}/tools");
                            pathEntries.Add(containerHomeDotnet);
                        }
                    }
                    else
                    {
                        pathEntries.Add($"{trimmedSdk}/tools");
                        pathEntries.Add(trimmedSdk);
                    }
                }
                else if (trimmedSdk.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase) || trimmedSdk == "/usr/share/dotnet")
                {
                    pathEntries.Add(trimmedSdk);
                    pathEntries.Add("/root/.dotnet/tools");
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
            pathEntries.Add("/root/.dotnet/tools");
        }

        pathEntries.Add(ContainerExecutorOptions.HostDotnetToolsContainerPath);
        pathEntries.Add($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools");
        pathEntries.Add($"{ContainerExecutorOptions.ContainerHomePath}/.local/bin");
        pathEntries.Add("/root/.dotnet/tools");
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

    public static List<string> BuildDockerRunArgs(ContainerExecutorOptions options)
    {
        var args = new List<string> { "run", "-d" };

        if (!string.IsNullOrWhiteSpace(options.ContainerNameOrId))
        {
            args.AddRange(["--name", options.ContainerNameOrId]);
        }

        var computedPath = ComputePathEnvironmentVariable(options);
        args.AddRange(["-e", $"PATH={computedPath}"]);
        args.AddRange(["-e", $"HOME={ContainerExecutorOptions.ContainerHomePath}"]);
        args.AddRange(["-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0"]);
        args.AddRange(["-e", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"]);
        args.AddRange(["-e", "LANG=en_US.UTF-8"]);
        args.AddRange(["-e", "LC_ALL=en_US.UTF-8"]);

        var dotnetSdkPath = options.SdkMountPaths?
            .FirstOrDefault(p => p.Equals("/usr/lib/dotnet", StringComparison.OrdinalIgnoreCase))
            ?? options.SdkMountPaths?
                .FirstOrDefault(p => p.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase) || p == "/usr/share/dotnet")
            ?? (options.SdkMountPaths != null && !options.IsRestrictedReadOnly && options.SdkMountPaths.Any(p => !string.IsNullOrWhiteSpace(p) && IsNonRootUserDotnetRoot(p.Trim().TrimEnd('/')))
                ? $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet"
                : null)
            ?? "/usr/share/dotnet";
        args.AddRange(["-e", $"DOTNET_ROOT={dotnetSdkPath}"]);

        if (options.MountPackageCaches)
        {
            foreach (var (name, value) in ContainerExecutorOptions.PackageCacheEnvironment)
            {
                args.AddRange(["-e", $"{name}={value}"]);
            }
        }

        var mounts = BuildVolumeMounts(options);
        foreach (var mount in mounts)
        {
            args.Add("-v");
            args.Add(mount.ToString());
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

        if (options.IsRestrictedReadOnly)
        {
            args.AddRange(["--network", "none"]);
            args.AddRange(["--read-only"]);
            args.AddRange(["--tmpfs", "/tmp:rw,noexec,nosuid,size=32m"]);
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

    public static void EnsureImageReady(
        IProcessRunner processRunner,
        ContainerExecutorOptions options,
        ILogger logger)
    {
        var image = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;

        bool isManagedBaseImage =
            image.StartsWith("ncf-agent-base", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase);

        if (!isManagedBaseImage)
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
}

