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
    public async Task GetEnvironmentAsync_WslDirect_NormalizesOnlyOnceForCachedEnvironment()
    {
        var git = new Mock<IGitWorkspaceManager>();
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();
        var workspace = CreateWorkspace(ExecutionMode.WslDirect);

        await provider.GetEnvironmentAsync(workspace, taskId: 9);
        await provider.GetEnvironmentAsync(workspace, taskId: 9);

        git.Verify(g => g.EnsureWorktreeRelativePathsAsync(workspace, 9, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetEnvironmentAsync_BareHost_DoesNotNormalize()
    {
        var git = new Mock<IGitWorkspaceManager>();
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();

        await provider.GetEnvironmentAsync(CreateWorkspace(ExecutionMode.BareHost), taskId: 9);

        git.Verify(g => g.EnsureWorktreeRelativePathsAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetEnvironmentAsync_NormalizationFailure_StillReturnsEnvironment()
    {
        var git = new Mock<IGitWorkspaceManager>();
        git.Setup(g => g.EnsureWorktreeRelativePathsAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("boom"));
        var provider = new TestExecutionEnvironmentProviderBuilder { GitWorkspaceManager = git.Object }.Build();

        var env = await provider.GetEnvironmentAsync(CreateWorkspace(ExecutionMode.WslDirect), taskId: 9);

        Assert.NotNull(env);
    }
}
