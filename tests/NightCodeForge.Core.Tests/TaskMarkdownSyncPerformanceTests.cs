using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskMarkdownSyncPerformanceTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _tempCloneDir;

    public TaskMarkdownSyncPerformanceTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "ncf-perf-sync-test-" + Guid.NewGuid().ToString("N"));
        _tempCloneDir = Path.Combine(Path.GetTempPath(), "ncf-perf-clone-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
        Directory.CreateDirectory(_tempCloneDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir))
            {
                Directory.Delete(_tempWorkspaceDir, true);
            }
            if (Directory.Exists(_tempCloneDir))
            {
                Directory.Delete(_tempCloneDir, true);
            }
        }
        catch (Exception)
        {
            // Justification: Best-effort cleanup of temporary directories during test disposal.
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
    public async Task SyncAllTasksToDiskAsync_ConsecutiveRuns_DoesNotAlterLastWriteTimeUtc()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 1, Name = "PerfWS", RepositoryPath = _tempWorkspaceDir };

        for (int i = 1; i <= 5; i++)
        {
            await repo.CreateWithBaselineAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = $"Task Number {i}",
                Description = $"Description for task {i}",
                Status = TaskOverallStatus.Open,
                CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
            }, null, "Test", "Init");
        }

        // 1. First sync to disk
        var firstResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        Assert.Equal(5, firstResult.WrittenPaths.Count);

        var timestamps = new Dictionary<string, DateTime>();
        foreach (var path in firstResult.WrittenPaths.Values)
        {
            Assert.True(File.Exists(path));
            timestamps[path] = File.GetLastWriteTimeUtc(path);
        }

        // Wait to guarantee that if a file were re-written, its timestamp would change
        await Task.Delay(100);

        // 2. Second sync to disk without any database changes
        var secondResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        Assert.Equal(5, secondResult.WrittenPaths.Count);

        // Verify zero files were re-written on disk
        foreach (var path in secondResult.WrittenPaths.Values)
        {
            Assert.True(timestamps.ContainsKey(path));
            var secondTimestamp = File.GetLastWriteTimeUtc(path);
            Assert.Equal(timestamps[path], secondTimestamp);
        }
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_WhenSingleTaskChanges_RewritesOnlyChangedTask()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 2, Name = "PerfWS2", RepositoryPath = _tempWorkspaceDir };

        var tasks = new List<ForgeTask>();
        for (int i = 1; i <= 4; i++)
        {
            var t = await repo.CreateWithBaselineAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = $"Task {i}",
                Description = $"Initial description {i}",
                Status = TaskOverallStatus.Open,
                CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
            }, null, "Test", "Init");
            tasks.Add(t);
        }

        var firstResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        var initialTimestamps = firstResult.WrittenPaths.ToDictionary(kvp => kvp.Key, kvp => File.GetLastWriteTimeUtc(kvp.Value));

        await Task.Delay(100);

        // Modify only task 2 in repository
        var taskToChange = tasks[1];
        taskToChange.Description = "Updated description for task 2";
        taskToChange.Version++;
        await repo.UpdateWithHistoryAsync(taskToChange, null, "User", "Update", CancellationToken.None);

        var secondResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);

        // Verify task 2 was rewritten
        var task2Path = secondResult.WrittenPaths[taskToChange.Id];
        Assert.True(File.GetLastWriteTimeUtc(task2Path) > initialTimestamps[taskToChange.Id]);

        // Verify tasks 1, 3, 4 were NOT rewritten
        foreach (var t in tasks.Where(x => x.Id != taskToChange.Id))
        {
            var p = secondResult.WrittenPaths[t.Id];
            Assert.Equal(initialTimestamps[t.Id], File.GetLastWriteTimeUtc(p));
        }
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_MovesTerminalTaskToArchive_AndCleansOpposite()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 3, Name = "PerfWS3", RepositoryPath = _tempWorkspaceDir };

        var task = await repo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Archivable Task",
            Description = "Will be closed",
            Status = TaskOverallStatus.Suggested,
            CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
        }, null, "Test", "Init");

        // 1. Initial sync puts task in docs/tasks
        var firstResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir, hostRepositoryPath: null, cloneDirectory: _tempCloneDir);
        var activeFilePath = firstResult.WrittenPaths[task.Id];
        Assert.True(File.Exists(activeFilePath));
        Assert.DoesNotContain("archive", activeFilePath, StringComparison.OrdinalIgnoreCase);

        var cloneActiveFilePath = Path.Combine(_tempCloneDir, "docs", "tasks", Path.GetFileName(activeFilePath));
        Assert.True(File.Exists(cloneActiveFilePath));

        // 2. Transition task to Closed
        task.Status = TaskOverallStatus.Closed;
        task.Version++;
        await repo.UpdateWithHistoryAsync(task, null, "User", "Closed task", CancellationToken.None);

        // 3. Second sync moves task to archive in both local and clone
        var secondResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir, hostRepositoryPath: null, cloneDirectory: _tempCloneDir);
        var archivedFilePath = secondResult.WrittenPaths[task.Id];
        Assert.True(File.Exists(archivedFilePath));
        Assert.Contains("archive", archivedFilePath, StringComparison.OrdinalIgnoreCase);

        // Active file must be deleted
        Assert.False(File.Exists(activeFilePath));

        // Clone must have archived file and no active file
        var cloneArchivedFilePath = Path.Combine(_tempCloneDir, "docs", "tasks", "archive", Path.GetFileName(archivedFilePath));
        Assert.True(File.Exists(cloneArchivedFilePath));
        Assert.False(File.Exists(cloneActiveFilePath));
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_LinearScaling_WithManyTasks()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 4, Name = "PerfWS4", RepositoryPath = _tempWorkspaceDir };

        for (int i = 1; i <= 30; i++)
        {
            await repo.CreateWithBaselineAsync(new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = $"Batch Task {i:D3}",
                Description = $"Batch task description {i}",
                Status = i % 5 == 0 ? TaskOverallStatus.Closed : TaskOverallStatus.Open,
                CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
            }, null, "Test", "Init");
        }

        // Initial sync creates 30 files
        var initialResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        Assert.Equal(30, initialResult.WrittenPaths.Count);

        // Second sync on untouched workspace completes rapidly with zero writes
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var untouchedResult = await sync.SyncAllTasksToDiskAsync(ws, _tempWorkspaceDir);
        sw.Stop();

        Assert.Equal(30, untouchedResult.WrittenPaths.Count);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Consecutive sync took {sw.ElapsedMilliseconds} ms, expected < 500 ms.");
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WithCloneDirectory_SkipsUnchangedCloneWriteAndCleansStaleOppositeFiles()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 5, Name = "PerfWS5", RepositoryPath = _tempWorkspaceDir };
        var task = await repo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Single Task Sync",
            Description = "Verify clone write skipping and opposite cleanup",
            Status = TaskOverallStatus.Open,
            CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
        }, null, "Test", "Init");

        // 1. Initial single-task sync to local and clone
        var writtenPath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: _tempCloneDir);
        var cloneActivePath = Path.Combine(_tempCloneDir, "docs", "tasks", Path.GetFileName(writtenPath));
        Assert.True(File.Exists(cloneActivePath));
        var firstCloneWriteTime = File.GetLastWriteTimeUtc(cloneActivePath);

        await Task.Delay(100);

        // 2. Second single-task sync without changes: clone file must NOT be rewritten
        await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: _tempCloneDir);
        Assert.Equal(firstCloneWriteTime, File.GetLastWriteTimeUtc(cloneActivePath));

        // 3. Transition task to Closed and sync single task: moves to archive and removes active file in both local and clone
        task.Status = TaskOverallStatus.Closed;
        task.Version++;
        var archivedPath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: _tempCloneDir);
        var cloneArchivedPath = Path.Combine(_tempCloneDir, "docs", "tasks", "archive", Path.GetFileName(archivedPath));

        Assert.True(File.Exists(archivedPath));
        Assert.False(File.Exists(writtenPath));
        Assert.True(File.Exists(cloneArchivedPath));
        Assert.False(File.Exists(cloneActivePath));
    }

    [Fact]
    public async Task DirectoryTaskIndex_PathNormalizationAndDeterministicOrdering_HandlesMixedSlashesAndCleanup()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        var fileB = Path.Combine(tasksDir, "2026-11-10-b-task.md");
        var fileA = Path.Combine(tasksDir, "2026-11-10-a-task.md");
        await File.WriteAllTextAsync(fileB, "---\nid: 10\ntitle: B Task\nversion: 1\n---\nBody B\n");
        await File.WriteAllTextAsync(fileA, "---\nid: 10\ntitle: A Task\nversion: 1\n---\nBody A\n");

        var index = await DirectoryTaskIndex.CreateAsync(
            tasksDir + Path.DirectorySeparatorChar,
            sync.ParseMarkdown,
            sync.ComputeContentHash);

        // Entries must be sorted deterministically by FilePath regardless of parallel read completion order
        var task10Files = index.GetFilesByTaskId(10);
        Assert.Equal(2, task10Files.Count);
        Assert.Equal("2026-11-10-a-task.md", task10Files[0].FileName);
        Assert.Equal("2026-11-10-b-task.md", task10Files[1].FileName);

        // Path lookups must normalize forward/backward slashes
        var altSlashPath = fileA.Replace('\\', '/');
        Assert.True(index.TryGetEntryByPath(altSlashPath, out var foundA));
        Assert.Equal(10, foundA.TaskId);
        Assert.NotNull(index.TryGetFileHash(altSlashPath));

        // Removing via alternate slash path removes only fileA while keeping fileB
        index.RemoveEntry(altSlashPath);
        Assert.False(index.TryGetEntryByPath(fileA, out _));
        Assert.Single(index.GetFilesByTaskId(10));
    }

    [Fact]
    public async Task ArchiveOrCleanupTaskFileAsync_WithCloneDirectory_MovesAndDeletesViaIndex()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var ws = new Workspace { Id = 6, Name = "PerfWS6", RepositoryPath = _tempWorkspaceDir };
        var task = await repo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Cleanup Target Task",
            Description = "Test archive and delete via index",
            Status = TaskOverallStatus.Open,
            CreatedAt = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc)
        }, null, "Test", "Init");

        var activePath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: _tempCloneDir);
        var fileName = Path.GetFileName(activePath);
        var cloneActivePath = Path.Combine(_tempCloneDir, "docs", "tasks", fileName);
        var localArchivePath = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "archive", fileName);
        var cloneArchivePath = Path.Combine(_tempCloneDir, "docs", "tasks", "archive", fileName);

        // 1. Archive task across local and clone
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempWorkspaceDir, archive: true, cloneDirectory: _tempCloneDir);
        Assert.False(File.Exists(activePath));
        Assert.False(File.Exists(cloneActivePath));
        Assert.True(File.Exists(localArchivePath));
        Assert.True(File.Exists(cloneArchivePath));

        // 2. Delete task across local and clone (including archive)
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempWorkspaceDir, archive: false, cloneDirectory: _tempCloneDir);
        Assert.False(File.Exists(localArchivePath));
        Assert.False(File.Exists(cloneArchivePath));
    }
}
