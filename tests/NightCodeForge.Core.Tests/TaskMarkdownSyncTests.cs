using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Tests;

public class TaskMarkdownSyncTests : IDisposable
{
    private readonly string _tempWorkspaceDir;

    public TaskMarkdownSyncTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_Tests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup errors in tests
        }
    }

    private static TaskMarkdownSyncService CreateSyncService(ITaskRepository repo)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<ITaskRepository>(repo);
        var depStore = new NightCodeForge.Core.Dependencies.InMemoryTaskDependencyStore();
        services.AddSingleton<NightCodeForge.Core.Dependencies.ITaskDependencyStore>(depStore);
        services.AddSingleton<NightCodeForge.Core.Dependencies.IDependencyResolutionEngine>(new NightCodeForge.Core.Dependencies.DependencyResolutionEngine(depStore));
        var sp = services.BuildServiceProvider();
        return new TaskMarkdownSyncService(
            sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            new Mock<IWorkspacePathResolver>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMarkdownSyncService>.Instance);
    }

    [Fact]
    public void GenerateMarkdown_And_ParseMarkdown_RoundTripPreservesFrontmatter()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 42,
            Title = "Implement OAuth2 Flow",
            Description = "Full OAuth2 with PKCE implementation details.",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review",
            Priority = 2,
            BaseBranch = "main",
            FeatureBranch = "feature/task-42",
            CreatedAt = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc)
        };

        var md = sync.GenerateMarkdown(task, dependencies: new[] { 10, 20 }, agentProfileId: "deep-analyst");
        var (frontmatter, body) = sync.ParseMarkdown(md);

        Assert.Equal(42, frontmatter.Id);
        Assert.Equal("Implement OAuth2 Flow", frontmatter.Title);
        Assert.Equal("Open", frontmatter.Status);
        Assert.True(frontmatter.WaitingForHuman);
        Assert.Equal("task-review", frontmatter.CurrentStage);
        Assert.Equal(2, frontmatter.Priority);
        Assert.Equal("main", frontmatter.BaseBranch);
        Assert.Equal("feature/task-42", frontmatter.FeatureBranch);
        Assert.Equal(new[] { 10, 20 }, frontmatter.DependsOn);
        Assert.Equal("deep-analyst", frontmatter.AgentProfile);
        Assert.Contains("Full OAuth2 with PKCE", body);
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_CreatesDatedMarkdownFileAndHandlesSlugCollisions()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task1 = new ForgeTask
        {
            Id = 101,
            Title = "Refactor Database",
            Description = "First refactor",
            CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc)
        };
        var task2 = new ForgeTask
        {
            Id = 102,
            Title = "Refactor Database",
            Description = "Second refactor with identical title",
            CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc)
        };

        var path1 = await sync.SyncTaskToDiskAsync(task1, _tempWorkspaceDir);
        var path2 = await sync.SyncTaskToDiskAsync(task2, _tempWorkspaceDir);

        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
        Assert.NotEqual(path1, path2);

        Assert.EndsWith("2026-10-13-refactor-database.md", path1);
        Assert.EndsWith("2026-10-13-refactor-database-2.md", path2);
    }

    [Fact]
    public async Task ProcessFileContentAsync_ProtectsSystemManagedFieldsAndSyncsUserFields()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 200,
            WorkspaceId = 1,
            Title = "Original Title",
            Description = "Original body",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "implementation",
            Priority = 1
        };
        await repo.CreateAsync(task);

        var filePath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir);

        // Tampering with status throws TaskMarkdownSyncException per Step 9.16 fail-fast policy
        var tamperedMarkdown = @"---
id: 200
title: Updated User Title
status: Closed
current_stage: code-review-fix
priority: 5
base_branch: develop
---

# Updated User Title

## 📋 Requirements & Description
Updated user requirements on disk.
";

        await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(filePath, tamperedMarkdown, 1));

        // When status is omitted or matches current status, user fields sync correctly
        var validMarkdown = @"---
id: 200
title: Updated User Title
status: Implemented
current_stage: code-review-fix
priority: 5
base_branch: develop
---

# Updated User Title

## 📋 Requirements & Description
Updated user requirements on disk.
";

        await sync.ProcessFileContentAsync(filePath, validMarkdown, 1);

        // Verify DB was updated for editable fields
        var updated = await repo.GetByIdAsync(200);
        Assert.NotNull(updated);
        Assert.Equal("Updated User Title", updated.Title);
        Assert.Equal(5, updated.Priority);
        Assert.Equal("develop", updated.BaseBranch);
        Assert.Equal("Updated user requirements on disk.", updated.Description);

        // Verify protected fields were NOT modified in DB
        Assert.Equal(TaskOverallStatus.Implemented, updated.Status);
        Assert.Equal("implementation", updated.CurrentStageId);
    }

    [Fact]
    public async Task ProcessFileContentAsync_AllocatesIdForNewDropInFileWithoutId()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var dropInMarkdown = @"---
title: Brand New Feature
priority: 3
base_branch: main
---

# Brand New Feature

## 📋 Requirements & Description
Detailed spec of drop-in feature.
";

        var targetFile = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "2026-10-13-brand-new-feature.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await File.WriteAllTextAsync(targetFile, dropInMarkdown);

        await sync.ProcessFileContentAsync(targetFile, dropInMarkdown, workspaceId: 1);

        // Verify task created in DB
        var tasks = await repo.GetByWorkspaceIdAsync(1);
        Assert.Single(tasks);
        var created = tasks[0];
        Assert.True(created.Id > 0);
        Assert.Equal("Brand New Feature", created.Title);
        Assert.Equal("Detailed spec of drop-in feature.", created.Description);
        Assert.Equal(3, created.Priority);
        Assert.Equal(TaskOverallStatus.Suggested, created.Status);

        // Verify file rewritten with allocated ID in-place without renaming
        var diskContent = await File.ReadAllTextAsync(targetFile);
        var (fm, _) = sync.ParseMarkdown(diskContent);
        Assert.Equal(created.Id, fm.Id);
        Assert.NotNull(fm.CreatedAt);
    }

    [Fact]
    public async Task EchoLoopSuppression_IgnoresSelfTriggeredWrites()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Title = "Echo Test",
            Description = "Test echo suppression",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        var filePath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir);
        var writtenContent = await File.ReadAllTextAsync(filePath);

        // When SyncTaskToDiskAsync wrote the file, it registered its hash in _suppressedHashes.
        // Processing this exact content should be suppressed without making DB calls or modifications.
        await sync.ProcessFileContentAsync(filePath, writtenContent, 1);

        var dbTask = await repo.GetByIdAsync(301);
        Assert.NotNull(dbTask);
        Assert.Equal("Echo Test", dbTask.Title);
    }

    [Fact]
    public async Task ParseMarkdown_ExtractDescriptionFromBody_PreservesArchitectureAndAcceptanceCriteriaSections()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var markdown = @"---
id: 50
title: Refactor Payment Gateway
---

# Refactor Payment Gateway

## 📋 Requirements & Description
Support Stripe and PayPal with unified interface.

## 🏛️ Architecture & Implementation Plan
1. Create `IPaymentGateway` interface.
2. Implement `StripeGateway` and `PayPalGateway`.
3. Add factory resolution in DI.

## 🧪 Acceptance Criteria
- [ ] 100% test coverage for `StripeGateway`.
- [ ] Graceful fallback on network timeout.

## ❓ Human Clarifications (Q&A)
### Question 1 (Asked during Stage: task-review on 2026-10-14 10:00:00)
**Prompt:** Should we support crypto?
**Answer:** No.
";

        var (fm, body) = sync.ParseMarkdown(markdown);
        Assert.Equal(50, fm.Id);

        // Process file content to simulate disk ingest
        await sync.ProcessFileContentAsync("dummy.md", markdown, 1);

        // Check that description extracted preserves architecture and acceptance criteria but omits human clarifications
        var (descFm, descBody) = sync.ParseMarkdown(markdown);
        Assert.Equal("Refactor Payment Gateway", descFm.Title);

        // Verify description extraction by calling ProcessFileContent on repo task
        var task = new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = "Refactor Payment Gateway",
            Description = "Old description"
        };
        await repo.CreateAsync(task);

        await sync.ProcessFileContentAsync("dummy.md", markdown, 1);

        var updated = await repo.GetByIdAsync(50);
        Assert.NotNull(updated);
        Assert.Contains("Support Stripe and PayPal with unified interface.", updated.Description);
        Assert.Contains("## 🏛️ Architecture & Implementation Plan", updated.Description);
        Assert.Contains("1. Create `IPaymentGateway` interface.", updated.Description);
        Assert.Contains("## 🧪 Acceptance Criteria", updated.Description);
        Assert.Contains("- [ ] 100% test coverage for `StripeGateway`.", updated.Description);
        Assert.DoesNotContain("Human Clarifications", updated.Description);
        Assert.DoesNotContain("Should we support crypto?", updated.Description);
    }

    [Fact]
    public async Task ProcessFileChangedAsync_WithExpectedTaskId_UpdatesExistingTaskWhenIdOmittedInFrontmatter()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Original ForgeTask Title",
            Description = "Initial requirement",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        // Agent accidentally stripped 'id: 88' from YAML frontmatter
        var editedMarkdownWithoutId = @"---
title: Enriched Title Without ID
status: Open
---

# Enriched Title Without ID

## 📋 Requirements & Description
Enriched description formulated by review agent directly on disk.
";

        var filePath = Path.Combine(_tempWorkspaceDir, "2026-10-14-enriched.md");
        await File.WriteAllTextAsync(filePath, editedMarkdownWithoutId);

        // Pass expectedTaskId: 88
        await sync.ProcessFileChangedAsync(filePath, 1, expectedTaskId: 88);

        // Verify task 88 was updated and NO duplicate task was created
        var updated = await repo.GetByIdAsync(88);
        Assert.NotNull(updated);
        Assert.Equal("Enriched Title Without ID", updated.Title);
        Assert.Equal("Enriched description formulated by review agent directly on disk.", updated.Description);

        // Verify no extra task was created
        var allTasks = await repo.GetByWorkspaceIdAsync(1);
        Assert.Single(allTasks);
    }

    [Fact]
    public void GenerateMarkdown_StrictlyEmitsLfLineEndings()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 55,
            Title = "Cross-Platform Hygiene Task",
            Description = "Ensure pure LF line endings across Windows and Linux environments.",
            Status = TaskOverallStatus.Open
        };

        var markdown = sync.GenerateMarkdown(task);

        Assert.DoesNotContain("\r\n", markdown);
        Assert.Contains("\n", markdown);
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WritesPureLfContent_ToDisk()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 56,
            Title = "Disk LF Task",
            Description = "Writing to disk must preserve strict LF endings.\r\nEven if input has CRLF.",
            Status = TaskOverallStatus.Open
        };

        var path = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir);

        Assert.True(File.Exists(path));
        var rawBytes = await File.ReadAllBytesAsync(path);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain("\r\n", rawText);
        Assert.Contains("\n", rawText);
    }

    [Theory]
    [InlineData(null, false, 0, 0, 0)]
    [InlineData("", false, 0, 0, 0)]
    [InlineData("   ", false, 0, 0, 0)]
    [InlineData("my-task.md", false, 0, 0, 0)]
    [InlineData("2026-10-task.md", false, 0, 0, 0)]
    [InlineData("2026-99-99-task.md", false, 0, 0, 0)]
    [InlineData("2026-10-20-task-alpha.md", true, 2026, 10, 20)]
    [InlineData("docs/tasks/2026-10-20-task-alpha.md", true, 2026, 10, 20)]
    [InlineData("C:\\repo\\docs\\tasks\\2026-10-20-task-alpha.md", true, 2026, 10, 20)]
    public void TryExtractDateFromPath_ExtractsDateCorrectly(
        string? path,
        bool shouldBeValid,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var result = TaskMarkdownSyncService.TryExtractDateFromPath(path);
        if (!shouldBeValid)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(DateTimeKind.Utc, result.Value.Kind);
            Assert.Equal(new DateTime(expectedYear, expectedMonth, expectedDay, 0, 0, 0, DateTimeKind.Utc), result.Value);
        }
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WithCloneDirectory_MirrorsToCloneDocsTasksFolder()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Clone Sync Test Task",
            Description = "Sync to both host repo and isolated clone",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(cloneDir);

        var hostFilePath = await sync.SyncTaskToDiskAsync(
            task,
            _tempWorkspaceDir,
            dependencies: null,
            agentProfileId: null,
            cloneDirectory: cloneDir);

        Assert.True(File.Exists(hostFilePath));

        var cloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        Assert.True(Directory.Exists(cloneTasksDir));

        var cloneFilePath = Path.Combine(cloneTasksDir, Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(cloneFilePath));

        var hostContent = await File.ReadAllTextAsync(hostFilePath);
        var cloneContent = await File.ReadAllTextAsync(cloneFilePath);
        Assert.Equal(hostContent, cloneContent);
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_WithCloneDirectory_MirrorsAllTasksToCloneDocsTasksFolder()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task1 = new ForgeTask { Id = 601, WorkspaceId = 1, Title = "Task 1", Status = TaskOverallStatus.Open };
        var task2 = new ForgeTask { Id = 602, WorkspaceId = 1, Title = "Task 2", Status = TaskOverallStatus.Suggested };
        await repo.CreateAsync(task1);
        await repo.CreateAsync(task2);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(cloneDir);

        var workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = _tempWorkspaceDir };

        var result = await sync.SyncAllTasksToDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: _tempWorkspaceDir,
            cloneDirectory: cloneDir);

        Assert.Equal(2, result.WrittenPaths.Count);

        var cloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        Assert.True(Directory.Exists(cloneTasksDir));

        var cloneFiles = Directory.GetFiles(cloneTasksDir, "*.md");
        Assert.Equal(2, cloneFiles.Length);
    }

    [Fact]
    public async Task ArchiveOrCleanupTaskFileAsync_WithCloneDirectory_CleansUpCloneFile()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 701,
            WorkspaceId = 1,
            Title = "Task to Archive",
            Status = TaskOverallStatus.Closed
        };
        await repo.CreateAsync(task);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(cloneDir);

        var hostFilePath = await sync.SyncTaskToDiskAsync(
            task,
            _tempWorkspaceDir,
            dependencies: null,
            agentProfileId: null,
            cloneDirectory: cloneDir);

        var cloneTasksArchiveDir = Path.Combine(cloneDir, "docs", "tasks", "archive");
        var cloneFilePath = Path.Combine(cloneTasksArchiveDir, Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(hostFilePath));
        Assert.True(File.Exists(cloneFilePath));

        // Archive or cleanup
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempWorkspaceDir, archive: false, cloneDirectory: cloneDir);

        Assert.False(File.Exists(hostFilePath));
        Assert.False(File.Exists(cloneFilePath));
    }

    [Fact]
    public async Task AllDiskWrites_EmitUtf8WithoutBom()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "NoBomRepo", RepositoryPath = _tempWorkspaceDir };

        var task = new ForgeTask
        {
            Id = 970,
            WorkspaceId = 1,
            Title = "UTF8 Without BOM Task",
            Description = "Verify no 0xEF 0xBB 0xBF preamble is emitted.",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone-nobom");
        Directory.CreateDirectory(cloneDir);

        // 1. SyncTaskToDiskAsync
        var singlePath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: cloneDir);
        AssertHasNoUtf8Bom(singlePath);
        var cloneSinglePath = Path.Combine(cloneDir, "docs", "tasks", Path.GetFileName(singlePath));
        AssertHasNoUtf8Bom(cloneSinglePath);

        // 2. SyncAllTasksToDiskAsync
        var hostDir = Path.Combine(_tempWorkspaceDir, "host-nobom");
        Directory.CreateDirectory(hostDir);
        var syncAllResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, hostDir, cloneDir);
        AssertHasNoUtf8Bom(syncAllResult.WrittenPaths[970]);
        AssertHasNoUtf8Bom(Path.Combine(hostDir, "docs", "tasks", Path.GetFileName(syncAllResult.WrittenPaths[970])));

        // 3. ProcessFileContentAsync (new drop-in task & existing task normalization)
        var dropInPath = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "2026-10-28-drop-in-nobom.md");
        await File.WriteAllTextAsync(dropInPath, "---\ntitle: Drop In No BOM\n---\n# Drop In No BOM\nBody text\n", new System.Text.UTF8Encoding(false));
        await sync.ProcessFileChangedAsync(dropInPath, workspace.Id);
        AssertHasNoUtf8Bom(dropInPath);

        // 4. SyncAllTasksFromDiskAsync (new task creation & existing task normalization)
        var batchNewPath = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "batch-new-nobom.md");
        await File.WriteAllTextAsync(batchNewPath, "---\ntitle: Batch New No BOM\n---\n# Batch New No BOM\nBody\n", new System.Text.UTF8Encoding(false));
        await sync.SyncAllTasksFromDiskAsync(workspace, _tempWorkspaceDir, hostDir, null, AuditActors.User, "No BOM test");
        foreach (var file in Directory.GetFiles(Path.Combine(_tempWorkspaceDir, "docs", "tasks"), "*.md"))
        {
            AssertHasNoUtf8Bom(file);
        }
    }

    [Fact]
    public async Task TerminalTasks_AreWrittenOnlyToArchive_AndCleanedUpFromActiveTasksFolder()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "ArchivePlacementRepo", RepositoryPath = _tempWorkspaceDir };

        var taskClosed = new ForgeTask { Id = 981, WorkspaceId = 1, Title = "Task Closed", Status = TaskOverallStatus.Open };
        var taskRejected = new ForgeTask { Id = 982, WorkspaceId = 1, Title = "Task Rejected", Status = TaskOverallStatus.Suggested };
        var taskCancelled = new ForgeTask { Id = 983, WorkspaceId = 1, Title = "Task Cancelled", Status = TaskOverallStatus.Open };
        await repo.CreateWithBaselineAsync(taskClosed, null, AuditActors.User, "Init");
        await repo.CreateWithBaselineAsync(taskRejected, null, AuditActors.User, "Init");
        await repo.CreateWithBaselineAsync(taskCancelled, null, AuditActors.User, "Init");

        var hostDir = Path.Combine(_tempWorkspaceDir, "host-archive-test");
        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone-archive-test");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(cloneDir);

        // Initially active -> written to docs/tasks
        var initialResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, hostDir, cloneDir);
        var activeDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        var archiveDir = Path.Combine(activeDir, "archive");
        Assert.Equal(3, Directory.GetFiles(activeDir, "*.md").Length);

        // Transition all three to terminal statuses
        taskClosed.Status = TaskOverallStatus.Closed;
        taskRejected.Status = TaskOverallStatus.Rejected;
        taskCancelled.Status = TaskOverallStatus.Cancelled;
        await repo.UpdateWithHistoryAsync(taskClosed, null, AuditActors.User, "Close");
        await repo.UpdateWithHistoryAsync(taskRejected, null, AuditActors.User, "Reject");
        await repo.UpdateWithHistoryAsync(taskCancelled, null, AuditActors.User, "Cancel");

        // SyncAllTasksToDiskAsync must move them to docs/tasks/archive and leave 0 files in docs/tasks
        var afterTerminal = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, hostDir, cloneDir);
        Assert.Empty(Directory.GetFiles(activeDir, "*.md"));
        Assert.Equal(3, Directory.GetFiles(archiveDir, "*.md").Length);

        Assert.Empty(Directory.GetFiles(Path.Combine(hostDir, "docs", "tasks"), "*.md"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(hostDir, "docs", "tasks", "archive"), "*.md").Length);

        Assert.Empty(Directory.GetFiles(Path.Combine(cloneDir, "docs", "tasks"), "*.md"));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(cloneDir, "docs", "tasks", "archive"), "*.md").Length);

        // Reopening a cancelled task moves it back to docs/tasks and removes it from docs/tasks/archive
        taskCancelled.Status = TaskOverallStatus.Open;
        await repo.UpdateWithHistoryAsync(taskCancelled, null, AuditActors.User, "Reopen");
        await sync.SyncTaskToDiskAsync(taskCancelled, _tempWorkspaceDir, dependencies: null, agentProfileId: null, cloneDirectory: cloneDir);

        Assert.Single(Directory.GetFiles(activeDir, "*.md"));
        Assert.Equal(2, Directory.GetFiles(archiveDir, "*.md").Length);
        Assert.Single(Directory.GetFiles(Path.Combine(cloneDir, "docs", "tasks"), "*.md"));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cloneDir, "docs", "tasks", "archive"), "*.md").Length);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_AutoCleansStaleLowerVersionDuplicateFile()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "StaleDupRepo", RepositoryPath = _tempWorkspaceDir };

        var task = new ForgeTask
        {
            Id = 990,
            WorkspaceId = 1,
            Title = "Original Short Title",
            Description = "Initial spec",
            Status = TaskOverallStatus.Suggested,
            Version = 1,
            CreatedAt = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);

        // Write version 1 under old slug
        var oldSlugFile = Path.Combine(tasksDir, "2026-10-16-original-short-title.md");
        await File.WriteAllTextAsync(oldSlugFile, sync.GenerateMarkdown(task));

        // Update task to version 2 with new title and write under new slug (simulating leftover v1 file)
        task.Title = "Refined Detailed Title After Review";
        task.Status = TaskOverallStatus.Open;
        await repo.UpdateWithHistoryAsync(task, null, AuditActors.User, "Refined");
        var newSlugFile = Path.Combine(tasksDir, "2026-10-16-refined-detailed-title-after-review.md");
        await File.WriteAllTextAsync(newSlugFile, sync.GenerateMarkdown(task));

        Assert.True(File.Exists(oldSlugFile));
        Assert.True(File.Exists(newSlugFile));

        // SyncAllTasksFromDiskAsync must auto-clean the untouched v1 duplicate and keep v2
        var synced = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: AuditActors.User,
            changeReason: "Startup sync");

        Assert.Single(synced);
        Assert.False(File.Exists(oldSlugFile));
        Assert.True(File.Exists(newSlugFile));
    }

    private static void AssertHasNoUtf8Bom(string filePath)
    {
        Assert.True(File.Exists(filePath), $"Expected file '{filePath}' to exist.");
        var bytes = File.ReadAllBytes(filePath);
        if (bytes.Length >= 3)
        {
            Assert.False(
                bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"File '{filePath}' starts with UTF-8 BOM (0xEF 0xBB 0xBF), expected UTF-8 without BOM.");
        }
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WithoutExplicitCloneDirectory_ResolvesSiblingCloneFromWorkspacePath()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var repoDir = Path.Combine(_tempWorkspaceDir, "repo");
        var siblingClone = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(siblingClone);

        var task = new ForgeTask
        {
            Id = 801,
            WorkspaceId = 1,
            Title = "Sibling Clone Task",
            Description = "Syncs without explicit clone directory",
            Status = TaskOverallStatus.Open,
            Workspace = null // Navigation property is null, mimicking real repository query
        };
        await repo.CreateAsync(task);

        // Call SyncTaskToDiskAsync without passing cloneDirectory
        var hostFilePath = await sync.SyncTaskToDiskAsync(task, repoDir);
        Assert.True(File.Exists(hostFilePath));

        var cloneTasksDir = Path.Combine(siblingClone, "docs", "tasks");
        Assert.True(Directory.Exists(cloneTasksDir));

        var cloneFilePath = Path.Combine(cloneTasksDir, Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(cloneFilePath));

        var hostContent = await File.ReadAllTextAsync(hostFilePath);
        var cloneContent = await File.ReadAllTextAsync(cloneFilePath);
        Assert.Equal(hostContent, cloneContent);
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_MatchesExactHostFilename_WhenTaskHasSuffix()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task1 = new ForgeTask { Id = 901, WorkspaceId = 1, Title = "Same Name Task", Status = TaskOverallStatus.Open };
        var task2 = new ForgeTask { Id = 902, WorkspaceId = 1, Title = "Same Name Task", Status = TaskOverallStatus.Open };
        await repo.CreateAsync(task1);
        await repo.CreateAsync(task2);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(cloneDir);

        var workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = _tempWorkspaceDir };

        var result = await sync.SyncAllTasksToDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: _tempWorkspaceDir,
            cloneDirectory: cloneDir);

        Assert.Equal(2, result.WrittenPaths.Count);

        var hostFile1 = result.WrittenPaths[901];
        var hostFile2 = result.WrittenPaths[902];

        var cloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        var cloneFile1 = Path.Combine(cloneTasksDir, Path.GetFileName(hostFile1));
        var cloneFile2 = Path.Combine(cloneTasksDir, Path.GetFileName(hostFile2));

        Assert.True(File.Exists(cloneFile1), $"Clone file 1 should exist at {cloneFile1}");
        Assert.True(File.Exists(cloneFile2), $"Clone file 2 should exist with matching suffix at {cloneFile2}");
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WithCloneDirectoryEndingInTasks_CorrectlyAppendsDocsTasks()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 910,
            WorkspaceId = 1,
            Title = "Task in Folder Ending in Tasks",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        // Path ends in the word "tasks", but is NOT a docs/tasks directory
        var cloneDir = Path.Combine(_tempWorkspaceDir, "worker-tasks");
        Directory.CreateDirectory(cloneDir);

        var hostFilePath = await sync.SyncTaskToDiskAsync(
            task,
            _tempWorkspaceDir,
            dependencies: null,
            agentProfileId: null,
            cloneDirectory: cloneDir);

        var expectedCloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        Assert.True(Directory.Exists(expectedCloneTasksDir), "docs/tasks subfolder should have been created inside worker-tasks");

        var cloneFilePath = Path.Combine(expectedCloneTasksDir, Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(cloneFilePath), $"Clone task should be placed in docs/tasks, not root of worker-tasks at {cloneFilePath}");

        var incorrectRootPlacement = Path.Combine(cloneDir, Path.GetFileName(hostFilePath));
        Assert.False(File.Exists(incorrectRootPlacement), "Task file should not be placed directly in worker-tasks root");
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WithCloneDirectoryHavingTrailingSlash_DoesNotDoubleNestDocsTasks()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 920,
            WorkspaceId = 1,
            Title = "Task with Trailing Slash Clone",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone-trailing");
        var cloneDocsTasks = Path.Combine(cloneDir, "docs", "tasks") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(cloneDocsTasks);

        var hostFilePath = await sync.SyncTaskToDiskAsync(
            task,
            _tempWorkspaceDir,
            dependencies: null,
            agentProfileId: null,
            cloneDirectory: cloneDocsTasks);

        var expectedFile = Path.Combine(cloneDir, "docs", "tasks", Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(expectedFile), $"File should exist at {expectedFile}");

        var doubleNestedDir = Path.Combine(cloneDir, "docs", "tasks", "docs", "tasks");
        Assert.False(Directory.Exists(doubleNestedDir), "Trailing slash must not cause docs/tasks/docs/tasks double nesting");
    }

    [Fact]
    public async Task SyncAllTasksToDiskAsync_WhenOldCloneFileHasDivergentName_DeletesOldCloneFileAndWritesMatchingHostFile()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var task = new ForgeTask
        {
            Id = 930,
            WorkspaceId = 1,
            Title = "Renamed Task",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task);

        var cloneDir = Path.Combine(_tempWorkspaceDir, "clone-stale-cleanup");
        var cloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        Directory.CreateDirectory(cloneTasksDir);

        // Pre-create stale file with old slug in clone directory
        var staleCloneFile = Path.Combine(cloneTasksDir, "2026-10-18-outdated-slug.md");
        var staleFrontmatter = "---\nid: 930\ntitle: Old Outdated Title\nstatus: Open\n---\n# Old Outdated Title\n";
        await File.WriteAllTextAsync(staleCloneFile, staleFrontmatter);

        var workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = _tempWorkspaceDir };

        var result = await sync.SyncAllTasksToDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: _tempWorkspaceDir,
            cloneDirectory: cloneDir);

        var hostFile = result.WrittenPaths[930];
        var matchingCloneFile = Path.Combine(cloneTasksDir, Path.GetFileName(hostFile));

        Assert.True(File.Exists(matchingCloneFile), $"New matching clone file must exist at {matchingCloneFile}");
        Assert.False(File.Exists(staleCloneFile), $"Stale clone file with divergent name {staleCloneFile} must have been deleted");
    }

    [Fact]
    public async Task ResolveCloneTasksDirectoryAsync_WhenWorkspacePathIsNotWorktree_DoesNotMatchSiblingFolder()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var customRepoDir = Path.Combine(_tempWorkspaceDir, "MyCustomProject");
        var siblingClone = Path.Combine(_tempWorkspaceDir, "clone");
        Directory.CreateDirectory(customRepoDir);
        Directory.CreateDirectory(siblingClone);

        var workspace = new Workspace
        {
            Id = 940,
            Name = "MyCustomProject",
            RepositoryPath = customRepoDir,
            Ext4MirrorPath = null
        };

        var resolved = await sync.ResolveCloneTasksDirectoryAsync(
            workspaceId: 940,
            workspace: workspace,
            workspacePath: customRepoDir);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenBaselineUnchanged_DoesNotOverwriteDatabaseMergeMode()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var workspace = new Workspace
        {
            Id = 950,
            Name = "SyncBaselineRepo",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main"
        };

        var task = new ForgeTask
        {
            Id = 951,
            WorkspaceId = workspace.Id,
            Title = "Task 951",
            Description = "Baseline description",
            Priority = 2,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        // Sync to disk to create initial markdown and snapshot
        var syncResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir, _tempWorkspaceDir);
        var snapshot = syncResult.Baseline;

        // User concurrently updates task MergeMode to Automatic in the DB
        task.MergeMode = MergeMode.Automatic;
        await repo.UpdateWithHistoryAsync(task, null, AuditActors.User, "User enabled auto-merge");

        // Now post-stage disk sync runs against the worktree where markdown was unchanged
        var refreshedTasks = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            _tempWorkspaceDir,
            snapshot,
            AuditActors.GetActorForStage("implementation"),
            "Stage completed disk synchronization");

        // The database task MergeMode must remain Automatic!
        var dbTask = await repo.GetByIdAsync(951);
        Assert.NotNull(dbTask);
        Assert.Equal(MergeMode.Automatic, dbTask.MergeMode);

        var refreshed = refreshedTasks.FirstOrDefault(t => t.Id == 951);
        Assert.NotNull(refreshed);
        Assert.Equal(MergeMode.Automatic, refreshed.MergeMode);
    }

    [Fact]
    public async Task ProcessFileContentAsync_WhenFrontmatterMergeModeMissing_PreservesDatabaseMergeMode()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var workspace = new Workspace
        {
            Id = 960,
            Name = "PreserveMergeModeRepo",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main"
        };

        var task = new ForgeTask
        {
            Id = 961,
            WorkspaceId = workspace.Id,
            Title = "Task 961",
            Description = "Task description",
            Priority = 2,
            MergeMode = MergeMode.Automatic,
            Status = TaskOverallStatus.Open
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        // Markdown content with NO merge_mode property
        var markdownWithoutMergeMode = @"---
id: 961
title: Task 961 Updated Title
priority: 2
---

Task description with updated content.";

        var filePath = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "task-961.md");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, markdownWithoutMergeMode);

        await sync.ProcessFileChangedAsync(filePath, workspace.Id);

        var dbTask = await repo.GetByIdAsync(961);
        Assert.NotNull(dbTask);
        Assert.Equal(MergeMode.Automatic, dbTask.MergeMode);
        Assert.Equal("Task 961 Updated Title", dbTask.Title);
    }

    [Fact]
    public async Task SyncTaskToDiskAsync_WhenArchivingTaskWithStaleFileInArchive_UsesNewerFilenameFromActiveDirAndCleansBoth()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        var archiveDir = Path.Combine(tasksDir, "archive");
        Directory.CreateDirectory(tasksDir);
        Directory.CreateDirectory(archiveDir);

        var task = new ForgeTask
        {
            Id = 991,
            WorkspaceId = 1,
            Title = "Updated Title For Task 991",
            Description = "Final description",
            Status = TaskOverallStatus.Closed,
            Version = 3,
            CreatedAt = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        // Stale v1 file under old slug left in archive/
        var staleArchiveFile = Path.Combine(archiveDir, "2026-10-20-old-stale-slug.md");
        await File.WriteAllTextAsync(staleArchiveFile, "---\nid: 991\ntitle: Old Stale Slug\nstatus: Cancelled\nversion: 1\n---\n# Old Stale Slug\n");

        // Newer v2 file under current slug in docs/tasks/
        var activeFile = Path.Combine(tasksDir, "2026-10-20-updated-title-for-task-991.md");
        await File.WriteAllTextAsync(activeFile, "---\nid: 991\ntitle: Updated Title For Task 991\nstatus: Open\nversion: 2\n---\n# Updated Title For Task 991\n");

        var writtenPath = await sync.SyncTaskToDiskAsync(task, _tempWorkspaceDir);

        var expectedArchivePath = Path.Combine(archiveDir, "2026-10-20-updated-title-for-task-991.md");
        Assert.Equal(expectedArchivePath, writtenPath);
        Assert.True(File.Exists(expectedArchivePath));
        Assert.False(File.Exists(staleArchiveFile), "Stale old-slug file in archive must be deleted");
        Assert.False(File.Exists(activeFile), "Active file in docs/tasks must be removed when task is archived");
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenAgentCopiesAndEditsFileAtSameVersion_AutoCleansUntouchedOriginal()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "SameVersionRenameRepo", RepositoryPath = _tempWorkspaceDir };

        var task = new ForgeTask
        {
            Id = 992,
            WorkspaceId = 1,
            Title = "Initial Draft Title",
            Description = "Initial draft body",
            Status = TaskOverallStatus.Suggested,
            Version = 1,
            CreatedAt = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        var initialSync = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir);
        var originalFile = initialSync.WrittenPaths[992];

        // Simulate agent copying the file to a new slug and editing the title/body in the copy while leaving original untouched (both have version: 1)
        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        var renamedFile = Path.Combine(tasksDir, "2026-10-21-refined-agent-title.md");
        var modifiedContent = (await File.ReadAllTextAsync(originalFile))
            .Replace("title: Initial Draft Title", "title: Refined Agent Title")
            .Replace("# Initial Draft Title", "# Refined Agent Title")
            .Replace("Initial draft body", "Refined body by agent");
        await File.WriteAllTextAsync(renamedFile, modifiedContent);

        var synced = await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: initialSync.Baseline,
            changeActor: "task-review",
            changeReason: "Refined task");

        Assert.Single(synced);
        Assert.False(File.Exists(originalFile), "Untouched original file should be auto-cleaned when agent writes modified copy");
        Assert.True(File.Exists(renamedFile), "Modified renamed file should be kept");

        var dbTask = await repo.GetByIdAsync(992);
        Assert.NotNull(dbTask);
        Assert.Equal("Refined Agent Title", dbTask.Title);
        Assert.Equal("Refined body by agent", dbTask.Description);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_And_ProcessFileContentAsync_ResolveFilenameDependencyOnArchivedTask()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "ArchivedDepRepo", RepositoryPath = _tempWorkspaceDir };

        var closedDepTask = new ForgeTask
        {
            Id = 993,
            WorkspaceId = 1,
            Title = "Completed Prerequisite Task",
            Status = TaskOverallStatus.Closed,
            CreatedAt = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateWithBaselineAsync(closedDepTask, null, AuditActors.User, "Created");

        // Write closed prerequisite task to docs/tasks/archive/ under a custom filename
        var archiveDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks", "archive");
        Directory.CreateDirectory(archiveDir);
        var archivedDepFile = Path.Combine(archiveDir, "2026-10-22-custom-archived-prereq.md");
        await File.WriteAllTextAsync(archivedDepFile, sync.GenerateMarkdown(closedDepTask));

        // Write open dependent task in docs/tasks/ referencing the archived file by filename
        var tasksDir = Path.Combine(_tempWorkspaceDir, "docs", "tasks");
        Directory.CreateDirectory(tasksDir);
        var dependentFile = Path.Combine(tasksDir, "2026-10-22-dependent-task.md");
        var dependentMarkdown = @"---
title: Dependent Task
status: Open
depends_on:
  - 2026-10-22-custom-archived-prereq.md
---

# Dependent Task

## 📋 Requirements & Description
Depends on archived prerequisite
";
        await File.WriteAllTextAsync(dependentFile, dependentMarkdown);

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: AuditActors.User,
            changeReason: "Sync dependent task");

        var allTasks = await repo.GetByWorkspaceIdAsync(workspace.Id);
        var createdDepTask = allTasks.FirstOrDefault(t => t.Title == "Dependent Task");
        Assert.NotNull(createdDepTask);

        var deps = await repo.GetDependenciesAsync(createdDepTask.Id);
        Assert.Equal([993], deps);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_WhenHostRepositoryPathProvided_WritesArchivedTaskToHostArchiveAndCleansHostActiveDir()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "HostArchiveSyncRepo", RepositoryPath = _tempWorkspaceDir };

        var hostDir = Path.Combine(_tempWorkspaceDir, "host-repo");
        var worktreeDir = Path.Combine(_tempWorkspaceDir, "worktree-repo");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(worktreeDir);

        var task = new ForgeTask
        {
            Id = 994,
            WorkspaceId = 1,
            Title = "Task Closing On Disk",
            Description = "Initial open description",
            Status = TaskOverallStatus.Open,
            Version = 1,
            CreatedAt = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc)
        };
        await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created");

        var syncToDisk = await sync.SyncAllTasksToDiskAsync(workspace, worktreeDir, hostRepositoryPath: hostDir);
        var worktreeActiveFile = syncToDisk.WrittenPaths[994];
        var hostActiveFile = Path.Combine(hostDir, "docs", "tasks", Path.GetFileName(worktreeActiveFile));
        Assert.True(File.Exists(worktreeActiveFile));
        Assert.True(File.Exists(hostActiveFile));

        // Task transitions to Closed in DB (e.g. sibling task closed while worktree still has the active file on disk)
        task.Status = TaskOverallStatus.Closed;
        await repo.UpdateWithHistoryAsync(task, null, AuditActors.User, "Closed in DB");

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            worktreeDir,
            hostRepositoryPath: hostDir,
            baselineSnapshot: syncToDisk.Baseline,
            changeActor: "implementation",
            changeReason: "Task closed");

        var worktreeArchiveFile = Path.Combine(worktreeDir, "docs", "tasks", "archive", Path.GetFileName(worktreeActiveFile));
        var hostArchiveFile = Path.Combine(hostDir, "docs", "tasks", "archive", Path.GetFileName(worktreeActiveFile));

        Assert.False(File.Exists(worktreeActiveFile), "Active file in worktree docs/tasks must be removed when closed");
        Assert.True(File.Exists(worktreeArchiveFile), "Archive file in worktree docs/tasks/archive must exist");
        Assert.False(File.Exists(hostActiveFile), "Active file in host docs/tasks must be removed when closed");
        Assert.True(File.Exists(hostArchiveFile), "Archive file in host docs/tasks/archive must exist");
    }

    [Fact]
    public void ParseMarkdown_And_ComputeContentHash_IgnoreLeadingBomCharacterInMemory()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateSyncService(repo);

        var raw = "---\nid: 50\ntitle: BOM Test\nstatus: Open\n---\n\n# BOM Test\n\n## 📋 Requirements & Description\nHello world\n";
        var withBom = "\uFEFF" + raw;

        var parsedRaw = sync.ParseMarkdown(raw);
        var parsedBom = sync.ParseMarkdown(withBom);

        Assert.Equal(50, parsedBom.Frontmatter.Id);
        Assert.Equal("BOM Test", parsedBom.Frontmatter.Title);
        Assert.Equal(parsedRaw.Body, parsedBom.Body);
        Assert.Equal(sync.ComputeContentHash(raw), sync.ComputeContentHash(withBom));
    }
}

