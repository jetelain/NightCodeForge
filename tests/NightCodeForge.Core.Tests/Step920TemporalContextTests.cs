using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class Step920TemporalContextTests
{
    private class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }

    private static (ForgeTask Task, PipelineContext Context) CreateTestTaskAndContext()
    {
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement Temporal Prompt Feature",
            Description = "Inject calendar dates without KV cache invalidation."
        };
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace" };
        var context = new PipelineContext(task, workspace);
        return (task, context);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WithCustomTimeProvider_RendersStandardContextBlock()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 14, 30, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Reviewing task #{{ task.id }}: {{ task.title }}"
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        Assert.StartsWith("Reviewing task #42: Implement Temporal Prompt Feature", prompt);
        Assert.Contains("[Context]:", prompt);
        Assert.Contains("- Current Date: 2026-10-18", prompt);
        // Verify strict yyyy-MM-dd format with no time component (no 14:30:00)
        Assert.DoesNotContain("14:30", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WithDateOverride_RendersOverriddenDate()
    {
        var (task, context) = CreateTestTaskAndContext();

        var stage = new GenericAgenticStage(
            StandardStageIds.Implementation,
            "Implementation",
            new AgenticStageConfig
            {
                PromptTemplate = "Implement task #{{ task.id }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            currentDate: "2027-01-01");

        Assert.Contains("[Context]:", prompt);
        Assert.Contains("- Current Date: 2027-01-01", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WhenTemplateExplicitlyReferencesCurrentDate_DoesNotAppendDuplicateBlock()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Task #{{ task.id }} authored on {{ context.current_date }}"
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        Assert.Equal("Task #42 authored on 2026-10-18", prompt);
        Assert.DoesNotContain("[Context]:", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WhenTopLevelCurrentDateReferenced_ResolvesCorrectly()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Task #{{ task.id }} on {{ current_date }}"
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        Assert.Equal("Task #42 on 2026-10-18", prompt);
        Assert.DoesNotContain("[Context]:", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WhenTaskDescriptionContainsCurrentDate_StillInjectsContextBlock()
    {
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Task with Date Mention",
            Description = "Refactor logic. Current Date: must not prevent temporal injection."
        };
        var context = new PipelineContext(task, new Workspace { Id = 1 });
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Review task #{{ task.id }}: {{ task.description }}"
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        Assert.Contains("[Context]:", prompt);
        Assert.Contains("- Current Date: 2026-10-18", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WhenTemplateContainsExistingContextBlock_InsertsUnderContextSection()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Task #{{ task.id }}\n\n[Context]:\n- Base Branch: {{ task.base_branch }}\n\n[Instructions]:\nDo not break anything."
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        // Date should be inserted inside [Context]: section before [Instructions]:
        var contextIdx = prompt.IndexOf("[Context]:", StringComparison.Ordinal);
        var dateIdx = prompt.IndexOf("- Current Date: 2026-10-18", StringComparison.Ordinal);
        var instructionsIdx = prompt.IndexOf("[Instructions]:", StringComparison.Ordinal);

        Assert.True(contextIdx >= 0);
        Assert.True(dateIdx > contextIdx);
        Assert.True(instructionsIdx > dateIdx);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_WhenTemplateReferencesCurrentDateCaseInsensitively_DoesNotAppendDuplicateBlock()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Task #{{ task.id }} on {{ context.current_date }}. Note: CURRENT_DATE must be respected."
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = DefaultBootstrapPromptBuilder.Build(
            stage,
            task,
            context,
            timeProvider: fakeTime);

        Assert.Equal("Task #42 on 2026-10-18. Note: CURRENT_DATE must be respected.", prompt);
        Assert.DoesNotContain("[Context]:", prompt);
    }

    [Fact]
    public void GenericAgenticStage_WithCustomTimeProvider_PassesTemporalContext()
    {
        var (task, context) = CreateTestTaskAndContext();
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

        var stage = new GenericAgenticStage(
            StandardStageIds.Implementation,
            "Implementation",
            new AgenticStageConfig
            {
                PromptTemplate = "Do implementation for #{{ task.id }}"
            },
            new ScribanPromptTemplateRenderer(),
            fakeTime);

        var prompt = stage.BuildBootstrapPrompt(task, context);

        Assert.Contains("[Context]:", prompt);
        Assert.Contains("- Current Date: 2026-10-20", prompt);
    }
}
