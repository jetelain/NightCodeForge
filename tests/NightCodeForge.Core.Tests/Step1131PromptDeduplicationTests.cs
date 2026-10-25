namespace NightCodeForge.Core.Tests;

using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

public class Step1131PromptDeduplicationTests
{
    [Fact]
    public void DefaultBootstrapPromptBuilder_WithStandardStages_OmitsTaskDescription()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var taskReviewStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.TaskReview);
        var implStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.Implementation);
        var reviewStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.CodeReviewAndFix);

        var taskReviewStage = new GenericAgenticStage(
            taskReviewStageDef.StageId,
            taskReviewStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(taskReviewStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var implStage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var reviewStage = new GenericAgenticStage(
            reviewStageDef.StageId,
            reviewStageDef.DisplayName,
            JsonSerializer.Deserialize<AgenticStageConfig>(reviewStageDef.ConfigurationJson)!,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var largeDescription = "CRITICAL_REQUIREMENT: " + new string('X', 5000) + " END_CRITICAL_REQUIREMENT";
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement Feature X",
            Description = largeDescription,
            FeatureBranch = "feature/task-42"
        };

        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "TestWorkspace" }, new PipelineDataBag());

        var reviewPrompt = DefaultBootstrapPromptBuilder.Build(taskReviewStage, task, context);
        var implPrompt = DefaultBootstrapPromptBuilder.Build(implStage, task, context);
        var codeReviewPrompt = DefaultBootstrapPromptBuilder.Build(reviewStage, task, context);

        // 1. All standard stage prompts MUST NOT contain the raw task description body
        Assert.DoesNotContain("CRITICAL_REQUIREMENT:", reviewPrompt);
        Assert.DoesNotContain(new string('X', 100), reviewPrompt);

        Assert.DoesNotContain("CRITICAL_REQUIREMENT:", implPrompt);
        Assert.DoesNotContain(new string('X', 100), implPrompt);

        Assert.DoesNotContain("CRITICAL_REQUIREMENT:", codeReviewPrompt);
        Assert.DoesNotContain(new string('X', 100), codeReviewPrompt);

        // 2. All prompts direct the agent to check the system prompt / task document
        Assert.Contains("docs/tasks/", reviewPrompt);
        Assert.Contains("docs/tasks/", implPrompt);
        Assert.Contains("docs/tasks/", codeReviewPrompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WithCustomTemplateReferencingDescription_PreservesAccess()
    {
        var customStage = new GenericAgenticStage(
            "custom-stage",
            "Custom Stage",
            new AgenticStageConfig
            {
                PromptTemplate = "Custom stage header.\nTask Description:\n{{ task.description }}\nCustom stage footer."
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var task = new ForgeTask
        {
            Id = 99,
            Title = "Custom Task",
            Description = "Dedicated custom task specifications here."
        };

        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "TestWorkspace" }, new PipelineDataBag());

        var prompt = DefaultBootstrapPromptBuilder.Build(customStage, task, context);

        Assert.Contains("Dedicated custom task specifications here.", prompt);
        Assert.Contains("Custom stage header.", prompt);
        Assert.Contains("Custom stage footer.", prompt);
    }

    [Fact]
    public void StandardWorkflowDefinitions_TaskReviewStageDirective_ProhibitsConversationalMonologue()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var taskReviewStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.TaskReview);
        var config = JsonSerializer.Deserialize<AgenticStageConfig>(taskReviewStageDef.ConfigurationJson);

        Assert.NotNull(config);
        Assert.NotNull(config.StageDirective);
        Assert.Contains("conversational", config.StageDirective, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("docs/tasks/", config.StageDirective);
    }

    [Fact]
    public void DeduplicatedPrompt_ReducesInitialUserMessageSize_ByAtLeast75Percent()
    {
        var workflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var implStageDef = workflow.Stages.First(s => s.StageId == StandardStageIds.Implementation);
        var implConfig = JsonSerializer.Deserialize<AgenticStageConfig>(implStageDef.ConfigurationJson)!;

        var implStage = new GenericAgenticStage(
            implStageDef.StageId,
            implStageDef.DisplayName,
            implConfig,
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        // Realistic large task description of 15,000 characters
        var description = new string('A', 15000);
        var task = new ForgeTask
        {
            Id = 55,
            Title = "Large Refactoring",
            Description = description,
            FeatureBranch = "feature/task-55"
        };

        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "TestWorkspace" }, new PipelineDataBag());

        // New deduplicated prompt
        var deduplicatedPrompt = DefaultBootstrapPromptBuilder.Build(implStage, task, context);

        // Old legacy prompt (which embedded {{ task.description }})
        var legacyStage = new GenericAgenticStage(
            "legacy-impl",
            "Legacy Implementation",
            new AgenticStageConfig
            {
                PromptTemplate = implStage.PromptTemplate + "\n## Task Specification\n{{ task.description }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);
        var legacyPrompt = DefaultBootstrapPromptBuilder.Build(legacyStage, task, context);

        // Deduplicated prompt size should be less than 25% of legacy prompt size (>= 75% reduction)
        double reduction = 1.0 - ((double)deduplicatedPrompt.Length / legacyPrompt.Length);
        Assert.True(reduction >= 0.75, $"Expected at least 75% reduction, but achieved {reduction:P1}");
    }
}
