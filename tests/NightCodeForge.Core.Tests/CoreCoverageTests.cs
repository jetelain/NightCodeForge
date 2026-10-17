using System.Text.Json;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class CoreCoverageTests : IDisposable
{
    private readonly string _tempDir;

    public CoreCoverageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_Cov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task DefaultWorkflowPipelineProvider_ReturnsConfiguredPipeline()
    {
        var stage = new GenericAgenticStage("task-review", "ForgeTask Review", new AgenticStageConfig { RequiredMode = AgentMode.Full });
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });
        var provider = new DefaultWorkflowPipelineProvider(pipeline);

        var retrieved = await provider.GetPipelineAsync(new ForgeTask());
        Assert.Same(pipeline, retrieved);
        Assert.Single(retrieved.Stages);
    }

    [Fact]
    public void PipelineDataBag_EdgeCases_HandledGracefully()
    {
        var bag = new PipelineDataBag();
        bag.Set("k1", 100);

        Assert.True(bag.ContainsKey("k1"));
        Assert.False(bag.ContainsKey("k2"));

        Assert.True(bag.Remove("k1"));
        Assert.False(bag.Remove("k1"));

        var all = bag.GetAll();
        Assert.Empty(all);

        // FromJson edge cases
        var emptyBag = PipelineDataBag.FromJson(null);
        Assert.NotNull(emptyBag);

        var corruptBag = PipelineDataBag.FromJson("not a valid json {} !");
        Assert.NotNull(corruptBag);
        Assert.Empty(corruptBag.GetAll());

        // Constructor with initial dictionary
        var dict = new Dictionary<string, JsonElement>
        {
            ["alpha"] = JsonSerializer.SerializeToElement("beta")
        };
        var preloaded = new PipelineDataBag(dict);
        Assert.Equal("beta", preloaded.Get<string>("alpha"));
    }

    [Fact]
    public async Task ArchiveOrCleanupTaskFileAsync_ArchivesAndDeletesCorrectly()
    {
        var repo = new InMemoryTaskRepository();
        var sync = new TaskMarkdownSyncService(repo);

        var task = new ForgeTask { Id = 500, Title = "Archival Test", Description = "Test" };
        var filePath = await sync.SyncTaskToDiskAsync(task, _tempDir);
        Assert.True(File.Exists(filePath));

        // Archive
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempDir, archive: true);
        Assert.False(File.Exists(filePath));
        var archivedPath = Path.Combine(_tempDir, "docs", "tasks", "archive", Path.GetFileName(filePath));
        Assert.True(File.Exists(archivedPath));

        // Clean up from archive
        await sync.SyncTaskToDiskAsync(task, _tempDir);
        Assert.True(File.Exists(filePath));
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempDir, archive: false);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void Watcher_StartAndStop_DoNotThrow()
    {
        var repo = new InMemoryTaskRepository();
        using var sync = new TaskMarkdownSyncService(repo);

        sync.StartWatching(1, _tempDir);
        sync.StartWatching(1, _tempDir); // Idempotent call
        sync.StopWatching(1);
        sync.StopWatching(1); // Idempotent call
    }

    [Fact]
    public async Task DependencyResolutionEngine_WorkspaceTopologicalOrder_QueriesStore()
    {
        var store = new InMemoryTaskDependencyStore();
        store.AddTask(1, 10);
        store.AddTask(2, 10);
        store.AddDependency(10, 2, 1);

        var engine = new DependencyResolutionEngine(store);
        var order = engine.GetTopologicalOrder(10);
        Assert.Equal(new[] { 1, 2 }, order);

        // Remove dependency
        store.RemoveDependency(10, 2, 1);
        var deps = await store.GetDependenciesForWorkspaceAsync(10);
        Assert.Empty(deps);
    }

    [Fact]
    public async Task WorkspaceLockProvider_Cancellation_CleansUpSemaphore()
    {
        var provider = new WorkspaceLockProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.AcquireLockAsync(99, cts.Token));

        // Lock should be immediately acquirable since canceled acquisition didn't leak
        using var releaser = await provider.AcquireLockAsync(99);
        Assert.NotNull(releaser);
    }

    [Fact]
    public async Task InMemoryTaskRepository_DeleteAsync_RemovesEntity()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Title = "To Delete" };
        var created = await repo.CreateAsync(task);

        var fetched = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);

        await repo.DeleteAsync(created.Id);
        var missing = await repo.GetByIdAsync(created.Id);
        Assert.Null(missing);
    }

    [Fact]
    public void GenerateMarkdown_IncludesQuestionsWhenPresent()
    {
        var repo = new InMemoryTaskRepository();
        var sync = new TaskMarkdownSyncService(repo);

        var task = new ForgeTask
        {
            Id = 600,
            Title = "ForgeTask With Questions",
            Description = "Description here"
        };
        task.Questions.Add(new Question
        {
            Id = 1,
            StageId = "task-review",
            Prompt = "Clarification prompt?",
            Answer = "Answered clarification."
        });

        var md = sync.GenerateMarkdown(task);
        Assert.Contains("## ❓ Human Clarifications (Q&A)", md);
        Assert.Contains("Clarification prompt?", md);
        Assert.Contains("Answered clarification.", md);
    }

    [Fact]
    public async Task WorkflowPipeline_StageNotFound_FailsTask()
    {
        var pipeline = new WorkflowPipeline(Array.Empty<IWorkflowStage>());
        var engine = new WorkflowPipelineEngine();
        var task = new ForgeTask { CurrentStageId = "non-existent" };
        var context = new PipelineContext(task, new Workspace());

        var result = await engine.ExecuteStepAsync(pipeline, context);
        Assert.False(result.IsSuccess);
        Assert.Equal(TaskOverallStatus.Failed, task.Status);
    }

    [Fact]
    public async Task InMemoryTaskRepository_IdeationConversations_Lifecycle_WorksCorrectly()
    {
        var repo = new InMemoryTaskRepository();

        // 1. No active conversation
        var none = await repo.GetActiveIdeationConversationAsync(100);
        Assert.Null(none);

        // 2. Start conversation
        var conv = await repo.StartIdeationConversationAsync(100, "Discovery 1", "deepseek-v3", "ideation");
        Assert.NotNull(conv);
        Assert.Null(conv.TaskId);
        Assert.Equal(100, conv.WorkspaceId);
        Assert.Equal(ConversationType.Ideation, conv.ConversationType);
        Assert.Equal(StageExecutionStatus.Running, conv.Status);

        // 3. Add turns
        var uTurn = await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Can we add auth?");
        Assert.Equal(0, uTurn.TurnIndex);

        var aTurn = await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Sure", thinkingTrace: "Thinking...");
        Assert.Equal(1, aTurn.TurnIndex);

        // 4. Get active
        var active = await repo.GetActiveIdeationConversationAsync(100);
        Assert.NotNull(active);
        Assert.Equal(conv.Id, active.Id);
        Assert.Equal(2, active.Turns.Count);
        Assert.Equal(0, active.Turns.ElementAt(0).TurnIndex);
        Assert.Equal(1, active.Turns.ElementAt(1).TurnIndex);

        // 5. Complete conversation
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed);
        var activeAfterComplete = await repo.GetActiveIdeationConversationAsync(100);
        Assert.Null(activeAfterComplete);

        // 6. Still visible in list
        var list = await repo.GetIdeationConversationsAsync(100);
        Assert.Single(list);
        Assert.Equal(conv.Id, list[0].Id);
    }
}
