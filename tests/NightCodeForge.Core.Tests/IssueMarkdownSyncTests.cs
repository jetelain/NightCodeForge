using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Core.Tests;

public class IssueMarkdownSyncTests : IDisposable
{
    private readonly string _tempWorkspaceDir;

    public IssueMarkdownSyncTests()
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
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue = new Issue
        {
            Id = 42,
            Title = "Implement OAuth2 Flow",
            Description = "Full OAuth2 with PKCE implementation details.",
            Status = IssueOverallStatus.BlockedQuestion,
            CurrentStageId = "issue-review",
            Priority = 2,
            BaseBranch = "main",
            FeatureBranch = "feature/issue-42",
            CreatedAt = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc)
        };

        var md = sync.GenerateMarkdown(issue, dependencies: new[] { 10, 20 }, agentProfileId: "deep-analyst");
        var (frontmatter, body) = sync.ParseMarkdown(md);

        Assert.Equal(42, frontmatter.Id);
        Assert.Equal("Implement OAuth2 Flow", frontmatter.Title);
        Assert.Equal("BlockedQuestion", frontmatter.Status);
        Assert.Equal("issue-review", frontmatter.CurrentStage);
        Assert.Equal(2, frontmatter.Priority);
        Assert.Equal("main", frontmatter.BaseBranch);
        Assert.Equal("feature/issue-42", frontmatter.FeatureBranch);
        Assert.Equal(new[] { 10, 20 }, frontmatter.DependsOn);
        Assert.Equal("deep-analyst", frontmatter.AgentProfile);
        Assert.Contains("Full OAuth2 with PKCE", body);
    }

    [Fact]
    public async Task SyncIssueToDiskAsync_CreatesDatedMarkdownFileAndHandlesSlugCollisions()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue1 = new Issue
        {
            Id = 101,
            Title = "Refactor Database",
            Description = "First refactor",
            CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc)
        };
        var issue2 = new Issue
        {
            Id = 102,
            Title = "Refactor Database",
            Description = "Second refactor with identical title",
            CreatedAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc)
        };

        var path1 = await sync.SyncIssueToDiskAsync(issue1, _tempWorkspaceDir);
        var path2 = await sync.SyncIssueToDiskAsync(issue2, _tempWorkspaceDir);

        Assert.True(File.Exists(path1));
        Assert.True(File.Exists(path2));
        Assert.NotEqual(path1, path2);

        Assert.EndsWith("2026-10-13-refactor-database.md", path1);
        Assert.EndsWith("2026-10-13-refactor-database-2.md", path2);
    }

    [Fact]
    public async Task ProcessFileContentAsync_ProtectsSystemManagedFieldsAndSyncsUserFields()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue = new Issue
        {
            Id = 200,
            WorkspaceId = 1,
            Title = "Original Title",
            Description = "Original body",
            Status = IssueOverallStatus.Running,
            CurrentStageId = "implementation",
            Priority = 1
        };
        await repo.CreateAsync(issue);

        var filePath = await sync.SyncIssueToDiskAsync(issue, _tempWorkspaceDir);

        // Simulate external edit: user changes Title, but also tampers with protected Status and Stage
        var tamperedMarkdown = @"---
id: 200
title: Updated User Title
status: Completed
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
        Assert.Equal(IssueOverallStatus.Running, updated.Status);
        Assert.Equal("implementation", updated.CurrentStageId);

        // Verify file on disk was rewritten to restore protected fields
        var diskContent = await File.ReadAllTextAsync(filePath);
        var (restoredFm, _) = sync.ParseMarkdown(diskContent);
        Assert.Equal("Running", restoredFm.Status);
        Assert.Equal("implementation", restoredFm.CurrentStage);
        Assert.Equal("Updated User Title", restoredFm.Title);
    }

    [Fact]
    public async Task ProcessFileContentAsync_AllocatesIdForNewDropInFileWithoutId()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var dropInMarkdown = @"---
title: Brand New Feature
priority: 3
base_branch: main
---

# Brand New Feature

## 📋 Requirements & Description
Detailed spec of drop-in feature.
";

        var targetFile = Path.Combine(_tempWorkspaceDir, "docs", "issues", "2026-10-13-brand-new-feature.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await File.WriteAllTextAsync(targetFile, dropInMarkdown);

        await sync.ProcessFileContentAsync(targetFile, dropInMarkdown, workspaceId: 1);

        // Verify issue created in DB
        var issues = await repo.GetByWorkspaceIdAsync(1);
        Assert.Single(issues);
        var created = issues[0];
        Assert.True(created.Id > 0);
        Assert.Equal("Brand New Feature", created.Title);
        Assert.Equal("Detailed spec of drop-in feature.", created.Description);
        Assert.Equal(3, created.Priority);

        // Verify file rewritten with allocated ID in-place without renaming
        var diskContent = await File.ReadAllTextAsync(targetFile);
        var (fm, _) = sync.ParseMarkdown(diskContent);
        Assert.Equal(created.Id, fm.Id);
        Assert.NotNull(fm.CreatedAt);
    }

    [Fact]
    public async Task EchoLoopSuppression_IgnoresSelfTriggeredWrites()
    {
        var repo = new InMemoryIssueRepository();
        var sync = new IssueMarkdownSyncService(repo);

        var issue = new Issue
        {
            Id = 301,
            WorkspaceId = 1,
            Title = "Echo Test",
            Description = "Test echo suppression",
            Status = IssueOverallStatus.Pending
        };
        await repo.CreateAsync(issue);

        var filePath = await sync.SyncIssueToDiskAsync(issue, _tempWorkspaceDir);
        var writtenContent = await File.ReadAllTextAsync(filePath);

        // When SyncIssueToDiskAsync wrote the file, it registered its hash in _suppressedHashes.
        // Processing this exact content should be suppressed without making DB calls or modifications.
        await sync.ProcessFileContentAsync(filePath, writtenContent, 1);

        var dbIssue = await repo.GetByIdAsync(301);
        Assert.NotNull(dbIssue);
        Assert.Equal("Echo Test", dbIssue.Title);
    }
}
