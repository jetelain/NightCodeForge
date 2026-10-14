using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceStateServiceTests
{
    [Fact]
    public void InitialState_ShouldHaveNoActiveWorkspace()
    {
        var service = new WorkspaceStateService();

        Assert.Null(service.ActiveWorkspaceId);
        Assert.Null(service.ActiveWorkspace);
    }

    [Fact]
    public void SetActiveWorkspace_ShouldUpdatePropertiesAndNotify()
    {
        var service = new WorkspaceStateService();
        var workspace = new Workspace
        {
            Id = 42,
            Name = "TestRepo",
            RepositoryPath = "C:/repos/TestRepo"
        };

        var eventFired = false;
        service.OnWorkspaceChanged += () => eventFired = true;

        service.SetActiveWorkspace(workspace);

        Assert.Equal(42, service.ActiveWorkspaceId);
        Assert.Same(workspace, service.ActiveWorkspace);
        Assert.True(eventFired);
    }

    [Fact]
    public void SetActiveWorkspace_WhenNull_ShouldThrowArgumentNullException()
    {
        var service = new WorkspaceStateService();

        Assert.Throws<ArgumentNullException>(() => service.SetActiveWorkspace(null!));
    }

    [Fact]
    public void ClearActiveWorkspace_ShouldResetPropertiesAndNotify()
    {
        var service = new WorkspaceStateService();
        var workspace = new Workspace { Id = 1, Name = "Alpha" };
        service.SetActiveWorkspace(workspace);

        var eventFired = false;
        service.OnWorkspaceChanged += () => eventFired = true;

        service.ClearActiveWorkspace();

        Assert.Null(service.ActiveWorkspaceId);
        Assert.Null(service.ActiveWorkspace);
        Assert.True(eventFired);
    }
}
