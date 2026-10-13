using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.Tests.Git;

namespace NightCodeForge.Infrastructure.Tests;

public class StartupRecoveryIntegrationTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    public StartupRecoveryIntegrationTests()
    {
        _repo = new TestGitRepository("main");
        _runner = new GitCliRunner();

        // Initial base commit
        _repo.CreateFile("README.md", "# Test Repository\nInitial commit.");
        _repo.CommitAll("Initial commit");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new AppDbContext(dbOptions);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WithRealGitRepo_PurgesLocksAndCreatesWipRecoveryCommit()
    {
        // 1. Create a dangling .git/index.lock
        var gitDir = Path.Combine(_repo.RepositoryPath, ".git");
        var lockFile = Path.Combine(gitDir, "index.lock");
        await File.WriteAllTextAsync(lockFile, "stale lock");
        Assert.True(File.Exists(lockFile));

        // 2. Create dirty uncommitted changes
        _repo.CreateFile("DirtyFile.cs", "public class DirtyFile {}");

        // 3. Register workspace and running issue in DB
        var workspace = new Workspace
        {
            Name = "Integration Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();

        var issue = new Issue
        {
            WorkspaceId = workspace.Id,
            Title = "Running Issue Interrupted",
            Status = IssueOverallStatus.Running,
            CurrentStageId = "issue-review"
        };
        _context.Issues.Add(issue);
        await _context.SaveChangesAsync();

        var workspaceRepo = new EfCoreWorkspaceRepository(_context);
        var issueRepo = new EfCoreIssueRepository(_context);
        var jobQueue = new FakeTestJobQueue();

        var recoveryService = new StartupRecoveryService(
            workspaceRepo,
            issueRepo,
            _runner,
            new DefaultWorkflowPipelineProvider(),
            jobQueue);

        // Run recovery
        await recoveryService.ReconcileWorkspacesAsync();

        // Verify lock is purged
        Assert.False(File.Exists(lockFile));

        // Verify git status is clean now (rescue commit created)
        var status = await _runner.ExecuteAsync(_repo.RepositoryPath, "status --porcelain");
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));

        // Verify git log contains WIP-RECOVERY commit
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, "log -n 1 --oneline");
        Assert.Contains("WIP-RECOVERY", log.StandardOutput);

        // Verify issue transitioned to Pending and re-enqueued to urgent
        var updated = await issueRepo.GetByIdAsync(issue.Id);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Pending, updated.Status);
        Assert.Contains(issue.Id, jobQueue.UrgentQueue);
    }
}

public class FakeTestJobQueue : IIssueJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> InferenceQueue { get; } = new();

    public string EnqueueUrgent(int issueId)
    {
        UrgentQueue.Add(issueId);
        return $"urgent-{issueId}";
    }

    public string EnqueueInference(int issueId)
    {
        InferenceQueue.Add(issueId);
        return $"inference-{issueId}";
    }
}
