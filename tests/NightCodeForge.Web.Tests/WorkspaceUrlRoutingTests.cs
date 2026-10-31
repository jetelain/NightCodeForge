using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Layout;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkspaceUrlRoutingTests : BunitContext
{
    [Theory]
    [InlineData("http://localhost/w/5/backlog", 5, "backlog")]
    [InlineData("http://localhost/w/12", 12, "")]
    [InlineData("http://localhost/w/12/", 12, "")]
    [InlineData("http://localhost/w/3/tasks/42", 3, "tasks/42")]
    [InlineData("/w/7/review/99", 7, "review/99")]
    public void TryExtractWorkspaceId_ValidWorkspaceUrls_ReturnsTrueAndExtractsIdAndSubPath(
        string uri, int expectedWorkspaceId, string expectedSubPath)
    {
        var success = WorkspaceUrlHelper.TryExtractWorkspaceId(uri, out var workspaceId, out var subPath, out _);

        Assert.True(success);
        Assert.Equal(expectedWorkspaceId, workspaceId);
        Assert.Equal(expectedSubPath, subPath);
    }

    [Theory]
    [InlineData("http://localhost/", "")]
    [InlineData("http://localhost/backlog", "backlog")]
    [InlineData("http://localhost/tasks/42", "tasks/42")]
    [InlineData("http://localhost/w/abc/backlog", "w/abc/backlog")]
    [InlineData("http://localhost/w/0/backlog", "w/0/backlog")]
    public void TryExtractWorkspaceId_UnprefixedOrInvalidUrls_ReturnsFalse(string uri, string expectedSubPath)
    {
        var success = WorkspaceUrlHelper.TryExtractWorkspaceId(uri, out var workspaceId, out var subPath, out _);

        Assert.False(success);
        Assert.Equal(0, workspaceId);
        Assert.Equal(expectedSubPath, subPath);
    }

    [Theory]
    [InlineData("http://localhost/w/1/backlog", 2, "/w/2/backlog")]
    [InlineData("http://localhost/w/1", 2, "/w/2")]
    [InlineData("http://localhost/w/1/tasks/15", 2, "/w/2/backlog")]
    [InlineData("http://localhost/w/1/tasks/create", 2, "/w/2/tasks/create")]
    [InlineData("http://localhost/w/1/review/15", 2, "/w/2/review")]
    [InlineData("http://localhost/w/1/review", 2, "/w/2/review")]
    [InlineData("http://localhost/w/1/inspector/15", 2, "/w/2/inspector")]
    [InlineData("http://localhost/w/1/transcripts/session-1.md", 2, "/w/2/transcripts")]
    [InlineData("http://localhost/backlog", 3, "/w/3/backlog")]
    public void GetTargetUrlForWorkspaceSwitch_RewritesWorkspacePrefixAndDropsTaskSpecificParameters(
        string currentUri, int targetWorkspaceId, string expectedTargetUrl)
    {
        var actual = WorkspaceUrlHelper.GetTargetUrlForWorkspaceSwitch(currentUri, targetWorkspaceId);

        Assert.Equal(expectedTargetUrl, actual);
    }

    [Theory]
    [InlineData("http://localhost/w/2/backlog", "Backlog & DAG")]
    [InlineData("http://localhost/w/2/tasks/15", "Tasks")]
    [InlineData("http://localhost/w/2/tasks/create", "Tasks")]
    [InlineData("http://localhost/w/2/review/15", "Review Room")]
    [InlineData("http://localhost/w/2", "Overview")]
    public void GetSectionDisplayName_StripsWorkspacePrefixBeforeResolvingSection(
        string uri, string expectedSection)
    {
        Assert.Equal(expectedSection, WorkspaceUrlHelper.GetSectionDisplayName(uri));
    }

    [Fact]
    public async Task MainLayout_WhenLoadedAtWorkspaceUrl_HydratesMatchingWorkspaceInsteadOfDefaultFirst()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });

        var workspaceState = new WorkspaceStateService();
        RegisterLayoutServices(workspaceRepo, workspaceState);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{ws2.Id}/backlog");

        var cut = Render<MainLayout>();

        Assert.NotNull(workspaceState.ActiveWorkspace);
        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspace!.Id);
        Assert.Equal("Beta Repo", workspaceState.ActiveWorkspace.Name);
        Assert.Equal("Backlog & DAG", cut.Find(".breadcrumb-item.active").TextContent.Trim());
    }

    [Fact]
    public async Task MainLayout_WhenLoadedAtUnprefixedUrl_NormalizesUrlToIncludeActiveWorkspaceId()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });

        var workspaceState = new WorkspaceStateService();
        RegisterLayoutServices(workspaceRepo, workspaceState);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/backlog");

        Render<MainLayout>();

        Assert.Equal(ws1.Id, workspaceState.ActiveWorkspaceId);
        Assert.EndsWith($"/w/{ws1.Id}/backlog", nav.Uri);
    }

    [Fact]
    public async Task TopNavBar_SwitchingWorkspaceOnTaskDetailRoute_NavigatesToTargetWorkspaceBacklog()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Beta Repo", RepositoryPath = "/repos/beta" });

        var workspaceState = new WorkspaceStateService();
        RegisterLayoutServices(workspaceRepo, workspaceState);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{ws1.Id}/tasks/42");

        var cut = Render<TopNavBar>();

        Assert.Equal(ws1.Id, workspaceState.ActiveWorkspaceId);

        var select = cut.Find("select.workspace-dropdown");
        await select.ChangeAsync(new ChangeEventArgs { Value = ws2.Id.ToString() });

        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspaceId);
        Assert.EndsWith($"/w/{ws2.Id}/backlog", nav.Uri);
    }

    [Fact]
    public void NavMenu_WhenActiveWorkspaceSet_RendersWorkspacePrefixedHrefsAndAgnosticHomeBrandLink()
    {
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetActiveWorkspace(new Workspace { Id = 9, Name = "Nine Repo", RepositoryPath = "/repos/nine" });

        Services.AddSingleton(new WorkerStateService(new Mock<IOperationsTelemetryService>().Object));
        Services.AddSingleton(workspaceState);

        var cut = Render<NavMenu>();

        var backlogLink = cut.Find("a[href='w/9/backlog']");
        Assert.NotNull(backlogLink);

        var homeLink = cut.Find("a.brand-logo-wrap[href='']");
        Assert.NotNull(homeLink);
    }

    [Fact]
    public async Task MainLayout_WhenLoadedAtRootHomepage_StaysAtRootWithoutRedirectingToWorkspacePrefix()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Alpha Repo", RepositoryPath = "/repos/alpha" });

        var workspaceState = new WorkspaceStateService();
        RegisterLayoutServices(workspaceRepo, workspaceState);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/");

        Render<MainLayout>();

        Assert.Equal(ws1.Id, workspaceState.ActiveWorkspaceId);
        Assert.Equal("http://localhost/", nav.Uri);
    }

    [Theory]
    [InlineData(4, "backlog?priority=1#top", "/w/4/backlog?priority=1#top", "w/4/backlog?priority=1#top")]
    [InlineData(4, "/w/1/tasks/new?draft=true", "/w/4/tasks/new?draft=true", "w/4/tasks/new?draft=true")]
    [InlineData(4, "/hangfire", "/hangfire", "hangfire")]
    [InlineData(4, "/Error", "/Error", "Error")]
    [InlineData(null, "backlog?filter=open", "/backlog?filter=open", "backlog?filter=open")]
    public void BuildPath_And_BuildNavHref_PreserveQueryAndFragment_AndSkipNonWorkspaceRoutes(
        int? workspaceId, string subPath, string expectedPath, string expectedNavHref)
    {
        Assert.Equal(expectedPath, WorkspaceUrlHelper.BuildPath(workspaceId, subPath));
        Assert.Equal(expectedNavHref, WorkspaceUrlHelper.BuildNavHref(workspaceId, subPath));
    }

    [Fact]
    public async Task MainLayout_WhenUrlWorkspaceIdDoesNotExist_FallsBackToFirstWorkspaceAndRedirects()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Fallback Repo", RepositoryPath = "/repos/fallback" });

        var workspaceState = new WorkspaceStateService();
        RegisterLayoutServices(workspaceRepo, workspaceState);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/w/999/backlog?view=dag");

        Render<MainLayout>();

        Assert.Equal(ws1.Id, workspaceState.ActiveWorkspaceId);
        Assert.EndsWith($"/w/{ws1.Id}/backlog?view=dag", nav.Uri);
    }

    [Fact]
    public async Task TaskDetail_WhenUrlWorkspaceIdDiffersFromTaskWorkspace_ActivatesTaskWorkspaceAndNormalizesUrl()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Workspace One", RepositoryPath = "/repos/one" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Workspace Two", RepositoryPath = "/repos/two" });

        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws2.Id,
            Title = "Cross-Workspace Task",
            Status = TaskOverallStatus.Open
        });

        var depStore = new InMemoryTaskDependencyStore();
        var markdownSync = new MockTaskMarkdownSyncService();
        var orchestrator = new MockTaskOrchestrator();
        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces(new[] { ws1, ws2 });
        workspaceState.SetActiveWorkspace(ws1);

        var reviewService = new TaskReviewService(
            taskRepo,
            workspaceRepo,
            new Mock<IGitWorkspaceManager>().Object,
            NullLogger<TaskReviewService>.Instance);

        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskDependencyStore>(depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        Services.AddSingleton<ITaskMarkdownSyncService>(markdownSync);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(TestHubConnectionHelper.Create());
        Services.AddSingleton<ITaskOrchestrator>(orchestrator);
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            taskRepo,
            workspaceRepo,
            markdownSync,
            orchestrator,
            NullLogger<TaskMutationService>.Instance));
        Services.AddSingleton<ITaskReviewService>(reviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(reviewService));
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{ws1.Id}/tasks/{task.Id}");

        Render<TaskDetail>(p => p
            .Add(x => x.WorkspaceId, ws1.Id)
            .Add(x => x.TaskId, task.Id));

        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspaceId);
        Assert.EndsWith($"/w/{ws2.Id}/tasks/{task.Id}", nav.Uri);
    }

    [Fact]
    public async Task Review_WhenUrlWorkspaceIdDiffersFromTaskWorkspace_ActivatesTaskWorkspaceAndNormalizesUrl()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var ws1 = await workspaceRepo.CreateAsync(new Workspace { Name = "Workspace One", RepositoryPath = "/repos/one" });
        var ws2 = await workspaceRepo.CreateAsync(new Workspace { Name = "Workspace Two", RepositoryPath = "/repos/two" });

        var taskRepo = new InMemoryTaskRepository();
        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws2.Id,
            Title = "Reviewable Task in WS2",
            Status = TaskOverallStatus.Ready
        });

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces(new[] { ws1, ws2 });
        workspaceState.SetActiveWorkspace(ws1);

        var reviewService = new TaskReviewService(
            taskRepo,
            workspaceRepo,
            new Mock<IGitWorkspaceManager>().Object,
            NullLogger<TaskReviewService>.Instance);

        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<ITaskReviewService>(reviewService);
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        Services.AddSingleton(new ReviewCommentStateService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(workspaceState);
        Services.AddSingleton<ITaskMutationService>(new Mock<ITaskMutationService>().Object);
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"http://localhost/w/{ws1.Id}/review/{task.Id}");

        Render<Review>(p => p
            .Add(x => x.WorkspaceId, ws1.Id)
            .Add(x => x.TaskId, task.Id));

        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspaceId);
        Assert.EndsWith($"/w/{ws2.Id}/review/{task.Id}", nav.Uri);
    }

    [Fact]
    public void GitSync_WhenWorkspaceIdParameterChanges_FetchesDataOncePerWorkspaceSwitch()
    {
        var ws1 = new Workspace { Id = 1, Name = "WS 1", RepositoryPath = "/repos/1", Ext4MirrorPath = "/mirror/1" };
        var ws2 = new Workspace { Id = 2, Name = "WS 2", RepositoryPath = "/repos/2", Ext4MirrorPath = "/mirror/2" };

        var workspaceState = new WorkspaceStateService();
        workspaceState.SetKnownWorkspaces(new[] { ws1, ws2 });

        var gitSyncMock = new Mock<IGitSyncService>();
        gitSyncMock
            .Setup(s => s.GetMirrorStatusAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Workspace w, CancellationToken _) => new MirrorStatusSnapshot(
                w.Id, w.Ext4MirrorPath ?? string.Empty, w.RepositoryPath, "main", 0, 0, DateTime.UtcNow, false));
        gitSyncMock
            .Setup(s => s.GetWorktreesAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WorktreeSnapshot>());

        Services.AddSingleton(workspaceState);
        Services.AddSingleton(TestHubConnectionHelper.Create());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<IGitMirrorSyncService>(new Mock<IGitMirrorSyncService>().Object);
        Services.AddSingleton<IGitSyncService>(gitSyncMock.Object);

        var cut = Render<GitSync>(p => p.Add(x => x.WorkspaceId, ws1.Id));
        Assert.Equal(ws1.Id, workspaceState.ActiveWorkspaceId);
        gitSyncMock.Verify(s => s.GetMirrorStatusAsync(It.Is<Workspace>(w => w.Id == ws1.Id), It.IsAny<CancellationToken>()), Times.Once);

        cut.Render(p => p.Add(x => x.WorkspaceId, ws2.Id));
        Assert.Equal(ws2.Id, workspaceState.ActiveWorkspaceId);
        gitSyncMock.Verify(s => s.GetMirrorStatusAsync(It.Is<Workspace>(w => w.Id == ws2.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    private void RegisterLayoutServices(IWorkspaceRepository workspaceRepo, WorkspaceStateService workspaceState)
    {
        Services.AddSingleton(new WorkerStateService(new Mock<IOperationsTelemetryService>().Object));
        Services.AddSingleton(workspaceRepo);
        Services.AddSingleton(workspaceState);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        Services.AddSingleton(mockGateway.Object);
        Services.AddSingleton(new Mock<IWorkspaceProvisioningService>().Object);
        Services.AddSingleton(NullLogger<TopNavBar>.Instance);
    }
}
