using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class EfCoreConversationResumptionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfCoreConversationResumptionTests()
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
    public async Task GetWaitingConversationAsync_ReturnsMostRecentWaitingForHumanConversation_WithTurnsOrderedByTurnIndex()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Test WS", RepositoryPath = "/repos/test" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask { WorkspaceId = ws.Id, Title = "Test Task", Status = TaskOverallStatus.Open };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        // Run 1: Completed
        var conv1 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(conv1.Id, StageExecutionStatus.Completed, summary: "Run 1 done");

        // Run 2: WaitingForHuman
        var conv2 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 2);
        await repo.AddConversationTurnAsync(conv2.Id, StageTurnRole.User, content: "Turn 0 User");
        await repo.AddConversationTurnAsync(conv2.Id, StageTurnRole.Tool, toolName: CanonicalToolNames.Ask, toolArguments: "{\"questions\":[]}");
        await repo.CompleteConversationAsync(conv2.Id, StageExecutionStatus.WaitingForHuman, summary: "Awaiting clarification");

        // Act
        var result = await repo.GetWaitingConversationAsync(task.Id, "task-review");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(conv2.Id, result.Id);
        Assert.Equal(StageExecutionStatus.WaitingForHuman, result.Status);
        Assert.Null(result.CompletedAt);
        var turnList = result.Turns.ToList();
        Assert.Equal(2, turnList.Count);
        Assert.Equal(0, turnList[0].TurnIndex);
        Assert.Equal(1, turnList[1].TurnIndex);
    }

    [Fact]
    public async Task ResumeConversationAsync_SetsStatusToRunning_AndClearsCompletedAt()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Test WS", RepositoryPath = "/repos/test" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask { WorkspaceId = ws.Id, Title = "Test Task", Status = TaskOverallStatus.Open };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));
        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting");

        // Act
        await repo.ResumeConversationAsync(conv.Id);

        // Assert reloaded from fresh context
        using var verifyContext = new AppDbContext(_options);
        var reloaded = await verifyContext.Conversations.FindAsync(conv.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(StageExecutionStatus.Running, reloaded.Status);
        Assert.Null(reloaded.CompletedAt);
    }

    [Fact]
    public async Task ResumeConversationAsync_WhenNotFound_ThrowsKeyNotFoundException()
    {
        using var context = await CreateContextAsync();
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.ResumeConversationAsync(99999));
    }

    [Fact]
    public async Task CompleteConversationAsync_WhenWaitingForHuman_LeavesCompletedAtNull_WhenTerminal_StampsCompletedAt()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Test WS", RepositoryPath = "/repos/test" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask { WorkspaceId = ws.Id, Title = "Test Task", Status = TaskOverallStatus.Open };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        // Case 1: Complete with WaitingForHuman -> CompletedAt is null
        var conv1 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(conv1.Id, StageExecutionStatus.WaitingForHuman);

        using var verifyContext1 = new AppDbContext(_options);
        var reloaded1 = await verifyContext1.Conversations.FindAsync(conv1.Id);
        Assert.NotNull(reloaded1);
        Assert.Equal(StageExecutionStatus.WaitingForHuman, reloaded1.Status);
        Assert.Null(reloaded1.CompletedAt);

        // Case 2: Complete with Completed -> CompletedAt is stamped
        var conv2 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 2);
        await repo.CompleteConversationAsync(conv2.Id, StageExecutionStatus.Completed);

        using var verifyContext2 = new AppDbContext(_options);
        var reloaded2 = await verifyContext2.Conversations.FindAsync(conv2.Id);
        Assert.NotNull(reloaded2);
        Assert.Equal(StageExecutionStatus.Completed, reloaded2.Status);
        Assert.NotNull(reloaded2.CompletedAt);

        // Case 3: Complete with Cancelled -> CompletedAt is stamped
        var conv3 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 3);
        await repo.CompleteConversationAsync(conv3.Id, StageExecutionStatus.Cancelled);

        using var verifyContext3 = new AppDbContext(_options);
        var reloaded3 = await verifyContext3.Conversations.FindAsync(conv3.Id);
        Assert.NotNull(reloaded3);
        Assert.Equal(StageExecutionStatus.Cancelled, reloaded3.Status);
        Assert.NotNull(reloaded3.CompletedAt);

        // Case 4: Complete with Interrupted -> CompletedAt is null
        var conv4 = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 4);
        await repo.CompleteConversationAsync(conv4.Id, StageExecutionStatus.Interrupted, summary: "Operations paused");

        using var verifyContext4 = new AppDbContext(_options);
        var reloaded4 = await verifyContext4.Conversations.FindAsync(conv4.Id);
        Assert.NotNull(reloaded4);
        Assert.Equal(StageExecutionStatus.Interrupted, reloaded4.Status);
        Assert.Null(reloaded4.CompletedAt);
        Assert.Equal("Operations paused", reloaded4.Summary);

        // Resuming conv4 clears Summary and CompletedAt
        await repo.ResumeConversationAsync(conv4.Id);
        using var verifyContext5 = new AppDbContext(_options);
        var reloaded5 = await verifyContext5.Conversations.FindAsync(conv4.Id);
        Assert.NotNull(reloaded5);
        Assert.Equal(StageExecutionStatus.Running, reloaded5.Status);
        Assert.Null(reloaded5.CompletedAt);
        Assert.Null(reloaded5.Summary);
    }

    [Fact]
    public async Task GetResumableConversationAsync_ReturnsInterruptedConversation_AndSanitizesTrailingInterruptedTurns()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Test WS", RepositoryPath = "/repos/test" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask { WorkspaceId = ws.Id, Title = "Test Task", Status = TaskOverallStatus.Open };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));
        var conv = await repo.StartConversationAsync(task.Id, "implementation", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Tool, toolName: "read", toolArguments: "{\"path\":\"a.cs\"}", metadataJson: "{\"CallId\":\"call_1\"}");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.ToolResult, toolName: "read", toolResult: "class A {}", metadataJson: "{\"CallId\":\"call_1\"}");
        // Trailing aborted step: thinking + unanswered tool call
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Thinking, content: "Running bash", thinkingTrace: "Running bash");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Tool, toolName: "bash", toolArguments: "{\"command\":\"dotnet test\"}", metadataJson: "{\"CallId\":\"call_2\"}");

        await repo.SanitizeTrailingInterruptedTurnsAsync(conv.Id);
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Operations paused");

        var resumable = await repo.GetResumableConversationAsync(task.Id, "implementation");
        Assert.NotNull(resumable);
        Assert.Equal(conv.Id, resumable.Id);
        Assert.Equal(StageExecutionStatus.Interrupted, resumable.Status);

        var turns = resumable.Turns.OrderBy(t => t.TurnIndex).ToList();
        Assert.Equal(3, turns.Count);
        Assert.Equal(StageTurnRole.User, turns[0].Role);
        Assert.Equal(StageTurnRole.Tool, turns[1].Role);
        Assert.Equal(StageTurnRole.ToolResult, turns[2].Role);

        // Next turn added after sanitization gets TurnIndex == 3
        var nextTurn = await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Resumed and completed");
        Assert.Equal(3, nextTurn.TurnIndex);
    }
}
