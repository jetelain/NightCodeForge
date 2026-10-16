using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class IdeationConversationPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public IdeationConversationPersistenceTests()
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
    public async Task IdeationConversation_PersistsWithNullableTaskId_AndRehydratesTurnsChronologically()
    {
        using var context = await CreateContextAsync();
        var ws = new Workspace { Name = "Ideation Persistence Workspace", RepositoryPath = "/repos/ideation-pers" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(context);

        // 1. Create Conversation with ConversationType = Ideation and TaskId = null
        var conv = await repo.StartIdeationConversationAsync(
            ws.Id,
            title: "Microservices Decomposition Ideation",
            modelName: "deepseek-r1:32b",
            agentProfileId: "ideation");

        Assert.NotNull(conv);
        Assert.True(conv.Id > 0);
        Assert.Null(conv.TaskId);
        Assert.Equal(ws.Id, conv.WorkspaceId);
        Assert.Equal(ConversationType.Ideation, conv.ConversationType);
        Assert.Equal(StageExecutionStatus.Running, conv.Status);

        // 2. Insert turns covering: User, Tool, ToolResult, Thinking, Assistant
        // Turn 0: User Prompt
        var turn0 = await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.User,
            content: "Please analyze our current authentication architecture and propose decoupling tasks.");
        Assert.Equal(0, turn0.TurnIndex);

        // Turn 1: Tool Execution
        var turn1 = await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            content: "Invoking grep_search for JWT handlers",
            toolName: "grep_search",
            toolArguments: "{\"pattern\": \"JwtSecurityTokenHandler\"}");
        Assert.Equal(1, turn1.TurnIndex);

        // Turn 2: ToolResult
        var turn2 = await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            content: "Found 4 occurrences in AuthController.cs and TokenService.cs",
            toolName: "grep_search",
            toolArguments: "{\"pattern\": \"JwtSecurityTokenHandler\"}",
            toolResult: "AuthController.cs:42, TokenService.cs:88",
            durationMs: 95,
            isError: false);
        Assert.Equal(2, turn2.TurnIndex);

        // Turn 3: Assistant with Thinking trace and proposal metadata
        var proposalsJson = """
        [
          {
            "Title": "Extract Token Verification Service",
            "Description": "Decouple JWT verification into a dedicated class library.",
            "Priority": 1,
            "DependsOnTitles": []
          }
        ]
        """;

        var turn3 = await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Assistant,
            content: "I have analyzed the JWT token handling and propose extracting the verification logic.",
            thinkingTrace: "Reasoning about cohesion and blast radius of auth refactoring...",
            metadataJson: proposalsJson);
        Assert.Equal(3, turn3.TurnIndex);

        // Clear tracking context to ensure rehydration strictly from SQLite
        context.ChangeTracker.Clear();

        // 3. Rehydrate conversation with turns
        var loaded = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loaded);
        Assert.Null(loaded.TaskId);
        Assert.Equal(ws.Id, loaded.WorkspaceId);
        Assert.Equal(ConversationType.Ideation, loaded.ConversationType);
        Assert.Equal("Microservices Decomposition Ideation", loaded.Title);
        Assert.Equal(4, loaded.Turns.Count);

        // Verify strictly chronological order by TurnIndex
        var turnsList = loaded.Turns.ToList();
        for (var i = 0; i < turnsList.Count; i++)
        {
            Assert.Equal(i, turnsList[i].TurnIndex);
        }

        // Verify details of Turn 0
        Assert.Equal(StageTurnRole.User, turnsList[0].Role);
        Assert.Contains("authentication architecture", turnsList[0].Content);

        // Verify details of Turn 1
        Assert.Equal(StageTurnRole.Tool, turnsList[1].Role);
        Assert.Equal("grep_search", turnsList[1].ToolName);
        Assert.Equal("{\"pattern\": \"JwtSecurityTokenHandler\"}", turnsList[1].ToolArguments);

        // Verify details of Turn 2
        Assert.Equal(StageTurnRole.ToolResult, turnsList[2].Role);
        Assert.Equal("grep_search", turnsList[2].ToolName);
        Assert.Equal(95, turnsList[2].DurationMs);
        Assert.False(turnsList[2].IsError);
        Assert.Contains("AuthController.cs:42", turnsList[2].ToolResult);

        // Verify details of Turn 3
        Assert.Equal(StageTurnRole.Assistant, turnsList[3].Role);
        Assert.Equal("Reasoning about cohesion and blast radius of auth refactoring...", turnsList[3].ThinkingTrace);
        Assert.Equal(proposalsJson, turnsList[3].MetadataJson);
        Assert.Contains("Extract Token Verification Service", turnsList[3].MetadataJson);

        // 4. Mark conversation as completed
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed, summary: "Discovered 1 architectural proposal.");

        context.ChangeTracker.Clear();
        var completed = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(completed);
        Assert.Equal(StageExecutionStatus.Completed, completed.Status);
    }
}
