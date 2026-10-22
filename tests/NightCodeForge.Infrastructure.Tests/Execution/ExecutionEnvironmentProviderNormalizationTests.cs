using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Tests.Fixtures;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class ExecutionEnvironmentProviderNormalizationTests
{
    private static Workspace CreateWorkspace(ExecutionMode mode) => new()
    {
        Id = 5,
        Name = "ws",
        RepositoryPath = Path.GetTempPath(),
        ExecutionMode = mode
    };

    [Fact]
    public void GetEnvironment_WslDirect_NormalizesOnlyOnceForCachedEnvironment()
    {
        var git = new Mock<IGitWorkspaceManager>();
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();
        var workspace = CreateWorkspace(ExecutionMode.WslDirect);

        provider.GetEnvironment(workspace, taskId: 9);
        provider.GetEnvironment(workspace, taskId: 9);

        git.Verify(g => g.EnsureWorktreeRelativePathsAsync(workspace, 9, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetEnvironment_BareHost_DoesNotNormalize()
    {
        var git = new Mock<IGitWorkspaceManager>();
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();

        provider.GetEnvironment(CreateWorkspace(ExecutionMode.BareHost), taskId: 9);

        git.Verify(g => g.EnsureWorktreeRelativePathsAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetEnvironment_NormalizationFailure_StillReturnsEnvironment()
    {
        var git = new Mock<IGitWorkspaceManager>();
        git.Setup(g => g.EnsureWorktreeRelativePathsAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();

        var env = provider.GetEnvironment(CreateWorkspace(ExecutionMode.WslDirect), taskId: 9);

        Assert.NotNull(env);
    }
}
