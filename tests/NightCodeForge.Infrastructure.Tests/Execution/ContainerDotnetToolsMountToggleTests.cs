using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class ContainerDotnetToolsMountToggleTests
{
    [Fact]
    public void BuildVolumeMounts_And_ComputePath_HonorMountHostDotnetToolsFlag()
    {
        var baseMounts = new List<ContainerVolumeMount>
        {
            new("/home/user/.dotnet/tools", ContainerExecutorOptions.HostDotnetToolsContainerPath, isReadOnly: true)
        };

        var enabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            AdditionalVolumeMounts = baseMounts
        };

        var disabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = false,
            AdditionalVolumeMounts = baseMounts
        };

        var enabledMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(enabledOptions);
        var disabledMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(disabledOptions);

        Assert.Contains(enabledMounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);
        Assert.DoesNotContain(disabledMounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);

        var enabledPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(enabledOptions);
        var disabledPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(disabledOptions);

        Assert.Contains(ContainerExecutorOptions.HostDotnetToolsContainerPath, enabledPath.Split(':'));
        Assert.DoesNotContain(ContainerExecutorOptions.HostDotnetToolsContainerPath, disabledPath.Split(':'));
    }

    [Fact]
    public void BuildVolumeMounts_ComputePath_And_DockerArgs_StripSdkDiscoveredToolsAndMaskUserDotnetToolsWhenDisabled()
    {
        var disabledOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.dotnet", "/home/user/.dotnet/tools", "/usr/share/dotnet"],
            AdditionalVolumeMounts =
            [
                new("/home/user/.dotnet/tools", ContainerExecutorOptions.HostDotnetToolsContainerPath, isReadOnly: true)
            ]
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(disabledOptions);
        Assert.Contains(mounts, m => m.ContainerPath == $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet");
        Assert.DoesNotContain(mounts, m => m.ContainerPath.Contains(".dotnet/tools", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mounts, m => m.ContainerPath == ContainerExecutorOptions.HostDotnetToolsContainerPath);

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(disabledOptions).Split(':');
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet", pathEntries);
        Assert.DoesNotContain(ContainerExecutorOptions.HostDotnetToolsContainerPath, pathEntries);
        Assert.DoesNotContain($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools", pathEntries);
        Assert.DoesNotContain("/root/.dotnet/tools", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(disabledOptions);
        var tmpfsIdx = dockerArgs.IndexOf("--tmpfs");
        Assert.True(tmpfsIdx >= 0);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools:ro", dockerArgs[tmpfsIdx + 1]);
    }

    [Fact]
    public void BuildDockerRunArgs_SupportsDisableNetworkAndDeduplicatedBlockedHosts()
    {
        var blockedOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = false,
            BlockedHosts = ["github.com", "api.github.com", "GITHUB.COM", "   "]
        };

        var blockedArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(blockedOptions);
        Assert.DoesNotContain("--network", blockedArgs);
        Assert.Equal(2, blockedArgs.Count(a => a == "--add-host"));
        Assert.Contains("github.com:127.0.0.1", blockedArgs);
        Assert.Contains("api.github.com:127.0.0.1", blockedArgs);

        var offlineOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = true,
            IsRestrictedReadOnly = false,
            BlockedHosts = ["github.com"]
        };

        var offlineArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(offlineOptions);
        var netIdx = offlineArgs.IndexOf("--network");
        Assert.True(netIdx >= 0);
        Assert.Equal("none", offlineArgs[netIdx + 1]);
        Assert.DoesNotContain("--read-only", offlineArgs);
        Assert.DoesNotContain("--add-host", offlineArgs);
    }

    [Fact]
    public void ComputePathAndBuildDockerRunArgs_IncludeContainerCargoBinAndRustupHomeWithoutLeakingRustupToPath()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/home/user/.cargo", "/home/user/.rustup"]
        };

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options).Split(':');
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.cargo/bin", pathEntries);
        Assert.Contains("/home/user/.cargo/bin", pathEntries);
        Assert.DoesNotContain("/home/user/.rustup", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("RUSTUP_HOME=/home/user/.rustup", dockerArgs);

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        Assert.Contains(mounts, m => m.ContainerPath == "/home/user/.cargo" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/home/user/.rustup" && m.IsReadOnly);
    }

    [Fact]
    public void ComputePathAndBuildDockerRunArgs_HandleCustomRustupDirectoryWithoutLeakingToPath()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/usr/local/cargo", "/usr/local/rustup"]
        };

        var pathEntries = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(options).Split(':');
        Assert.Contains("/usr/local/cargo/bin", pathEntries);
        Assert.DoesNotContain("/usr/local/rustup", pathEntries);

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("RUSTUP_HOME=/usr/local/rustup", dockerArgs);
    }

    [Fact]
    public async Task PersistentContainerShellSession_WriteEnvironmentPreambleAsync_ConfiguresCargoPathAndQuotedRustupHome()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-preamble-test",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/opt/my's/.rustup"]
        };

        var runnerMock = new Mock<IProcessRunner>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        async Task<string> CapturePreambleAsync(IReadOnlyDictionary<string, string>? envVars)
        {
            using var stdinBuffer = new MemoryStream();
            using var stdinWriter = new StreamWriter(stdinBuffer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var stdoutStream = new MemoryStream();
            using var stdoutReader = new StreamReader(stdoutStream);

            var streamSession = new Mock<IProcessStreamSession>();
            streamSession.SetupGet(s => s.StandardInput).Returns(stdinWriter);
            streamSession.SetupGet(s => s.StandardOutput).Returns(stdoutReader);
            streamSession.SetupGet(s => s.HasExited).Returns(false);

            runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>())).Returns(streamSession.Object);

            using var session = new PersistentContainerShellSession(options, runnerMock.Object, telemetryMock.Object, NullLogger.Instance);
            session.Start();
            await session.WriteEnvironmentPreambleAsync(envVars);
            await stdinWriter.FlushAsync();

            stdinBuffer.Position = 0;
            using var reader = new StreamReader(stdinBuffer, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        var defaultPreamble = await CapturePreambleAsync(null);
        Assert.Contains("if [ -d \"$HOME/.cargo/bin\" ]", defaultPreamble);
        Assert.Contains("export RUSTUP_HOME='/opt/my'\\''s/.rustup'", defaultPreamble);

        var customEnvPreamble = await CapturePreambleAsync(new Dictionary<string, string>
        {
            ["PATH"] = "/custom/bin",
            ["RUSTUP_HOME"] = "/custom/rustup"
        });
        Assert.DoesNotContain("if [ -d \"$HOME/.cargo/bin\" ]", customEnvPreamble);
        Assert.DoesNotContain("export RUSTUP_HOME='/opt/my'\\''s/.rustup'", customEnvPreamble);
        Assert.Contains("export RUSTUP_HOME='/custom/rustup'", customEnvPreamble);
    }

    [Fact]
    public void BuildDockerRunArgs_MasksRootDotnetToolsWhenRootDotnetIsMountedAndToolsDisabled()
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/root/.dotnet"]
        };

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.Contains("/root/.dotnet/tools:ro,noexec,nosuid,size=1m", dockerArgs);
    }

    [Theory]
    [InlineData("github.com:443")]
    [InlineData("bad host")]
    [InlineData("-flag")]
    [InlineData("example.com/path")]
    public void BuildDockerRunArgs_ThrowsArgumentExceptionForMalformedBlockedHosts(string invalidHost)
    {
        var options = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = false,
            BlockedHosts = [invalidHost]
        };

        Assert.Throws<ArgumentException>(() => ContainerMountAndDriftBuilder.BuildDockerRunArgs(options));
    }

    [Fact]
    public async Task DetectContainerDrift_DetectsStaleDotnetToolsMounts_AndAcceptsMaskedTmpfs()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-drift-tools",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountHostDotnetTools = false,
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.dotnet"]
        };

        var expectedMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var baseInspect = string.Concat(expectedMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

        static Mock<IProcessRunner> CreateRunner(string mountsOut, string tmpfsOut = "")
        {
            var runner = new Mock<IProcessRunner>();
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, mountsOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, mountsOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, tmpfsOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range $k, $v := .HostConfig.Tmpfs}}{{$k}}={{$v}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, tmpfsOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}"))))
                .Returns(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            return runner;
        }

        // 1. Missing ro tmpfs mask in HostConfig.Tmpfs on ContainerHomePath/.dotnet/tools
        var missingMaskRunner = CreateRunner(baseInspect, tmpfsOut: string.Empty);
        var missingMaskDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(missingMaskRunner.Object, options);
        Assert.NotNull(missingMaskDrift);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools (missing ro tmpfs mask)", missingMaskDrift!);
        Assert.Equal(missingMaskDrift, ContainerMountAndDriftBuilder.DetectContainerDrift(missingMaskRunner.Object, options));

        // 1b. Writable (rw) tmpfs entry instead of ro tmpfs mask is rejected
        var writableTmpfsRunner = CreateRunner(baseInspect, tmpfsOut: $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools=rw,noexec,nosuid,size=1m;");
        var writableTmpfsDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(writableTmpfsRunner.Object, options);
        Assert.NotNull(writableTmpfsDrift);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools (missing ro tmpfs mask)", writableTmpfsDrift!);

        // 2. Stale HostDotnetToolsContainerPath bind mount + real ContainerHomePath/.dotnet/tools bind mount
        var staleBindRunner = CreateRunner(baseInspect + $"{ContainerExecutorOptions.HostDotnetToolsContainerPath}=/home/user/.dotnet/tools=false;{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools=/home/user/.dotnet/tools=false;");
        var staleBindDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleBindRunner.Object, options);
        Assert.NotNull(staleBindDrift);
        Assert.Contains($"{ContainerExecutorOptions.HostDotnetToolsContainerPath} (expected absent)", staleBindDrift!);
        Assert.Contains($"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools (expected absent)", staleBindDrift!);

        // 3. Valid ro tmpfs mask in HostConfig.Tmpfs (matching real Docker '--tmpfs' inspect behavior where .Mounts has no entry)
        var cleanRunner = CreateRunner(
            baseInspect,
            tmpfsOut: $"{ContainerExecutorOptions.ContainerHomePath}/.dotnet/tools=ro,noexec,nosuid,size=1m;");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(cleanRunner.Object, options));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(cleanRunner.Object, options));
    }

    [Fact]
    public async Task DetectContainerDrift_DetectsNetworkModeBlockedHostsAndRustupHomeDrift()
    {
        static Mock<IProcessRunner> CreateRunner(
            ContainerExecutorOptions opts,
            string networkMode = "none",
            string extraHosts = "",
            string envOut = "")
        {
            var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(opts);
            var mountsOut = string.Concat(mounts.Select(m =>
                $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

            var runner = new Mock<IProcessRunner>();
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, mountsOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, mountsOut, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}"))))
                .Returns(new ProcessExecutionResult(0, "alpine\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.User}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, ContainerExecutorOptions.RestrictedReadOnlyUserUidGid + "\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.User}}"))))
                .Returns(new ProcessExecutionResult(0, ContainerExecutorOptions.RestrictedReadOnlyUserUidGid + "\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.NetworkMode}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, networkMode + "\n", string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.HostConfig.NetworkMode}}"))))
                .Returns(new ProcessExecutionResult(0, networkMode + "\n", string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .HostConfig.ExtraHosts}}{{.}};{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, extraHosts, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .HostConfig.ExtraHosts}}{{.}};{{end}}"))))
                .Returns(new ProcessExecutionResult(0, extraHosts, string.Empty));
            runner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, envOut, string.Empty));
            runner.Setup(r => r.Run(It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}"))))
                .Returns(new ProcessExecutionResult(0, envOut, string.Empty));
            return runner;
        }

        // 1. DisableNetwork drift
        var offlineOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-drift-net",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            DisableNetwork = true
        };
        var bridgeRunner = CreateRunner(offlineOptions, networkMode: "bridge");
        var netDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(bridgeRunner.Object, offlineOptions);
        Assert.NotNull(netDrift);
        Assert.Contains("Network mode mismatch", netDrift!);
        Assert.Equal(netDrift, ContainerMountAndDriftBuilder.DetectContainerDrift(bridgeRunner.Object, offlineOptions));

        var noneRunner = CreateRunner(offlineOptions, networkMode: "none");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(noneRunner.Object, offlineOptions));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(noneRunner.Object, offlineOptions));

        // 2. BlockedHosts drift
        var blockedOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-drift-hosts",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            BlockedHosts = ["github.com", "api.github.com"]
        };
        var missingHostRunner = CreateRunner(blockedOptions, extraHosts: "github.com:127.0.0.1;");
        var hostsDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(missingHostRunner.Object, blockedOptions);
        Assert.NotNull(hostsDrift);
        Assert.Contains("api.github.com:127.0.0.1", hostsDrift!);
        Assert.Equal(hostsDrift, ContainerMountAndDriftBuilder.DetectContainerDrift(missingHostRunner.Object, blockedOptions));

        var presentHostsRunner = CreateRunner(blockedOptions, extraHosts: "github.com:127.0.0.1;api.github.com:127.0.0.1;");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(presentHostsRunner.Object, blockedOptions));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(presentHostsRunner.Object, blockedOptions));

        // 3. RUSTUP_HOME drift
        var rustOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-drift-rust",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/home/user/.rustup"]
        };
        var missingEnvRunner = CreateRunner(rustOptions, envOut: "PATH=/usr/bin\n");
        var rustDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(missingEnvRunner.Object, rustOptions);
        Assert.NotNull(rustDrift);
        Assert.Contains("RUSTUP_HOME='/home/user/.rustup'", rustDrift!);
        Assert.Equal(rustDrift, ContainerMountAndDriftBuilder.DetectContainerDrift(missingEnvRunner.Object, rustOptions));

        var presentEnvRunner = CreateRunner(rustOptions, envOut: "PATH=/usr/bin\nRUSTUP_HOME=/home/user/.rustup\n");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(presentEnvRunner.Object, rustOptions));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(presentEnvRunner.Object, rustOptions));

        // 4. RestrictedReadOnly enforces NetworkMode='none' even when DisableNetwork=false
        var readOnlyOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-drift-ro",
            ContainerImage = "alpine",
            HostWorkspacePath = "/tmp/bench-wks",
            MountPackageCaches = false,
            MountHostSdks = false,
            IsRestrictedReadOnly = true,
            DisableNetwork = false
        };
        var roBridgeRunner = CreateRunner(readOnlyOptions, networkMode: "bridge");
        var roNetDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(roBridgeRunner.Object, readOnlyOptions);
        Assert.NotNull(roNetDrift);
        Assert.Contains("Network mode mismatch", roNetDrift!);
        Assert.Equal(roNetDrift, ContainerMountAndDriftBuilder.DetectContainerDrift(roBridgeRunner.Object, readOnlyOptions));

        var roCleanRunner = CreateRunner(readOnlyOptions, networkMode: "none");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(roCleanRunner.Object, readOnlyOptions));
        Assert.Null(ContainerMountAndDriftBuilder.DetectContainerDrift(roCleanRunner.Object, readOnlyOptions));
    }
}
