namespace NightCodeForge.Infrastructure.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

public class TestExecutionEnvironmentProviderBuilder
{
    public IProcessRunner ProcessRunner { get; set; } = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
    public IWorkspacePathResolver PathResolver { get; set; } = new WorkspacePathResolver(
        new Mock<IWslMetadataService>().Object,
        Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
    public BareHostExecutionEnvironment? BareHost { get; set; }
    public IOptions<ExecutionEnvironmentProviderOptions> Options { get; set; } = Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions());
    public IHostSdkDiscoveryService SdkDiscoveryService { get; set; } = new Mock<IHostSdkDiscoveryService>().Object;
    public IGitWorkspaceManager GitWorkspaceManager { get; set; } = new Mock<IGitWorkspaceManager>().Object;
    public IHostUserIdentityResolver UserIdentityResolver { get; set; } = new Mock<IHostUserIdentityResolver>().Object;
    public ILogger<NightCodeForgeContainerExecutor> Logger { get; set; } = NullLogger<NightCodeForgeContainerExecutor>.Instance;

    public ExecutionEnvironmentProvider Build()
    {
        var bareHost = BareHost ?? new BareHostExecutionEnvironment(ProcessRunner, PathResolver);
        return new ExecutionEnvironmentProvider(
            bareHost,
            ProcessRunner,
            Options,
            PathResolver,
            SdkDiscoveryService,
            GitWorkspaceManager,
            Logger,
            UserIdentityResolver);
    }
}
