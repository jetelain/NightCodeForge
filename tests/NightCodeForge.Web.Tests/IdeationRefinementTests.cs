using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Transcripts;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationRefinementTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState = new();
    private readonly InMemoryTaskRepository _taskRepository = new();
    private readonly MockIdeationConversationCoordinator _coordinator;
    private readonly IdeationSessionState _session;
    private readonly CompanionTriageDraftStore _companionDraftStore = new(TimeProvider.System);

    public IdeationRefinementTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var workspaceRepository = new InMemoryWorkspaceRepository();
        workspaceRepository.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        _coordinator = new MockIdeationConversationCoordinator(_taskRepository);
        _session = TestServices.CreateIdeationSessionState(
            coordinator: _coordinator,
            taskRepository: _taskRepository,
            companionDraftStore: _companionDraftStore);

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepository);
        Services.AddSingleton<ITaskMarkdownSyncService>(new MockTaskMarkdownSyncService());
        Services.AddSingleton<IIdeationConversationCoordinator>(_coordinator);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(_session);
        Services.AddSingleton(new IdeationRefinementService(_taskRepository));
        Services.AddSingleton<ICompanionTriageDraftStore>(_companionDraftStore);
        Services.AddSingleton(new Moq.Mock<ITaskMutationService>().Object);
        Services.AddSingleton(new Moq.Mock<IDevQueueService>().Object);
        TestServices.AddDefaultWebUiServices(Services);
    }

    private async Task<ForgeTask> SeedTaskAsync(TaskOverallStatus status = TaskOverallStatus.Open, bool hasExecutionRun = false, int workspaceId = 1)
    {
        return await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspaceId,
            Title = "Harden retry policy",
            Description = "d",
            Status = status,
            HasExecutionRun = hasExecutionRun
        });
    }

    [Theory]
    [InlineData(TaskOverallStatus.Draft, false, true)]
    [InlineData(TaskOverallStatus.Suggested, false, true)]
    [InlineData(TaskOverallStatus.Open, false, true)]
    [InlineData(TaskOverallStatus.Open, true, false)]
    [InlineData(TaskOverallStatus.Reviewed, false, false)]
    [InlineData(TaskOverallStatus.Implemented, false, false)]
    [InlineData(TaskOverallStatus.Ready, false, false)]
    [InlineData(TaskOverallStatus.Closed, false, false)]
    public void Drawer_RendersRefineButton_OnlyForUnstartedTasks(TaskOverallStatus status, bool hasExecutionRun, bool expected)
    {
        var task = new ForgeTask { Id = 5, WorkspaceId = 2, Title = "T", Status = status, HasExecutionRun = hasExecutionRun };

        var cut = Render<TaskDetailDrawer>(p => p.Add(d => d.IsOpen, true).Add(d => d.Task, task));

        var buttons = cut.FindAll("#btn-drawer-refine-ideation");
        Assert.Equal(expected, buttons.Count == 1);
        if (expected)
        {
            Assert.Equal("/w/2/ideation?taskId=5", buttons[0].GetAttribute("href"));
        }
    }

    [Fact]
    public void RefinementHelper_BuildsExpectedPromptTitleAndUrl()
    {
        var task = new ForgeTask { Id = 14, WorkspaceId = 3, Title = "Add retry" };

        Assert.Equal("/w/3/ideation?taskId=14", IdeationRefinementHelper.BuildRefineUrl(task));
        Assert.Equal("Refining Task #14: Add retry", IdeationRefinementHelper.BuildConversationTitle(task));
        Assert.Contains("get_task(14)", IdeationRefinementHelper.BuildSeedPrompt(task));
        Assert.Contains("'Add retry'", IdeationRefinementHelper.BuildSeedPrompt(task));
    }

    [Fact]
    public async Task Service_CreatesTaskScopedConversation_ThenResumesIt()
    {
        var task = await SeedTaskAsync();
        var service = new IdeationRefinementService(_taskRepository);

        var first = await service.InitializeTaskRefinementAsync(1, task.Id);
        var second = await service.InitializeTaskRefinementAsync(1, task.Id);

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.Conversation.Id, second.Conversation.Id);
        Assert.Equal($"Refining Task #{task.Id}: Harden retry policy", first.Conversation.Title);
        Assert.Single(await _taskRepository.GetIdeationConversationsAsync(1));
    }

    [Fact]
    public async Task Service_ThrowsDomainExceptions_ForUnknownOrStartedTasks()
    {
        var started = await SeedTaskAsync(hasExecutionRun: true);
        var otherWorkspace = await SeedTaskAsync(workspaceId: 9);
        var service = new IdeationRefinementService(_taskRepository);

        await Assert.ThrowsAsync<TaskNotFoundException>(() => service.InitializeTaskRefinementAsync(1, 12345));
        await Assert.ThrowsAsync<TaskNotFoundException>(() => service.InitializeTaskRefinementAsync(1, otherWorkspace.Id));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => service.InitializeTaskRefinementAsync(1, started.Id));
    }

    [Fact]
    public async Task IdeationPage_WithTaskIdQuery_PreSeedsRefinementPrompt()
    {
        var task = await SeedTaskAsync();
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/w/1/ideation?taskId={task.Id}");

        var cut = Render<Ideation>(p => p.Add(i => i.WorkspaceId, 1));

        cut.WaitForAssertion(() =>
        {
            var textarea = cut.Find("textarea.chat-textarea");
            Assert.Contains($"get_task({task.Id})", textarea.GetAttribute("value"));
            Assert.Equal($"Refining Task #{task.Id}: Harden retry policy", _session.ActiveConversationTitle);
        });

        var conversations = await _taskRepository.GetIdeationConversationsAsync(1);
        var onlyConversation = Assert.Single(conversations);
        Assert.Equal($"Refining Task #{task.Id}: Harden retry policy", onlyConversation.Title);
    }

    [Fact]
    public async Task IdeationPage_WithStartedTaskId_ShowsNoSeedAndDoesNotCreateRefinementSession()
    {
        var task = await SeedTaskAsync(hasExecutionRun: true);
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/w/1/ideation?taskId={task.Id}");

        var cut = Render<Ideation>(p => p.Add(i => i.WorkspaceId, 1));

        cut.WaitForAssertion(() => Assert.Empty(cut.Find("textarea.chat-textarea").GetAttribute("value") ?? string.Empty));
        Assert.DoesNotContain(
            (await _taskRepository.GetIdeationConversationsAsync(1)),
            c => c.Title != null && c.Title.StartsWith("Refining Task"));
    }

    [Fact]
    public void IdeationToolCall_RendersBacklogCard_ForMutationTools_AndRawCardOtherwise()
    {
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "Repo" });

        var card = Render<IdeationToolCall>(p => p
            .Add(c => c.ToolName, CanonicalToolNames.RemoveTask)
            .Add(c => c.Arguments, "{\"task_id\":18,\"reason\":\"dup\"}")
            .Add(c => c.Result, "Task #18 removed (status Cancelled)."));

        Assert.Single(card.FindAll("[data-testid='backlog-mutation-card']"));
        Assert.Contains("Removed Task #18", card.Markup);

        var raw = Render<IdeationToolCall>(p => p
            .Add(c => c.ToolName, "read")
            .Add(c => c.Arguments, "{\"path\":\"a\"}"));

        Assert.Empty(raw.FindAll("[data-testid='backlog-mutation-card']"));
    }

    [Fact]
    public void ProposalResolveDependencies_ExcludesAndRemovesCancelledOrRejectedTasks()
    {
        var activeTask = new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Active Prerequisite", Status = TaskOverallStatus.Open };
        var cancelledTask = new ForgeTask { Id = 11, WorkspaceId = 1, Title = "Obsolete Prerequisite", Status = TaskOverallStatus.Suggested };

        var proposal = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("New Feature", "desc", DependsOnTitles: ["#10", "Obsolete Prerequisite"]),
            [activeTask, cancelledTask]);

        Assert.Equal([10, 11], proposal.SelectedDependencyIds);

        cancelledTask.Status = TaskOverallStatus.Cancelled;
        proposal.ResolveDependencies([activeTask, cancelledTask]);

        Assert.Equal([10], proposal.SelectedDependencyIds);
    }

    [Fact]
    public async Task IdeationPage_WithDraftIdQuery_StartsConversationAndPreSeedsReportPrompt()
    {
        var coordinator = (MockIdeationConversationCoordinator)Services.GetRequiredService<IIdeationConversationCoordinator>();
        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Assay Exploratory Report — Checkout Crash",
            reportMarkdown: "# Exploratory Report\n\nClicking Pay with empty cart returns HTTP 500.",
            baseBranch: "release/v2",
            sessionDirectoryPath: @"C:\sessions\assay-1");

        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" });
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/w/1/ideation?draftId={draft.DraftId}");

        var cut = Render<Ideation>(p => p.Add(i => i.WorkspaceId, 1));

        cut.WaitForAssertion(() =>
        {
            var textarea = cut.Find("textarea.chat-textarea");
            var value = textarea.GetAttribute("value");
            Assert.Contains("Assay Exploratory Report — Checkout Crash", value);
            Assert.Contains("release/v2", value);
            Assert.Contains("Clicking Pay with empty cart returns HTTP 500.", value);
            Assert.Equal("Assay Exploratory Report — Checkout Crash", _session.ActiveConversationTitle);
        });

        var conversations = await _taskRepository.GetIdeationConversationsAsync(1);
        var onlyConversation = Assert.Single(conversations);
        Assert.Equal("Assay Exploratory Report — Checkout Crash", onlyConversation.Title);
        Assert.False(_session.IsGenerating);
        Assert.Empty(_session.Messages);
        Assert.Null(coordinator.LastConversationId);
    }

    [Fact]
    public void IdeationHelper_BuildsCompanionDraftUrlAndSeedPrompt_AndDeduplicatesDraftId()
    {
        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 3,
            title: "Assay Report",
            reportMarkdown: "# Findings\nSomething broke.",
            baseBranch: "main");

        Assert.Equal($"/w/3/ideation?draftId={draft.DraftId}", IdeationRefinementHelper.BuildCompanionDraftUrl(3, draft.DraftId));

        string? lastApplied = null;
        Assert.True(IdeationRefinementHelper.TryResolveCompanionDraft(_companionDraftStore, 3, $" {draft.DraftId} ", ref lastApplied, out var resolved));
        Assert.Same(draft, resolved);
        Assert.Equal(draft.DraftId, lastApplied);
        Assert.False(IdeationRefinementHelper.TryResolveCompanionDraft(_companionDraftStore, 3, draft.DraftId, ref lastApplied, out _));

        var prompt = IdeationRefinementHelper.BuildCompanionSeedPrompt(draft);
        Assert.Contains("Assay Report", prompt);
        Assert.Contains("base branch 'main'", prompt);
        Assert.Contains("Something broke.", prompt);
    }

    [Fact]
    public async Task IdeationPage_WhenSwitchingWorkspaceWithDraftIdQuery_InitializesSingleDraftConversationWithoutClobbering()
    {
        var ws1 = new Workspace { Id = 1, Name = "Repo One", DefaultBaseBranch = "main" };
        var ws2 = new Workspace { Id = 2, Name = "Repo Two", DefaultBaseBranch = "develop" };
        _workspaceState.SetKnownWorkspaces([ws1, ws2]);
        _workspaceState.SetActiveWorkspace(ws1);

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/w/1/ideation");

        var cut = Render<Ideation>(p => p.Add(i => i.WorkspaceId, 1));
        cut.WaitForAssertion(() => Assert.Equal(1, _session.BoundWorkspaceId));

        var ws2Draft = _companionDraftStore.CreateDraft(
            workspaceId: 2,
            title: "Assay Report — WS2 Freeze",
            reportMarkdown: "# Exploratory Report\n\nCross-workspace UI freeze.",
            baseBranch: "develop");

        nav.NavigateTo(IdeationRefinementHelper.BuildCompanionDraftUrl(2, ws2Draft.DraftId));
        cut.Render(p => p.Add(i => i.WorkspaceId, 2));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _workspaceState.ActiveWorkspaceId);
            Assert.Equal(2, _session.BoundWorkspaceId);
            Assert.Equal("Assay Report — WS2 Freeze", _session.ActiveConversationTitle);
            var textareaValue = cut.Find("textarea.chat-textarea").GetAttribute("value");
            Assert.Contains("Assay Report — WS2 Freeze", textareaValue);
            Assert.Contains("develop", textareaValue);
            Assert.Contains("Cross-workspace UI freeze.", textareaValue);
        });

        await cut.InvokeAsync(() => Task.CompletedTask);
        var ws2Conversations = await _taskRepository.GetIdeationConversationsAsync(2);
        var onlyWs2Conversation = Assert.Single(ws2Conversations);
        Assert.Equal("Assay Report — WS2 Freeze", onlyWs2Conversation.Title);
        Assert.Equal(onlyWs2Conversation.Id, _session.ActiveConversationId);
    }
}
