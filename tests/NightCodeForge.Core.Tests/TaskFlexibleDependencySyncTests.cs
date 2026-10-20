using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Tests;

public class TaskFlexibleDependencySyncTests : IDisposable
{
    private readonly string _tempWorkspaceDir;

    public TaskFlexibleDependencySyncTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_FlexDepTests_" + Guid.NewGuid().ToString("N"));
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
        var depStore = new Dependencies.InMemoryTaskDependencyStore();
        services.AddSingleton<Dependencies.ITaskDependencyStore>(depStore);
        services.AddSingleton<Dependencies.IDependencyResolutionEngine>(new Dependencies.DependencyResolutionEngine(depStore));
        var sp = services.BuildServiceProvider();
        return new TaskMarkdownSyncService(sp.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public void TaskDependencyReferenceList_SerializationAndDeserialization_PreservesMixedTypes()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var markdown = @"---
title: Sample Task
depends_on:
  - 10
  - ""2026-10-20-task-b.md""
  - ""task-c""
---

# Sample Task

## 📋 Requirements & Description
Test description
";

        var (frontmatter, _) = sync.ParseMarkdown(markdown);
        Assert.NotNull(frontmatter.DependsOn);
        Assert.Equal(3, frontmatter.DependsOn.Count);
        Assert.Equal(10, frontmatter.DependsOn[0]);
        Assert.Equal("2026-10-20-task-b.md", frontmatter.DependsOn[1]);
        Assert.Equal("task-c", frontmatter.DependsOn[2]);

        var task = new ForgeTask
        {
            Id = 5,
            WorkspaceId = 1,
            Title = "Sample Task",
            Description = "Test description",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var regenerated = sync.GenerateMarkdown(task, new[] { 10, 20 });
        var (regeneratedFm, _) = sync.ParseMarkdown(regenerated);
        Assert.NotNull(regeneratedFm.DependsOn);
        Assert.Equal(new[] { 10, 20 }, regeneratedFm.DependsOn);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_BatchNewTasks_ResolvesDependenciesByFilenameAndNormalizes()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // File A has no dependencies
        var fileA = Path.Combine(tasksDir, "2026-10-20-task-alpha.md");
        await File.WriteAllTextAsync(fileA, @"---
title: Task Alpha
priority: 1
---

# Task Alpha

## 📋 Requirements & Description
Initial foundation
");

        // File B depends on File A by full filename
        var fileB = Path.Combine(tasksDir, "2026-10-20-task-beta.md");
        await File.WriteAllTextAsync(fileB, @"---
title: Task Beta
priority: 2
depends_on:
  - ""2026-10-20-task-alpha.md""
---

# Task Beta

## 📋 Requirements & Description
Depends on alpha
");

        var syncedTasks = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "Tester",
            changeReason: "Initial batch import");

        Assert.Equal(2, syncedTasks.Count);

        var taskAlpha = syncedTasks.FirstOrDefault(t => t.Title == "Task Alpha");
        var taskBeta = syncedTasks.FirstOrDefault(t => t.Title == "Task Beta");

        Assert.NotNull(taskAlpha);
        Assert.NotNull(taskBeta);

        // Verify taskBeta dependencies in repository
        var betaDeps = await repo.GetDependenciesAsync(taskBeta.Id);
        Assert.Single(betaDeps);
        Assert.Equal(taskAlpha.Id, betaDeps[0]);

        // Verify that File B on disk was normalized to canonical integer ID
        var updatedContentB = await File.ReadAllTextAsync(fileB);
        var (fmB, _) = sync.ParseMarkdown(updatedContentB);
        Assert.NotNull(fmB.DependsOn);
        Assert.Contains(taskAlpha.Id, fmB.DependsOn);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_BatchNewTasks_ResolvesDependenciesBySlugAndTitle()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var fileA = Path.Combine(tasksDir, "task-a.md");
        await File.WriteAllTextAsync(fileA, @"---
title: User Authentication
---

# User Authentication

## 📋 Requirements & Description
Implement auth
");

        var fileB = Path.Combine(tasksDir, "task-b.md");
        await File.WriteAllTextAsync(fileB, @"---
title: User Profile
depends_on:
  - ""user-authentication""
---

# User Profile

## 📋 Requirements & Description
Implement profile
");

        var syncedTasks = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "Tester",
            changeReason: "Slug import");

        Assert.Equal(2, syncedTasks.Count);

        var authTask = syncedTasks.First(t => t.Title == "User Authentication");
        var profileTask = syncedTasks.First(t => t.Title == "User Profile");

        var profileDeps = await repo.GetDependenciesAsync(profileTask.Id);
        Assert.Equal(new[] { authTask.Id }, profileDeps);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_BatchNewTasks_SortsTopologicallyMultiLevel()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // Level 3 depends on Level 2
        var file3 = Path.Combine(tasksDir, "task-3.md");
        await File.WriteAllTextAsync(file3, @"---
title: Level Three
depends_on:
  - ""task-2""
---

# Level Three
## 📋 Requirements & Description
Third
");

        // Level 1 has no deps
        var file1 = Path.Combine(tasksDir, "task-1.md");
        await File.WriteAllTextAsync(file1, @"---
title: Level One
---

# Level One
## 📋 Requirements & Description
First
");

        // Level 2 depends on Level 1
        var file2 = Path.Combine(tasksDir, "task-2.md");
        await File.WriteAllTextAsync(file2, @"---
title: Level Two
depends_on:
  - ""task-1""
---

# Level Two
## 📋 Requirements & Description
Second
");

        var syncedTasks = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "Tester",
            changeReason: "Topological test");

        Assert.Equal(3, syncedTasks.Count);

        var t1 = syncedTasks.First(t => t.Title == "Level One");
        var t2 = syncedTasks.First(t => t.Title == "Level Two");
        var t3 = syncedTasks.First(t => t.Title == "Level Three");

        var t2Deps = await repo.GetDependenciesAsync(t2.Id);
        var t3Deps = await repo.GetDependenciesAsync(t3.Id);

        Assert.Equal(new[] { t1.Id }, t2Deps);
        Assert.Equal(new[] { t2.Id }, t3Deps);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_ResolvesDependencyOnExistingTaskBySlug()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        // Create pre-existing task in DB
        var existingTask = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Core Data Pipeline",
                Description = "Existing core pipeline",
                Status = TaskOverallStatus.Closed,
                CurrentStageId = "closed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Version = 1
            },
            null,
            "Setup",
            "Initial");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // New task on disk references existing task by slug
        var fileNew = Path.Combine(tasksDir, "analytics.md");
        await File.WriteAllTextAsync(fileNew, @"---
title: Analytics Dashboard
depends_on:
  - ""core-data-pipeline""
---

# Analytics Dashboard

## 📋 Requirements & Description
Dashboard
");

        var synced = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "Tester",
            changeReason: "Sync with existing");

        var newTask = synced.FirstOrDefault(t => t.Title == "Analytics Dashboard");
        Assert.NotNull(newTask);

        var deps = await repo.GetDependenciesAsync(newTask.Id);
        Assert.Single(deps);
        Assert.Equal(existingTask.Id, deps[0]);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_DetectsCycleInNewTasks_ThrowsException()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var fileA = Path.Combine(tasksDir, "task-a.md");
        await File.WriteAllTextAsync(fileA, @"---
title: Cycle Task A
depends_on:
  - ""cycle-task-b""
---

# Cycle Task A
## 📋 Requirements & Description
A
");

        var fileB = Path.Combine(tasksDir, "task-b.md");
        await File.WriteAllTextAsync(fileB, @"---
title: Cycle Task B
depends_on:
  - ""cycle-task-a""
---

# Cycle Task B
## 📋 Requirements & Description
B
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                hostRepositoryPath: null,
                baselineSnapshot: null,
                changeActor: "Tester",
                changeReason: "Cycle check"));

        Assert.Contains("Circular dependency", ex.Message);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_DetectsSelfDependency_ThrowsException()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var fileA = Path.Combine(tasksDir, "self-task.md");
        await File.WriteAllTextAsync(fileA, @"---
title: Self Task
depends_on:
  - ""self-task""
---

# Self Task
## 📋 Requirements & Description
Self
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                hostRepositoryPath: null,
                baselineSnapshot: null,
                changeActor: "Tester",
                changeReason: "Self check"));

        Assert.Contains("cannot depend on itself", ex.Message);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_UnresolvableDependency_ThrowsException()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var fileA = Path.Combine(tasksDir, "missing-dep.md");
        await File.WriteAllTextAsync(fileA, @"---
title: Task With Missing Dep
depends_on:
  - ""non-existent-task-slug""
---

# Task With Missing Dep
## 📋 Requirements & Description
Missing
");

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.SyncAllTasksFromDiskAsync(
                workspace,
                _tempWorkspaceDir,
                hostRepositoryPath: null,
                baselineSnapshot: null,
                changeActor: "Tester",
                changeReason: "Missing dep check"));

        Assert.Contains("could not be resolved", ex.Message);
    }

    [Fact]
    public async Task ProcessFileContentAsync_NewTaskWithSlugDependency_ResolvesAndNormalizesFile()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        // Pre-create dependency in repo
        var existing = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Base Feature",
                Description = "Base",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            },
            null,
            "Setup",
            "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var filePath = Path.Combine(tasksDir, "child-feature.md");
        var content = @"---
title: Child Feature
depends_on:
  - ""base-feature""
---

# Child Feature
## 📋 Requirements & Description
Child details
";
        await File.WriteAllTextAsync(filePath, content);

        await sync.ProcessFileContentAsync(filePath, content, workspaceId: 1);

        var allTasks = await repo.GetByWorkspaceIdAsync(1);
        var childTask = allTasks.FirstOrDefault(t => t.Title == "Child Feature");
        Assert.NotNull(childTask);

        var deps = await repo.GetDependenciesAsync(childTask.Id);
        Assert.Equal(new[] { existing.Id }, deps);

        // Check normalization on disk
        var normalizedOnDisk = await File.ReadAllTextAsync(filePath);
        var (normFm, _) = sync.ParseMarkdown(normalizedOnDisk);
        Assert.NotNull(normFm.Id);
        Assert.Equal(childTask.Id, normFm.Id);
        Assert.NotNull(normFm.DependsOn);
        Assert.Equal(new[] { existing.Id }, normFm.DependsOn);
    }

    [Fact]
    public async Task ProcessFileContentAsync_ExistingTaskWithSlugDependency_ResolvesAndNormalizesFile()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var dep1 = await repo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Prerequisite One", Description = "Pre1" },
            null, "Setup", "Init");

        var targetTask = await repo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Target Task", Description = "Target" },
            null, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var filePath = Path.Combine(tasksDir, $"{targetTask.Id}-target.md");
        var content = $@"---
id: {targetTask.Id}
title: Target Task
depends_on:
  - ""prerequisite-one""
---

# Target Task
## 📋 Requirements & Description
Updated target
";
        await File.WriteAllTextAsync(filePath, content);

        await sync.ProcessFileContentAsync(filePath, content, workspaceId: 1);

        var updatedDeps = await repo.GetDependenciesAsync(targetTask.Id);
        Assert.Equal(new[] { dep1.Id }, updatedDeps);

        var normalizedOnDisk = await File.ReadAllTextAsync(filePath);
        var (normFm, _) = sync.ParseMarkdown(normalizedOnDisk);
        Assert.Equal(new[] { dep1.Id }, normFm.DependsOn);
    }

    [Fact]
    public async Task ProcessFileContentAsync_DetectsCircularDependencyWithExistingTasks_ThrowsException()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        // Pre-create Task 1
        var task1 = await repo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Task 1", Description = "T1" },
            null, "Setup", "Init");

        // Pre-create Task 2 depending on Task 1
        var task2 = await repo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Task 2", Description = "T2" },
            new[] { task1.Id }, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // Modify Task 1 to depend on Task 2 -> creates 1 -> 2 -> 1 cycle
        var filePath = Path.Combine(tasksDir, $"{task1.Id}-task-1.md");
        var content = $@"---
id: {task1.Id}
title: Task 1
depends_on:
  - {task2.Id}
---

# Task 1
## 📋 Requirements & Description
Updated Task 1 with circular dep
";
        await File.WriteAllTextAsync(filePath, content);

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(filePath, content, workspaceId: 1));

        Assert.Contains("Circular dependency", ex.Message);
    }

    [Fact]
    public async Task ProcessFileContentAsync_DetectsSelfDependencyByIdAndSlug_ThrowsException()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task1 = await repo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Isolated Task", Description = "Desc" },
            null, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // 1. Self dependency by ID
        var filePathId = Path.Combine(tasksDir, $"{task1.Id}-self-id.md");
        var contentId = $@"---
id: {task1.Id}
title: Isolated Task
depends_on:
  - {task1.Id}
---

# Isolated Task
## 📋 Requirements & Description
Self dep by ID
";
        await File.WriteAllTextAsync(filePathId, contentId);

        var ex1 = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(filePathId, contentId, workspaceId: 1));
        Assert.Contains("cannot depend on itself", ex1.Message);

        // 2. Self dependency by slug
        var filePathSlug = Path.Combine(tasksDir, $"{task1.Id}-self-slug.md");
        var contentSlug = $@"---
id: {task1.Id}
title: Isolated Task
depends_on:
  - ""isolated-task""
---

# Isolated Task
## 📋 Requirements & Description
Self dep by slug
";
        await File.WriteAllTextAsync(filePathSlug, contentSlug);

        var ex2 = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(filePathSlug, contentSlug, workspaceId: 1));
        Assert.Contains("cannot depend on itself", ex2.Message);
    }

    [Fact]
    public async Task ProcessFileContentAsync_SyncsMergeModeAndBaseBranchModifications()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Configurable Task",
                Description = "Desc",
                MergeMode = MergeMode.ManualReviewRequired,
                BaseBranch = "main"
            },
            null, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var filePath = Path.Combine(tasksDir, $"{task.Id}-configurable.md");
        var content = $@"---
id: {task.Id}
title: Configurable Task
merge_mode: ""automatic""
base_branch: ""develop""
---

# Configurable Task
## 📋 Requirements & Description
Updated merge_mode and base_branch
";
        await File.WriteAllTextAsync(filePath, content);

        await sync.ProcessFileContentAsync(filePath, content, workspaceId: 1);

        var updated = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);
        Assert.Equal("develop", updated.BaseBranch);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_SyncsMergeModeAndBaseBranchModifications()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempWorkspaceDir };

        var task = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Batch Existing Task",
                Description = "Initial",
                MergeMode = MergeMode.ManualReviewRequired,
                BaseBranch = "main"
            },
            null, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        var filePath = Path.Combine(tasksDir, $"{task.Id}-batch.md");
        var content = $@"---
id: {task.Id}
title: Batch Existing Task
merge_mode: ""automatic""
base_branch: ""release/v1.0""
---

# Batch Existing Task
## 📋 Requirements & Description
Initial
";
        await File.WriteAllTextAsync(filePath, content);

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: "Tester",
            changeReason: "Sync merge_mode");

        var updated = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);
        Assert.Equal("release/v1.0", updated.BaseBranch);
    }

    [Fact]
    public void TaskDependencyReferenceListYamlConverter_HandlesNullTildeEmptyAndFlowStyle()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        // 1. depends_on: null
        var mdNull = @"---
title: Task Null
depends_on: null
---
# Task Null
";
        var (fmNull, _) = sync.ParseMarkdown(mdNull);
        Assert.Null(fmNull.DependsOn);

        // 2. depends_on: ~
        var mdTilde = @"---
title: Task Tilde
depends_on: ~
---
# Task Tilde
";
        var (fmTilde, _) = sync.ParseMarkdown(mdTilde);
        Assert.Null(fmTilde.DependsOn);

        // 3. depends_on: []
        var mdEmptyFlow = @"---
title: Task Empty Flow
depends_on: []
---
# Task Empty Flow
";
        var (fmEmptyFlow, _) = sync.ParseMarkdown(mdEmptyFlow);
        Assert.NotNull(fmEmptyFlow.DependsOn);
        Assert.Empty(fmEmptyFlow.DependsOn);

        // 4. depends_on: [ 101, "task-b" ]
        var mdFlowMixed = @"---
title: Task Flow Mixed
depends_on: [ 101, ""task-b"" ]
---
# Task Flow Mixed
";
        var (fmFlowMixed, _) = sync.ParseMarkdown(mdFlowMixed);
        Assert.NotNull(fmFlowMixed.DependsOn);
        Assert.Equal(2, fmFlowMixed.DependsOn.Count);
        Assert.Equal(101, fmFlowMixed.DependsOn[0]);
        Assert.Equal("task-b", fmFlowMixed.DependsOn[1]);
    }

    [Fact]
    public void TaskDependencyReferenceList_CrossTypeMatchingAndStringEquality()
    {
        var list = new TaskDependencyReferenceList();
        list.Add(42);
        list.Add("task-feature");

#pragma warning disable xUnit2017 // Specifically testing TaskDependencyReferenceList.Contains cross-type matching
        // Cross-type Contains
        Assert.True(list.Contains("42"));
        Assert.True(list.Contains(42));
        Assert.True(list.Contains("TASK-FEATURE"));
#pragma warning restore xUnit2017

        // IndexOf & Remove
        Assert.Equal(0, list.IndexOf("42"));
        Assert.True(list.Remove("42"));
        Assert.Single(list);

        // Equals string should be false, not throwing
        Assert.False(list.Equals("not-a-list"));
    }

    [Fact]
    public async Task ResolveTargetFilePathAsync_ReusesExistingFile_EvenWhenNotMatchingDatePrefixSlugPattern()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = 1,
                Title = "Special Feature",
                Description = "Special",
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            },
            null, "Setup", "Init");

        var tasksDir = Path.Combine(_tempWorkspaceDir, TaskSyncConstants.DocsSegment, TaskSyncConstants.TasksSegment);
        Directory.CreateDirectory(tasksDir);

        // File does NOT match standard date-prefix pattern
        var customNamedFile = Path.Combine(tasksDir, "custom-named-task.md");
        var content = $@"---
id: {task.Id}
title: Special Feature
---
# Special Feature
## 📋 Requirements & Description
Special
";
        await File.WriteAllTextAsync(customNamedFile, content);

        // Syncing to disk should reuse custom-named-task.md and NOT create a new duplicate file
        var writtenPath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir);

        Assert.Equal(customNamedFile, writtenPath);

        var allFiles = Directory.GetFiles(tasksDir, "*.md");
        Assert.Single(allFiles);
    }
}
