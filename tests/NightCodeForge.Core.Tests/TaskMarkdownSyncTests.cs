using NightCodeForge.Core.Domain;
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

    [Fact]
    public void GenerateMarkdown_And_ParseMarkdown_RoundTripPreservesFrontmatter()
    {
        var repo = new InMemoryTaskRepository();
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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

        // Simulate external edit: user changes Title, but also tampers with protected Status and Stage
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

        await sync.ProcessFileContentAsync(filePath, tamperedMarkdown, 1);

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

        // Verify file on disk was rewritten to restore protected fields
        var diskContent = await File.ReadAllTextAsync(filePath);
        var (restoredFm, _) = sync.ParseMarkdown(diskContent);
        Assert.Equal("Implemented", restoredFm.Status);
        Assert.Equal("implementation", restoredFm.CurrentStage);
        Assert.Equal("Updated User Title", restoredFm.Title);
    }

    [Fact]
    public async Task ProcessFileContentAsync_AllocatesIdForNewDropInFileWithoutId()
    {
        var repo = new InMemoryTaskRepository();
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
        var sync = new TaskMarkdownSyncService(repo);

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
}
