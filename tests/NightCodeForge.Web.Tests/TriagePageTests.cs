using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
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
    private readonly ToastService _toastService;

    public TriagePageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _triageService = new MockTriageService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITriageService>(_triageService);
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
}

public class MockTriageService : ITriageService
{
    public Func<Workspace, TriageReportRequest, CancellationToken, Task<IReadOnlyList<ForgeTask>>>? Handler { get; set; }
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
}
