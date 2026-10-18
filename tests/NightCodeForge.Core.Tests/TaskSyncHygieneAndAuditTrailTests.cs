using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class TaskSyncHygieneAndAuditTrailTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _tempHostDir;

    public TaskSyncHygieneAndAuditTrailTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "NCF_Wks_" + Guid.NewGuid().ToString("N"));
        _tempHostDir = Path.Combine(Path.GetTempPath(), "NCF_Host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
        Directory.CreateDirectory(_tempHostDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir)) Directory.Delete(_tempWorkspaceDir, true);
            if (Directory.Exists(_tempHostDir)) Directory.Delete(_tempHostDir, true);
        }
        catch { }
    }

    private static (TaskMarkdownSyncService SyncService, InMemoryTaskRepository TaskRepo, DependencyResolutionEngine DepEngine, InMemoryTaskDependencyStore DepStore)
        CreateTestEnvironment()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(taskRepo);
        services.AddSingleton<ITaskDependencyStore>(depStore);
        services.AddSingleton<IDependencyResolutionEngine>(depEngine);
        var sp = services.BuildServiceProvider();

        var syncService = new TaskMarkdownSyncService(sp.GetRequiredService<IServiceScopeFactory>());
        return (syncService, taskRepo, depEngine, depStore);
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_ExportsAllTasks_IncludingActiveAndClosed_WithDependencies()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var t1 = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Task One", Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Task Two", Status = TaskOverallStatus.Closed };
        var t3 = new ForgeTask { Id = 3, WorkspaceId = 1, Title = "Task Three", Status = TaskOverallStatus.Cancelled };

        await repo.CreateWithBaselineAsync(t1, null, AuditActors.User, "Create 1");
        await repo.CreateWithBaselineAsync(t2, new[] { 1 }, AuditActors.User, "Create 2");
        await repo.CreateWithBaselineAsync(t3, new[] { 1, 2 }, AuditActors.User, "Create 3");

        depStore.AddTask(1, 1, TaskOverallStatus.Open);
        depStore.AddTask(2, 1, TaskOverallStatus.Closed);
        depStore.AddTask(3, 1, TaskOverallStatus.Cancelled);
        depStore.AddDependency(1, 2, 1);
        depStore.AddDependency(1, 3, 1);
        depStore.AddDependency(1, 3, 2);

        var result = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        Assert.Equal(3, result.WrittenPaths.Count);
        Assert.Equal(3, result.Baseline.Count);

        // Verify files written on disk in workspace
        foreach (var (taskId, path) in result.WrittenPaths)
        {
            Assert.True(File.Exists(path));
            var content = await File.ReadAllTextAsync(path);
            var (fm, _) = sync.ParseMarkdown(content, path);
            Assert.Equal(taskId, fm.Id);

            // Verify mirror on host
            var hostFile = Path.Combine(_tempHostDir, "docs", "tasks", Path.GetFileName(path));
            Assert.True(File.Exists(hostFile));
        }

        // Check dependencies populated in frontmatter
        var t2Content = await File.ReadAllTextAsync(result.WrittenPaths[2]);
        var (t2Fm, _) = sync.ParseMarkdown(t2Content);
        Assert.NotNull(t2Fm.DependsOn);
        Assert.Single(t2Fm.DependsOn);
        Assert.Contains(1, t2Fm.DependsOn);

        var t3Content = await File.ReadAllTextAsync(result.WrittenPaths[3]);
        var (t3Fm, _) = sync.ParseMarkdown(t3Content);
        Assert.NotNull(t3Fm.DependsOn);
        Assert.Equal(2, t3Fm.DependsOn.Count);
        Assert.Contains(1, t3Fm.DependsOn);
        Assert.Contains(2, t3Fm.DependsOn);
    }

    [Fact]
    public async Task DependencySemantics_MissingDependsOnPreserves_ExplicitEmptyRemoves()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var t1 = new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Root Task", Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { Id = 20, WorkspaceId = 1, Title = "Child Task A", Status = TaskOverallStatus.Open };
        var t3 = new ForgeTask { Id = 30, WorkspaceId = 1, Title = "Child Task B", Status = TaskOverallStatus.Open };

        await repo.CreateWithBaselineAsync(t1, null, AuditActors.User, "Root");
        await repo.CreateWithBaselineAsync(t2, new[] { 10 }, AuditActors.User, "Child A");
        await repo.CreateWithBaselineAsync(t3, new[] { 10 }, AuditActors.User, "Child B");

        depStore.AddTask(10, 1);
        depStore.AddTask(20, 1);
        depStore.AddTask(30, 1);
        depStore.AddDependency(1, 20, 10);
        depStore.AddDependency(1, 30, 10);

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        // Edit t2 markdown: omit depends_on completely
        var t2Path = syncResult.WrittenPaths[20];
        var t2MarkdownWithoutDependsOn = @"---
id: 20
title: Child Task A
priority: 4
---

# Child Task A
Description
";
        await File.WriteAllTextAsync(t2Path, t2MarkdownWithoutDependsOn);

        // Edit t3 markdown: explicit empty depends_on: []
        var t3Path = syncResult.WrittenPaths[30];
        var t3MarkdownWithEmptyDependsOn = @"---
id: 30
title: Child Task B
depends_on: []
---

# Child Task B
Description
";
        await File.WriteAllTextAsync(t3Path, t3MarkdownWithEmptyDependsOn);

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempHostDir,
            syncResult.Baseline,
            AuditActors.GetActorForStage("task-review"),
            "Update deps test");

        // Verify t2: missing depends_on preserved existing dependency
        var t2Deps = await repo.GetDependenciesAsync(20);
        Assert.Single(t2Deps);
        Assert.Equal(10, t2Deps[0]);

        // Verify t3: explicit empty depends_on wiped dependencies
        var t3Deps = await repo.GetDependenciesAsync(30);
        Assert.Empty(t3Deps);
    }

    [Fact]
    public async Task CycleDetection_AcrossTwoEditedFiles_ThrowsTaskMarkdownSyncException()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var t1 = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Task Alpha", Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Task Beta", Status = TaskOverallStatus.Open };

        await repo.CreateWithBaselineAsync(t1, null, AuditActors.User, "A");
        await repo.CreateWithBaselineAsync(t2, null, AuditActors.User, "B");

        depStore.AddTask(1, 1);
        depStore.AddTask(2, 1);

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        // Task 1 modified to depend on Task 2
        var t1Path = syncResult.WrittenPaths[1];
        await File.WriteAllTextAsync(t1Path, @"---
id: 1
title: Task Alpha
depends_on: [2]
---
# Task Alpha
");

        // Task 2 modified to depend on Task 1 -> Circular dependency!
        var t2Path = syncResult.WrittenPaths[2];
        await File.WriteAllTextAsync(t2Path, @"---
id: 2
title: Task Beta
depends_on: [1]
---
# Task Beta
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                syncResult.Baseline,
                AuditActors.GetActorForStage("task-review"),
                "Cycle test"));

        Assert.Contains("Circular dependency", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelfDependency_And_CrossWorkspaceDependency_ThrowTaskMarkdownSyncException()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var t1 = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Task Alpha", Status = TaskOverallStatus.Open };
        await repo.CreateWithBaselineAsync(t1, null, AuditActors.User, "A");
        depStore.AddTask(1, 1);

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        // 1. Self dependency
        var t1Path = syncResult.WrittenPaths[1];
        await File.WriteAllTextAsync(t1Path, @"---
id: 1
title: Task Alpha
depends_on: [1]
---
# Task Alpha
");

        var exSelf = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                syncResult.Baseline,
                AuditActors.User,
                "Self dep"));
        Assert.Contains("cannot depend on itself", exSelf.Message, StringComparison.OrdinalIgnoreCase);

        // 2. Unknown or foreign workspace dependency
        await File.WriteAllTextAsync(t1Path, @"---
id: 1
title: Task Alpha
depends_on: [9999]
---
# Task Alpha
");

        var exUnknown = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                syncResult.Baseline,
                AuditActors.User,
                "Unknown dep"));
        Assert.Contains("does not exist in workspace", exUnknown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrencyConflict_ThrowsTaskConcurrencyException()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var task = new ForgeTask { Id = 5, WorkspaceId = 1, Title = "Concurrent Task", Status = TaskOverallStatus.Open };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Initial");
        depStore.AddTask(5, 1);

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);
        var baseline = syncResult.Baseline;

        // DB task gets updated in background, incrementing its version to 2
        task.Title = "Concurrent Task - Changed in DB";
        await repo.UpdateWithHistoryAsync(task, null, AuditActors.User, "Background DB change");
        Assert.Equal(2, task.Version);

        // Disk also gets edited
        var taskFile = syncResult.WrittenPaths[5];
        await File.WriteAllTextAsync(taskFile, @"---
id: 5
title: Concurrent Task - Changed on Disk
---
# New disk content
");

        // Sync from disk detects conflict
        var ex = await Assert.ThrowsAsync<TaskConcurrencyException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                baseline,
                AuditActors.GetActorForStage("task-review"),
                "Read-back"));

        Assert.Equal(5, ex.TaskId);
        Assert.Equal(1, ex.ExpectedVersion);
        Assert.Equal(2, ex.ActualVersion);
    }

    [Fact]
    public async Task StatusTamperGuard_ThrowsTaskMarkdownSyncException()
    {
        var (sync, repo, _, depStore) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var task = new ForgeTask { Id = 7, WorkspaceId = 1, Title = "Status Guard Task", Status = TaskOverallStatus.Open };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Initial");
        depStore.AddTask(7, 1);

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        var taskFile = syncResult.WrittenPaths[7];
        await File.WriteAllTextAsync(taskFile, @"---
id: 7
title: Status Guard Task
status: Implemented
---
# Tampered status
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                syncResult.Baseline,
                AuditActors.GetActorForStage("task-review"),
                "Read-back"));

        Assert.Contains("forbidden", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workflow gates", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewFileWithoutId_AllocatesId_RenamesFile_MirrorsToHost_AndPreventsDuplicates()
    {
        var (sync, repo, _, _) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        var newFilePath = Path.Combine(tasksDir, "drop-in-task.md");
        await File.WriteAllTextAsync(newFilePath, @"---
title: Automatic Drop-In Feature
priority: 2
---
# Automatic Drop-In Feature
## 📋 Requirements & Description
Detailed spec of drop in.
");

        // Mirror old file initially to host
        var hostTasksDir = Path.Combine(_tempHostDir, "docs", "tasks");
        Directory.CreateDirectory(hostTasksDir);
        var oldHostFile = Path.Combine(hostTasksDir, "drop-in-task.md");
        await File.WriteAllTextAsync(oldHostFile, await File.ReadAllTextAsync(newFilePath));

        var refreshed = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempHostDir,
            baselineSnapshot: null,
            AuditActors.User,
            "Drop-in sync");

        Assert.Single(refreshed);
        var created = refreshed[0];
        Assert.True(created.Id > 0);
        Assert.Equal("Automatic Drop-In Feature", created.Title);
        Assert.Equal(TaskOverallStatus.Suggested, created.Status);

        // Old file deleted or replaced with standard named file
        Assert.False(File.Exists(newFilePath));
        Assert.False(File.Exists(oldHostFile));

        var datePrefix = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var expectedFileName = $"{datePrefix}-automatic-drop-in-feature.md";
        var renamedWorkPath = Path.Combine(tasksDir, expectedFileName);
        var renamedHostPath = Path.Combine(hostTasksDir, expectedFileName);

        Assert.True(File.Exists(renamedWorkPath));
        Assert.True(File.Exists(renamedHostPath));

        var diskContent = await File.ReadAllTextAsync(renamedWorkPath);
        var (fm, _) = sync.ParseMarkdown(diskContent, renamedWorkPath);
        Assert.Equal(created.Id, fm.Id);

        // Second read-back creates NO duplicate task
        var secondRun = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempHostDir,
            baselineSnapshot: null,
            AuditActors.User,
            "Second read-back");

        var allTasks = await repo.GetByWorkspaceIdAsync(1);
        Assert.Single(allTasks);
    }

    [Fact]
    public async Task NewFile_WithBlankTitle_ThrowsTaskMarkdownSyncException()
    {
        var (sync, repo, _, _) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        var blankTitleFile = Path.Combine(tasksDir, "blank-title.md");
        await File.WriteAllTextAsync(blankTitleFile, @"---
priority: 1
---
# No title in frontmatter
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                baselineSnapshot: null,
                AuditActors.User,
                "Blank title test"));

        Assert.Contains("non-blank title", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TaskReview_UnmodifiedSpecification_ThrowsWorkflowExecutionException()
    {
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Specification Review",
            Description = "Original specification document.",
            CurrentStageId = "task-review",
            Status = TaskOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Repo1",
            RepositoryPath = _tempHostDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(42, 1);
        var depEngine = new DependencyResolutionEngine(depStore);

        var (sync, _, _, _) = CreateTestEnvironment();

        var stage = new TestWorkflowStage("task-review", WorkflowStageKind.Agentic)
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { stage })),
            MarkdownSyncService = sync
        }.Build();

        // Stage completed without agent editing the markdown file on disk -> must throw WorkflowExecutionException
        await Assert.ThrowsAsync<WorkflowExecutionException>(() =>
            orchestrator.ProcessTaskAsync(task.Id));
    }

    [Fact]
    public async Task CancellationPassthrough_OperationCanceledException_NotWrapped()
    {
        var (sync, repo, _, _) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 1. SyncAllTasksToDiskAsync throws OperationCanceledException directly without wrapping
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir, cancellationToken: cts.Token));

        // 2. SyncAllTasksFromDiskAsync throws OperationCanceledException directly without wrapping
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                _tempHostDir,
                baselineSnapshot: null,
                AuditActors.User,
                "Reason",
                cancellationToken: cts.Token));

        // 3. In orchestrator, cancellation does not set task status to Failed (unlike sync errors)
        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Cancel Test",
            CurrentStageId = "implementation",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(88, 1);
        var depEngine = new DependencyResolutionEngine(depStore);

        var mockSync = new Mock<ITaskMarkdownSyncService>();
        mockSync.Setup(s => s.SyncAllTasksToDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            DependencyStore = depStore,
            MarkdownSyncService = mockSync.Object
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        // Verification: task was not marked Failed
        Assert.NotEqual(TaskOverallStatus.Failed, task.Status);
    }

    [Fact]
    public void GitIgnoreEntries_ArePresentAcrossManagedLocations()
    {
        var expectedEntries = TaskSyncConstants.RequiredGitIgnoreEntries;

        Assert.Contains("docs/tasks/", expectedEntries);
        Assert.Contains("docs/transcripts/", expectedEntries);
        Assert.Contains("TestResults/", expectedEntries);
        Assert.Contains("*.trx", expectedEntries);

        // Check root .gitignore
        var rootGitIgnore = Path.Combine(Directory.GetCurrentDirectory(), ".gitignore");
        if (File.Exists(rootGitIgnore))
        {
            var content = File.ReadAllText(rootGitIgnore);
            foreach (var entry in expectedEntries)
            {
                Assert.Contains(entry, content);
            }
        }
    }

    [Fact]
    public async Task History_BaselineCreatedExactlyOnce_VersionsIncrementOnlyOnRealChanges()
    {
        var (sync, repo, _, _) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var task = new ForgeTask
        {
            Id = 15,
            WorkspaceId = 1,
            Title = "History Stability Task",
            Description = "Base spec",
            Priority = 2,
            Status = TaskOverallStatus.Open
        };

        var created = await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");
        Assert.Equal(1, created.Version);

        var initialHistory = await repo.GetHistoryAsync(15);
        Assert.Single(initialHistory);
        Assert.Equal(1, initialHistory[0].Version);

        // Pre-sync to disk
        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);

        // Read-back with UNCHANGED files
        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempHostDir,
            syncResult.Baseline,
            AuditActors.GetActorForStage("task-review"),
            "Read-back unchanged");

        // Verify version and history count did NOT change
        var fetched1 = await repo.GetByIdAsync(15);
        Assert.NotNull(fetched1);
        Assert.Equal(1, fetched1.Version);

        var historyAfterUnchanged = await repo.GetHistoryAsync(15);
        Assert.Single(historyAfterUnchanged);

        // Now modify description on disk
        var taskPath = syncResult.WrittenPaths[15];
        var modifiedMarkdown = @"---
id: 15
title: History Stability Task
priority: 2
---
# History Stability Task
## 📋 Requirements & Description
Mutated spec on disk
";
        await File.WriteAllTextAsync(taskPath, modifiedMarkdown);

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempHostDir,
            syncResult.Baseline,
            AuditActors.GetActorForStage("task-review"),
            "Agent edited spec");

        // Verify version incremented to 2 and exactly one new history entry was appended
        var fetched2 = await repo.GetByIdAsync(15);
        Assert.NotNull(fetched2);
        Assert.Equal(2, fetched2.Version);
        Assert.Equal("Mutated spec on disk", fetched2.Description);

        var finalHistory = await repo.GetHistoryAsync(15);
        Assert.Equal(2, finalHistory.Count);
        Assert.Equal(1, finalHistory[0].Version);
        Assert.Equal(2, finalHistory[1].Version);
        Assert.Equal(AuditActors.GetActorForStage("task-review"), finalHistory[1].ChangedBy);
    }

    [Fact]
    public async Task WatcherSuppression_MirroredFilesDoNotTriggerDuplicateSync()
    {
        var (sync, repo, _, _) = CreateTestEnvironment();
        var workspace = new Workspace { Id = 1, Name = "Repo1", RepositoryPath = _tempHostDir };

        var task = new ForgeTask
        {
            Id = 25,
            WorkspaceId = 1,
            Title = "Suppression Task",
            Description = "Initial description",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempHostDir);
        var hostFile = Path.Combine(_tempHostDir, "docs", "tasks", Path.GetFileName(syncResult.WrittenPaths[25]));
        Assert.True(File.Exists(hostFile));

        var hostContent = await File.ReadAllTextAsync(hostFile);

        // Simulated watcher event on host file
        await sync.ProcessFileContentAsync(hostFile, hostContent, workspaceId: 1);

        // Verify task version remains 1 and history count remains 1 (suppressed)
        var t = await repo.GetByIdAsync(25);
        Assert.NotNull(t);
        Assert.Equal(1, t.Version);
        var h = await repo.GetHistoryAsync(25);
        Assert.Single(h);
    }

    [Fact]
    public async Task TaskReview_ResumedStageAfterEarlierEdit_DoesNotFail()
    {
        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Resumed Review Task",
            Description = "Spec that was modified prior to pause",
            CurrentStageId = "task-review",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "task-review", // Resumed run
            Version = 2 // Already modified earlier
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Repo1",
            RepositoryPath = _tempHostDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var (sync, syncRepo, _, _) = CreateTestEnvironment();
        await syncRepo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(55, 1);
        var depEngine = new DependencyResolutionEngine(depStore);

        var stage = new TestWorkflowStage("task-review", WorkflowStageKind.Agentic)
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = syncRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { stage })),
            MarkdownSyncService = sync
        }.Build();

        // Resumed run does NOT throw WorkflowExecutionException even if not modified in this execution
        await orchestrator.ProcessTaskAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Ready, task.Status);
    }

    [Fact]
    public async Task InterruptedStage_AgentEditsBeforeAsk_SurviveResume()
    {
        var task = new ForgeTask
        {
            Id = 77,
            WorkspaceId = 1,
            Title = "Interrupted Task",
            Description = "Base spec",
            CurrentStageId = "task-review",
            Status = TaskOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Repo1",
            RepositoryPath = _tempHostDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var (sync, taskRepo, depEngine, depStore) = CreateTestEnvironment();
        await taskRepo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        depStore.AddTask(77, 1);


        // 1. Initial stage run: agent modifies disk markdown, then stage returns NeedsClarification (WaitingForHuman)
        var stage1 = new TestWorkflowStage("task-review", WorkflowStageKind.Agentic)
        {
            Handler = async (ctx, ct) =>
            {
                var tasksDir = Path.Combine(_tempHostDir, "docs", "tasks");
                var files = Directory.GetFiles(tasksDir, "*.md");
                Assert.NotEmpty(files);
                var targetFile = files[0];

                var modified = @"---
id: 77
title: Interrupted Task
priority: 3
---
# Interrupted Task
## 📋 Requirements & Description
Agent formulated detailed architecture before asking human.
";
                await File.WriteAllTextAsync(targetFile, modified, ct);
                throw new HumanInterruptionException("Clarify question?");
            }
        };

        var orchestrator1 = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { stage1 })),
            MarkdownSyncService = sync
        }.Build();

        await orchestrator1.ProcessTaskAsync(task.Id);

        // Task is now waiting for human
        var pausedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(pausedTask);
        Assert.True(pausedTask.IsWaitingForHuman);
        Assert.Equal("task-review", pausedTask.InterruptedStageId);

        // 2. Human responds:
        pausedTask.IsWaitingForHuman = false;
        await taskRepo.UpdateAsync(pausedTask);
        await depStore.SetWaitingForHumanAsync(pausedTask.Id, false);

        // 3. Resume stage run:
        string observedDescriptionOnResume = string.Empty;
        var stage2 = new TestWorkflowStage("task-review", WorkflowStageKind.Agentic)
        {
            Handler = async (ctx, ct) =>
            {
                var tasksDir = Path.Combine(_tempHostDir, "docs", "tasks");
                var files = Directory.GetFiles(tasksDir, "*.md");
                var content = await File.ReadAllTextAsync(files[0], ct);
                var (fm, body) = sync.ParseMarkdown(content);
                observedDescriptionOnResume = body;
                return StageExecutionResult.Success();
            }
        };

        var orchestrator2 = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { stage2 })),
            MarkdownSyncService = sync
        }.Build();

        await orchestrator2.ProcessTaskAsync(task.Id);

        // Verify agent edits made before the ask survived the resume and were not wiped by pre-sync!
        Assert.Contains("Agent formulated detailed architecture before asking human.", observedDescriptionOnResume);
    }
}
