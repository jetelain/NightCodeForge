using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class CreateTaskPageTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly MockTaskJobQueue _jobQueue;
    private readonly ToastService _toastService;

    public CreateTaskPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _jobQueue = new MockTaskJobQueue();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<ITaskJobQueue>(_jobQueue);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void CreateTask_WhenNoWorkspace_RendersEmptyNotice()
    {
        var cut = Render<CreateTask>();

        Assert.NotEmpty(cut.FindAll(".empty-workspace-notice"));
        Assert.Contains("No Active Workspace Selected", cut.Find(".empty-workspace-notice").TextContent);
        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
    }

    [Fact]
    public void CreateTask_WhenWorkspaceActive_RendersFormWithDefaults()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Engine", DefaultBaseBranch = "develop" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        Assert.Contains("Create New Task", cut.Find("h1").TextContent);
        Assert.Contains("Alpha Engine", cut.Markup);

        var baseBranchInput = cut.Find("#base-branch-input");
        Assert.Equal("develop", baseBranchInput.GetAttribute("value"));

        // Default Priority P3 Normal selected
        var p3Chip = cut.FindAll(".priority-select-chip")[2];
        Assert.Contains("selected", p3Chip.ClassName);
        Assert.Contains("P3", p3Chip.TextContent);

        // Default MergeMode ManualReviewRequired selected
        var manualCard = cut.FindAll(".mergemode-card")[0];
        Assert.Contains("selected", manualCard.ClassName);
        Assert.Contains("Manual Review Required", manualCard.TextContent);
    }

    [Fact]
    public void CreateTask_WithoutTitle_DisplaysValidationError()
    {
        var ws = new Workspace { Id = 2, Name = "Beta Workspace" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        var btnOpen = cut.Find("#btn-save-as-open");
        btnOpen.Click();

        var alert = cut.Find(".alert-error");
        Assert.Contains("Please enter a task title.", alert.TextContent);
    }

    [Fact]
    public async Task CreateTask_SaveAsSuggested_CreatesSuggestedTask()
    {
        var ws = new Workspace { Id = 3, Name = "Gamma System" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        var titleInput = cut.Find("#task-title-input");
        titleInput.Input("Propose distributed caching layer");

        var descInput = cut.Find("#task-desc-input");
        descInput.Input("Redis sentinel cluster with fallback to memory cache.");

        var btnSuggested = cut.Find("#btn-save-as-suggested");
        btnSuggested.Click();

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(3);
        Assert.Single(tasks);

        var created = tasks[0];
        Assert.Equal("Propose distributed caching layer", created.Title);
        Assert.Equal(TaskOverallStatus.Suggested, created.Status);
        Assert.Equal(TaskPriority.Normal, created.Priority);
        Assert.Equal(MergeMode.ManualReviewRequired, created.MergeMode);
    }

    [Fact]
    public async Task CreateTask_SaveAsOpen_CreatesOpenTaskWithDependencies()
    {
        var ws = new Workspace { Id = 4, Name = "Delta Architecture" };
        _workspaceState.SetActiveWorkspace(ws);

        // Seed an existing prerequisite task
        var prereq = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 4,
            Title = "Database Migration Engine",
            Status = TaskOverallStatus.Open
        });

        var cut = Render<CreateTask>();

        var titleInput = cut.Find("#task-title-input");
        titleInput.Input("User Identity Store");

        // Select Priority 2 (High)
        var p2Chip = cut.FindAll(".priority-select-chip")[1];
        p2Chip.Click();

        // Select the prerequisite task
        var prereqOption = cut.Find(".prereq-option-item");
        prereqOption.Click();

        var btnOpen = cut.Find("#btn-save-as-open");
        btnOpen.Click();

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(4);
        var created = tasks.FirstOrDefault(t => t.Title == "User Identity Store");
        Assert.NotNull(created);

        Assert.Equal(TaskOverallStatus.Open, created.Status);
        Assert.Equal(TaskPriority.High, created.Priority);

        var deps = await _taskRepository.GetDependenciesAsync(created.Id);
        Assert.Contains(prereq.Id, deps);
    }

    [Fact]
    public async Task CreateTask_SaveAndLaunch_DispatchesCriticalToUrgent()
    {
        var ws = new Workspace { Id = 5, Name = "Epsilon Core" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        var titleInput = cut.Find("#task-title-input");
        titleInput.Input("Critical Security Hotfix");

        // Select Priority 1 (Critical)
        var p1Chip = cut.FindAll(".priority-select-chip")[0];
        p1Chip.Click();

        var btnLaunch = cut.Find("#btn-save-and-launch");
        btnLaunch.Click();

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(5);
        var created = tasks.FirstOrDefault(t => t.Title == "Critical Security Hotfix");
        Assert.NotNull(created);

        Assert.Equal(TaskPriority.Critical, created.Priority);
        Assert.Contains(created.Id, _jobQueue.UrgentEnqueued);
        Assert.Empty(_jobQueue.InferenceEnqueued);
    }

    [Fact]
    public async Task CreateTask_SaveAndLaunch_DispatchesNormalToInference()
    {
        var ws = new Workspace { Id = 6, Name = "Zeta Engine" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        var titleInput = cut.Find("#task-title-input");
        titleInput.Input("Standard Feature Pipeline");

        // Default Priority is 3 (Normal)
        var btnLaunch = cut.Find("#btn-save-and-launch");
        btnLaunch.Click();

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(6);
        var created = tasks.FirstOrDefault(t => t.Title == "Standard Feature Pipeline");
        Assert.NotNull(created);

        Assert.Equal(TaskPriority.Normal, created.Priority);
        Assert.Contains(created.Id, _jobQueue.InferenceEnqueued);
        Assert.Empty(_jobQueue.UrgentEnqueued);
    }

    [Fact]
    public void CreateTask_LiveMarkdownPreview_TogglesAndRendersMarkup()
    {
        var ws = new Workspace { Id = 7, Name = "Markdown Workspace" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<CreateTask>();

        var descInput = cut.Find("#task-desc-input");
        descInput.Input("## Feature Header\n\n- [x] Item 1\n- [ ] Item 2");

        // Click Preview tab
        var previewBtn = cut.FindAll(".forge-tab-btn").First(b => b.TextContent.Contains("Preview"));
        previewBtn.Click();

        var previewPane = cut.Find(".markdown-preview-pane");
        Assert.Contains("Feature Header", previewPane.InnerHtml);
    }

    [Fact]
    public async Task CreateTask_WhenWorkspaceChanges_ClearsSelectedPrerequisites()
    {
        var ws1 = new Workspace { Id = 8, Name = "Workspace Eight" };
        var ws2 = new Workspace { Id = 9, Name = "Workspace Nine" };

        var taskWs1 = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 8,
            Title = "Task in Workspace 8",
            Status = TaskOverallStatus.Open
        });
        var taskWs2 = await _taskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = 9,
            Title = "Task in Workspace 9",
            Status = TaskOverallStatus.Open
        });

        _workspaceState.SetActiveWorkspace(ws1);
        var cut = Render<CreateTask>();

        // Select task from Workspace 8 as prereq
        var prereqOption = cut.Find(".prereq-option-item");
        prereqOption.Click();

        Assert.NotEmpty(cut.FindAll(".selected-prereqs-chips"));
        Assert.Contains($"#{taskWs1.Id}", cut.Find(".selected-prereqs-chips").TextContent);

        // Change workspace to Workspace 9
        _workspaceState.SetActiveWorkspace(ws2);

        // Selected prerequisite chips from Workspace 8 should be cleared
        Assert.Empty(cut.FindAll(".selected-prereqs-chips"));
    }

    [Fact]
    public async Task CreateTask_FiltersOutTerminalTasksFromPrerequisites()
    {
        var ws = new Workspace { Id = 10, Name = "Filtering Workspace" };
        _workspaceState.SetActiveWorkspace(ws);

        await _taskRepository.CreateAsync(new ForgeTask { WorkspaceId = 10, Title = "Open Prereq", Status = TaskOverallStatus.Open });
        await _taskRepository.CreateAsync(new ForgeTask { WorkspaceId = 10, Title = "Failed Task", Status = TaskOverallStatus.Failed });
        await _taskRepository.CreateAsync(new ForgeTask { WorkspaceId = 10, Title = "Rejected Task", Status = TaskOverallStatus.Rejected });
        await _taskRepository.CreateAsync(new ForgeTask { WorkspaceId = 10, Title = "Cancelled Task", Status = TaskOverallStatus.Cancelled });

        var cut = Render<CreateTask>();

        var options = cut.FindAll(".prereq-option-item");
        Assert.Single(options);
        Assert.Contains("Open Prereq", options[0].TextContent);
        Assert.DoesNotContain("Failed Task", cut.Markup);
        Assert.DoesNotContain("Rejected Task", cut.Markup);
        Assert.DoesNotContain("Cancelled Task", cut.Markup);
    }
}
