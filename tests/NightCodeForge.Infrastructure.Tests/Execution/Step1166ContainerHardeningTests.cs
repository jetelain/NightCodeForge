using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class Step1166ContainerHardeningTests
{
    [Fact]
    public async Task DetectContainerDrift_DetectsReverseOnlineTransitions_ReadonlyRootfs_AndProtectCloneRemoval()
    {
        // 1. Offline -> Online transition: stale network='none', npm_config_offline=true, NPM_CONFIG_OFFLINE=true,
        //    and stale fallback npm_config_cache=/tmp/ncf-npm-cache must all be flagged.
        var onlineOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-online",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = true,
            NugetPackageCachePath = null,
            PnpmCachePath = null,
            UvCachePath = null,
            MavenCachePath = null,
            PlaywrightCachePath = null,
            CargoCachePath = null,
            NpmCachePath = "/home/user/.npm",
            MountHostSdks = false,
            DisableNetwork = false,
            IsRestrictedReadOnly = false,
            ProtectCloneWorkingTree = false,
            BlockedHosts = []
        };

        var expectedMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(onlineOptions);
        var mountsOut = string.Concat(expectedMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

        static Mock<IProcessRunner> CreateOnlineDriftRunner(
            string mountsInspectOut,
            string readonlyRootfsOut = "false",
            string networkModeOut = "bridge",
            string extraHostsOut = "",
            string envOut = "npm_config_cache=/cache/npm\n",
            string tmpfsOut = "")
        {
            var runner = new Mock<IProcessRunner>();
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, mountsInspectOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, mountsInspectOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, tmpfsOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, tmpfsOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}"))))
                .Returns(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.ReadonlyRootfs}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, readonlyRootfsOut + "\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.ReadonlyRootfs}}"))))
                .Returns(new ProcessExecutionResult(0, readonlyRootfsOut + "\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.NetworkMode}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, networkModeOut + "\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.NetworkMode}}"))))
                .Returns(new ProcessExecutionResult(0, networkModeOut + "\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .HostConfig.ExtraHosts}}{{.}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, extraHostsOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .HostConfig.ExtraHosts}}{{.}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, extraHostsOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, envOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}"))))
                .Returns(new ProcessExecutionResult(0, envOut, string.Empty));
            return runner;
        }

        // 1a. Stale readonly rootfs
        var staleReadonly = CreateOnlineDriftRunner(mountsOut, readonlyRootfsOut: "true");
        Assert.Contains("Readonly rootfs mismatch", await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleReadonly.Object, onlineOptions));
        Assert.Contains("Readonly rootfs mismatch", ContainerMountAndDriftBuilder.DetectContainerDrift(staleReadonly.Object, onlineOptions));

        // 1b. Stale network='none'
        var staleNetworkNone = CreateOnlineDriftRunner(mountsOut, networkModeOut: "none");
        Assert.Contains("online network", await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleNetworkNone.Object, onlineOptions));

        // 1c. Stale npm_config_offline=true / NPM_CONFIG_OFFLINE=true
        var staleOfflineLower = CreateOnlineDriftRunner(mountsOut, envOut: "npm_config_offline=true\nnpm_config_cache=/cache/npm\n");
        Assert.Contains("stale npm_config_offline='true'", await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleOfflineLower.Object, onlineOptions));

        var staleOfflineUpper = CreateOnlineDriftRunner(mountsOut, envOut: "NPM_CONFIG_OFFLINE=true\nnpm_config_cache=/cache/npm\n");
        Assert.Contains("stale NPM_CONFIG_OFFLINE='true'", await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleOfflineUpper.Object, onlineOptions));

        // 1d. Stale fallback npm_config_cache=/tmp/ncf-npm-cache when online cache expected
        var staleNpmCache = CreateOnlineDriftRunner(mountsOut, envOut: $"npm_config_cache={ContainerExecutorOptions.FallbackNpmCachePath}\n");
        Assert.Contains("npm_config_cache='/cache/npm'", await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleNpmCache.Object, onlineOptions));

        // 1e. Stale fallback npm_config_cache=/tmp/ncf-npm-cache when online with MountPackageCaches = false
        var onlineNoCacheOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-online-nocache",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = false,
            IsRestrictedReadOnly = false,
            ProtectCloneWorkingTree = false,
            BlockedHosts = []
        };
        var noCacheMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(onlineNoCacheOptions);
        var noCacheMountsOut = string.Concat(noCacheMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));
        var staleNoCacheFallbackRunner = CreateOnlineDriftRunner(
            noCacheMountsOut,
            envOut: $"npm_config_cache={ContainerExecutorOptions.FallbackNpmCachePath}\n");
        Assert.Contains(
            $"stale {ContainerExecutorOptions.NpmCacheEnvVarName}='{ContainerExecutorOptions.FallbackNpmCachePath}'",
            await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleNoCacheFallbackRunner.Object, onlineNoCacheOptions));

        // 2. Cleared and reduced BlockedHosts detection (ignoring non-loopback entries)
        var staleExtraHostRunner = CreateOnlineDriftRunner(mountsOut, extraHostsOut: "host.docker.internal:host-gateway;github.com:127.0.0.1;");
        var clearedHostsDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleExtraHostRunner.Object, onlineOptions);
        Assert.Contains("unexpected github.com:127.0.0.1", clearedHostsDrift);

        // Non-loopback host-gateway entry alone does not trigger drift
        var hostGatewayOnlyRunner = CreateOnlineDriftRunner(mountsOut, extraHostsOut: "host.docker.internal:host-gateway;");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(hostGatewayOnlyRunner.Object, onlineOptions));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(hostGatewayOnlyRunner.Object, onlineOptions));

        // Reduced BlockedHosts flags stale entry
        var reducedHostsOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-reduced-hosts",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            ProtectCloneWorkingTree = false,
            BlockedHosts = ["github.com"]
        };
        var reducedHostsRunner = CreateOnlineDriftRunner(
            mountsOut,
            extraHostsOut: "github.com:127.0.0.1;api.github.com:127.0.0.1;",
            envOut: string.Empty);
        var reducedHostsDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(reducedHostsRunner.Object, reducedHostsOptions);
        Assert.Contains("unexpected api.github.com:127.0.0.1", reducedHostsDrift);

        // 3. Stale ProtectCloneWorkingTree (:ro /workspace/clone and /workspace/clone/.git submounts when ProtectCloneWorkingTree = false)
        var staleCloneSubmountRunner = CreateOnlineDriftRunner(mountsOut + "/workspace/clone=/tmp/bench-wks/clone=false;");
        var staleCloneDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleCloneSubmountRunner.Object, onlineOptions);
        Assert.Contains("/workspace/clone (expected absent or rw)", staleCloneDrift);

        var staleCloneGitSubmountRunner = CreateOnlineDriftRunner(mountsOut + "/workspace/clone/.git=/tmp/bench-wks/clone/.git=true;");
        var staleCloneGitDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleCloneGitSubmountRunner.Object, onlineOptions);
        Assert.Contains("/workspace/clone/.git (expected absent)", staleCloneGitDrift);

        // 4. Re-enabled MountHostDotnetTools flags stale tmpfs mask over <dotnet-root>/tools
        var dotnetToolsEnabledOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-tools-on",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.dotnet"],
            MountHostDotnetTools = true
        };
        var dotnetMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(dotnetToolsEnabledOptions);
        var dotnetMountsOut = string.Concat(dotnetMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

        var staleTmpfsRunner = CreateOnlineDriftRunner(
            dotnetMountsOut,
            envOut: string.Empty,
            tmpfsOut: $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools=ro,noexec,nosuid,size=1m;");
        var staleTmpfsDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleTmpfsRunner.Object, dotnetToolsEnabledOptions);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools (stale tmpfs mask)", staleTmpfsDrift);

        var cleanToolsRunner = CreateOnlineDriftRunner(dotnetMountsOut, envOut: string.Empty, tmpfsOut: string.Empty);
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(cleanToolsRunner.Object, dotnetToolsEnabledOptions));
    }

    [Fact]
    public async Task DetectContainerDrift_HandlesNullInspectResultsGracefullyAcrossStages()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-null",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            UserUidGid = "1000:1000",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.dotnet"],
            MountHostDotnetTools = false,
            DisableNetwork = false,
            IsRestrictedReadOnly = false
        };

        var expectedMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var mountsOut = string.Concat(expectedMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));
        var validTmpfs = $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools=ro,noexec,nosuid,size=1m;";

        static Mock<IProcessRunner> CreateRunnerReturningNullAt(
            string nullInspectToken,
            string mountsOut,
            string validTmpfs)
        {
            var runner = new Mock<IProcessRunner>();

            void SetupToken(string token, ProcessExecutionResult? result)
            {
                runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains(token)), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(result!);
                runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains(token))))
                    .Returns(result!);
            }

            SetupToken("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}",
                nullInspectToken == "mounts" ? null : new ProcessExecutionResult(0, mountsOut, string.Empty));
            SetupToken("{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}",
                nullInspectToken == "tmpfs" ? null : new ProcessExecutionResult(0, validTmpfs, string.Empty));
            SetupToken("{{.Config.Image}}",
                nullInspectToken == "image" ? null : new ProcessExecutionResult(0, "alpine\n", string.Empty));
            SetupToken("{{.Config.User}}",
                nullInspectToken == "user" ? null : new ProcessExecutionResult(0, "1000:1000\n", string.Empty));
            SetupToken("{{.HostConfig.ReadonlyRootfs}}",
                nullInspectToken == "readonly" ? null : new ProcessExecutionResult(0, "false\n", string.Empty));
            SetupToken("{{.HostConfig.NetworkMode}}",
                nullInspectToken == "network" ? null : new ProcessExecutionResult(0, "bridge\n", string.Empty));
            SetupToken("{{range .HostConfig.ExtraHosts}}{{.}};{{end}}",
                nullInspectToken == "extraHosts" ? null : new ProcessExecutionResult(0, string.Empty, string.Empty));
            SetupToken("{{range .Config.Env}}{{.}}\n{{end}}",
                nullInspectToken == "env" ? null : new ProcessExecutionResult(0, string.Empty, string.Empty));

            return runner;
        }

        foreach (var stage in new[] { "mounts", "tmpfs", "image", "user", "readonly", "network", "extraHosts", "env" })
        {
            var runner = CreateRunnerReturningNullAt(stage, mountsOut, validTmpfs);
            var asyncDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(runner.Object, options);
            var syncDrift = ContainerMountAndDriftBuilder.DetectContainerDrift(runner.Object, options);
            Assert.NotNull(asyncDrift);
            Assert.Contains("Failed to inspect", asyncDrift);
            Assert.Equal(asyncDrift, syncDrift);
        }
    }

    [Fact]
    public void BuildDockerRunArgs_ExportsDotNetRollForward_And_ExpandsTildeSdkPathsConsistently()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-env",
            ContainerImage = "alpine",
            HostWorkspacePath = "/home/alice/wks",
            UseWsl = true,
            WslUserName = "alice",
            ExpandTildeInMounts = true,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["~/.rustup", "~/.cargo/bin"]
        };

        var args = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains($"{ContainerExecutorOptions.DotNetRollForwardEnvVar}={ContainerExecutorOptions.DotNetRollForwardLatestMajor}", args);
        Assert.Contains($"{ContainerExecutorOptions.RustupHomeEnvVarName}=/home/alice/.rustup", args);
        Assert.Equal("/home/alice/.rustup", ContainerMountAndDriftBuilder.ResolveRustupHomePath(options));

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        Assert.Contains(mounts, m => m.HostPath == "/home/alice/.rustup" && m.ContainerPath == "/home/alice/.rustup" && m.IsReadOnly);
        Assert.DoesNotContain(mounts, m => m.ContainerPath.StartsWith('~'));

        var path = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options);
        var pathEntries = path.Split(':');
        Assert.Contains("/home/alice/.cargo/bin", pathEntries);
        Assert.DoesNotContain(pathEntries, p => p.StartsWith('~'));

        // 1. MountHostSdks = false must default DOTNET_ROOT to /usr/share/dotnet even when DefaultSdkMountPaths contains /usr/lib/dotnet
        var noHostSdkOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-no-sdk",
            ContainerImage = "alpine",
            HostWorkspacePath = "/home/alice/wks",
            MountPackageCaches = false,
            MountHostSdks = false
        };
        var noHostSdkArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(noHostSdkOptions);
        Assert.Contains("DOTNET_ROOT=/usr/share/dotnet", noHostSdkArgs);
        Assert.DoesNotContain("DOTNET_ROOT=/usr/lib/dotnet", noHostSdkArgs);

        // 2. Tilde-prefixed ~/dotnet expands in DOTNET_ROOT, and non-POSIX Windows drive paths are never leaked when UseWsl = false
        var tildeDotnetOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-tilde-dotnet",
            ContainerImage = "alpine",
            HostWorkspacePath = "/home/alice/wks",
            UseWsl = true,
            WslUserName = "alice",
            ExpandTildeInMounts = true,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["~/dotnet"]
        };
        Assert.Contains("DOTNET_ROOT=/home/alice/dotnet", ContainerMountAndDriftBuilder.BuildDockerRunArgs(tildeDotnetOptions));

        var unexpandedToolsAndWindowsOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-step1166-unexpanded",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/wks",
            UseWsl = false,
            ExpandTildeInMounts = false,
            MountPackageCaches = false,
            MountHostSdks = true,
            MountHostDotnetTools = true,
            SdkMountPaths = ["~/.dotnet/tools", @"C:\Program Files\dotnet"]
        };
        var unexpandedArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(unexpandedToolsAndWindowsOptions);
        Assert.Contains("DOTNET_ROOT=/usr/share/dotnet", unexpandedArgs);
        Assert.DoesNotContain(unexpandedArgs, a => a.Contains("C:", StringComparison.OrdinalIgnoreCase));

        var unexpandedMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(unexpandedToolsAndWindowsOptions);
        Assert.DoesNotContain(unexpandedMounts, m => m.ContainerPath.StartsWith('~') || m.ContainerPath.Contains("C:", StringComparison.OrdinalIgnoreCase));

        var unexpandedPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(unexpandedToolsAndWindowsOptions).Split(':');
        Assert.DoesNotContain(unexpandedPath, p => p.StartsWith('~') || p.Contains("C:", StringComparison.OrdinalIgnoreCase));
    }
}
