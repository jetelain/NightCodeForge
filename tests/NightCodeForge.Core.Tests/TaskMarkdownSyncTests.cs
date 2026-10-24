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

        var cloneTasksDir = Path.Combine(cloneDir, "docs", "tasks");
        var cloneFilePath = Path.Combine(cloneTasksDir, Path.GetFileName(hostFilePath));
        Assert.True(File.Exists(hostFilePath));
        Assert.True(File.Exists(cloneFilePath));

        // Archive or cleanup
        await sync.ArchiveOrCleanupTaskFileAsync(task, _tempWorkspaceDir, archive: false, cloneDirectory: cloneDir);

        Assert.False(File.Exists(hostFilePath));
        Assert.False(File.Exists(cloneFilePath));
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
}

