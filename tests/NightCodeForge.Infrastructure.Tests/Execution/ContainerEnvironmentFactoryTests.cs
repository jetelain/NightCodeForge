using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class ContainerEnvironmentFactoryTests
{
    private readonly Mock<IProcessRunner> _processRunnerMock = new();
    private readonly IOptions<ExecutionEnvironmentProviderOptions> _providerOptions;
    private readonly Mock<IWorkspacePathResolver> _pathResolverMock = new();
    private readonly Mock<IHostSdkDiscoveryService> _sdkDiscoveryMock = new();
    private readonly Mock<IHostUserIdentityResolver> _identityResolverMock = new();
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Mock<ICompanionToolInstaller> _companionInstallerMock = new();
    private readonly ILogger<NightCodeForgeContainerExecutor> _logger = NullLogger<NightCodeForgeContainerExecutor>.Instance;

    public ContainerEnvironmentFactoryTests()
    {
        _providerOptions = Options.Create(new ExecutionEnvironmentProviderOptions
        {
            DefaultWslDistribution = "Ubuntu-24.04",
            WslExecutable = "wsl.exe"
        });

        _pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        _pathResolverMock.Setup(p => p.GetWslUserName()).Returns("testuser");
        _sdkDiscoveryMock.Setup(s => s.DiscoverSdkMountPaths(It.IsAny<string?>())).Returns(["/host/dotnet"]);
        _identityResolverMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>()))
            .Returns("1000:1000");
    }

    private ContainerEnvironmentFactory CreateSut() => new(
        _processRunnerMock.Object,
        _providerOptions,
        _pathResolverMock.Object,
        _sdkDiscoveryMock.Object,
        _identityResolverMock.Object,
        _telemetryMock.Object,
        _companionInstallerMock.Object,
        _logger);

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(null!, _providerOptions, _pathResolverMock.Object, _sdkDiscoveryMock.Object, _identityResolverMock.Object, _telemetryMock.Object, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, null!, _pathResolverMock.Object, _sdkDiscoveryMock.Object, _identityResolverMock.Object, _telemetryMock.Object, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, null!, _sdkDiscoveryMock.Object, _identityResolverMock.Object, _telemetryMock.Object, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, _pathResolverMock.Object, null!, _identityResolverMock.Object, _telemetryMock.Object, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, _pathResolverMock.Object, _sdkDiscoveryMock.Object, null!, _telemetryMock.Object, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, _pathResolverMock.Object, _sdkDiscoveryMock.Object, _identityResolverMock.Object, null!, _companionInstallerMock.Object, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, _pathResolverMock.Object, _sdkDiscoveryMock.Object, _identityResolverMock.Object, _telemetryMock.Object, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new ContainerEnvironmentFactory(_processRunnerMock.Object, _providerOptions, _pathResolverMock.Object, _sdkDiscoveryMock.Object, _identityResolverMock.Object, _telemetryMock.Object, _companionInstallerMock.Object, null!));
    }

    [Fact]
    public void DefaultWslDistribution_ReturnsConfiguredOption()
    {
        var sut = CreateSut();
        Assert.Equal("Ubuntu-24.04", sut.DefaultWslDistribution);
    }

    [Fact]
    public void CreateContainerEnvironment_WithTaskId_ConstructsExecutorWithTaskSpecificNameWorkingDirAndWritableTmpMount()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Container Factory Test",
            RepositoryPath = "/host/wks/1/clone",
            ContainerName = "ncf-sandbox-wks-1",
            ContainerImage = "ncf-sandbox:custom",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var env = sut.CreateContainerEnvironment(workspace, SandboxSecurityTier.Standard, effectiveTaskId: 99);

        var executor = Assert.IsType<NightCodeForgeContainerExecutor>(env);
        Assert.Equal("ncf-sandbox-wks-1-task-99", executor.Options.ContainerNameOrId);
        Assert.Equal("ncf-sandbox:custom", executor.Options.ContainerImage);
        Assert.Equal("/host/wks/1", executor.Options.HostWorkspacePath);
        Assert.Equal("/host/wks/1/clone", executor.Options.CanonicalClonePath);
        Assert.Equal("/workspace", executor.Options.ContainerWorkspacePath);
        Assert.Equal("/workspace/task-99", executor.Options.DefaultWorkingDirectory);
        Assert.False(executor.Options.IsRestrictedReadOnly);
        Assert.Contains("/host/dotnet", executor.Options.SdkMountPaths);
        Assert.Equal("1000:1000", executor.Options.UserUidGid);
        Assert.Contains(executor.Options.AdditionalVolumeMounts, m => m.ContainerPath == "/tmp" && !m.IsReadOnly);
    }

    [Fact]
    public void CreateContainerEnvironment_RestrictedReadOnlyWithoutTaskId_UsesBaseContainerNameAndOmitsTmpMount()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 2,
            Name = "ReadOnly Test",
            RepositoryPath = "/host/wks/2/clone",
            ContainerImage = "ncf-agent-base:latest",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var env = sut.CreateContainerEnvironment(workspace, SandboxSecurityTier.RestrictedReadOnly, effectiveTaskId: null);

        var executor = Assert.IsType<NightCodeForgeContainerExecutor>(env);
        Assert.Equal("ncf-sandbox-2", executor.Options.ContainerNameOrId);
        Assert.Equal(ContainerExecutorOptions.DefaultAgentBaseImage, executor.Options.ContainerImage);
        Assert.Equal("/workspace/clone", executor.Options.DefaultWorkingDirectory);
        Assert.True(executor.Options.IsRestrictedReadOnly);
        Assert.DoesNotContain(executor.Options.AdditionalVolumeMounts, m => m.ContainerPath == "/tmp");
    }

    [Fact]
    public void CreateContainerEnvironment_WhenCompanionToolInstallerThrows_SwallowsAndStillConstructsExecutor()
    {
        _companionInstallerMock
            .Setup(c => c.EnsureToolsInstalledAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Installer failed"));

        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 5,
            Name = "Installer Failure WS",
            Ext4MirrorPath = "/home/user/.ncf/wks/5/clone",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var env = sut.CreateContainerEnvironment(workspace, SandboxSecurityTier.Standard, effectiveTaskId: 1);
        Assert.IsType<NightCodeForgeContainerExecutor>(env);
    }

    [Fact]
    public async Task RemoveTaskContainerAsync_And_RemoveBaseContainerAsync_InvokeDockerRmForceForContainerSandboxOnly()
    {
        var sut = CreateSut();
        var executedSpecs = new List<ProcessStartSpec>();
        _processRunnerMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => executedSpecs.Add(spec))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var bareHostWorkspace = new Workspace { Id = 1, ExecutionMode = ExecutionMode.BareHost };
        await sut.RemoveTaskContainerAsync(bareHostWorkspace, 10);
        await sut.RemoveBaseContainerAsync(bareHostWorkspace);
        Assert.Empty(executedSpecs);

        var containerWorkspace = new Workspace
        {
            Id = 3,
            ContainerName = "ncf-wks-3",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            UseWslForContainers = false
        };
        await sut.RemoveTaskContainerAsync(containerWorkspace, 42);
        await sut.RemoveBaseContainerAsync(containerWorkspace);

        Assert.Equal(2, executedSpecs.Count);
        Assert.Contains(executedSpecs, s => string.Join(" ", s.ArgumentList ?? []).Contains("ncf-wks-3-task-42"));
        Assert.Contains(executedSpecs, s => string.Join(" ", s.ArgumentList ?? []).EndsWith("ncf-wks-3"));
    }

    [Fact]
    public async Task ListTaskContainerIdsAsync_ParsesValidTaskIdsAndHandlesNonContainerOrFailedProcessGracefully()
    {
        var sut = CreateSut();
        var bareWorkspace = new Workspace { Id = 1, ExecutionMode = ExecutionMode.BareHost };
        Assert.Empty(await sut.ListTaskContainerIdsAsync(bareWorkspace));

        var dockerPsOutput = """
            /ncf-wks-4-task-12
            ncf-wks-4-task-25
            ncf-wks-4-task-notanumber
            ncf-wks-40-task-99
            """;

        _processRunnerMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, dockerPsOutput, string.Empty));

        var workspace = new Workspace
        {
            Id = 4,
            ContainerName = "ncf-wks-4",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var ids = await sut.ListTaskContainerIdsAsync(workspace);
        Assert.Equal([12, 25], ids.OrderBy(x => x).ToList());

        // Non-zero exit code returns empty list
        _processRunnerMock.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "Cannot connect to Docker daemon"));
        Assert.Empty(await sut.ListTaskContainerIdsAsync(workspace));
    }
}
