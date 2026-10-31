using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class WorkflowRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public WorkflowRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
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
    public async Task SqliteWalPersistence_WorkflowAndStages_PersistsAndLoadsCorrectly()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var workflow = new WorkflowDefinition
        {
            Name = "Security Verification Pipeline",
            Code = "sec-pipe",
            Icon = "🔒",
            Description = "Enforces static analysis before code review.",
            IsDefault = false,
            Stages = new List<StageDefinition>
            {
                new()
                {
                    StageId = "sast-check",
                    DisplayName = "SAST Check",
                    Icon = "🛡️",
                    SequenceOrder = 1,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-sast",
                    ConfigurationJson = """{"Action":"Sast","FailOnError":true}"""
                },
                new()
                {
                    StageId = "peer-review",
                    DisplayName = "Peer Review",
                    Icon = "👀",
                    SequenceOrder = 2,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """{"RequiredMode":2}"""
                }
            }
        };

        var created = await repo.CreateWorkflowAsync(workflow);
        Assert.True(created.Id > 0);
        Assert.Equal(2, created.Stages.Count);

        // Clear tracking cache to ensure loading from SQLite database
        context.ChangeTracker.Clear();

        var loaded = await repo.GetByIdWithStagesAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Security Verification Pipeline", loaded.Name);
        Assert.Equal("sec-pipe", loaded.Code);
        Assert.Equal(2, loaded.Stages.Count);
        var orderedStages = loaded.Stages.OrderBy(s => s.SequenceOrder).ToList();
        Assert.Equal("sast-check", orderedStages[0].StageId);
        Assert.Equal("peer-review", orderedStages[1].StageId);
    }

    [Fact]
    public async Task CascadeDelete_WhenWorkflowDeleted_RemovesAssociatedStages()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreWorkflowRepository(new AppDbContextFactory(_options));

        var workflow = new WorkflowDefinition
        {
            Name = "Temporary Workflow To Delete",
            Code = "temp-delete-wf",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "step-a", DisplayName = "Step A", SequenceOrder = 1, CsharpImplementationType = "agentic" },
                new() { StageId = "step-b", DisplayName = "Step B", SequenceOrder = 2, CsharpImplementationType = "agentic" },
                new() { StageId = "step-c", DisplayName = "Step C", SequenceOrder = 3, CsharpImplementationType = "agentic" }
            }
        };

        var created = await repo.CreateWorkflowAsync(workflow);
        var workflowId = created.Id;

        // Verify stages exist in database
        var initialStages = await context.StageDefinitions.Where(s => s.WorkflowId == workflowId).ToListAsync();
        Assert.Equal(3, initialStages.Count);

        // Delete workflow via repository
        await repo.DeleteWorkflowAsync(workflowId);

        // Verify workflow is deleted
        var fetchedWorkflow = await repo.GetByIdWithStagesAsync(workflowId);
        Assert.Null(fetchedWorkflow);

        // Verify child stages were cascaded and deleted
        var remainingStages = await context.StageDefinitions.Where(s => s.WorkflowId == workflowId).ToListAsync();
        Assert.Empty(remainingStages);
    }

    [Fact]
    public async Task ForeignKeyBehavior_TaskWorkflowId_ValidAssignmentAndNullability()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "FK Test Repo", RepositoryPath = "/repo/fk" };
        context.Workspaces.Add(ws);

        var workflow = new WorkflowDefinition
        {
            Name = "Standard Linked Pipeline",
            Code = "std-linked",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "step-1", DisplayName = "Step 1", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        // 1. Task with valid WorkflowId
        var taskWithWf = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Task with Workflow",
            WorkflowId = workflow.Id
        };
        context.Tasks.Add(taskWithWf);

        // 2. Task with null WorkflowId (allowed)
        var taskWithoutWf = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Task without Workflow",
            WorkflowId = null
        };
        context.Tasks.Add(taskWithoutWf);

        await context.SaveChangesAsync();

        Assert.True(taskWithWf.Id > 0);
        Assert.True(taskWithoutWf.Id > 0);

        context.ChangeTracker.Clear();

        var reloadedWithWf = await context.Tasks.Include(t => t.Workflow).FirstOrDefaultAsync(t => t.Id == taskWithWf.Id);
        Assert.NotNull(reloadedWithWf);
        Assert.Equal(workflow.Id, reloadedWithWf.WorkflowId);
        Assert.NotNull(reloadedWithWf.Workflow);
        Assert.Equal("Standard Linked Pipeline", reloadedWithWf.Workflow.Name);

        var reloadedWithoutWf = await context.Tasks.Include(t => t.Workflow).FirstOrDefaultAsync(t => t.Id == taskWithoutWf.Id);
        Assert.NotNull(reloadedWithoutWf);
        Assert.Null(reloadedWithoutWf.WorkflowId);
        Assert.Null(reloadedWithoutWf.Workflow);
    }

    [Fact]
    public async Task ForeignKeyBehavior_DeletingWorkflowReferencedByTask_IsRestricted()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Restrict WS", RepositoryPath = "/repo/restrict" };
        context.Workspaces.Add(ws);

        var workflow = new WorkflowDefinition
        {
            Name = "Protected Workflow",
            Code = "protected-wf",
            Stages = new List<StageDefinition>
            {
                new() { StageId = "s1", DisplayName = "S1", SequenceOrder = 1, CsharpImplementationType = "agentic" }
            }
        };
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Active Task",
            WorkflowId = workflow.Id
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        // Attempting to delete workflow should fail due to DeleteBehavior.Restrict foreign key constraint
        var attachedWf = new WorkflowDefinition { Id = workflow.Id };
        context.WorkflowDefinitions.Attach(attachedWf);
        context.WorkflowDefinitions.Remove(attachedWf);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
