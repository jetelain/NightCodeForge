using Bunit;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Ideation;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Triage;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class Step1238TriageWebAndInspectorTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly WorkspaceStateService _workspaceState;
    private readonly CompanionTriageDraftStore _draftStore;
    private readonly MockTriageService _triageService;

    public Step1238TriageWebAndInspectorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _workspaceState = new WorkspaceStateService();
        _draftStore = new CompanionTriageDraftStore(TimeProvider.System);
        _triageService = new MockTriageService();

        var depStore = new InMemoryTaskDependencyStore();
        var mockOrchestrator = new MockTaskOrchestrator();
        var devQueue = CreateEmptyDevQueue();
        var taskReview = Mock.Of<ITaskReviewService>();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<ITaskDependencyStore>(depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ICompanionTriageDraftStore>(_draftStore);
        Services.AddSingleton<ITriageService>(_triageService);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<ITaskOrchestrator>(mockOrchestrator);
        Services.AddSingleton(devQueue);
        Services.AddSingleton(taskReview);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(taskReview));
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            depStore,
            new MockTaskMarkdownSyncService(),
            mockOrchestrator,
            Mock.Of<IOperationsTelemetryService>(),
            Mock.Of<NightCodeForge.Core.Interfaces.IIdeationConversationCoordinator>(),
            NullLogger<TaskMutationService>.Instance));
        Services.AddSingleton(TestHubConnectionHelper.Create());
        Services.AddSingleton<ReviewCommentStateService>();
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        TestServices.AddDefaultWebUiServices(Services);
    }

    private static IDevQueueService CreateEmptyDevQueue()
    {
        var mock = new Mock<IDevQueueService>();
        mock.Setup(d => d.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DevQueueSnapshot([], [], [], [], [], []));
        return mock.Object;
    }

    [Fact]
    public void BacklogStatusClassifier_ExcludesTriageFromBacklogColumnsAndRunnableEligibility()
    {
        var triageTask = new ForgeTask
        {
            Id = 1,
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage
        };

        Assert.True(BacklogStatusClassifier.IsNonBacklog(triageTask));
        Assert.Equal(BacklogKanbanColumn.NonBacklog, BacklogStatusClassifier.GetColumn(triageTask, activeTaskId: 1));
        Assert.False(BacklogStatusClassifier.IsEligibleToRun(triageTask));
        Assert.False(BacklogStatusClassifier.IsRunning(triageTask, activeTaskId: 1));
    }

    [Fact]
    public void TaskTableFilterService_ExcludesTriageSessionTasks()
    {
        var tasks = new[]
        {
            new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Triage: incoming bug", Status = TaskOverallStatus.Triage, HasExecutionRun = true },
            new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Open feature", Status = TaskOverallStatus.Open },
            new ForgeTask { Id = 3, WorkspaceId = 1, Title = "Suggested fix", Status = TaskOverallStatus.Suggested }
        };

        var filtered = TaskTableFilterService.FilterAndSort(
            tasks,
            searchQuery: null,
            statusFilter: TaskStatusFilter.All,
            priorityFilter: null,
            recencyFilter: TaskRecencyFilter.AllTime,
            sortColumn: TaskSortColumn.Id,
            sortAscending: true);

        Assert.Equal([2, 3], filtered.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task HomeDashboardBuilder_ExcludesTriageTasksFromWorkspaceStatsAndActivity()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Dashboard Repo",
            RepositoryPath = "/repos/dash",
            DefaultBaseBranch = "main"
        });

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: report",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            UpdatedAt = DateTime.UtcNow
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Real Open Task",
            Status = TaskOverallStatus.Open,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var snapshot = await HomeDashboardBuilder.BuildSnapshotAsync(
            workspaceRepo,
            taskRepo,
            CreateEmptyDevQueue(),
            NullLogger.Instance);

        var summary = Assert.Single(snapshot.RecentWorkspaces);
        Assert.Equal(1, summary.TotalTaskCount);
        Assert.Equal(1, summary.OpenTaskCount);
        Assert.Equal(0, summary.SuggestedTaskCount);
    }

    [Fact]
    public async Task DevQueueService_RetryCurrentStageAsync_RejectsTriageStatus()
    {
        var taskRepo = new InMemoryTaskRepository();
        var triageTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Triage: failed intake",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage
        });

        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(taskRepo);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var devQueue = new DevQueueService(
            new WorkspaceLockProvider(),
            Mock.Of<IBackgroundJobClient>(),
            Mock.Of<ITaskJobQueue>(),
            scopeFactory,
            Mock.Of<JobStorage>(),
            Mock.Of<ITaskCancellationManager>(),
            Mock.Of<IWorkspacePathResolver>(),
            Mock.Of<IDevQueueSkippedJobRegistry>(),
            Mock.Of<ITaskOrchestrator>(),
            NullLogger<DevQueueService>.Instance);

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => devQueue.RetryCurrentStageAsync(triageTask.Id));
    }

    [Fact]
    public void TaskInspectorGrouping_And_StageNameFormatting_HandleTriageSessions()
    {
        var runningTriage = new ForgeTask
        {
            Id = 10,
            Title = "Triage: live",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage
        };
        var finishedTriage = new ForgeTask
        {
            Id = 11,
            Title = "Triage: finished",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(
            [runningTriage, finishedTriage],
            activeTaskId: 10);

        Assert.Equal(2, groups.Count);
        Assert.Equal(TaskInspectorGroupingHelper.GroupRunning, groups[0].Label);
        Assert.Equal(10, Assert.Single(groups[0].Tasks).Id);
        Assert.Equal(TaskInspectorGroupingHelper.GroupTriage, groups[1].Label);
        Assert.Equal(11, Assert.Single(groups[1].Tasks).Id);

        Assert.Equal("Untrusted Intake Triage", StageConversationParser.FormatStageName(StandardStageIds.Triage));
        Assert.Equal("Untrusted Intake Triage", StageConversationParser.FormatStageName("TRIAGE"));
    }

    [Fact]
    public async Task TaskExecutionInspector_ForTriageSession_HidesStepperDiffAndRetry_ShowsVerbatimIntakeReportAndSkipsDefaultWorkflow()
    {
        var strictWorkflowRepo = new Mock<IWorkflowRepository>(MockBehavior.Strict);
        Services.AddSingleton(strictWorkflowRepo.Object);

        var triageTask = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Triage: NullReferenceException in Parser",
            Description = "## Initial Intake Report\n\nVerbatim crash details token_abc123",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "901",
            BaseBranch = "release/v2",
            FeatureBranch = string.Empty,
            Priority = TaskPriority.Normal
        });

        await _taskRepository.StartConversationAsync(
            triageTask.Id,
            StandardStageIds.Triage,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: StandardStageIds.Triage);

        var cut = Render<TaskExecutionInspector>(p => p.Add(x => x.TaskId, triageTask.Id));

        cut.WaitForAssertion(() => Assert.Contains("Untrusted Intake Triage", cut.Markup));

        strictWorkflowRepo.Verify(w => w.GetDefaultWorkflowWithStagesAsync(It.IsAny<CancellationToken>()), Times.Never);
        strictWorkflowRepo.VerifyNoOtherCalls();

        Assert.DoesNotContain("pipeline-stepper", cut.Markup);
        Assert.DoesNotContain("Retry Stage", cut.Markup);
        Assert.DoesNotContain("Restart Pipeline", cut.Markup);
        Assert.DoesNotContain("Git Diff", cut.Markup);
        Assert.Contains("GitHub Issue", cut.Markup);
        Assert.Contains("901", cut.Markup);
        Assert.Contains("release/v2", cut.Markup);

        var overviewBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Task Overview", StringComparison.Ordinal));
        await cut.InvokeAsync(() => overviewBtn.Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Full Initial Intake Report", cut.Markup);
            Assert.Contains("Verbatim crash details token_abc123", cut.Markup);
            Assert.DoesNotContain("Feature Branch", cut.Markup);
            Assert.DoesNotContain("Merge Mode", cut.Markup);
        });
    }

    [Fact]
    public async Task TriagePage_ManualSubmission_ShowsWatchLiveInspectorLinkWithBlankTargetWhileRunning_AndInspectLinkAndUpdatedBadgeAfter()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Core Repo",
            RepositoryPath = "/repos/core",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var tcs = new TaskCompletionSource<TriageSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _triageService.SessionHandler = (ws, req, onCreated, ct) =>
        {
            onCreated?.Invoke(456);
            return tcs.Task;
        };

        var cut = Render<Triage>();
        cut.Find("#triage-report-title").Input("Regression in webhook");
        cut.Find("#triage-description").Input("Reproduction details");

        var startTask = cut.InvokeAsync(() => cut.Find("#triage-submit-btn").ClickAsync(new()));

        cut.WaitForAssertion(() =>
        {
            var liveLink = cut.Find("#triage-watch-live-inspector-link");
            Assert.Equal("/w/1/inspector/456", liveLink.GetAttribute("href"));
            Assert.Equal("_blank", liveLink.GetAttribute("target"));
        });

        var createdTask = new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "New Suggested Bug",
            Description = "Details",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            BaseBranch = "main"
        };
        var updatedTask = new ForgeTask
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Updated Prior Bug",
            Description = "Updated repro",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            BaseBranch = "main"
        };

        tcs.SetResult(new TriageSessionResult(
            TriageTaskId: 456,
            Tasks: [createdTask, updatedTask],
            CreatedTaskIds: new HashSet<int> { 501 },
            UpdatedTaskIds: new HashSet<int> { 502 },
            RemovedTaskIds: new HashSet<int>()));

        await startTask;

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#triage-watch-live-inspector-link"));
            var inspectLink = cut.Find("#triage-inspect-session-link");
            Assert.Equal("/w/1/inspector/456", inspectLink.GetAttribute("href"));

            Assert.Empty(cut.FindAll("#triage-task-updated-badge-501"));
            var updatedBadge = cut.Find("#triage-task-updated-badge-502");
            Assert.Equal("Updated", updatedBadge.TextContent.Trim());
        });
    }

    [Fact]
    public async Task TriagePage_CompanionDraftRunHandle_SurfacesLiveInspectorLinkBeforeCompletion()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Core Repo",
            RepositoryPath = "/repos/core",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var runHandle = new TriageRunHandle();
        var sessionTcs = new TaskCompletionSource<TriageSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var draft = _draftStore.CreateDraft(
            workspaceId: 1,
            title: "Companion Bug",
            reportMarkdown: "Detailed stack trace",
            baseBranch: "main",
            runHandle: runHandle,
            sessionExecutionTask: sessionTcs.Task);

        runHandle.NotifyTriageTaskCreated(789);

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/w/1/triage?{TriageSubmissionModel.DraftIdQueryParameter}={Uri.EscapeDataString(draft.DraftId)}");

        var cut = Render<Triage>(p => p
            .Add(x => x.WorkspaceId, 1));

        cut.WaitForAssertion(() =>
        {
            var liveLink = cut.Find("#triage-watch-live-inspector-link");
            Assert.Equal("/w/1/inspector/789", liveLink.GetAttribute("href"));
            Assert.Equal("_blank", liveLink.GetAttribute("target"));
        });

        sessionTcs.SetResult(new TriageSessionResult(
            TriageTaskId: 789,
            Tasks:
            [
                new ForgeTask
                {
                    Id = 601,
                    WorkspaceId = 1,
                    Title = "Companion Validated Issue",
                    Description = "Scope",
                    Status = TaskOverallStatus.Suggested,
                    Origin = TaskOrigin.UntrustedIntake,
                    BaseBranch = "main"
                }
            ],
            CreatedTaskIds: new HashSet<int> { 601 },
            UpdatedTaskIds: new HashSet<int>(),
            RemovedTaskIds: new HashSet<int>()));

        await cut.InvokeAsync(() => sessionTcs.Task);

        cut.WaitForAssertion(() =>
        {
            var inspectLink = cut.Find("#triage-inspect-session-link");
            Assert.Equal("/w/1/inspector/789", inspectLink.GetAttribute("href"));
            Assert.NotNull(cut.Find("#triage-task-proposal-601"));
        });
    }

    [Fact]
    public async Task TaskMutationService_RejectsAllMutationsOnTriageTask_AndRejectsTriageDependency()
    {
        var triageTask = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Triage: incoming report",
            Description = "Verbatim",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            BaseBranch = "main"
        });
        var openTask = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Open Task",
            Description = "Work",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });

        var mutationService = Services.GetRequiredService<ITaskMutationService>();

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateTitleAsync(triageTask, "New Title"));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateDescriptionAsync(triageTask, "New Desc"));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdatePriorityAsync(triageTask, TaskPriority.Critical));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateDifficultyAsync(triageTask, TaskDifficulty.Expert));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateMergeModeAsync(triageTask, MergeMode.Automatic));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateBaseBranchAsync(triageTask, "develop"));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.UpdateDependenciesAsync(triageTask, [openTask.Id]));
        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() => mutationService.RejectTaskAsync(triageTask, "Reject"));

        var depEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutationService.UpdateDependenciesAsync(openTask, [triageTask.Id]));
        Assert.Contains("triage session", depEx.Message);
    }

    [Fact]
    public async Task TaskCreationUiService_ExcludesTriagePrerequisites_AndRejectsTriageCreationOrDependencies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf-step1238-create-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var ws = await _workspaceRepository.CreateAsync(new Workspace
            {
                Name = "Creation WS",
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            });

            var triageTask = await _taskRepository.CreateAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = "Triage: incoming",
                Status = TaskOverallStatus.Triage,
                HasExecutionRun = true,
                CurrentStageId = StandardStageIds.Triage
            });
            var openTask = await _taskRepository.CreateAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = "Valid Open Prerequisite",
                Status = TaskOverallStatus.Open
            });

            var creationService = Services.GetRequiredService<ITaskCreationUiService>();

            var selectable = await creationService.LoadSelectablePrerequisitesAsync(ws.Id);
            Assert.Equal([openTask.Id], selectable.Select(t => t.Id).ToArray());

            await Assert.ThrowsAsync<ArgumentException>(() => creationService.CreateTaskAsync(new TaskCreationRequest(
                WorkspaceId: ws.Id,
                Workspace: ws,
                RepositoryPath: tempDir,
                Title: "Manual Triage",
                Description: "Should fail",
                WorkflowId: null,
                Priority: TaskPriority.Normal,
                MergeMode: MergeMode.ManualReviewRequired,
                BaseBranch: "main",
                FeatureBranch: null,
                InitialStatus: TaskOverallStatus.Triage,
                PrerequisiteIds: null,
                LaunchImmediately: false,
                AuditReason: "Test")));

            var depEx = await Assert.ThrowsAsync<InvalidOperationException>(() => creationService.CreateTaskAsync(new TaskCreationRequest(
                WorkspaceId: ws.Id,
                Workspace: ws,
                RepositoryPath: tempDir,
                Title: "Task depending on Triage",
                Description: "Should fail",
                WorkflowId: null,
                Priority: TaskPriority.Normal,
                MergeMode: MergeMode.ManualReviewRequired,
                BaseBranch: "main",
                FeatureBranch: null,
                InitialStatus: TaskOverallStatus.Open,
                PrerequisiteIds: [triageTask.Id],
                LaunchImmediately: false,
                AuditReason: "Test")));
            Assert.Contains("triage session", depEx.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* Ignore temp cleanup errors */ }
        }
    }

    [Fact]
    public void TriagePresentationHelper_And_ProposalCard_FormatSessionSummaryAndNonSuggestedStatus()
    {
        var createdOnly = new TriageSessionResult(
            TriageTaskId: 10,
            Tasks: [new ForgeTask { Id = 1 }],
            CreatedTaskIds: new HashSet<int> { 1 },
            UpdatedTaskIds: new HashSet<int>(),
            RemovedTaskIds: new HashSet<int>());
        Assert.Equal("Triage agent formulated 1 suggested task(s).", TriagePresentationHelper.FormatSessionCompleteMessage(createdOnly));

        var mixed = new TriageSessionResult(
            TriageTaskId: 10,
            Tasks: [new ForgeTask { Id = 1 }, new ForgeTask { Id = 2 }],
            CreatedTaskIds: new HashSet<int> { 1 },
            UpdatedTaskIds: new HashSet<int> { 2 },
            RemovedTaskIds: new HashSet<int> { 3 });
        Assert.Equal("Triage complete (1 created, 1 updated, 1 removed).", TriagePresentationHelper.FormatSessionCompleteMessage(mixed));

        var updatedOnly = new TriageSessionResult(
            TriageTaskId: 10,
            Tasks: [new ForgeTask { Id = 2 }],
            CreatedTaskIds: new HashSet<int>(),
            UpdatedTaskIds: new HashSet<int> { 2 },
            RemovedTaskIds: new HashSet<int>());
        Assert.Equal("Triage complete (1 updated).", TriagePresentationHelper.FormatSessionCompleteMessage(updatedOnly));

        var updatedOpenTask = new ForgeTask
        {
            Id = 77,
            WorkspaceId = 1,
            Title = "Promoted triage item",
            Description = "Updated repro details",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.UntrustedIntake,
            BaseBranch = "main"
        };

        var card = Render<TriageTaskProposalCard>(p => p
            .Add(x => x.Task, updatedOpenTask)
            .Add(x => x.IsUpdated, true));

        Assert.Empty(card.FindAll(".badge-suggested"));
        Assert.Contains("Open", card.Find(".task-proposal-meta").TextContent);
        Assert.Equal("Updated", card.Find("#triage-task-updated-badge-77").TextContent.Trim());
    }

    [Fact]
    public async Task TriagePage_DisposedMidRun_DoesNotThrowOnCallbackOrCompletion_AndRetainsInspectLinkOnFailure()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Dispose Repo",
            RepositoryPath = "/repos/dispose",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        // 1. Disposing while manual triage is in flight does not throw on late callback or completion.
        var tcs = new TaskCompletionSource<TriageSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int>? capturedOnCreated = null;
        _triageService.SessionHandler = (ws, req, onCreated, ct) =>
        {
            capturedOnCreated = onCreated;
            return tcs.Task;
        };

        var cut = Render<Triage>();
        cut.Find("#triage-report-title").Input("Crash");
        cut.Find("#triage-description").Input("Details");
        var submitTask = cut.InvokeAsync(() => cut.Find("#triage-submit-btn").ClickAsync(new()));

        cut.Dispose();
        capturedOnCreated?.Invoke(991);
        tcs.SetResult(new TriageSessionResult(
            991,
            Array.Empty<ForgeTask>(),
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>()));
        await submitTask;

        // 2. Companion draft failure after TriageTaskId was created still displays the Inspect link.
        var runHandle = new TriageRunHandle();
        var failingTcs = new TaskCompletionSource<TriageSessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var draft = _draftStore.CreateDraft(
            workspaceId: 1,
            title: "Failing companion report",
            reportMarkdown: "Trace",
            baseBranch: "main",
            runHandle: runHandle,
            sessionExecutionTask: failingTcs.Task);
        runHandle.NotifyTriageTaskCreated(882);

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/w/1/triage?{TriageSubmissionModel.DraftIdQueryParameter}={Uri.EscapeDataString(draft.DraftId)}");

        var cut2 = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));
        cut2.WaitForAssertion(() => Assert.NotNull(cut2.Find("#triage-watch-live-inspector-link")));

        failingTcs.SetException(new InvalidOperationException("Simulated model failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failingTcs.Task);

        cut2.WaitForAssertion(() =>
        {
            Assert.Contains("Simulated model failure", cut2.Find("#triage-error-alert").TextContent);
            var inspectLink = cut2.Find("#triage-inspect-session-link");
            Assert.Equal("/w/1/inspector/882", inspectLink.GetAttribute("href"));
        });
    }

    [Fact]
    public async Task TaskDetail_ForTriageSession_HidesMutableControlsAndChangesTab_AndShowsVerbatimReport()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Name = "Detail WS",
            RepositoryPath = "/repos/detail",
            DefaultBaseBranch = "main"
        });

        var triageTask = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Triage: NRE on empty body",
            Description = "## Initial Intake Report\n\nVerbatim report token_xyz",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "321",
            BaseBranch = "release/v3",
            FeatureBranch = string.Empty
        });

        var cut = Render<TaskDetail>(p => p
            .Add(x => x.WorkspaceId, ws.Id)
            .Add(x => x.TaskId, triageTask.Id));

        cut.WaitForAssertion(() => Assert.Contains("Untrusted Intake Triage", cut.Markup));

        Assert.Contains("Full Initial Intake Report", cut.Markup);
        Assert.Contains("Verbatim report token_xyz", cut.Markup);
        Assert.Contains("GitHub Issue", cut.Markup);
        Assert.Contains("321", cut.Markup);
        Assert.Contains("release/v3", cut.Markup);

        Assert.Empty(cut.FindAll("#btn-edit-description"));
        Assert.Empty(cut.FindAll("#tab-btn-changes"));
        Assert.Empty(cut.FindAll("#btn-link-dependencies"));
        Assert.Empty(cut.FindAll("#btn-reject-task"));
        Assert.Empty(cut.FindAll("button[title='Edit title']"));
        Assert.DoesNotContain("Priority Level", cut.Markup);
        Assert.DoesNotContain("Task Difficulty Tier", cut.Markup);
        Assert.DoesNotContain("Merge Delivery Mode", cut.Markup);
        Assert.DoesNotContain("Feature Branch", cut.Markup);
        Assert.DoesNotContain($"forge/task-{triageTask.Id}", cut.Markup);
    }

    [Fact]
    public async Task IdeationProposalCard_ExcludesTriageSessionTasksFromSelectableDependencies()
    {
        var triageTask = new ForgeTask
        {
            Id = 901,
            WorkspaceId = 1,
            Title = "Triage: incoming report",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true
        };
        var openTask = new ForgeTask
        {
            Id = 902,
            WorkspaceId = 1,
            Title = "Real Backlog Task",
            Status = TaskOverallStatus.Open
        };
        var proposal = new IdeationProposalViewModel
        {
            Title = "New Proposal",
            Description = "Scope",
            Priority = TaskPriority.Normal
        };

        var cut = Render<IdeationProposalCard>(p => p
            .Add(x => x.Proposal, proposal)
            .Add(x => x.WorkspaceTasks, new[] { triageTask, openTask }));

        await cut.InvokeAsync(() => cut.Find(".dependency-toggle-btn").Click());

        cut.WaitForAssertion(() =>
        {
            var panel = cut.Find(".dependency-list-panel");
            Assert.Contains("#902", panel.TextContent);
            Assert.Contains("Real Backlog Task", panel.TextContent);
            Assert.DoesNotContain("#901", panel.TextContent);
            Assert.DoesNotContain("Triage: incoming report", panel.TextContent);
        });
    }

    [Fact]
    public async Task GitSyncService_MarksTriageWorktreeInactive_AndPrunesIt()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), $"ncf-step1238-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRepo);
        try
        {
            var ws = new Workspace
            {
                Id = 31,
                Name = "Git Sync WS",
                RepositoryPath = tempRepo,
                Ext4MirrorPath = tempRepo,
                DefaultBaseBranch = "main"
            };

            var triageTask = await _taskRepository.CreateAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = "Triage: phantom",
                Status = TaskOverallStatus.Triage,
                HasExecutionRun = true,
                CurrentStageId = StandardStageIds.Triage,
                BaseBranch = "main"
            });

            var gitCli = new Mock<IGitCliRunner>();
            gitCli.Setup(g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitCli.Setup(g => g.ExecuteAsync(tempRepo, It.Is<IEnumerable<string>>(a => a.SequenceEqual(new[] { "worktree", "list", "--porcelain" })), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, $"worktree {tempRepo}/task-{triageTask.Id}\nbranch refs/heads/feature/task-{triageTask.Id}\n\n", "", TimeSpan.Zero));

            var gitWorkspaceMgr = new Mock<IGitWorkspaceManager>();
            var envMock = new Mock<IExecutionEnvironment>();

            var gitSync = new GitSyncService(
                gitCli.Object,
                gitWorkspaceMgr.Object,
                new Mock<IGitMirrorSyncService>().Object,
                _taskRepository,
                new Mock<IWorkspacePathResolver>().Object,
                new Mock<IWslMetadataService>().Object,
                new SingleExecutionEnvironmentProvider(envMock.Object),
                new NightCodeForge.Core.Concurrency.WorkspaceLockProvider(),
                NullLogger<GitSyncService>.Instance);

            var worktrees = await gitSync.GetWorktreesAsync(ws);
            var wt = Assert.Single(worktrees);
            Assert.Equal(triageTask.Id, wt.TaskId);
            Assert.False(wt.IsActive);

            await gitSync.PruneWorktreesAsync(ws);
            gitWorkspaceMgr.Verify(m => m.RemoveWorktreeAsync(ws, triageTask.Id, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(tempRepo, true); } catch { /* Ignore temp cleanup errors */ }
        }
    }
}
