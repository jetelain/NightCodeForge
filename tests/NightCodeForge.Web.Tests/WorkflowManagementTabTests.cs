using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkflowManagementTabTests : BunitContext
{
    private readonly InMemoryWorkflowRepository _workflowRepo;
    private readonly ToastService _toastService;
    private readonly WorkspaceStateService _workspaceState;

    public WorkflowManagementTabTests()
    {
        _workflowRepo = new InMemoryWorkflowRepository();
        _toastService = new ToastService();
        _workspaceState = new WorkspaceStateService();

        Services.AddSingleton<IWorkflowRepository>(_workflowRepo);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workspaceState);
    }

    [Fact]
    public async Task WorkflowManagementTab_RendersWorkflowsAndStageDetails()
    {
        var wf = new WorkflowDefinition
        {
            Id = 1,
            Name = "Standard 3-Agent Pipeline",
            Code = "standard-3-agent",
            Icon = "⚡",
            Description = "Default autonomous pipeline with review and test gates.",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new()
                {
                    Id = 10,
                    SequenceOrder = 1,
                    StageId = "task-review",
                    DisplayName = "ForgeTask Review",
                    Icon = "🔍",
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """
                    {
                        "AgentProfileId": "architect-profile",
                        "RequiredMode": 1,
                        "MaxRetries": 3,
                        "TimeoutMinutes": 25,
                        "StageDirective": "Perform deep codebase inspection."
                    }
                    """
                },
                new()
                {
                    Id = 20,
                    SequenceOrder = 2,
                    StageId = "deterministic-gate",
                    DisplayName = "Compilation Gate",
                    Icon = "⚙️",
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """
                    {
                        "Action": "Build",
                        "FailOnError": true,
                        "TimeoutSeconds": 450,
                        "ExtraParameters": { "Tool": "dotnet" }
                    }
                    """
                }
            }
        };

        await _workflowRepo.CreateWorkflowAsync(wf);

        var cut = Render<WorkflowManagementTab>(p => p.Add(w => w.WorkspaceId, 42));

        // Workflows list
        Assert.Contains("Standard 3-Agent Pipeline", cut.Markup);
        Assert.Contains("standard-3-agent", cut.Markup);
        Assert.Contains("Default", cut.Markup);

        // Stage 1 (Agentic)
        var stage1 = cut.Find("#stage-inspector-1");
        Assert.Contains("ForgeTask Review", stage1.TextContent);
        Assert.Contains("⚡ GPU", stage1.TextContent);
        Assert.Contains("architect-profile", stage1.TextContent);
        Assert.Contains("Perform deep codebase inspection.", stage1.TextContent);

        // Stage 2 (Deterministic)
        var stage2 = cut.Find("#stage-inspector-2");
        Assert.Contains("Compilation Gate", stage2.TextContent);
        Assert.Contains("⚙️ CPU", stage2.TextContent);
        Assert.Contains("Build", stage2.TextContent);
        Assert.Contains("450 s", stage2.TextContent);
        Assert.Contains("Tool: dotnet", stage2.TextContent);
    }

    [Fact]
    public async Task WorkflowManagementTab_CloneWorkflow_CreatesWorkspaceCopy()
    {
        var wf = new WorkflowDefinition
        {
            Id = 1,
            Name = "Template Workflow",
            Code = "template-wf",
            Icon = "🛡️",
            Description = "Base template.",
            IsDefault = true,
            Stages = new List<StageDefinition>
            {
                new()
                {
                    Id = 10,
                    SequenceOrder = 1,
                    StageId = "audit",
                    DisplayName = "Security Audit",
                    Icon = "🛡️",
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic"
                }
            }
        };

        await _workflowRepo.CreateWorkflowAsync(wf);

        var cut = Render<WorkflowManagementTab>(p => p.Add(w => w.WorkspaceId, 99));

        var cloneBtn = cut.Find(".btn-clone-workflow");
        await cut.InvokeAsync(() => cloneBtn.Click());

        var allWorkflows = await _workflowRepo.GetWorkflowsAsync(99);
        Assert.Equal(2, allWorkflows.Count);

        var cloned = allWorkflows.FirstOrDefault(w => w.WorkspaceId == 99);
        Assert.NotNull(cloned);
        Assert.Equal("Copy of Template Workflow", cloned.Name);
        Assert.Single(cloned.Stages);
        Assert.Equal("audit", cloned.Stages.First().StageId);
        Assert.Equal("Security Audit", cloned.Stages.First().DisplayName);
    }
}
