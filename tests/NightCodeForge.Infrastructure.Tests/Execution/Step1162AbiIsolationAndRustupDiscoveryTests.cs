using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class Step1162AbiIsolationAndRustupDiscoveryTests
{
    [Theory]
    [InlineData("/usr/lib/x86_64-linux-gnu", true)]
    [InlineData("/usr/lib/x86_64-linux-gnu/", true)]
    [InlineData("/usr/lib/aarch64-linux-gnu", true)]
    [InlineData("/usr/lib/arm-linux-gnueabihf", true)]
    [InlineData("/lib/x86_64-linux-gnu", true)]
    [InlineData("/lib/aarch64-linux-gnu/libcrypto.so.3", true)]
    [InlineData("/usr/lib", false)]
    [InlineData("/lib", false)]
    [InlineData("/usr/lib/dotnet", false)]
    [InlineData("/usr/lib/jvm", false)]
    [InlineData("/usr/share/dotnet", false)]
    [InlineData("/etc/ssl/certs", false)]
    [InlineData("/home/user/.rustup", false)]
    public void IsHostMultiarchSharedLibraryPath_MatchesOnlyLinuxGnuMultiarchLibPaths(string path, bool expected)
    {
        Assert.Equal(expected, ContainerMountAndDriftBuilder.IsHostMultiarchSharedLibraryPath(path));
    }

    [Theory]
    [InlineData("python:3.9")]
    [InlineData("python:3.11")]
    [InlineData("rust:1.80")]
    public void BuildVolumeMounts_NonManagedCustomImages_ExcludeMultiarchSharedLibsWhileKeepingNonAbiMounts(string customImage)
    {
        var options = new ContainerExecutorOptions
        {
            ContainerImage = customImage,
            HostWorkspacePath = "/home/user/wks/1",
            MountPackageCaches = true,
            MountHostSdks = true,
            SdkMountPaths =
            [
                "/usr/share/dotnet",
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/aarch64-linux-gnu",
                "/lib/x86_64-linux-gnu",
                "/usr/lib/arm-linux-gnueabihf",
                "/etc/ssl/certs",
                "/etc/fonts",
                "/home/user/.rustup"
            ]
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);

        Assert.DoesNotContain(mounts, m => ContainerMountAndDriftBuilder.IsHostMultiarchSharedLibraryPath(m.ContainerPath));
        Assert.Contains(mounts, m => m.ContainerPath == "/workspace");
        Assert.Contains(mounts, m => m.ContainerPath == "/etc/ssl/certs" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/etc/fonts" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/home/user/.rustup" && m.IsReadOnly);
        Assert.Contains(mounts, m => m.ContainerPath == "/cache/uv");

        var runArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.DoesNotContain(runArgs, a => a.Contains("linux-gnu", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ContainerExecutorOptions.DefaultAgentBaseImage)]
    [InlineData(ContainerExecutorOptions.AlternateAgentBaseImage)]
    public void BuildVolumeMounts_ManagedBaseImages_PreserveHostMultiarchSharedLibMounts(string? managedImage)
    {
        var options = new ContainerExecutorOptions
        {
            ContainerImage = managedImage,
            HostWorkspacePath = "/home/user/wks/1",
            MountHostSdks = true,
            SdkMountPaths = ["/usr/share/dotnet", "/usr/lib/x86_64-linux-gnu", "/etc/ssl/certs"]
        };

        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        Assert.Contains(mounts, m => m.ContainerPath == "/usr/lib/x86_64-linux-gnu" && m.IsReadOnly);
    }

    [Fact]
    public async Task DetectContainerDriftAsync_FlagsStaleHostMultiarchMountOnCustomImage_AndAllowsManagedImage()
    {
        var customOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-custom-py311",
            ContainerImage = "python:3.11",
            HostWorkspacePath = "/wks/1",
            MountPackageCaches = false,
            MountHostSdks = true,
            SdkMountPaths = ["/usr/lib/x86_64-linux-gnu", "/etc/ssl/certs"]
        };

        var expectedCustomMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(customOptions);
        Assert.DoesNotContain(expectedCustomMounts, m => m.ContainerPath == "/usr/lib/x86_64-linux-gnu");

        var staleMountsOutput = string.Concat(expectedCustomMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};")) +
            "/usr/lib/x86_64-linux-gnu=/usr/lib/x86_64-linux-gnu=false;";

        var staleRunner = new Mock<IProcessRunner>();
        staleRunner.Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, staleMountsOutput, string.Empty));

        var drift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(staleRunner.Object, customOptions);
        Assert.NotNull(drift);
        Assert.Contains("/usr/lib/x86_64-linux-gnu (expected absent)", drift);
    }

    [Fact]
    public void ProbeScript_ResolvesDistroRustupViaRustupWhichForCargoAndRustc()
    {
        Assert.Contains("rustup which", HostSdkDiscoveryService.ProbeScript, StringComparison.Ordinal);
        Assert.Contains("/usr/bin|/usr/local/bin|/bin|/sbin|/usr/sbin|/usr/local/sbin|/snap/bin", HostSdkDiscoveryService.ProbeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDiscoveryOutput_AndDeriveSdkRoot_HandleDistroRustupShRustupAndToolchainLessCases()
    {
        // 1. Distro-packaged rustup: /usr/bin/{cargo,rustc} resolved via rustup which -> ~/.rustup/toolchains/.../bin
        var distroOutput = """
            ---ENV---
            ---CMD---
            cargo=/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/cargo=/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/cargo
            rustc=/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/rustc=/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin/rustc
            ---DIR---
            /etc/ssl/certs
            /home/ubuntu/.rustup
            """;

        var distroDetails = HostSdkDiscoveryService.ParseDiscoveryOutput(distroOutput);
        Assert.Contains("/home/ubuntu/.rustup", distroDetails.MountPaths);
        Assert.DoesNotContain("/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu", distroDetails.MountPaths);
        Assert.DoesNotContain("/usr/bin", distroDetails.MountPaths);

        var binDirs = HostSdkDiscoveryService.ResolveMountableRustToolchainBinDirectories(distroDetails);
        Assert.Equal(["/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin"], binDirs);

        // 2. Non-rustup or toolchain-less /usr/bin/{cargo,rustc}
        var systemOutput = """
            ---ENV---
            ---CMD---
            cargo=/usr/bin/cargo=/usr/bin/rustup
            rustc=/usr/bin/rustc=/usr/bin/rustup
            ---DIR---
            /etc/ssl/certs
            /home/ubuntu/.rustup
            """;

        var systemDetails = HostSdkDiscoveryService.ParseDiscoveryOutput(systemOutput);
        Assert.DoesNotContain("/usr/bin", systemDetails.MountPaths);
        Assert.Empty(HostSdkDiscoveryService.ResolveMountableRustToolchainBinDirectories(systemDetails));

        // 3. Traditional sh.rustup.rs (~/.cargo/bin + ~/.rustup)
        var shRustupOutput = """
            ---ENV---
            CARGO_HOME=/home/ubuntu/.cargo
            RUSTUP_HOME=/home/ubuntu/.rustup
            ---CMD---
            cargo=/home/ubuntu/.cargo/bin/cargo=/home/ubuntu/.cargo/bin/rustup
            rustc=/home/ubuntu/.cargo/bin/rustc=/home/ubuntu/.cargo/bin/rustup
            ---DIR---
            /home/ubuntu/.cargo
            /home/ubuntu/.rustup
            """;

        var shDetails = HostSdkDiscoveryService.ParseDiscoveryOutput(shRustupOutput);
        Assert.Contains("/home/ubuntu/.cargo", shDetails.MountPaths);
        Assert.Contains("/home/ubuntu/.rustup", shDetails.MountPaths);
        Assert.Empty(HostSdkDiscoveryService.ResolveMountableRustToolchainBinDirectories(shDetails));
    }

    [Fact]
    public async Task ContainerEnvironmentFactory_AndShellPreamble_WireDistroRustupToolchainPathWithoutNestedMounts()
    {
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(m => m.GetWorkspaceMemoryPath(It.IsAny<int>())).Returns<int>(id => $"~/.nightcodeforge/wks/{id}/memory");
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("ubuntu");

        const string toolchainBin = "/home/ubuntu/.rustup/toolchains/stable-x86_64-unknown-linux-gnu/bin";
        var sdkDetails = new HostSdkDiscoveryDetails(
            MountPaths: ["/etc/ssl/certs", "/home/ubuntu/.rustup"],
            EnvironmentVariables: new Dictionary<string, string>(),
            CommandPaths: new Dictionary<string, string>
            {
                ["cargo"] = $"{toolchainBin}/cargo",
                ["rustc"] = $"{toolchainBin}/rustc"
            });

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkDetails(It.IsAny<string?>())).Returns(sdkDetails);
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(sdkDetails.MountPaths);

        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        var factory = new ContainerEnvironmentFactory(
            runnerMock.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { UseWslForContainers = false }),
            Options.Create(new SecurityOptions()),
            pathResolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);

        var scope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.ContainerSandbox,
            SandboxSecurityTier.Standard,
            SandboxMountTable.ForWorkspace("/tmp/wks-rust", isReadOnly: false),
            containerName: "ncf-rust-test",
            repositoryPath: "/tmp/wks-rust",
            hostWorkspacePath: "/tmp/wks-rust",
            mountHostDotnetTools: false,
            mountAcpHostPaths: false);

        var executor = Assert.IsType<NightCodeForgeContainerExecutor>(await factory.CreateContainerEnvironmentAsync(scope));
        Assert.Contains(toolchainBin, executor.Options.ExtraPathDirectories);

        // Even if a nested toolchain path is manually included in SdkMountPaths, BuildVolumeMounts must NOT create nested mounts under .rustup.
        executor.Options.SdkMountPaths.Add(toolchainBin);
        var mounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(executor.Options);
        Assert.Single(mounts, m => m.ContainerPath == "/home/ubuntu/.rustup");
        Assert.DoesNotContain(mounts, m => m.ContainerPath.StartsWith("/home/ubuntu/.rustup/", StringComparison.Ordinal));
        Assert.DoesNotContain(mounts, m => m.ContainerPath == "/usr/bin");

        var computedPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(executor.Options);
        Assert.Contains(toolchainBin, computedPath.Split(':'));

        var runArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(executor.Options);
        Assert.Contains($"PATH={computedPath}", runArgs);
        Assert.Contains("RUSTUP_HOME=/home/ubuntu/.rustup", runArgs);
        Assert.Contains("CARGO_HOME=/cache/cargo", runArgs);

        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var sessionMock = new Mock<IProcessStreamSession>();
        sessionMock.SetupGet(s => s.StandardInput).Returns(writer);
        sessionMock.SetupGet(s => s.StandardOutput).Returns(new StreamReader(new MemoryStream()));
        var shellRunnerMock = new Mock<IProcessRunner>();
        shellRunnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>())).Returns(sessionMock.Object);

        var shell = new PersistentContainerShellSession(
            executor.Options,
            shellRunnerMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger.Instance);
        shell.Start();
        await shell.WriteEnvironmentPreambleAsync(null);
        await writer.FlushAsync();

        var preamble = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Contains($"export PATH=\"{computedPath}\"", preamble);
        Assert.Contains("export RUSTUP_HOME='/home/ubuntu/.rustup'", preamble);
        Assert.Contains("export CARGO_HOME=\"/cache/cargo\"", preamble);
    }
}
