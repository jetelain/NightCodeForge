using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Tests.Fixtures;

namespace NightCodeForge.Infrastructure.Tests;

public class ExecutionEnvironmentProviderTests
{
    [Fact]
    public void GetEnvironment_BareHost_ReturnsBareHostExecutionEnvironment()
    {
        var services = new ServiceCollection();
        services.AddNightCodeForgeCoreAndInfrastructure();
        using var provider = services.BuildServiceProvider();

        var envProvider = provider.GetRequiredService<IExecutionEnvironmentProvider>();
        var workspace = new Workspace
        {
            ExecutionMode = ExecutionMode.BareHost
        };

        var env = envProvider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<BareHostExecutionEnvironment>(env);
    }

    [Fact]
    public void GetEnvironment_WslDirect_ReturnsWslExecutionEnvironment()
    {
        var services = new ServiceCollection();
        services.AddNightCodeForgeCoreAndInfrastructure();
        using var provider = services.BuildServiceProvider();

        var envProvider = provider.GetRequiredService<IExecutionEnvironmentProvider>();
        var workspace = new Workspace
        {
            ExecutionMode = ExecutionMode.WslDirect,
            Ext4MirrorPath = "/mnt/wsl/repo"
        };

        var env = envProvider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<WslExecutionEnvironment>(env);
    }

    [Fact]
    public void GetEnvironment_ContainerSandbox_ReturnsContainerExecutor()
    {
        var services = new ServiceCollection();
        services.AddNightCodeForgeCoreAndInfrastructure();
        using var provider = services.BuildServiceProvider();

        var envProvider = provider.GetRequiredService<IExecutionEnvironmentProvider>();
        var workspace = new Workspace
        {
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = "mcr.microsoft.com/dotnet/sdk:10.0"
        };

        var env = envProvider.GetEnvironment(workspace);
        Assert.NotNull(env);
        Assert.IsType<NightCodeForgeContainerExecutor>(env);
    }

    [Fact]
    public void GetEnvironment_ContainerSandbox_CachesDistinctInstancesBySecurityTier()
    {
        var envProvider = new TestExecutionEnvironmentProviderBuilder().Build();
        var workspace = new Workspace
        {
            Id = 42,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = "alpine:latest"
        };

        var standardEnv = envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard);
        var readOnlyEnv = envProvider.GetEnvironment(workspace, SandboxSecurityTier.RestrictedReadOnly);
        var standardEnv2 = envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard);

        Assert.NotSame(standardEnv, readOnlyEnv);
        Assert.Same(standardEnv, standardEnv2);
    }

    [Fact]
    public void GetEnvironment_ContainerSandbox_UsesHostSdkDiscoveryServiceWhenAvailable()
    {
        var mockDiscovery = new FakeHostSdkDiscoveryService(new[] { "/opt/discovered-dotnet", "/opt/discovered-java" });
        var envProvider = new TestExecutionEnvironmentProviderBuilder { SdkDiscoveryService = mockDiscovery }.Build();
        var workspace = new Workspace
        {
            Id = 99,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            ContainerImage = "alpine:latest"
        };

        var env = envProvider.GetEnvironment(workspace);
        var containerExecutor = Assert.IsType<NightCodeForgeContainerExecutor>(env);

        Assert.Contains("/opt/discovered-dotnet", containerExecutor.Options.SdkMountPaths);
        Assert.Contains("/opt/discovered-java", containerExecutor.Options.SdkMountPaths);
    }

    [Fact]
    public void GetEnvironment_ContainerSandbox_WithTaskId_ConfiguresIsolatedWorktreeAndSeparateContainers()
    {
        var envProvider = new TestExecutionEnvironmentProviderBuilder().Build();
        var workspace = new Workspace
        {
            Id = 55,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = "/home/developer/.nightcodeforge/wks/55/clone",
            ContainerName = "ncf-sandbox-55"
        };

        var envTask1 = envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, taskId: 1);
        var envTask2 = envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, taskId: 2);
        var envNoTask = envProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard);

        Assert.NotSame(envTask1, envTask2);
        Assert.NotSame(envTask1, envNoTask);

        var exec1 = Assert.IsType<NightCodeForgeContainerExecutor>(envTask1);
        var exec2 = Assert.IsType<NightCodeForgeContainerExecutor>(envTask2);
        var execNo = Assert.IsType<NightCodeForgeContainerExecutor>(envNoTask);

        // ForgeTask 1 options
        Assert.Equal("ncf-sandbox-55-task-1", exec1.Options.ContainerNameOrId);
        Assert.EndsWith("55", exec1.Options.HostWorkspacePath?.Replace('\\', '/'));
        Assert.Equal("/workspace/task-1", exec1.Options.DefaultWorkingDirectory);
        Assert.EndsWith("clone", exec1.Options.CanonicalClonePath?.Replace('\\', '/'));

        // ForgeTask 2 options
        Assert.Equal("ncf-sandbox-55-task-2", exec2.Options.ContainerNameOrId);
        Assert.EndsWith("55", exec2.Options.HostWorkspacePath?.Replace('\\', '/'));
        Assert.Equal("/workspace/task-2", exec2.Options.DefaultWorkingDirectory);
        Assert.EndsWith("clone", exec2.Options.CanonicalClonePath?.Replace('\\', '/'));

        // No task (e.g. Triage on main)
        Assert.Equal("ncf-sandbox-55", execNo.Options.ContainerNameOrId);
        Assert.EndsWith("55", execNo.Options.HostWorkspacePath?.Replace('\\', '/'));
        Assert.Equal("/workspace/clone", execNo.Options.DefaultWorkingDirectory);
        Assert.EndsWith("clone", execNo.Options.CanonicalClonePath?.Replace('\\', '/'));
    }

    private class FakeHostSdkDiscoveryService : IHostSdkDiscoveryService
    {
        private readonly IReadOnlyList<string> _paths;
        public FakeHostSdkDiscoveryService(IReadOnlyList<string> paths) => _paths = paths;

        public IReadOnlyList<string> DiscoverSdkMountPaths(string? distribution = null) => _paths;
        public HostSdkDiscoveryDetails DiscoverSdkDetails(string? distribution = null) =>
            new(_paths, new Dictionary<string, string>(), new Dictionary<string, string>());
        public Task<IReadOnlyList<string>> DiscoverSdkMountPathsAsync(string? distribution = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_paths);
        public Task<HostSdkDiscoveryDetails> DiscoverSdkDetailsAsync(string? distribution = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(DiscoverSdkDetails(distribution));
        public void ClearCache() { }
    }
}

