using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskCreationModalTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkflowRepository _workflowRepo;
    private readonly ToastService _toastService;

    public TaskCreationModalTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workflowRepo = new InMemoryWorkflowRepository();
        _toastService = new ToastService();

        Services.AddSingleton<ITaskRepository>(_taskRepo);
        Services.AddSingleton<IWorkflowRepository>(_workflowRepo);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void TaskCreationModal_WhenNotOpen_DoesNotRender()
    {
        var cut = Render<TaskCreationModal>(p => p.Add(m => m.IsOpen, false));
        Assert.Empty(cut.FindAll(".task-creation-modal"));
    }

    [Fact]
    public async Task TaskCreationModal_WhenOpen_LoadsWorkflowsAndDefaultsToDefaultWorkflow()
    {
        var wf1 = new WorkflowDefinition { Id = 1, Name = "Standard 3-Agent", Code = "std-3", Icon = "⚡", IsDefault = true };
        var wf2 = new WorkflowDefinition { Id = 2, Name = "Fast Prototype", Code = "fast-proto", Icon = "🚀", IsDefault = false };

        await _workflowRepo.CreateWorkflowAsync(wf1);
        await _workflowRepo.CreateWorkflowAsync(wf2);

        var cut = Render<TaskCreationModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.WorkspaceId, 10));

        var modal = cut.Find(".task-creation-modal");
        Assert.NotNull(modal);
        Assert.Contains("Create New Task", modal.TextContent);

        var select = cut.Find("#workflow-select");
        var options = select.QuerySelectorAll("option");
        Assert.Equal(2, options.Length);
        Assert.Contains("Standard 3-Agent", select.TextContent);
        Assert.Contains("Fast Prototype", select.TextContent);
    }

    [Fact]
    public async Task TaskCreationModal_WhenSubmitted_CreatesTaskWithSelectedWorkflow()
    {
        var wf1 = new WorkflowDefinition { Id = 1, Name = "Standard Pipeline", Code = "std", Icon = "⚡", IsDefault = true };
        var wf2 = new WorkflowDefinition { Id = 2, Name = "Security Review Pipeline", Code = "sec", Icon = "🛡️", IsDefault = false };

        await _workflowRepo.CreateWorkflowAsync(wf1);
        await _workflowRepo.CreateWorkflowAsync(wf2);

        ForgeTask? createdTask = null;
        var cut = Render<TaskCreationModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.WorkspaceId, 5)
            .Add(m => m.DefaultWorkflowId, 2)
            .Add(m => m.OnTaskCreated, t => createdTask = t));

        // Enter Title
        var titleInput = cut.Find("#task-title-input");
        titleInput.Change("Implement RBAC Policy Validation");

        // Enter Description
        var descInput = cut.Find("#task-desc-input");
        descInput.Change("Verify role-based access control rules for maintainers.");

        // Workflow select should default to 2
        var wfSelect = cut.Find("#workflow-select");
        Assert.Equal("2", wfSelect.GetAttribute("value"));

        // Click Create button
        var submitBtn = cut.Find("#btn-confirm-create-task");
        await cut.InvokeAsync(() => submitBtn.Click());

        Assert.NotNull(createdTask);
        Assert.Equal("Implement RBAC Policy Validation", createdTask.Title);
        Assert.Equal(5, createdTask.WorkspaceId);
        Assert.Equal(2, createdTask.WorkflowId);
        Assert.Equal(TaskOverallStatus.Open, createdTask.Status);
        Assert.Contains("feature/task-", createdTask.FeatureBranch);
    }
}
