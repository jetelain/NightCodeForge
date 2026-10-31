using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class EfCoreWorkflowRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfCoreWorkflowRepositoryTests()
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
        await context.EnsureDefaultWorkflowsAsync();

        // Seed workspaces to satisfy foreign keys for workspace-scoped workflows
        context.Workspaces.Add(new Workspace { Id = 1, Name = "Workspace 1", RepositoryPath = "/repo1" });
        context.Workspaces.Add(new Workspace { Id = 2, Name = "Workspace 2", RepositoryPath = "/repo2" });
        await context.SaveChangesAsync();

        return context;
    }

    [Fact]
    public async Task GetDefaultWorkflowWithStagesAsync_ReturnsSeededStandardPipeline()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var defaultWf = await repo.GetDefaultWorkflowWithStagesAsync();

        Assert.NotNull(defaultWf);
        Assert.Equal(StandardWorkflowDefinitions.Standard3AgentCode, defaultWf.Code);
        Assert.True(defaultWf.IsDefault);
        Assert.Equal(6, defaultWf.Stages.Count);
        Assert.Equal(StandardStageIds.TaskReview, defaultWf.Stages.First().StageId);
        Assert.Equal(StandardStageIds.PostReviewVerification, defaultWf.Stages.Last().StageId);
    }

    [Fact]
    public async Task CreateWorkflowAsync_And_GetByIdWithStagesAsync_WorksAsExpected()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var customWf = new WorkflowDefinition
        {
            Name = "Custom 2-Stage",
            Code = "custom-2-stage",
            IsDefault = false,
            WorkspaceId = 1,
            Stages = new List<StageDefinition>
            {
                new()
                {
                    StageId = "impl",
                    DisplayName = "Implementation",
                    SequenceOrder = 1,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic"
                },
                new()
                {
                    StageId = "review",
                    DisplayName = "Review",
                    SequenceOrder = 2,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic"
                }
            }
        };

        var created = await repo.CreateWorkflowAsync(customWf);
        Assert.True(created.Id > 0);

        var retrieved = await repo.GetByIdWithStagesAsync(created.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Custom 2-Stage", retrieved.Name);
        Assert.Equal(2, retrieved.Stages.Count);
        Assert.Equal("impl", retrieved.Stages.First().StageId);
    }

    [Fact]
    public async Task GetWorkflowsAsync_FiltersByWorkspaceAndIncludesGlobal()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        // Standard workflow seeded is global (WorkspaceId == null)
        var customWs2 = new WorkflowDefinition
        {
            Name = "Workspace 2 Dedicated",
            Code = "ws2-dedicated",
            WorkspaceId = 2,
            Stages = new List<StageDefinition>
            {
                new() { StageId = "ws2-stage", DisplayName = "WS2 Stage", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };
        await repo.CreateWorkflowAsync(customWs2);

        // Query for workspace 2 should include global (standard-3-agent) + ws2-dedicated
        var ws2Workflows = await repo.GetWorkflowsAsync(workspaceId: 2);
        Assert.Equal(2, ws2Workflows.Count);
        Assert.Contains(ws2Workflows, w => w.Code == StandardWorkflowDefinitions.Standard3AgentCode);
        Assert.Contains(ws2Workflows, w => w.Code == "ws2-dedicated");

        // Query for workspace 99 should return only global
        var ws99Workflows = await repo.GetWorkflowsAsync(workspaceId: 99);
        Assert.Single(ws99Workflows);
        Assert.Equal(StandardWorkflowDefinitions.Standard3AgentCode, ws99Workflows[0].Code);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_And_DeleteWorkflowAsync_WorkCorrectly()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var wf = new WorkflowDefinition
        {
            Name = "Temporary Workflow",
            Code = "temp-wf",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "step1", DisplayName = "Step 1", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };
        await repo.CreateWorkflowAsync(wf);

        wf.Name = "Renamed Temporary Workflow";
        await repo.UpdateWorkflowAsync(wf);

        var updated = await repo.GetByIdWithStagesAsync(wf.Id);
        Assert.Equal("Renamed Temporary Workflow", updated!.Name);

        await repo.DeleteWorkflowAsync(wf.Id);
        var deleted = await repo.GetByIdWithStagesAsync(wf.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task UpdateWorkflowAsync_WithStageIdMatchZeroId_AndAddedOrRemovedStages_SyncsStagesCorrectly()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var wf = new WorkflowDefinition
        {
            Name = "Sync Stages Workflow",
            Code = "sync-stages-wf",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "step1", DisplayName = "Step 1", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "step2", DisplayName = "Step 2", SequenceOrder = 2, CsharpImplementationType = "agentic" }
            }
        };
        var created = await repo.CreateWorkflowAsync(wf);
        var originalStep1Id = created.Stages.First(s => s.StageId == "step1").Id;
        Assert.True(originalStep1Id > 0);

        // Construct a detached update where step1 has Id = 0 (matched by StageId), step2 is removed, and step3 is added
        var detachedUpdate = new WorkflowDefinition
        {
            Id = created.Id,
            Name = "Sync Stages Workflow Updated",
            Code = created.Code,
            Stages = new List<StageDefinition>
            {
                new() { Id = 0, StageId = "step1", DisplayName = "Step 1 Updated", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { Id = 0, StageId = "step3", DisplayName = "Step 3 Added", SequenceOrder = 2, CsharpImplementationType = "deterministic" }
            }
        };

        await repo.UpdateWorkflowAsync(detachedUpdate);

        var reloaded = await repo.GetByIdWithStagesAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Sync Stages Workflow Updated", reloaded.Name);
        Assert.Equal(2, reloaded.Stages.Count);

        var step1 = reloaded.Stages.First(s => s.StageId == "step1");
        Assert.Equal(originalStep1Id, step1.Id);
        Assert.Equal("Step 1 Updated", step1.DisplayName);

        var step3 = reloaded.Stages.First(s => s.StageId == "step3");
        Assert.True(step3.Id > 0);
        Assert.Equal("Step 3 Added", step3.DisplayName);
        Assert.DoesNotContain(reloaded.Stages, s => s.StageId == "step2");
    }
}
