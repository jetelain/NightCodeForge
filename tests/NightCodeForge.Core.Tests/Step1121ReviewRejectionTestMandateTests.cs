using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class Step1121ReviewRejectionTestMandateTests
{
    [Fact]
    public void StandardWorkflowDefinitions_ReviewStageDirective_ContainsMandatoryBugRegressionPolicy()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var reviewStage = Assert.Single(workflow.Stages, s => s.StageId == StandardStageIds.CodeReviewAndFix);

        var config = JsonSerializer.Deserialize<AgenticStageConfig>(reviewStage.ConfigurationJson);
        Assert.NotNull(config);
        Assert.NotNull(config.StageDirective);

        Assert.Contains("MANDATORY BUG REGRESSION POLICY", config.StageDirective);
        Assert.Contains("dedicated automated test", config.StageDirective);
        Assert.Contains("reproducing each bug found before declaring the fix complete", config.StageDirective);
    }

    [Fact]
    public void StandardWorkflowDefinitions_ImplementationStagePrompt_ContainsTestMandateWhenReviewerFeedbackPresent()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var implStageDef = Assert.Single(workflow.Stages, s => s.StageId == StandardStageIds.Implementation);
        var implStage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement Feature X",
            Description = "Feature requirements...",
            BaseBranch = "main",
            FeatureBranch = "feature/task-42"
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ReviewerFeedback, "Found null reference in ProcessMessage when token is expired.");

        var context = new PipelineContext(
            task,
            new Workspace { Id = 1, Name = "TestWks" },
            bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(implStage, task, context);

        Assert.Contains("[Reviewer Requested Changes / Feedback]:", prompt);
        Assert.Contains("Found null reference in ProcessMessage", prompt);
        Assert.Contains("Notice: You are required to implement a dedicated automated test for every bug or defect identified in the reviewer's feedback before completing your implementation.", prompt);
    }

    [Fact]
    public void StandardWorkflowDefinitions_ImplementationStagePrompt_OmitsTestMandateWhenNoReviewerFeedback()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var implStageDef = Assert.Single(workflow.Stages, s => s.StageId == StandardStageIds.Implementation);
        var implStage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement Feature X",
            Description = "Feature requirements...",
            BaseBranch = "main",
            FeatureBranch = "feature/task-42"
        };

        var bag = new PipelineDataBag();

        var context = new PipelineContext(
            task,
            new Workspace { Id = 1, Name = "TestWks" },
            bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(implStage, task, context);

        Assert.DoesNotContain("[Reviewer Requested Changes / Feedback]:", prompt);
        Assert.DoesNotContain("Notice: You are required to implement a dedicated automated test", prompt);
    }
}
