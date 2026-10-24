using System.Text.Json;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class WorkflowDefinitionTests
{
    [Fact]
    public void WorkflowDefinition_PropertyAssignments_AndDefaultValues()
    {
        var now = DateTime.UtcNow;
        var workflow = new WorkflowDefinition
        {
            Id = 42,
            Name = "Autonomous Security Hardened Pipeline",
            Code = "security-hardened-pipeline",
            Icon = "🛡️",
            Description = "A pipeline that enforces AST security review.",
            IsDefault = true,
            WorkspaceId = 5,
            CreatedAt = now,
            UpdatedAt = now.AddMinutes(10)
        };

        Assert.Equal(42, workflow.Id);
        Assert.Equal("Autonomous Security Hardened Pipeline", workflow.Name);
        Assert.Equal("security-hardened-pipeline", workflow.Code);
        Assert.Equal("🛡️", workflow.Icon);
        Assert.Equal("A pipeline that enforces AST security review.", workflow.Description);
        Assert.True(workflow.IsDefault);
        Assert.Equal(5, workflow.WorkspaceId);
        Assert.Equal(now, workflow.CreatedAt);
        Assert.Equal(now.AddMinutes(10), workflow.UpdatedAt);
        Assert.NotNull(workflow.Stages);
        Assert.Empty(workflow.Stages);
        Assert.NotNull(workflow.Tasks);
        Assert.Empty(workflow.Tasks);
    }

    [Fact]
    public void StageDefinition_PropertyAssignments_AndParentWorkflowRelationship()
    {
        var workflow = new WorkflowDefinition
        {
            Id = 1,
            Name = "Parent Workflow",
            Code = "parent-wf"
        };

        var stage = new StageDefinition
        {
            Id = 101,
            WorkflowId = 1,
            StageId = "security-scan",
            DisplayName = "Security Scan",
            Icon = "🔒",
            SequenceOrder = 3,
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = "deterministic-sast",
            ConfigurationJson = """{"Action":"Sast","FailOnError":true}""",
            Workflow = workflow
        };

        workflow.Stages.Add(stage);

        Assert.Equal(101, stage.Id);
        Assert.Equal(1, stage.WorkflowId);
        Assert.Equal("security-scan", stage.StageId);
        Assert.Equal("Security Scan", stage.DisplayName);
        Assert.Equal("🔒", stage.Icon);
        Assert.Equal(3, stage.SequenceOrder);
        Assert.Equal(WorkflowStageKind.Deterministic, stage.Kind);
        Assert.Equal("deterministic-sast", stage.CsharpImplementationType);
        Assert.Equal("""{"Action":"Sast","FailOnError":true}""", stage.ConfigurationJson);
        Assert.Same(workflow, stage.Workflow);
        Assert.Single(workflow.Stages);
        Assert.Same(stage, workflow.Stages.First());
    }

    [Fact]
    public void StageDefinition_SequenceOrdering_SortsStagesCorrectly()
    {
        var stages = new List<StageDefinition>
        {
            new() { Id = 3, StageId = "stage-3", DisplayName = "Stage 3", SequenceOrder = 3 },
            new() { Id = 1, StageId = "stage-1", DisplayName = "Stage 1", SequenceOrder = 1 },
            new() { Id = 4, StageId = "stage-4", DisplayName = "Stage 4", SequenceOrder = 4 },
            new() { Id = 2, StageId = "stage-2", DisplayName = "Stage 2", SequenceOrder = 2 }
        };

        var ordered = stages.OrderBy(s => s.SequenceOrder).ToList();

        Assert.Equal("stage-1", ordered[0].StageId);
        Assert.Equal("stage-2", ordered[1].StageId);
        Assert.Equal("stage-3", ordered[2].StageId);
        Assert.Equal("stage-4", ordered[3].StageId);
        for (var i = 0; i < ordered.Count; i++)
        {
            Assert.Equal(i + 1, ordered[i].SequenceOrder);
        }
    }

    [Fact]
    public void StageDefinition_DuplicateSequenceOrder_IsDetectable()
    {
        var stages = new List<StageDefinition>
        {
            new() { StageId = "s1", SequenceOrder = 1 },
            new() { StageId = "s2", SequenceOrder = 2 },
            new() { StageId = "s3", SequenceOrder = 2 } // Duplicate sequence order
        };

        var duplicates = stages
            .GroupBy(s => s.SequenceOrder)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Single(duplicates);
        Assert.Equal(2, duplicates[0]);
    }

    [Fact]
    public void AgenticStageConfig_DefaultValues_AreCorrect()
    {
        var config = new AgenticStageConfig();

        Assert.Null(config.AgentProfileId);
        Assert.Null(config.StageDirective);
        Assert.Null(config.PromptTemplate);
        Assert.Equal(AgentMode.Full, config.RequiredMode);
        Assert.Null(config.LoopBackStageId);
        Assert.Equal(3, config.MaxRetries);
        Assert.Null(config.TimeoutMinutes);
    }

    [Fact]
    public void AgenticStageConfig_JsonSerializationRoundTrip_PreservesAllFields()
    {
        var config = new AgenticStageConfig
        {
            AgentProfileId = "architect-pro",
            StageDirective = "Review the specification for completeness and consistency.",
            PromptTemplate = "Task #{{ task.id }}: {{ task.title }}\n{{ task.description }}",
            RequiredMode = AgentMode.Review,
            LoopBackStageId = "task-review",
            MaxRetries = 5,
            TimeoutMinutes = 45
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<AgenticStageConfig>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("architect-pro", deserialized.AgentProfileId);
        Assert.Equal("Review the specification for completeness and consistency.", deserialized.StageDirective);
        Assert.Equal("Task #{{ task.id }}: {{ task.title }}\n{{ task.description }}", deserialized.PromptTemplate);
        Assert.Equal(AgentMode.Review, deserialized.RequiredMode);
        Assert.Equal("task-review", deserialized.LoopBackStageId);
        Assert.Equal(5, deserialized.MaxRetries);
        Assert.Equal(45, deserialized.TimeoutMinutes);
    }

    [Fact]
    public void AgenticStageConfig_Deserialization_CaseInsensitiveAndHandlesEmptyJson()
    {
        var emptyJson = "{}";
        var deserializedEmpty = JsonSerializer.Deserialize<AgenticStageConfig>(emptyJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(deserializedEmpty);
        Assert.Equal(AgentMode.Full, deserializedEmpty.RequiredMode);
        Assert.Equal(3, deserializedEmpty.MaxRetries);

        var camelCaseJson = """
        {
            "agentProfileId": "custom-agent",
            "requiredMode": 0,
            "maxRetries": 1,
            "timeoutMinutes": 15
        }
        """;
        var deserializedCamel = JsonSerializer.Deserialize<AgenticStageConfig>(camelCaseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(deserializedCamel);
        Assert.Equal("custom-agent", deserializedCamel.AgentProfileId);
        Assert.Equal(AgentMode.ReadOnly, deserializedCamel.RequiredMode);
        Assert.Equal(1, deserializedCamel.MaxRetries);
        Assert.Equal(15, deserializedCamel.TimeoutMinutes);
    }

    [Fact]
    public void DeterministicStageConfig_DefaultValues_AreCorrect()
    {
        var config = new DeterministicStageConfig();

        Assert.Equal("Build", config.Action);
        Assert.Null(config.Command);
        Assert.Equal("implementation", config.LoopBackStageId);
        Assert.True(config.FailOnError);
        Assert.Equal(300, config.TimeoutSeconds);
        Assert.NotNull(config.ExtraParameters);
        Assert.Empty(config.ExtraParameters);
    }

    [Fact]
    public void DeterministicStageConfig_JsonSerializationRoundTrip_PreservesAllFields()
    {
        var config = new DeterministicStageConfig
        {
            Action = "CompositeVerification",
            Command = "dotnet test --no-build",
            LoopBackStageId = "code-review-fix",
            FailOnError = false,
            TimeoutSeconds = 900,
            ExtraParameters = new Dictionary<string, string>
            {
                { "Stages", "format,build,test-with-coverage" },
                { "MinimumLineCoveragePercent", "85.5" }
            }
        };

        var json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<DeterministicStageConfig>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("CompositeVerification", deserialized.Action);
        Assert.Equal("dotnet test --no-build", deserialized.Command);
        Assert.Equal("code-review-fix", deserialized.LoopBackStageId);
        Assert.False(deserialized.FailOnError);
        Assert.Equal(900, deserialized.TimeoutSeconds);
        Assert.Equal(2, deserialized.ExtraParameters.Count);
        Assert.Equal("format,build,test-with-coverage", deserialized.ExtraParameters["Stages"]);
        Assert.Equal("85.5", deserialized.ExtraParameters["MinimumLineCoveragePercent"]);
    }

    [Fact]
    public void DeterministicStageConfig_Deserialization_CaseInsensitiveAndHandlesEmptyJson()
    {
        var emptyJson = "{}";
        var deserializedEmpty = JsonSerializer.Deserialize<DeterministicStageConfig>(emptyJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(deserializedEmpty);
        Assert.Equal("Build", deserializedEmpty.Action);
        Assert.Equal("implementation", deserializedEmpty.LoopBackStageId);
        Assert.True(deserializedEmpty.FailOnError);
        Assert.Equal(300, deserializedEmpty.TimeoutSeconds);

        var camelCaseJson = """
        {
            "action": "Script",
            "command": "python test.py",
            "loopBackStageId": "dev",
            "failOnError": true,
            "timeoutSeconds": 120,
            "extraParameters": {
                "Key1": "Val1"
            }
        }
        """;
        var deserializedCamel = JsonSerializer.Deserialize<DeterministicStageConfig>(camelCaseJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(deserializedCamel);
        Assert.Equal("Script", deserializedCamel.Action);
        Assert.Equal("python test.py", deserializedCamel.Command);
        Assert.Equal("dev", deserializedCamel.LoopBackStageId);
        Assert.True(deserializedCamel.FailOnError);
        Assert.Equal(120, deserializedCamel.TimeoutSeconds);
        Assert.Equal("Val1", deserializedCamel.ExtraParameters["Key1"]);
    }

    [Fact]
    public void WorkflowStageFactory_StageDefinitionWithoutQueueTier_ThrowsWorkflowValidationException()
    {
        var registry = new WorkflowStageRegistry();
        var spMock = new Mock<IServiceProvider>();
        var factory = new WorkflowStageFactory(registry, spMock.Object);

        var def = new StageDefinition
        {
            StageId = "untyped-stage",
            DisplayName = "Untyped Stage",
            CsharpImplementationType = "deterministic",
            QueueTier = 0 // Invalid / unassigned
        };

        var ex = Assert.Throws<WorkflowValidationException>(() => factory.CreateStage(def));
        Assert.Contains("does not have a designated QueueTier", ex.Message);
    }

    [Fact]
    public void StandardWorkflowDefinitions_AllStagesHaveAssignedQueueTiers()
    {
        var stages = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow().Stages;
        Assert.NotEmpty(stages);

        foreach (var stage in stages)
        {
            Assert.True(Enum.IsDefined(typeof(StageQueueTier), stage.QueueTier), $"Stage {stage.StageId} has undefined QueueTier: {stage.QueueTier}");
            Assert.NotEqual((StageQueueTier)0, stage.QueueTier);
        }

        // Verify specific tiers for standard stages
        var taskReview = stages.First(s => s.StageId == StandardStageIds.TaskReview);
        Assert.Equal(StageQueueTier.TaskReview, taskReview.QueueTier);

        var impl = stages.First(s => s.StageId == StandardStageIds.Implementation);
        Assert.Equal(StageQueueTier.Implementation, impl.QueueTier);

        var preImpl = stages.First(s => s.StageId == StandardStageIds.PreImplementationBaseline);
        Assert.Equal(StageQueueTier.Implementation, preImpl.QueueTier);

        var postImpl = stages.First(s => s.StageId == StandardStageIds.PostImplementationGates);
        Assert.Equal(StageQueueTier.Implementation, postImpl.QueueTier);

        var codeReview = stages.First(s => s.StageId == StandardStageIds.CodeReviewAndFix);
        Assert.Equal(StageQueueTier.CodeReview, codeReview.QueueTier);

        var postReview = stages.First(s => s.StageId == StandardStageIds.PostReviewVerification);
        Assert.Equal(StageQueueTier.CodeReview, postReview.QueueTier);
    }
}
