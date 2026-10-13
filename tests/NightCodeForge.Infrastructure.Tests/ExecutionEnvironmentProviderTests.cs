using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

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
}
