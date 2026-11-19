using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TriagePageTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTriageService _triageService;
    private readonly CompanionTriageDraftStore _companionDraftStore;
    private readonly ToastService _toastService;

    public TriagePageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _triageService = new MockTriageService();
        _companionDraftStore = new CompanionTriageDraftStore(TimeProvider.System);
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITriageService>(_triageService);
        Services.AddSingleton<ICompanionTriageDraftStore>(_companionDraftStore);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void WhenNoActiveWorkspace_RendersEmptyHero()
    {
        var cut = Render<Triage>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.Empty(cut.FindAll("#triage-form-card"));
    }

    [Fact]
    public void WhenActiveWorkspace_RendersHeaderAndSecurityBanner()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Production Core Repo",
            RepositoryPath = @"C:\repos\core",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<Triage>();

        Assert.NotNull(cut.Find("#triage-portal-container"));
        Assert.NotNull(cut.Find("#triage-security-banner"));
        Assert.Contains("RestrictedReadOnly", cut.Find("#triage-security-banner").TextContent);
        Assert.Contains("INJECTION PROTECTED", cut.Find("#triage-security-banner").TextContent);
        Assert.Contains(":ro", cut.Find("#triage-security-banner").TextContent);
        Assert.Contains("--network none", cut.Find("#triage-security-banner").TextContent);
        Assert.Contains("--cap-drop=ALL", cut.Find("#triage-security-banner").TextContent);

        // Awaiting intake placeholder is visible initially
        Assert.NotNull(cut.Find("#triage-awaiting-card"));
    }

    [Fact]
    public void FormValidation_SubmitButtonDisabledUntilRequiredFieldsFilled()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<Triage>();

        var submitBtn = cut.Find("#triage-submit-btn");
        Assert.True(submitBtn.HasAttribute("disabled"));

        // Fill only title
        var titleInput = cut.Find("#triage-report-title");
        titleInput.Input("Memory leak in buffer pool");

        submitBtn = cut.Find("#triage-submit-btn");
        Assert.True(submitBtn.HasAttribute("disabled"));

        // Fill description
        var descInput = cut.Find("#triage-description");
        descInput.Input("Buffers are not being recycled when socket drops unexpectedly.");

        submitBtn = cut.Find("#triage-submit-btn");
        Assert.False(submitBtn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task SubmitReport_InvokesTriageService_WithFormattedDescriptionAndOrigin()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "master" };
        _workspaceState.SetActiveWorkspace(workspace);

        _triageService.Handler = (ws, req, ct) =>
        {
            var suggestedTask = new ForgeTask
            {
                Id = 42,
                WorkspaceId = ws.Id,
                Title = "Fix buffer pool recycling leak",
                Description = "Recycle rented byte buffers in dispose path",
                Status = TaskOverallStatus.Suggested,
                Priority = 1,
                BaseBranch = req.BaseBranch ?? "master",
                FeatureBranch = "fix/buffer-leak-42",
                CurrentStageId = "task-review",
                CreatedAt = DateTime.UtcNow
            };
            return Task.FromResult<IReadOnlyList<ForgeTask>>(new List<ForgeTask> { suggestedTask });
        };

        var cut = Render<Triage>();

        cut.Find("#triage-report-title").Input("Buffer pool leak");
        cut.Find("#triage-base-branch").Input("master");
        cut.Find("#triage-external-url").Change("https://github.com/org/repo/issues/402");
        cut.Find("#triage-description").Input("Buffers leak on disconnected client sockets.");
        cut.Find("#triage-stack-trace").Change("System.OutOfMemoryException: Array buffer exceeded");

        var submitBtn = cut.Find("#triage-submit-btn");
        await cut.InvokeAsync(() => submitBtn.Click());

        // Verify request payload sent to backend service
        Assert.NotNull(_triageService.LastRequest);
        Assert.Equal("Buffer pool leak", _triageService.LastRequest.Title);
        Assert.Equal("master", _triageService.LastRequest.BaseBranch);
        Assert.Contains("Buffers leak on disconnected client sockets.", _triageService.LastRequest.Description);
        Assert.Contains("System.OutOfMemoryException", _triageService.LastRequest.Description);
        Assert.Contains("https://github.com/org/repo/issues/402", _triageService.LastRequest.Description);

        // Verify results rendered
        var resultCard = cut.Find("#triage-task-proposal-42");
        Assert.NotNull(resultCard);
        Assert.Contains("Fix buffer pool recycling leak", resultCard.TextContent);
        Assert.Contains("Suggested", resultCard.TextContent);
        Assert.Contains("#42", resultCard.TextContent);
        Assert.Contains("P1", resultCard.TextContent);
        Assert.Contains("fix/buffer-leak-42", resultCard.TextContent);
    }

    [Fact]
    public void LoadSampleBugReport_PopulatesFieldsAndEnablesSubmission()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<Triage>();

        var sampleBtn = cut.Find("button[title='Load sample crash report']");
        sampleBtn.Click();

        var titleInput = cut.Find("#triage-report-title");
        var descInput = cut.Find("#triage-description");
        var submitBtn = cut.Find("#triage-submit-btn");

        Assert.Contains("NullReferenceException", titleInput.GetAttribute("value"));
        var descValue = descInput.GetAttribute("value") ?? descInput.TextContent;
        Assert.Contains("MessageParser", descValue);
        Assert.False(submitBtn.HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenTriageThrows_RendersErrorMessageAndEnablesRetry()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        _triageService.Handler = (ws, req, ct) =>
            throw new InvalidOperationException("Failed to spin up RestrictedReadOnly sandbox container.");

        var cut = Render<Triage>();

        cut.Find("#triage-report-title").Input("Crash on empty input");
        cut.Find("#triage-description").Input("Steps to reproduce crash...");

        var submitBtn = cut.Find("#triage-submit-btn");
        await cut.InvokeAsync(() => submitBtn.Click());

        var errorAlert = cut.Find("#triage-error-alert");
        Assert.NotNull(errorAlert);
        Assert.Contains("Failed to spin up RestrictedReadOnly sandbox container.", errorAlert.TextContent);

        // Submit button is re-enabled for retry
        Assert.False(cut.Find("#triage-submit-btn").HasAttribute("disabled"));
    }

    [Fact]
    public async Task WhenExistingSuggestedTasksExistInWorkspace_LoadsThemOnInitialize()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var preExistingTask = new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Investigate unexpected deserialization format",
            Description = "Incoming webhook payload schema changed",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = 2,
            BaseBranch = "main",
            FeatureBranch = "suggested/task-99",
            CurrentStageId = "triage",
            CreatedAt = DateTime.UtcNow
        };
        await _taskRepository.CreateAsync(preExistingTask);

        var cut = Render<Triage>();

        Assert.NotNull(cut.Find("#triage-task-proposal-99"));
        Assert.Contains("Investigate unexpected deserialization format", cut.Find("#triage-task-proposal-99").TextContent);
        Assert.Contains("Suggested", cut.Find("#triage-task-proposal-99").TextContent);
    }

    [Fact]
    public async Task WhenMixedSuggestedTasksExist_TriagePortalOnlyDisplaysUntrustedIntakeOrigin()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        // 1. Untrusted Intake Suggested task (should display)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Untrusted Vulnerability Report",
            Description = "Report from external researcher",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            CreatedAt = DateTime.UtcNow
        });

        // 2. Ideation Suggested task (should NOT display in triage)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 100,
            WorkspaceId = 1,
            Title = "Internal Ideation Feature",
            Description = "Brainstormed feature",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.Ideation,
            CreatedAt = DateTime.UtcNow
        });

        // 3. Manual Suggested task (should NOT display in triage)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Manual User Task",
            Description = "Manually created task",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.Manual,
            CreatedAt = DateTime.UtcNow
        });

        // 4. Untrusted intake task already accepted to Open (should NOT display in triage proposals)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Title = "Accepted Open Task",
            Description = "Already in active development queue",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.UntrustedIntake,
            CreatedAt = DateTime.UtcNow
        });

        var cut = Render<Triage>();

        // Only Task 99 is rendered
        Assert.NotNull(cut.Find("#triage-task-proposal-99"));
        Assert.Contains("Untrusted Vulnerability Report", cut.Find("#triage-task-proposal-99").TextContent);

        Assert.Null(cut.FindAll("#triage-task-proposal-100").FirstOrDefault());
        Assert.Null(cut.FindAll("#triage-task-proposal-101").FirstOrDefault());
        Assert.Null(cut.FindAll("#triage-task-proposal-102").FirstOrDefault());

        Assert.DoesNotContain("Internal Ideation Feature", cut.Markup);
        Assert.DoesNotContain("Manual User Task", cut.Markup);
        Assert.DoesNotContain("Accepted Open Task", cut.Markup);
    }

    [Fact]
    public async Task TriagePage_RendersSuggestedGitHubIssueTasksWithIssueBadge()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 103,
            WorkspaceId = 1,
            Title = "GitHub Synced Issue Proposal",
            Description = "Synced from GitHub issue #42",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.GitHubIssue,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "#42",
            CreatedAt = DateTime.UtcNow
        });

        var cut = Render<Triage>();

        var card = cut.Find("#triage-task-proposal-103");
        Assert.Contains("GitHub Synced Issue Proposal", card.TextContent);
        var issueBadge = cut.Find("#triage-github-issue-badge-103");
        Assert.Contains("#42", issueBadge.TextContent);
    }

    [Fact]
    public async Task WhenDraftIdMatchesActiveWorkspace_PopulatesFormFieldsAndAutoStartsTriage()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        _triageService.Handler = (_, _, _) => Task.FromResult<IReadOnlyList<ForgeTask>>(
        [
            new ForgeTask { Id = 77, Title = "Fix checkout 500 error" }
        ]);

        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Assay Exploratory Report — Checkout Error",
            reportMarkdown: "# Exploratory Report\n\nObserved 500 error on checkout.",
            baseBranch: "develop",
            sessionDirectoryPath: @"C:\sessions\2026-11-15");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", draft.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Equal("Assay Exploratory Report — Checkout Error", cut.Find("#triage-report-title").GetAttribute("value"));
        Assert.Equal("develop", cut.Find("#triage-base-branch").GetAttribute("value"));
        var descValue = cut.Find("#triage-description").GetAttribute("value") ?? cut.Find("#triage-description").TextContent;
        Assert.Contains("Observed 500 error on checkout.", descValue);
        Assert.False(cut.Find("#triage-submit-btn").HasAttribute("disabled"));
        Assert.NotNull(_triageService.LastRequest);
        Assert.NotNull(cut.Find("#triage-task-proposal-77"));
    }

    [Fact]
    public void WhenDraftHasPreStartedExecutionTask_ShowsRunningStateThenRendersProposalsWithoutDoubleExecution()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var tcs = new TaskCompletionSource<IReadOnlyList<ForgeTask>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Pre-started Assay Report",
            reportMarkdown: "# Exploratory Report\n\nPre-started background triage.",
            baseBranch: "main",
            executionTask: tcs.Task);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", draft.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));

        Assert.NotNull(cut.Find("#triage-running-card"));
        Assert.Null(_triageService.LastRequest);

        tcs.SetResult(
        [
            new ForgeTask { Id = 88, Title = "Investigate background triage finding" }
        ]);

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("#triage-running-card"));
            Assert.NotNull(cut.Find("#triage-task-proposal-88"));
        });
        Assert.Null(_triageService.LastRequest);
    }

    [Fact]
    public void WhenDraftIdBelongsToDifferentWorkspace_DoesNotPopulateFormFields()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var otherDraft = _companionDraftStore.CreateDraft(
            workspaceId: 2,
            title: "Other Workspace Report",
            reportMarkdown: "Should not appear in workspace 1",
            baseBranch: "release");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", otherDraft.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));

        Assert.True(string.IsNullOrEmpty(cut.Find("#triage-report-title").GetAttribute("value")));
        Assert.Equal("main", cut.Find("#triage-base-branch").GetAttribute("value"));
        Assert.True(cut.Find("#triage-submit-btn").HasAttribute("disabled"));
    }

    [Fact]
    public void WhenReRenderedWithSameDraftId_PreservesUserEdits()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Initial Draft Title",
            reportMarkdown: "Initial Draft Body",
            baseBranch: "main");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", draft.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));

        cut.Find("#triage-report-title").Input("Edited Title By User");

        cut.Render(p => p.Add(x => x.WorkspaceId, 1));

        Assert.Equal("Edited Title By User", cut.Find("#triage-report-title").GetAttribute("value"));
    }

    [Fact]
    public async Task WhenNavigatingToSecondDraftIdAndResettingForm_UpdatesFieldsClearsErrorAndPreservesReset()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        var draft1 = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "First Draft",
            reportMarkdown: "First Draft Body",
            baseBranch: "main");
        var draft2 = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Second Draft",
            reportMarkdown: "Second Draft Body",
            baseBranch: "release/v2");

        _triageService.Handler = (_, _, _) => throw new InvalidOperationException("Sandbox startup error");

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", draft1.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));
        cut.Find("#triage-external-url").Change("https://github.com/org/repo/issues/1");
        cut.Find("#triage-stack-trace").Change("System.Exception: boom");
        await cut.InvokeAsync(() => cut.Find("#triage-submit-btn").Click());

        Assert.NotNull(cut.Find("#triage-error-alert"));

        // Reset handler so automatic triage on draft2 succeeds and clears the prior error banner
        var draft2TriageCalls = 0;
        _triageService.Handler = (_, _, _) =>
        {
            draft2TriageCalls++;
            return Task.FromResult<IReadOnlyList<ForgeTask>>(Array.Empty<ForgeTask>());
        };

        // Navigating to a second distinct draftId replaces fields, clears ExternalUrl/StackTrace, and clears error banner
        nav.NavigateTo(nav.GetUriWithQueryParameter("draftId", draft2.DraftId));
        cut.Render(p => p.Add(x => x.WorkspaceId, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Equal(1, draft2TriageCalls);
        Assert.Empty(cut.FindAll("#triage-error-alert"));
        Assert.Equal("Second Draft", cut.Find("#triage-report-title").GetAttribute("value"));
        Assert.Equal("release/v2", cut.Find("#triage-base-branch").GetAttribute("value"));
        var descValue = cut.Find("#triage-description").GetAttribute("value") ?? cut.Find("#triage-description").TextContent;
        Assert.Contains("Second Draft Body", descValue);
        Assert.True(string.IsNullOrEmpty(cut.Find("#triage-external-url").GetAttribute("value")));
        var stackTraceValue = cut.Find("#triage-stack-trace").GetAttribute("value") ?? cut.Find("#triage-stack-trace").TextContent;
        Assert.True(string.IsNullOrEmpty(stackTraceValue));

        // Clicking Reset Form while ?draftId=... is active clears the form and stays cleared across re-renders
        cut.Find("button[title='Clear form inputs']").Click();
        cut.Render(p => p.Add(x => x.WorkspaceId, 1));

        Assert.True(string.IsNullOrEmpty(cut.Find("#triage-report-title").GetAttribute("value")));
        var clearedDesc = cut.Find("#triage-description").GetAttribute("value") ?? cut.Find("#triage-description").TextContent;
        Assert.True(string.IsNullOrEmpty(clearedDesc));
        Assert.Equal("main", cut.Find("#triage-base-branch").GetAttribute("value"));
        Assert.True(cut.Find("#triage-submit-btn").HasAttribute("disabled"));
    }

    [Fact]
    public void TryApplyCompanionDraft_NormalizesWhitespaceAroundDraftIdConsistently()
    {
        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 5,
            title: "Trimmed Draft Title",
            reportMarkdown: "Trimmed Draft Markdown",
            baseBranch: "develop");

        var model = new TriageSubmissionModel
        {
            ExternalUrl = "https://example.com/issue/1",
            StackTrace = "stack trace"
        };
        string? lastAppliedDraftId = null;

        var applied = model.TryApplyCompanionDraft(_companionDraftStore, 5, $"  {draft.DraftId}  ", ref lastAppliedDraftId);

        Assert.True(applied);
        Assert.Equal(draft.DraftId, lastAppliedDraftId);
        Assert.Equal("Trimmed Draft Title", model.Title);
        Assert.Equal("Trimmed Draft Markdown", model.Description);
        Assert.Equal("develop", model.BaseBranch);
        Assert.Null(model.ExternalUrl);
        Assert.Null(model.StackTrace);

        // Subsequent call with the unpadded draftId should treat it as the same draft
        model.Title = "User Edited Title";
        var reapplied = model.TryApplyCompanionDraft(_companionDraftStore, 5, draft.DraftId, ref lastAppliedDraftId);
        Assert.False(reapplied);
        Assert.Equal("User Edited Title", model.Title);
    }

    [Fact]
    public async Task WhenDraftExecutionTaskFails_DoesNotFallbackToPreExistingSuggestedTasks()
    {
        var workspace = new Workspace { Id = 1, Name = "Core", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(workspace);

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Pre-existing Unrelated Suggested Task",
            Description = "Old intake item",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            CreatedAt = DateTime.UtcNow
        });

        var faultedTask = Task.FromException<IReadOnlyList<ForgeTask>>(new InvalidOperationException("Boom"));
        var draft = _companionDraftStore.CreateDraft(
            workspaceId: 1,
            title: "Failed Assay Report",
            reportMarkdown: "# Exploratory Report\n\nRepro failed.",
            baseBranch: "main",
            executionTask: faultedTask);

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter(TriageSubmissionModel.DraftIdQueryParameter, draft.DraftId));

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));

        cut.WaitForAssertion(() =>
        {
            var errorAlert = cut.Find("#triage-error-alert");
            Assert.Contains("Boom", errorAlert.TextContent);
        });

        Assert.Empty(cut.FindAll("#triaged-tasks-list"));
        Assert.DoesNotContain("Pre-existing Unrelated Suggested Task", cut.Markup);
    }

    [Fact]
    public async Task WhenNavigatingAcrossWorkspacesToDraftId_AppliesDraftAndKeepsGeneratedProposals()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace One", DefaultBaseBranch = "main" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Two", DefaultBaseBranch = "develop" };
        _workspaceState.SetKnownWorkspaces([ws1, ws2]);
        _workspaceState.SetActiveWorkspace(ws1);

        var cut = Render<Triage>(p => p.Add(x => x.WorkspaceId, 1));
        Assert.Equal(1, _workspaceState.ActiveWorkspaceId);

        var ws2Draft = _companionDraftStore.CreateDraft(
            workspaceId: 2,
            title: "Cross-Workspace Assay Report",
            reportMarkdown: "# Exploratory Report\n\nObserved issue in workspace 2.",
            baseBranch: "release/ws2");

        var triageCallCount = 0;
        _triageService.Handler = (ws, req, _) =>
        {
            Interlocked.Increment(ref triageCallCount);
            return Task.FromResult<IReadOnlyList<ForgeTask>>(
            [
                new ForgeTask
                {
                    Id = 202,
                    WorkspaceId = ws.Id,
                    Title = "Fix WS2 regression",
                    Status = TaskOverallStatus.Suggested,
                    Origin = TaskOrigin.UntrustedIntake,
                    BaseBranch = req.BaseBranch ?? "develop"
                }
            ]);
        };

        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(TriageSubmissionModel.BuildCompanionDraftUrl(2, ws2Draft.DraftId));
        cut.Render(p => p.Add(x => x.WorkspaceId, 2));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _workspaceState.ActiveWorkspaceId);
            Assert.Equal("Cross-Workspace Assay Report", cut.Find("#triage-report-title").GetAttribute("value"));
            Assert.Equal("release/ws2", cut.Find("#triage-base-branch").GetAttribute("value"));
            Assert.NotNull(cut.Find("#triage-task-proposal-202"));
        });

        await cut.InvokeAsync(() => Task.CompletedTask);
        Assert.Equal(1, triageCallCount);
        Assert.NotNull(cut.Find("#triage-task-proposal-202"));
    }
}

public class MockTriageService : ITriageService
{
    public Func<Workspace, TriageReportRequest, CancellationToken, Task<IReadOnlyList<ForgeTask>>>? Handler { get; set; }
    public Func<Workspace, TriageReportRequest, Action<int>?, CancellationToken, Task<TriageSessionResult>>? SessionHandler { get; set; }
    public TriageReportRequest? LastRequest { get; private set; }
    public Workspace? LastWorkspace { get; private set; }

    public async Task<IReadOnlyList<ForgeTask>> TriageExternalReportAsync(
        Workspace workspace,
        TriageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        LastWorkspace = workspace;
        LastRequest = request;
        if (Handler != null)
        {
            return await Handler(workspace, request, cancellationToken);
        }
        return Array.Empty<ForgeTask>();
    }

    public async Task<TriageSessionResult> RunTriageSessionAsync(
        Workspace workspace,
        TriageReportRequest request,
        Action<int>? onTriageTaskCreated = null,
        CancellationToken cancellationToken = default)
    {
        LastWorkspace = workspace;
        LastRequest = request;
        if (SessionHandler != null)
        {
            return await SessionHandler(workspace, request, onTriageTaskCreated, cancellationToken);
        }

        var tasks = Handler != null
            ? await Handler(workspace, request, cancellationToken)
            : Array.Empty<ForgeTask>();
        var createdIds = tasks.Where(t => t.Id > 0).Select(t => t.Id).ToHashSet();
        return new TriageSessionResult(
            TriageTaskId: 0,
            Tasks: tasks,
            CreatedTaskIds: createdIds,
            UpdatedTaskIds: new HashSet<int>(),
            RemovedTaskIds: new HashSet<int>());
    }
}
