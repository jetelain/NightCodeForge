using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class WorkflowDefinitionReconciliationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public WorkflowDefinitionReconciliationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task EnsureDefaultWorkflowsAsync_WhenOutdatedTaskReviewStageExists_ReconcilesToCanonicalConfiguration()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var workflow = await context.EnsureDefaultWorkflowsAsync();

        // 1. Artificially simulate pre-Step-11.41 outdated TaskReview configuration
        var taskReviewStage = workflow.Stages.First(s => s.StageId == StandardStageIds.TaskReview);
        taskReviewStage.ConfigurationJson = """
        {
          "AgentProfileId": null,
          "RequiredMode": 1,
          "MaxRetries": 3,
          "TimeoutMinutes": 30,
          "StageDirective": "Legacy task review directive without plain-text question warning.",
          "PromptTemplate": "Legacy prompt template without strict gating notice."
        }
        """;
        taskReviewStage.DisplayName = "Old Review Name";
        taskReviewStage.Icon = "❓";
        await context.SaveChangesAsync();

        // 2. Run EnsureDefaultWorkflowsAsync to trigger reconciliation
        var reconciled = await context.EnsureDefaultWorkflowsAsync();

        var canonical = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var canonicalReview = canonical.Stages.First(s => s.StageId == StandardStageIds.TaskReview);
        var reconciledReview = reconciled.Stages.First(s => s.StageId == StandardStageIds.TaskReview);

        // 3. Verify that TaskReview was updated to the latest canonical definition
        Assert.Equal(canonicalReview.DisplayName, reconciledReview.DisplayName);
        Assert.Equal(canonicalReview.Icon, reconciledReview.Icon);
        Assert.Equal(canonicalReview.ConfigurationJson, reconciledReview.ConfigurationJson);
        Assert.Contains("Never output questions as conversational plain text", reconciledReview.ConfigurationJson);
        Assert.Contains("Stage completion is strictly gated on writing or editing this markdown document", reconciledReview.ConfigurationJson);
    }

    [Fact]
    public async Task EnsureDefaultWorkflowsAsync_WhenMissingOrObsoleteStagesExist_ReconcilesStagesAccurately()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var workflow = await context.EnsureDefaultWorkflowsAsync();

        // 1. Remove PostReviewVerification (simulate missing canonical stage)
        var postReviewStage = workflow.Stages.First(s => s.StageId == StandardStageIds.PostReviewVerification);
        context.StageDefinitions.Remove(postReviewStage);
        workflow.Stages.Remove(postReviewStage);

        // 2. Add an obsolete stage not present in canonical
        var obsoleteStage = new StageDefinition
        {
            WorkflowId = workflow.Id,
            Workflow = workflow,
            StageId = "obsolete-legacy-stage",
            DisplayName = "Obsolete Stage",
            Icon = "🗑️",
            SequenceOrder = 99,
            QueueTier = StageQueueTier.Implementation,
            Kind = WorkflowStageKind.Deterministic,
            CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
            ConfigurationJson = "{}"
        };
        context.StageDefinitions.Add(obsoleteStage);
        workflow.Stages.Add(obsoleteStage);

        await context.SaveChangesAsync();

        // 3. Reconcile
        var reconciled = await context.EnsureDefaultWorkflowsAsync();
        var canonical = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();

        // 4. Verify missing stage was re-created
        Assert.Contains(reconciled.Stages, s => s.StageId == StandardStageIds.PostReviewVerification);

        // 5. Verify obsolete stage was pruned
        Assert.DoesNotContain(reconciled.Stages, s => s.StageId == "obsolete-legacy-stage");

        // 6. Total stage count matches canonical
        Assert.Equal(canonical.Stages.Count, reconciled.Stages.Count);
    }
}
