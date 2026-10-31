using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskSyncVersionAndInterruptionTests : IDisposable
{
    private readonly string _tempWorkspaceDir;

    public TaskSyncVersionAndInterruptionTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_SyncVersionTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir))
            {
                Directory.Delete(_tempWorkspaceDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static TaskMarkdownSyncService CreateSyncService(ITaskRepository repo)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(repo);
        var depStore = new InMemoryTaskDependencyStore();
        services.AddSingleton<ITaskDependencyStore>(depStore);
        services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        var sp = services.BuildServiceProvider();
        return new TaskMarkdownSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IWorkspacePathResolver>().Object,
            NullLogger<TaskMarkdownSyncService>.Instance);
    }

    [Fact]
    public void GenerateMarkdown_IncludesVersionAndContentHashInFrontmatter()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 41,
            Title = "Fleet cartography API",
            Description = "Initial description",
            Status = TaskOverallStatus.Open,
            Version = 3
        };

        var md = sync.GenerateMarkdown(task);
        var (frontmatter, body) = sync.ParseMarkdown(md);
        var desc = TaskMarkdownSyncService.ExtractDescriptionFromBody(body);

        Assert.Equal(41, frontmatter.Id);
        Assert.Equal(3, frontmatter.Version);
        Assert.False(string.IsNullOrWhiteSpace(frontmatter.ContentHash));
        Assert.Equal(TaskMarkdownSyncService.ComputeTaskContentHash(frontmatter, desc), frontmatter.ContentHash);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenContentHashMatches_IgnoresFileCompletely()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Untouched Task",
            Description = "Original DB Description",
            Status = TaskOverallStatus.Open,
            Version = 2
        };
        await repo.CreateAsync(task);

        // Sync to disk so file has embedded content_hash
        await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);

        // Now change status in DB without incrementing version (to prove content_hash check alone skips status tamper check & update)
        task.Status = TaskOverallStatus.Closed;

        // Scan back: since content_hash on disk matches the file's content, the file was untouched on disk and is ignored
        var refreshed = await sync.SyncAllTasksFromDiskAsync(
            ws,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "test",
            changeReason: "Test scan back");

        Assert.Single(refreshed);
        Assert.Equal(TaskOverallStatus.Closed, refreshed[0].Status);
        Assert.Equal("Original DB Description", refreshed[0].Description);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenOtherTaskClosedConcurrentlyInDb_DoesNotThrowAndPreservesDbState()
    {
        // Repro of Task 41 / Task 42 live issue:
        // Task 41 is the executing task (Open).
        // Task 42 was completed and closed in DB (Version 2, Status Closed, UpdatedAt T2).
        // On disk, Task 42 file is still at Version 1 with Status Open (stale worktree state).
        // Because Task 42 in DB is at Version 2 (> 1), DB is authoritative!
        // SyncAllTasksFromDiskAsync must NOT throw and must NOT overwrite Task 42's status in DB.

        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var task41 = new ForgeTask
        {
            Id = 41,
            WorkspaceId = 1,
            Title = "Task 41 - Fleet Map",
            Description = "Agent modified description for 41",
            Status = TaskOverallStatus.Open,
            Version = 1,
            UpdatedAt = new DateTime(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateAsync(task41);

        var task42 = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Task 42 - Split Monolith",
            Description = "Completed work for 42",
            Status = TaskOverallStatus.Closed, // Completed meanwhile in DB!
            Version = 2,                       // Incremented in DB!
            UpdatedAt = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateAsync(task42);

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // Write disk file for Task 41 with agent modifications
        var md41 = sync.GenerateMarkdown(task41);
        var file41 = Path.Combine(tasksDir, "2026-10-31-task-41.md");
        await File.WriteAllTextAsync(file41, md41);

        // Write disk file for Task 42 with STALE version 1 and STALE status Open (without matching content_hash so version check is exercised)
        var staleMd42 = @"---
id: 42
title: Task 42 - Split Monolith
status: Open
version: 1
---

# Task 42 - Split Monolith

## 📋 Requirements & Description
Old description from version 1
";
        var file42 = Path.Combine(tasksDir, "2026-10-31-task-42.md");
        await File.WriteAllTextAsync(file42, staleMd42);

        // Run SyncAllTasksFromDiskAsync with baselineSnapshot: null (as happens during resume read-back)
        var refreshed = await sync.SyncAllTasksFromDiskAsync(
            ws,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "task-review",
            changeReason: "Resume readback");

        // Must succeed without throwing TaskMarkdownSyncException
        Assert.NotNull(refreshed);

        // Verify Task 42 in database was NOT overwritten or downgraded to Open
        var reloaded42 = await repo.GetByIdAsync(42);
        Assert.NotNull(reloaded42);
        Assert.Equal(TaskOverallStatus.Closed, reloaded42.Status);
        Assert.Equal("Completed work for 42", reloaded42.Description);
        Assert.Equal(2, reloaded42.Version);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenMaintainerChangesMergeModeInDb_PreservesMergeModeAndTakesAgentUpdatedDescription()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var task41 = new ForgeTask
        {
            Id = 41,
            WorkspaceId = 1,
            Title = "Fleet cartography API",
            Description = "Initial requirements",
            Status = TaskOverallStatus.Open,
            MergeMode = Domain.MergeMode.ManualReviewRequired,
            Version = 1
        };
        await repo.CreateAsync(task41);

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // 1. Maintainer changes MergeMode in DB (which bumps version in DB to 2)
        task41.MergeMode = Domain.MergeMode.Automatic;
        await repo.UpdateWithHistoryAsync(task41, null, AuditActors.User, "Maintainer enabled automatic merge");
        Assert.Equal(2, task41.Version);

        // 2. Meanwhile, agent worked on disk refining the spec with older version 1 frontmatter
        var agentFile = Path.Combine(tasksDir, "2026-10-31-task-41.md");
        var agentMarkdown = @"---
id: 41
title: Fleet cartography API
status: Open
version: 1
merge_mode: manual_review_required
---

# Fleet cartography API

## 📋 Requirements & Description
Initial requirements

## 🏛️ Architecture & Refinements
- Added distributed Redis caching layer
- Added fallback circuit breaker
";
        await File.WriteAllTextAsync(agentFile, agentMarkdown);

        // 3. Sync from disk runs with Task #41 as current task
        var refreshed = await sync.SyncAllTasksFromDiskAsync(
            ws,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "task-review",
            changeReason: "Stage 'task-review' completed disk synchronization",
            currentTaskId: 41);

        Assert.NotNull(refreshed);

        // 4. Verify DB has BOTH: maintainer's Automatic MergeMode AND agent's refined description!
        var reloaded = await repo.GetByIdAsync(41);
        Assert.NotNull(reloaded);
        Assert.Equal(Domain.MergeMode.Automatic, reloaded.MergeMode);
        Assert.Contains("Architecture & Refinements", reloaded.Description);
        Assert.Contains("distributed Redis caching layer", reloaded.Description);
    }

    [Fact]
    public async Task SyncSingleTaskFromDiskAsync_WhenVersionDiffersFromDb_DoesNotThrowOnStatusMismatch()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task42 = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Task 42 - Split Monolith",
            Description = "Completed work",
            Status = TaskOverallStatus.Closed,
            Version = 2
        };
        await repo.CreateAsync(task42);

        // Stale file with version 1 and status Open (without matching content_hash so version check is exercised)
        var staleMd = @"---
id: 42
title: Task 42 - Split Monolith
status: Open
version: 1
---

# Task 42 - Split Monolith

## 📋 Requirements & Description
Old description
";

        // Process file should not throw
        await sync.ProcessFileContentAsync("task-42.md", staleMd, workspaceId: 1, expectedTaskId: 42);

        var reloaded = await repo.GetByIdAsync(42);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Closed, reloaded.Status);
        Assert.Equal("Completed work", reloaded.Description);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WithBaselineSnapshot_WhenSiblingTaskHasOlderVersionOnDisk_PreservesDbState()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var task41 = new ForgeTask
        {
            Id = 41,
            WorkspaceId = 1,
            Title = "Task 41",
            Description = "Initial 41",
            Status = TaskOverallStatus.Open,
            Version = 1
        };
        await repo.CreateAsync(task41);

        var task42 = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Task 42",
            Description = "Final 42 in DB",
            Status = TaskOverallStatus.Closed,
            Version = 2
        };
        await repo.CreateAsync(task42);

        var syncResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);

        // Agent updates Task 41 on disk
        await File.WriteAllTextAsync(syncResult.WrittenPaths[41], @"---
id: 41
title: Task 41
status: Open
version: 1
---

# Task 41

## 📋 Requirements & Description
Updated 41 by agent
");

        // Simulate git operation in worktree reverting Task 42's file to an older version 1 (even referencing deleted dep 999)
        await File.WriteAllTextAsync(syncResult.WrittenPaths[42], @"---
id: 42
title: Task 42
status: Open
version: 1
depends_on:
  - 999
---

# Task 42

## 📋 Requirements & Description
Reverted old 42 description
");

        var refreshed = await sync.SyncAllTasksFromDiskAsync(
            ws,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: syncResult.Baseline,
            changeActor: "implementation",
            changeReason: "Stage completed",
            currentTaskId: 41);

        Assert.NotNull(refreshed);

        var reloaded41 = await repo.GetByIdAsync(41);
        Assert.Equal("Updated 41 by agent", reloaded41!.Description);

        var reloaded42 = await repo.GetByIdAsync(42);
        Assert.Equal(TaskOverallStatus.Closed, reloaded42!.Status);
        Assert.Equal("Final 42 in DB", reloaded42.Description);
        Assert.Equal(2, reloaded42.Version);
    }

    [Fact]
    public async Task ConsecutiveDiskEdits_NormalizesVersionAndContentHashOnDisk_AllowingSubsequentEdits()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Task 10",
            Description = "Initial description",
            Status = TaskOverallStatus.Open,
            Version = 1
        };
        await repo.CreateAsync(task);

        var syncResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        var taskFile = syncResult.WrittenPaths[10];

        // First edit on disk (frontmatter still has version: 1)
        var edit1 = (await File.ReadAllTextAsync(taskFile)).Replace("Initial description", "First disk edit");
        await File.WriteAllTextAsync(taskFile, edit1);

        await sync.SyncAllTasksFromDiskAsync(ws, _tempWorkspaceDir, null, null, "user", "First sync");

        var afterFirst = await repo.GetByIdAsync(10);
        Assert.Equal("First disk edit", afterFirst!.Description);
        Assert.Equal(2, afterFirst.Version);

        // Verify disk file was updated with version: 2
        var diskAfterFirst = await File.ReadAllTextAsync(taskFile);
        var (fmAfterFirst, _) = sync.ParseMarkdown(diskAfterFirst);
        Assert.Equal(2, fmAfterFirst.Version);

        // Second edit on disk must not be ignored as stale!
        var edit2 = diskAfterFirst.Replace("First disk edit", "Second disk edit");
        await File.WriteAllTextAsync(taskFile, edit2);

        await sync.SyncAllTasksFromDiskAsync(ws, _tempWorkspaceDir, null, null, "user", "Second sync");

        var afterSecond = await repo.GetByIdAsync(10);
        Assert.Equal("Second disk edit", afterSecond!.Description);
        Assert.Equal(3, afterSecond.Version);
    }

    [Fact]
    public async Task TaskOrchestrator_WhenInterruptingForAsk_SyncsDiskChangesToDatabase()
    {
        var repo = new InMemoryTaskRepository();
        var mockMdSync = new Mock<ITaskMarkdownSyncService>();
        mockMdSync.Setup(m => m.SyncAllTasksToDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), new Dictionary<int, TaskBaselineEntry>()));
        mockMdSync.Setup(m => m.SyncAllTasksFromDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ForgeTask
                {
                    Id = 41,
                    WorkspaceId = 1,
                    Title = "Task 41",
                    Description = "Refined spec before ask",
                    Status = TaskOverallStatus.Open,
                    Version = 2
                }
            });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            MarkdownSyncService = mockMdSync.Object
        };

        var ws = new Workspace
        {
            Id = 1,
            Name = "TestWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 41,
            WorkspaceId = 1,
            Title = "Task 41",
            Description = "Original spec",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        };
        await repo.CreateAsync(task);
        (builder.DependencyStore as InMemoryTaskDependencyStore)?.AddTask(task.Id, ws.Id, TaskOverallStatus.Open);

        // Stage handler throws HumanInterruptionException (as ask tool does)
        var testStage = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
        {
            QueueTier = StageQueueTier.TaskReview,
            Handler = (ctx, ct) => throw new HumanInterruptionException(new[] { new QuestionItem("Need approval?") })
        };

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { testStage }));

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsWaitingForHuman);
        Assert.Equal("Refined spec before ask", reloaded.Description);
        Assert.Equal(2, reloaded.Version);

        // Verify SyncAllTasksFromDiskAsync was called exactly once in total (not duplicated!) with explicit currentTaskId: 41
        mockMdSync.Verify(m => m.SyncAllTasksFromDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<int?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        mockMdSync.Verify(m => m.SyncAllTasksFromDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
            It.IsAny<string>(),
            It.Is<string>(r => r.Contains("pausing for human clarification")),
            41,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
