using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleConversationCoordinatorSqliteTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ncf-coord-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public CrucibleConversationCoordinatorSqliteTests()
    {
        Directory.CreateDirectory(_tempDir);
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_dbOptions);
        context.EnsureDatabaseCreatedAndWalAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private Workspace SeedWorkspace()
    {
        using var context = new AppDbContext(_dbOptions);
        var workspace = new Workspace
        {
            Name = "SQLite Transient Task WS",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        context.SaveChanges();
        return workspace;
    }

    [Fact]
    public async Task Coordinator_BeginAsync_WithTransientForgeTaskIdZero_PersistsNullTaskIdWithoutSqliteFkViolation()
    {
        var workspace = SeedWorkspace();
        var repo = new EfCoreTaskRepository(new TestDbContextFactory(_dbOptions));
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(131072);

        var coordinator = new CrucibleConversationCoordinator(
            repo,
            new SessionTranscriptService(),
            gatewayMock.Object,
            NullLogger<CrucibleConversationCoordinator>.Instance);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask
            {
                Id = 0,
                WorkspaceId = workspace.Id,
                Title = "Transient Triage Run",
                Description = "Inspect untrusted report"
            },
            Workspace = workspace,
            Profile = new AgentProfile
            {
                Id = "triage",
                Name = "Triage",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = "test-model",
                Mode = AgentMode.ReadOnly
            },
            StageId = StandardStageIds.Triage,
            RequiredMode = AgentMode.ReadOnly,
            WorkspacePath = _tempDir,
            BootstrapPrompt = "Inspect issue"
        };

        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Null(state.Conversation.TaskId);

        await using var verifyDb = new AppDbContext(_dbOptions);
        var persisted = await verifyDb.Conversations.AsNoTracking().SingleAsync();
        Assert.Null(persisted.TaskId);
        Assert.Equal(workspace.Id, persisted.WorkspaceId);
        Assert.Equal(StageExecutionStatus.Running, persisted.Status);
    }

    [Fact]
    public async Task CrucibleHarness_RunStageAsync_WithTransientForgeTaskIdZero_CompletesAgainstSqliteWithoutFkError()
    {
        var workspace = SeedWorkspace();
        var repo = new EfCoreTaskRepository(new TestDbContextFactory(_dbOptions));
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = new SingleResponseChatClient("Triage analysis complete."),
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var sessionContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask
            {
                Id = 0,
                WorkspaceId = workspace.Id,
                Title = "Transient ForgeTask Execution",
                Description = "Run read-only triage"
            },
            Workspace = workspace,
            Profile = new AgentProfile
            {
                Id = "triage",
                Name = "Triage",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = "test-model",
                Mode = AgentMode.ReadOnly,
                SecurityTier = SandboxSecurityTier.RestrictedReadOnly
            },
            StageId = StandardStageIds.Triage,
            RequiredMode = AgentMode.ReadOnly,
            WorkspacePath = _tempDir,
            BootstrapPrompt = "Run triage cleanly"
        };

        var result = await harness.RunStageAsync(sessionContext);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        await using var verifyDb = new AppDbContext(_dbOptions);
        var persisted = await verifyDb.Conversations.AsNoTracking().SingleAsync();
        Assert.Null(persisted.TaskId);
        Assert.Equal(StageExecutionStatus.Completed, persisted.Status);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class SingleResponseChatClient(string responseText) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new ChatResponseUpdate { Contents = [new TextContent(responseText)] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
