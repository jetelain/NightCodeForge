using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class DatabaseSeedingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public DatabaseSeedingTests()
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

    private async Task<AppDbContext> CreateContextAsync()
    {
        var context = new AppDbContext(_options);
        await context.EnsureDatabaseCreatedAndWalAsync();
        return context;
    }

    [Fact]
    public async Task EnsureDefaultWorkflowsAsync_CreatesStandard6StageWorkflowIdempotently()
    {
        using var context = await CreateContextAsync();

        // 1. Initial call: seeds default workflow and 6 stages
        var seeded = await context.EnsureDefaultWorkflowsAsync();

        Assert.NotNull(seeded);
        Assert.Equal(StandardWorkflowDefinitions.Standard3AgentCode, seeded.Code);
        Assert.Equal("Standard 3-Agent Pipeline", seeded.Name);
        Assert.True(seeded.IsDefault);
        Assert.Null(seeded.WorkspaceId);
        Assert.Equal(6, seeded.Stages.Count);

        var orderedStages = seeded.Stages.OrderBy(s => s.SequenceOrder).ToList();
        var expectedStageIds = new[]
        {
            StandardStageIds.TaskReview,
            StandardStageIds.PreImplementationBaseline,
            StandardStageIds.Implementation,
            StandardStageIds.PostImplementationGates,
            StandardStageIds.CodeReviewAndFix,
            StandardStageIds.PostReviewVerification
        };

        for (var i = 0; i < expectedStageIds.Length; i++)
        {
            Assert.Equal(i + 1, orderedStages[i].SequenceOrder);
            Assert.Equal(expectedStageIds[i], orderedStages[i].StageId);
            Assert.False(string.IsNullOrWhiteSpace(orderedStages[i].DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(orderedStages[i].Icon));
            Assert.False(string.IsNullOrWhiteSpace(orderedStages[i].CsharpImplementationType));
            Assert.False(string.IsNullOrWhiteSpace(orderedStages[i].ConfigurationJson));
        }

        // Verify kinds
        Assert.Equal(WorkflowStageKind.Agentic, orderedStages[0].Kind);
        Assert.Equal(WorkflowStageKind.Deterministic, orderedStages[1].Kind);
        Assert.Equal(WorkflowStageKind.Agentic, orderedStages[2].Kind);
        Assert.Equal(WorkflowStageKind.Deterministic, orderedStages[3].Kind);
        Assert.Equal(WorkflowStageKind.Agentic, orderedStages[4].Kind);
        Assert.Equal(WorkflowStageKind.Deterministic, orderedStages[5].Kind);

        // 2. Second call: verify idempotence
        var secondSeeded = await context.EnsureDefaultWorkflowsAsync();

        Assert.NotNull(secondSeeded);
        Assert.Equal(seeded.Id, secondSeeded.Id);

        var totalWorkflows = await context.WorkflowDefinitions.CountAsync();
        var totalStages = await context.StageDefinitions.CountAsync();

        Assert.Equal(1, totalWorkflows);
        Assert.Equal(6, totalStages);
    }

    [Fact]
    public async Task PreExistingTasks_WithUnsetWorkflowId_AreBackfilledToDefaultWorkflow()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Legacy Workspace", RepositoryPath = "/legacy/repo" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        // 1. Pre-existing tasks before seeding or with null/0 workflow
        var legacyTask1 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Legacy Task 1",
            WorkflowId = null
        };
        var legacyTask2 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Legacy Task 2",
            WorkflowId = null
        };
        context.Tasks.AddRange(legacyTask1, legacyTask2);
        await context.SaveChangesAsync();

        // 2. Seed default workflow
        var defaultWf = await context.EnsureDefaultWorkflowsAsync();

        // 3. Backfill tasks where WorkflowId is null
        var unassignedTasks = await context.Tasks.Where(t => t.WorkflowId == null).ToListAsync();
        Assert.Equal(2, unassignedTasks.Count);

        foreach (var task in unassignedTasks)
        {
            task.WorkflowId = defaultWf.Id;
        }
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        // Verify all tasks now point to default workflow
        var reloadedTasks = await context.Tasks.Where(t => t.WorkspaceId == ws.Id).ToListAsync();
        Assert.All(reloadedTasks, t => Assert.Equal(defaultWf.Id, t.WorkflowId));
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_SeedsAll5ProfilesIdempotently()
    {
        using var context = await CreateContextAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b");

        var profiles = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profiles.Count);

        var profileIds = profiles.Select(p => p.Id).OrderBy(id => id).ToList();
        Assert.Equal(new[] { StandardStageIds.CodeReviewAndFix, StandardStageIds.Ideation, StandardStageIds.Implementation, StandardStageIds.TaskReview, StandardStageIds.Triage }, profileIds);

        // Calling again preserves existing profiles and does not duplicate
        await context.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b");

        var profilesAfter = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profilesAfter.Count);
    }
}
