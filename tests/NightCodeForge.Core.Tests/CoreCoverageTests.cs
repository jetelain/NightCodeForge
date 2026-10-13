using System.Text.Json;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
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
    public void Default3AgentPipeline_CreatesConfiguredPipeline()
    {
        var pipeline = Default3AgentPipeline.Create();
        Assert.NotNull(pipeline);
        Assert.Equal(6, pipeline.Stages.Count);

        Assert.IsType<IssueReviewStage>(pipeline.Stages[0]);
        Assert.IsType<DeterministicPreImplementationStage>(pipeline.Stages[1]);
        Assert.IsType<ImplementationStage>(pipeline.Stages[2]);
        Assert.IsType<DeterministicPostImplementationStage>(pipeline.Stages[3]);
        Assert.IsType<CodeReviewAndFixStage>(pipeline.Stages[4]);
        Assert.IsType<DeterministicPostReviewStage>(pipeline.Stages[5]);

        Assert.Equal(IssueReviewStage.DefaultStageId, pipeline.Stages[0].StageId);
        Assert.Equal(DeterministicPreImplementationStage.DefaultStageId, pipeline.Stages[1].StageId);
        Assert.Equal(ImplementationStage.DefaultStageId, pipeline.Stages[2].StageId);
        Assert.Equal(DeterministicPostImplementationStage.DefaultStageId, pipeline.Stages[3].StageId);
        Assert.Equal(CodeReviewAndFixStage.DefaultStageId, pipeline.Stages[4].StageId);
        Assert.Equal(DeterministicPostReviewStage.DefaultStageId, pipeline.Stages[5].StageId);
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
    public async Task ArchiveOrCleanupIssueFileAsync_ArchivesAndDeletesCorrectly()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue = new Issue { Id = 500, Title = "Archival Test", Description = "Test" };
        var filePath = await sync.SyncIssueToDiskAsync(issue, _tempDir);
        Assert.True(File.Exists(filePath));

        // Archive
        await sync.ArchiveOrCleanupIssueFileAsync(issue, _tempDir, archive: true);
        Assert.False(File.Exists(filePath));
        var archivedPath = Path.Combine(_tempDir, "docs", "issues", "archive", Path.GetFileName(filePath));
        Assert.True(File.Exists(archivedPath));

        // Clean up from archive
        await sync.SyncIssueToDiskAsync(issue, _tempDir);
        Assert.True(File.Exists(filePath));
        await sync.ArchiveOrCleanupIssueFileAsync(issue, _tempDir, archive: false);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void Watcher_StartAndStop_DoNotThrow()
    {
        var repo = new InMemoryIssueRepository();
        using var sync = new IssueMarkdownSyncService(repo);

        sync.StartWatching(1, _tempDir);
        sync.StartWatching(1, _tempDir); // Idempotent call
        sync.StopWatching(1);
        sync.StopWatching(1); // Idempotent call
    }

    [Fact]
    public async Task DependencyResolutionEngine_WorkspaceTopologicalOrder_QueriesStore()
    {
        var store = new InMemoryIssueDependencyStore();
        store.AddIssue(1, 10);
        store.AddIssue(2, 10);
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
    public async Task InMemoryIssueRepository_DeleteAsync_RemovesEntity()
    {
        var repo = new InMemoryIssueRepository();
        var issue = new Issue { Title = "To Delete" };
        var created = await repo.CreateAsync(issue);

        var fetched = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(fetched);

        await repo.DeleteAsync(created.Id);
        var missing = await repo.GetByIdAsync(created.Id);
        Assert.Null(missing);
    }

    [Fact]
    public void GenerateMarkdown_IncludesQuestionsWhenPresent()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue = new Issue
        {
            Id = 600,
            Title = "Issue With Questions",
            Description = "Description here"
        };
        issue.Questions.Add(new Question
        {
            Id = 1,
            StageId = "issue-review",
            Prompt = "Clarification prompt?",
            Answer = "Answered clarification."
        });

        var md = sync.GenerateMarkdown(issue);
        Assert.Contains("## ❓ Human Clarifications (Q&A)", md);
        Assert.Contains("Clarification prompt?", md);
        Assert.Contains("Answered clarification.", md);
    }

    [Fact]
    public async Task WorkflowPipeline_StageNotFound_FailsIssue()
    {
        var pipeline = new WorkflowPipeline(Array.Empty<IWorkflowStage>());
        var engine = new WorkflowPipelineEngine();
        var issue = new Issue { CurrentStageId = "non-existent" };
        var context = new PipelineContext(issue, new Workspace());

        var result = await engine.ExecuteStepAsync(pipeline, context);
        Assert.False(result.IsSuccess);
        Assert.Equal(IssueOverallStatus.Failed, issue.Status);
    }
}
