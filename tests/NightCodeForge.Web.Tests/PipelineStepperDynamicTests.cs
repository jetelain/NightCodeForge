using Bunit;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class PipelineStepperDynamicTests : BunitContext
{
    [Fact]
    public void PipelineStepper_With3StageWorkflow_Renders3StagesAnd2Connectors()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 1, SequenceOrder = 1, StageId = "investigation", DisplayName = "Investigation", Icon = "🔍", Kind = WorkflowStageKind.Agentic },
            new() { Id = 2, SequenceOrder = 2, StageId = "dev-gate", DisplayName = "Build Gate", Icon = "⚙️", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 3, SequenceOrder = 3, StageId = "delivery", DisplayName = "Delivery", Icon = "🚀", Kind = WorkflowStageKind.Agentic }
        };

        var cut = Render<PipelineStepper>(p => p.Add(s => s.WorkflowStages, stages));

        var steps = cut.FindAll(".step-item");
        Assert.Equal(4, steps.Count);

        var connectors = cut.FindAll(".step-connector");
        Assert.Equal(3, connectors.Count);

        // Stage 1
        Assert.Contains("Investigation", steps[0].TextContent);
        Assert.Contains("⚡ GPU", steps[0].TextContent);
        Assert.Contains("🔍", steps[0].TextContent);

        // Stage 2
        Assert.Contains("Build Gate", steps[1].TextContent);
        Assert.Contains("⚙️ CPU", steps[1].TextContent);
        Assert.Contains("⚙️", steps[1].TextContent);

        // Stage 3
        Assert.Contains("Delivery", steps[2].TextContent);
        Assert.Contains("⚡ GPU", steps[2].TextContent);
        Assert.Contains("🚀", steps[2].TextContent);

        // Stage 4: Implicit Merge Stage
        Assert.Contains("Merge Resolution", steps[3].TextContent);
        Assert.Contains("⚙️ CPU", steps[3].TextContent);
        Assert.Contains("🔀", steps[3].TextContent);
    }

    [Fact]
    public void PipelineStepper_With5StageWorkflow_Renders5StagesAnd4Connectors()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 1, SequenceOrder = 1, StageId = "stage-1", DisplayName = "Stage One", Icon = "1️⃣", Kind = WorkflowStageKind.Agentic },
            new() { Id = 2, SequenceOrder = 2, StageId = "stage-2", DisplayName = "Stage Two", Icon = "2️⃣", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 3, SequenceOrder = 3, StageId = "stage-3", DisplayName = "Stage Three", Icon = "3️⃣", Kind = WorkflowStageKind.Agentic },
            new() { Id = 4, SequenceOrder = 4, StageId = "stage-4", DisplayName = "Stage Four", Icon = "4️⃣", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 5, SequenceOrder = 5, StageId = "stage-5", DisplayName = "Stage Five", Icon = "5️⃣", Kind = WorkflowStageKind.Agentic }
        };

        var cut = Render<PipelineStepper>(p => p.Add(s => s.WorkflowStages, stages));

        var steps = cut.FindAll(".step-item");
        Assert.Equal(6, steps.Count);

        var connectors = cut.FindAll(".step-connector");
        Assert.Equal(5, connectors.Count);
    }

    [Fact]
    public void PipelineStepper_With6StageWorkflow_AppliesTransitionsCorrectly()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 1, SequenceOrder = 1, StageId = "task-review", DisplayName = "ForgeTask Review", Icon = "🔍", Kind = WorkflowStageKind.Agentic },
            new() { Id = 2, SequenceOrder = 2, StageId = "deterministic-pre-impl", DisplayName = "Pre-Impl Baseline", Icon = "⚙️", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 3, SequenceOrder = 3, StageId = "implementation", DisplayName = "Implementation", Icon = "💻", Kind = WorkflowStageKind.Agentic },
            new() { Id = 4, SequenceOrder = 4, StageId = "deterministic-post-impl", DisplayName = "Post-Impl Gates", Icon = "🧪", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 5, SequenceOrder = 5, StageId = "code-review-fix", DisplayName = "Code Review", Icon = "🧐", Kind = WorkflowStageKind.Agentic },
            new() { Id = 6, SequenceOrder = 6, StageId = "deterministic-post-review", DisplayName = "Post-Review Verification", Icon = "🛡️", Kind = WorkflowStageKind.Deterministic }
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(s => s.WorkflowStages, stages)
            .Add(s => s.CurrentStageId, "implementation")
            .Add(s => s.CurrentStageStatus, StageExecutionStatus.Running));

        var steps = cut.FindAll(".step-item");
        Assert.Equal(7, steps.Count);

        // Stages 1 and 2 should be completed
        Assert.Contains("step-completed", steps[0].ClassName);
        Assert.Contains("step-completed", steps[1].ClassName);

        // Stage 3 should be active
        Assert.Contains("step-active", steps[2].ClassName);
        Assert.NotEmpty(steps[2].QuerySelectorAll(".spinner-pulse"));

        // Stages 4, 5, 6, 7 should be pending
        Assert.Contains("step-pending", steps[3].ClassName);
        Assert.Contains("step-pending", steps[4].ClassName);
        Assert.Contains("step-pending", steps[5].ClassName);
        Assert.Contains("step-pending", steps[6].ClassName);

        // Connectors: 6 connectors total, first 2 completed
        var connectors = cut.FindAll(".step-connector");
        Assert.Equal(6, connectors.Count);
        Assert.Contains("connector-completed", connectors[0].ClassName);
        Assert.Contains("connector-completed", connectors[1].ClassName);
        Assert.DoesNotContain("connector-completed", connectors[2].ClassName);
    }

    [Fact]
    public void PipelineStepper_WhenCompleted_RendersIconWithStatusOverlay()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 1, SequenceOrder = 1, StageId = "audit", DisplayName = "Security Audit", Icon = "🛡️", Kind = WorkflowStageKind.Agentic }
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(s => s.WorkflowStages, stages)
            .Add(s => s.OverallStatus, TaskOverallStatus.Closed));

        var step = cut.Find(".step-item");
        Assert.Contains("step-completed", step.ClassName);
        Assert.Contains("🛡️", step.TextContent);
        Assert.Contains("✓", step.TextContent);
        Assert.NotNull(step.QuerySelector(".status-overlay-success"));
    }

    [Fact]
    public void PipelineStepper_With7StageWorkflow_Renders8Steps7ConnectorsAndCorrectBadges()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 1, SequenceOrder = 1, StageId = "triage", DisplayName = "Triage", Icon = "📋", Kind = WorkflowStageKind.Agentic },
            new() { Id = 2, SequenceOrder = 2, StageId = "plan", DisplayName = "Plan", Icon = "📐", Kind = WorkflowStageKind.Agentic },
            new() { Id = 3, SequenceOrder = 3, StageId = "build", DisplayName = "Build Baseline", Icon = "⚙️", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 4, SequenceOrder = 4, StageId = "code", DisplayName = "Implementation", Icon = "💻", Kind = WorkflowStageKind.Agentic },
            new() { Id = 5, SequenceOrder = 5, StageId = "test", DisplayName = "Unit Tests", Icon = "🧪", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 6, SequenceOrder = 6, StageId = "audit", DisplayName = "Security Audit", Icon = "🛡️", Kind = WorkflowStageKind.Deterministic },
            new() { Id = 7, SequenceOrder = 7, StageId = "review", DisplayName = "Maintainer Review", Icon = "🧐", Kind = WorkflowStageKind.Agentic }
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(s => s.WorkflowStages, stages)
            .Add(s => s.CurrentStageId, "code")
            .Add(s => s.CurrentStageStatus, StageExecutionStatus.Running));

        // 7 configured stages + 1 implicit Merge stage = 8 step items
        var steps = cut.FindAll(".step-item");
        Assert.Equal(8, steps.Count);

        // 7 connectors connecting the 8 steps
        var connectors = cut.FindAll(".step-connector");
        Assert.Equal(7, connectors.Count);

        // Assert step circles are present on each step
        var circles = cut.FindAll(".step-icon-circle");
        Assert.Equal(8, circles.Count);

        // GPU vs CPU badges
        Assert.Contains("⚡ GPU", steps[0].TextContent); // triage (Agentic)
        Assert.Contains("⚡ GPU", steps[1].TextContent); // plan (Agentic)
        Assert.Contains("⚙️ CPU", steps[2].TextContent); // build (Deterministic)
        Assert.Contains("⚡ GPU", steps[3].TextContent); // code (Agentic)
        Assert.Contains("⚙️ CPU", steps[4].TextContent); // test (Deterministic)
        Assert.Contains("⚙️ CPU", steps[5].TextContent); // audit (Deterministic)
        Assert.Contains("⚡ GPU", steps[6].TextContent); // review (Agentic)
        Assert.Contains("⚙️ CPU", steps[7].TextContent); // merge (Deterministic)

        // Status circle states
        Assert.Contains("step-completed", steps[0].ClassName);
        Assert.Contains("step-completed", steps[1].ClassName);
        Assert.Contains("step-completed", steps[2].ClassName);
        Assert.Contains("step-active", steps[3].ClassName);
        Assert.Contains("step-pending", steps[4].ClassName);
        Assert.Contains("step-pending", steps[5].ClassName);
        Assert.Contains("step-pending", steps[6].ClassName);
        Assert.Contains("step-pending", steps[7].ClassName);
    }
}
