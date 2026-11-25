using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public sealed class Step1160BaseImageCargoCacheAndCustomImageTests : IDisposable
{
    private readonly string _tempDir;

    public Step1160BaseImageCargoCacheAndCustomImageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-step1160-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BaseImageConstantsAndDockerfile_UseV6WithTzdataLegacyAndCargoCache()
    {
        Assert.Equal("ncf-agent-base:v6", ContainerExecutorOptions.DefaultAgentBaseImage);
        Assert.Equal("nightcodeforge-agent:v6", ContainerExecutorOptions.AlternateAgentBaseImage);

        var dockerfile = ContainerExecutorOptions.DefaultAgentBaseDockerfile;
        Assert.Contains("ENV DEBIAN_FRONTEND=noninteractive", dockerfile);
        Assert.Contains("tzdata", dockerfile);
        Assert.Contains("tzdata-legacy", dockerfile);
        Assert.Contains("rm -rf /var/lib/apt/lists/*", dockerfile);
        Assert.Contains("/cache/cargo", dockerfile);
        Assert.Contains("ENV CARGO_HOME=/cache/cargo", dockerfile);
    }

    [Fact]
    public void BuildVolumeMounts_MountsCargoCacheSeparateFromHostCargoSdk_RespectingReadOnlyScope()
    {
        var standardOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/1",
            MountPackageCaches = true,
            MountHostSdks = true,
            SdkMountPaths = ["/home/wsluser/.cargo", "/home/wsluser/.rustup"],
            IsRestrictedReadOnly = false
        };

        var standardMounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(standardOptions);
        var cargoCacheMount = Assert.Single(standardMounts, m => m.ContainerPath == ContainerExecutorOptions.ContainerCargoCachePath);
        Assert.Equal(ContainerExecutorOptions.DefaultCargoCachePath, cargoCacheMount.HostPath);
        Assert.False(cargoCacheMount.IsReadOnly);

        var hostCargoSdkMount = Assert.Single(standardMounts, m => m.ContainerPath == "/home/wsluser/.cargo");
        Assert.True(hostCargoSdkMount.IsReadOnly);
        var hostRustupSdkMount = Assert.Single(standardMounts, m => m.ContainerPath == "/home/wsluser/.rustup");
        Assert.True(hostRustupSdkMount.IsReadOnly);

        var roOptions = new ContainerExecutorOptions
        {
            HostWorkspacePath = "/home/wsluser/.nightcodeforge/wks/1",
            MountPackageCaches = true,
            IsRestrictedReadOnly = true
        };
        var roMounts = NightCodeForgeContainerExecutor.BuildVolumeMounts(roOptions);
        var roCargoCacheMount = Assert.Single(roMounts, m => m.ContainerPath == ContainerExecutorOptions.ContainerCargoCachePath);
        Assert.True(roCargoCacheMount.IsReadOnly);
    }

    [Fact]
    public async Task DetectContainerDrift_DetectsMissingCargoCacheMountModeAndCargoHomeEnv()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-cargo-drift",
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = "/wks/1",
            MountPackageCaches = true,
            MountHostSdks = false
        };

        var expectedMounts = ContainerMountAndDriftBuilder.BuildVolumeMounts(options);
        var matchingMounts = string.Concat(expectedMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(!m.IsReadOnly).ToString().ToLowerInvariant()};"));

        static Mock<IProcessRunner> BuildDriftRunner(string mountsOut, string imageOut, string envOut)
        {
            var runner = new Mock<IProcessRunner>();
            runner.Setup(r => r.RunAsync(
                    It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Mounts}}{{.Destination}}={{.Source}}={{.RW}};{{end}}")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, mountsOut, string.Empty));
            runner.Setup(r => r.RunAsync(
                    It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{.Config.Image}}")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, imageOut + "\n", string.Empty));
            runner.Setup(r => r.RunAsync(
                    It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("{{range .Config.Env}}{{.}}\n{{end}}")),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProcessExecutionResult(0, envOut, string.Empty));
            return runner;
        }

        // 1. Clean match
        var cleanRunner = BuildDriftRunner(matchingMounts, ContainerExecutorOptions.DefaultAgentBaseImage, "CARGO_HOME=/cache/cargo\n");
        Assert.Null(await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(cleanRunner.Object, options));

        // 2. Legacy v5 container image triggers drift
        var legacyRunner = BuildDriftRunner(matchingMounts, "ncf-agent-base:v5", "CARGO_HOME=/cache/cargo\n");
        var legacyDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(legacyRunner.Object, options);
        Assert.NotNull(legacyDrift);
        Assert.Contains("Image mismatch", legacyDrift);

        // 3. Missing CARGO_HOME=/cache/cargo triggers drift
        var missingCargoEnvRunner = BuildDriftRunner(matchingMounts, ContainerExecutorOptions.DefaultAgentBaseImage, "PATH=/usr/bin\n");
        var envDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(missingCargoEnvRunner.Object, options);
        Assert.NotNull(envDrift);
        Assert.Contains("CARGO_HOME='/cache/cargo'", envDrift);

        // 4. Mode mismatch (:ro instead of :rw) on /cache/cargo triggers drift
        var roCargoMounts = string.Concat(expectedMounts.Select(m =>
            $"{m.ContainerPath}={m.HostPath}={(m.ContainerPath == "/cache/cargo" ? "false" : (!m.IsReadOnly).ToString().ToLowerInvariant())};"));
        var roCargoRunner = BuildDriftRunner(roCargoMounts, ContainerExecutorOptions.DefaultAgentBaseImage, "CARGO_HOME=/cache/cargo\n");
        var mountDrift = await ContainerMountAndDriftBuilder.DetectContainerDriftAsync(roCargoRunner.Object, options);
        Assert.NotNull(mountDrift);
        Assert.Contains("/cache/cargo (expected rw)", mountDrift);
    }

    private static async Task<string> CapturePreambleAsync(
        ContainerExecutorOptions options,
        IReadOnlyDictionary<string, string>? envOverrides)
    {
        var memoryStream = new MemoryStream();
        var writer = new StreamWriter(memoryStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdout = new StreamReader(new MemoryStream());
        var sessionMock = new Mock<IProcessStreamSession>();
        sessionMock.SetupGet(s => s.StandardInput).Returns(writer);
        sessionMock.SetupGet(s => s.StandardOutput).Returns(stdout);

        var runnerMock = new Mock<IProcessRunner>();
        runnerMock.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>())).Returns(sessionMock.Object);

        var shell = new PersistentContainerShellSession(
            options,
            runnerMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger.Instance);
        shell.Start();

        await shell.WriteEnvironmentPreambleAsync(envOverrides);
        await writer.FlushAsync();
        return Encoding.UTF8.GetString(memoryStream.ToArray());
    }

    private static ContainerEnvironmentFactory CreateContainerFactory(IProcessRunner processRunner, bool useWsl)
    {
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(m => m.GetWorkspaceMemoryPath(It.IsAny<int>())).Returns<int>(id => $"~/.nightcodeforge/wks/{id}/memory");
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("wsluser");

        var sdkMock = new Mock<IHostSdkDiscoveryService>();
        sdkMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns([]);
        sdkMock.Setup(s => s.DiscoverSdkDetails(It.IsAny<string?>()))
            .Returns(new HostSdkDiscoveryDetails([], new Dictionary<string, string>(), new Dictionary<string, string>()));
        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");

        return new ContainerEnvironmentFactory(
            processRunner,
            Options.Create(new ExecutionEnvironmentProviderOptions
            {
                UseWslForContainers = useWsl,
                DefaultWslDistribution = useWsl ? "Ubuntu-24.04" : null,
                WslExecutable = "wsl.exe"
            }),
            Options.Create(new SecurityOptions()),
            pathResolverMock.Object,
            sdkMock.Object,
            identityMock.Object,
            new Mock<IOperationsTelemetryService>().Object,
            new Mock<ICompanionToolInstaller>().Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance);
    }

    [Fact]
    public async Task WriteEnvironmentPreambleAsync_ExportsCargoHomeAndRespectsExplicitOverride()
    {
        var managedOptions = new ContainerExecutorOptions
        {
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            MountPackageCaches = true,
            MountHostSdks = false
        };

        var defaultPreamble = await CapturePreambleAsync(managedOptions, null);
        Assert.Contains("export CARGO_HOME=\"/cache/cargo\"", defaultPreamble);
        Assert.Contains("export LANG=en_US.UTF-8", defaultPreamble);
        Assert.Contains("export LC_ALL=en_US.UTF-8", defaultPreamble);

        var overridePreamble = await CapturePreambleAsync(
            managedOptions,
            new Dictionary<string, string> { ["CARGO_HOME"] = "/custom/cargo-home" });
        Assert.DoesNotContain("export CARGO_HOME=\"/cache/cargo\"", overridePreamble);
        Assert.Contains("export CARGO_HOME='/custom/cargo-home'", overridePreamble);

        var customImageOptions = new ContainerExecutorOptions
        {
            ContainerImage = "python:3.9",
            MountPackageCaches = true,
            MountHostSdks = false
        };
        var customPreamble = await CapturePreambleAsync(customImageOptions, null);
        Assert.DoesNotContain("\nexport LANG=en_US.UTF-8\n", customPreamble);
        Assert.Contains("locale -a 2>/dev/null", customPreamble);
        Assert.Contains("C.UTF-8", customPreamble);
    }

    [Fact]
    public async Task WriteEnvironmentPreambleAsync_CustomImage_RespectsPartialAndFullLocaleOverrides()
    {
        var customImageOptions = new ContainerExecutorOptions
        {
            ContainerImage = "python:3.9",
            MountPackageCaches = true,
            MountHostSdks = false
        };

        // 1. Only LANG provided: must probe and export LC_ALL without overwriting LANG
        var langOnlyPreamble = await CapturePreambleAsync(
            customImageOptions,
            new Dictionary<string, string> { ["LANG"] = "fr_FR.UTF-8" });
        Assert.Contains("export LC_ALL=\"$_ncf_locale\"", langOnlyPreamble);
        Assert.DoesNotContain("LANG=\"$_ncf_locale\"", langOnlyPreamble);
        Assert.Contains("export LANG='fr_FR.UTF-8'", langOnlyPreamble);

        // 2. Only LC_ALL provided: must probe and export LANG without overwriting LC_ALL
        var lcAllOnlyPreamble = await CapturePreambleAsync(
            customImageOptions,
            new Dictionary<string, string> { ["LC_ALL"] = "fr_FR.UTF-8" });
        Assert.Contains("export LANG=\"$_ncf_locale\"", lcAllOnlyPreamble);
        Assert.DoesNotContain("LC_ALL=\"$_ncf_locale\"", lcAllOnlyPreamble);
        Assert.Contains("export LC_ALL='fr_FR.UTF-8'", lcAllOnlyPreamble);

        // 3. Both LANG and LC_ALL provided: skips locale probe entirely
        var bothPreamble = await CapturePreambleAsync(
            customImageOptions,
            new Dictionary<string, string> { ["LANG"] = "fr_FR.UTF-8", ["LC_ALL"] = "fr_FR.UTF-8" });
        Assert.DoesNotContain("locale -a", bothPreamble);
        Assert.Contains("export LANG='fr_FR.UTF-8'", bothPreamble);
        Assert.Contains("export LC_ALL='fr_FR.UTF-8'", bothPreamble);
    }

    [Fact]
    public async Task CargoCachePathNull_OmitsCargoHomeFromDockerArgsAndShellPreamble()
    {
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-no-cargo-cache",
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            MountPackageCaches = true,
            MountHostSdks = false,
            CargoCachePath = null
        };

        var dockerArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(options);
        Assert.DoesNotContain("CARGO_HOME=/cache/cargo", dockerArgs);
        Assert.Contains("NUGET_PACKAGES=/cache/nuget", dockerArgs);

        var preamble = await CapturePreambleAsync(options, null);
        Assert.DoesNotContain("export CARGO_HOME=\"/cache/cargo\"", preamble);
        Assert.Contains("export NUGET_PACKAGES=\"/cache/nuget\"", preamble);
    }

    [Fact]
    public void BuildDockerRunArgs_DifferentiatesManagedAndNonManagedCustomImagesForLocaleAndHome()
    {
        var customArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-py39",
            ContainerImage = "python:3.9",
            MountPackageCaches = true,
            MountHostSdks = false
        });

        Assert.Contains("HOME=/tmp", customArgs);
        Assert.Contains("LANG=C.UTF-8", customArgs);
        Assert.Contains("LC_ALL=C.UTF-8", customArgs);
        Assert.Contains("CARGO_HOME=/cache/cargo", customArgs);

        var managedArgs = ContainerMountAndDriftBuilder.BuildDockerRunArgs(new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-v6",
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            MountPackageCaches = true,
            MountHostSdks = false
        });

        Assert.Contains($"HOME={ContainerExecutorOptions.ContainerHomePath}", managedArgs);
        Assert.Contains("LANG=en_US.UTF-8", managedArgs);
        Assert.Contains("LC_ALL=en_US.UTF-8", managedArgs);
        Assert.Contains("CARGO_HOME=/cache/cargo", managedArgs);
    }

    [Fact]
    public async Task ContainerEnvironmentFactory_PreCreatesHostPackageCacheDirectories_InWslAndNonWslModes()
    {
        // 1. WSL mode pre-creates cache directories via wsl.exe sh -c mkdir -p && chmod 777
        var processRunnerMock = new Mock<IProcessRunner>();
        ProcessStartSpec? capturedWslSpec = null;
        processRunnerMock
            .Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                    s.FileName == "wsl.exe" &&
                    s.ArgumentList != null &&
                    s.ArgumentList.Any(a => a.Contains("ncf-cargo", StringComparison.Ordinal))),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedWslSpec = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var wslFactory = CreateContainerFactory(processRunnerMock.Object, useWsl: true);

        var scope = new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.WorkspaceClone,
            executionMode: ExecutionMode.ContainerSandbox,
            securityTier: SandboxSecurityTier.Standard,
            mountTable: SandboxMountTable.ForWorkspace("/home/wsluser/repo", isReadOnly: false),
            workspaceId: null,
            taskId: null,
            containerImage: "python:3.9",
            containerName: "ncf-test-cache-precreate",
            repositoryPath: "/home/wsluser/repo",
            virtualRootPath: "/workspace",
            defaultWorkingDirectory: "/workspace",
            scopeCacheKey: "test-wsl-cache",
            hostWorkspacePath: "/home/wsluser/repo",
            mountHostDotnetTools: false,
            useWslForContainers: true,
            wslDistribution: "Ubuntu-24.04",
            mountAcpHostPaths: false);

        await wslFactory.CreateContainerEnvironmentAsync(scope);

        Assert.NotNull(capturedWslSpec);
        var cmdArg = capturedWslSpec!.ArgumentList!.Last();
        Assert.Contains("mkdir -p", cmdArg);
        Assert.Contains("/home/wsluser/.cache/ncf-cargo", cmdArg);
        Assert.Contains("chmod 777", cmdArg);

        // 2. Non-WSL mode pre-creates configured cache directories on disk without invoking wsl.exe
        var nonWslRunnerMock = new Mock<IProcessRunner>(MockBehavior.Strict);
        var nonWslFactory = CreateContainerFactory(nonWslRunnerMock.Object, useWsl: false);

        var cargoDir = Path.Combine(_tempDir, "cargo-cache");
        var nugetDir = Path.Combine(_tempDir, "nuget-cache");
        var nonWslOptions = new ContainerExecutorOptions
        {
            UseWsl = false,
            MountPackageCaches = true,
            NugetPackageCachePath = nugetDir,
            NpmCachePath = null,
            PnpmCachePath = null,
            UvCachePath = null,
            MavenCachePath = null,
            PlaywrightCachePath = null,
            CargoCachePath = cargoDir
        };

        await nonWslFactory.EnsureHostPackageCacheDirectoriesOnceAsync(nonWslOptions);

        Assert.True(Directory.Exists(cargoDir));
        Assert.True(Directory.Exists(nugetDir));
        nonWslRunnerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EnsureHostPackageCacheDirectoriesOnceAsync_WslWithoutUserName_QuotesSubPathAndSkipsHomeRoot()
    {
        var processRunnerMock = new Mock<IProcessRunner>();
        ProcessStartSpec? capturedWslSpec = null;
        processRunnerMock
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedWslSpec = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var factory = CreateContainerFactory(processRunnerMock.Object, useWsl: true);
        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslExecutable = "wsl.exe",
            WslDistribution = "Ubuntu-24.04",
            WslUserName = null,
            MountPackageCaches = true,
            NugetPackageCachePath = "~",
            NpmCachePath = null,
            PnpmCachePath = null,
            UvCachePath = null,
            MavenCachePath = null,
            PlaywrightCachePath = null,
            CargoCachePath = "~/.cache/ncf-$USER-cargo"
        };

        await factory.EnsureHostPackageCacheDirectoriesOnceAsync(options);

        Assert.NotNull(capturedWslSpec);
        var cmdArg = capturedWslSpec!.ArgumentList!.Last();
        Assert.Contains("\"$HOME\"/'.cache/ncf-$USER-cargo'", cmdArg);
        Assert.DoesNotContain("\"$HOME\" ", cmdArg);
        Assert.DoesNotContain("\"$HOME\"\"", cmdArg);
        Assert.DoesNotContain("\"$HOME/.cache", cmdArg);
    }

    [Fact]
    public async Task EnsureHostPackageCacheDirectoriesOnceAsync_RetriesAfterTransientFailureAndCachesAfterSuccess()
    {
        var callCount = 0;
        var processRunnerMock = new Mock<IProcessRunner>();
        processRunnerMock
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new ProcessExecutionResult(1, string.Empty, "transient wsl error")
                    : new ProcessExecutionResult(0, string.Empty, string.Empty);
            });

        var factory = CreateContainerFactory(processRunnerMock.Object, useWsl: true);
        var options = new ContainerExecutorOptions
        {
            UseWsl = true,
            WslExecutable = "wsl.exe",
            WslDistribution = "Ubuntu-24.04",
            WslUserName = "wsluser",
            MountPackageCaches = true
        };

        await factory.EnsureHostPackageCacheDirectoriesOnceAsync(options);
        await factory.EnsureHostPackageCacheDirectoriesOnceAsync(options);
        await factory.EnsureHostPackageCacheDirectoriesOnceAsync(options);

        Assert.Equal(2, callCount);
    }
}
