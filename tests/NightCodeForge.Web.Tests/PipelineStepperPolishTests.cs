using Bunit;
using Microsoft.AspNetCore.Components.Web;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class PipelineStepperPolishTests : BunitContext
{
    private static List<StageDefinition> CreateTestStages() => new()
    {
        new() { SequenceOrder = 1, StageId = StandardStageIds.TaskReview, DisplayName = "ForgeTask Review", Kind = WorkflowStageKind.Agentic, Icon = "🔍" },
        new() { SequenceOrder = 2, StageId = StandardStageIds.PreImplementationBaseline, DisplayName = "Pre-Impl Baseline", Kind = WorkflowStageKind.Deterministic, Icon = "⚙️" },
        new() { SequenceOrder = 3, StageId = StandardStageIds.Implementation, DisplayName = "Implementation", Kind = WorkflowStageKind.Agentic, Icon = "💻" }
    };

    [Fact]
    public void Stepper_ExposesAriaLabelAndTitleAndHiddenText_WithoutVisibleTitleAndStatus()
    {
        var cut = Render<PipelineStepper>(p => p.Add(s => s.WorkflowStages, CreateTestStages()));

        var steps = cut.FindAll(".step-item");
        Assert.Equal(4, steps.Count); // 3 configured + 1 merge gate

        // Assert that visible title and status classes are completely absent from DOM
        Assert.Empty(cut.FindAll(".step-title"));
        Assert.Empty(cut.FindAll(".step-status"));

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // Exposes aria-label
            var ariaLabel = step.GetAttribute("aria-label");
            Assert.False(string.IsNullOrWhiteSpace(ariaLabel));

            // Exposes native tooltip title
            var title = step.GetAttribute("title");
            Assert.False(string.IsNullOrWhiteSpace(title));

            // Visually hidden text is present with aria-hidden="true" to prevent double-announcement
            var hidden = step.QuerySelector(".visually-hidden");
            Assert.NotNull(hidden);
            Assert.Equal("true", hidden.GetAttribute("aria-hidden"));
            Assert.False(string.IsNullOrWhiteSpace(hidden.TextContent));
        }
    }

    [Fact]
    public void Stepper_WhenNonClickable_DoesNotHaveButtonRoleOrTabIndex()
    {
        var cut = Render<PipelineStepper>(p => p.Add(s => s.WorkflowStages, CreateTestStages()));

        var steps = cut.FindAll(".step-item");
        foreach (var step in steps)
        {
            Assert.Null(step.GetAttribute("role"));
            Assert.Null(step.GetAttribute("tabindex"));
            Assert.DoesNotContain("step-clickable", step.ClassName);
        }
    }

    [Fact]
    public void Stepper_WhenClickable_HasButtonRoleAndTabIndex_AndActivatesOnEnterAndSpace()
    {
        string? selectedStage = null;
        var cut = Render<PipelineStepper>(p => p
            .Add(s => s.WorkflowStages, CreateTestStages())
            .Add(s => s.OnStageSelected, stageId => selectedStage = stageId));

        var steps = cut.FindAll(".step-item");
        foreach (var step in steps)
        {
            Assert.Equal("button", step.GetAttribute("role"));
            Assert.Equal("0", step.GetAttribute("tabindex"));
            Assert.Contains("step-clickable", step.ClassName);
        }

        // Test Enter activation on stage 1
        cut.Find("#stage-step-1").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal(StandardStageIds.TaskReview, selectedStage);

        // Test Space activation on stage 3
        selectedStage = null;
        cut.Find("#stage-step-3").KeyDown(new KeyboardEventArgs { Key = " " });
        Assert.Equal(StandardStageIds.Implementation, selectedStage);

        // Test click on stage 2
        selectedStage = null;
        cut.Find("#stage-step-2").Click();
        Assert.Equal(StandardStageIds.PreImplementationBaseline, selectedStage);
    }
}
