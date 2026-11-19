using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class Step1238TriageLifecycleIsolationTests : IDisposable
{
    private readonly string _tempDir;

    public Step1238TriageLifecycleIsolationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NCF_Step1238_Core_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore temp directory cleanup errors in tests.
        }
    }

    private static TaskMarkdownSyncService CreateMarkdownSyncService(ITaskRepository repo)
    {
        var services = new ServiceCollection();
        services.AddSingleton(repo);
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
    public async Task MarkdownSync_SkipsTriageTasksOnWrite_AndIgnoresTriageFrontmatterOnImport()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateMarkdownSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "Repo", RepositoryPath = _tempDir, DefaultBaseBranch = "main" };

        var triageTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: crash on startup",
            Description = "Verbatim untrusted intake",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.Normal,
            BaseBranch = "main"
        });

        var openTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Real Open Backlog Item",
            Description = "Implement feature",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.High,
            BaseBranch = "main"
        });

        var singleResult = await sync.SyncTaskToDiskAsync(triageTask, _tempDir);
        Assert.Equal(string.Empty, singleResult);

        var syncAll = await sync.SyncAllTasksToDiskAsync(workspace, _tempDir);
        Assert.False(syncAll.WrittenPaths.ContainsKey(triageTask.Id));
        Assert.True(syncAll.WrittenPaths.ContainsKey(openTask.Id));

        var docsTasksDir = Path.Combine(_tempDir, "docs", "tasks");
        Assert.True(Directory.Exists(docsTasksDir));
        var files = Directory.GetFiles(docsTasksDir, "*.md", SearchOption.AllDirectories);
        Assert.Single(files);

        // Attempting to re-import a manually crafted Triage markdown file must neither overwrite the DB triage session
        // nor create a synthetic Triage task from disk.
        var forgedMdPath = Path.Combine(docsTasksDir, $"task-{triageTask.Id}-forged.md");
        await File.WriteAllTextAsync(forgedMdPath, $"""
---
id: {triageTask.Id}
title: Hijacked Title
status: Triage
priority: 1
---

Hijacked body
""");
        await sync.ProcessFileContentAsync(forgedMdPath, await File.ReadAllTextAsync(forgedMdPath), workspace.Id);
        await sync.SyncAllTasksFromDiskAsync(workspace, _tempDir, null, null, AuditActors.FileWatcher, "sync");

        var reloaded = await repo.GetByIdAsync(triageTask.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Triage: crash on startup", reloaded.Title);
        Assert.Equal("Verbatim untrusted intake", reloaded.Description);
        Assert.Equal(TaskOverallStatus.Triage, reloaded.Status);
    }

    [Fact]
    public void LifecycleGuards_ExcludeTriageFromRunnable_PendingBadges_AndPreserveContainerReaping()
    {
        var triageTask = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.UntrustedIntake
        };

        Assert.False(triageTask.IsRunnable());
        Assert.True(TaskMarkdownSyncService.IsNonSyncedStatus(TaskOverallStatus.Triage));
        Assert.True(TaskContainerReaper.ShouldReapTaskContainer(triageTask));
        Assert.False(TaskBadgeCounter.IsPendingQuestion(new Question { Status = QuestionStatus.Pending }, triageTask));
        Assert.False(TaskBadgeCounter.IsPendingHumanReview(triageTask));
    }

    [Fact]
    public async Task BacklogDigest_ExcludesTriageSessionTasks_AndIncludesOriginMetadata()
    {
        var repo = new InMemoryTaskRepository();
        await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Triage: incoming report",
            Status = TaskOverallStatus.Triage,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.Normal
        });
        await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Manual backlog work",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            Priority = TaskPriority.High
        });
        await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Fix null parser bug",
            Status = TaskOverallStatus.Suggested,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.Normal
        });

        var service = new WorkspaceBacklogSummaryService(repo, new WorkspaceBacklogDigestOptions());
        var digest = await service.BuildDigestAsync(workspaceId: 1);

        Assert.DoesNotContain("Triage: incoming report", digest);
        Assert.Contains("Origin:Manual]: Manual backlog work", digest);
        Assert.Contains("Origin:UntrustedIntake]: Fix null parser bug", digest);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Triage)]
    [InlineData(TaskOverallStatus.Draft)]
    public async Task TaskOrchestrator_AbortsImmediately_WhenStatusIsTriageOrDraft(TaskOverallStatus status)
    {
        var builder = new TestOrchestratorBuilder();
        var workspace = await builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        });
        var task = await builder.TaskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = $"Non-runnable {status} task",
            Status = status,
            Priority = TaskPriority.Normal,
            BaseBranch = "main"
        });

        var orchestrator = builder.Build();
        await orchestrator.ProcessTaskAsync(task.Id);

        builder.GitHubTaskMirrorMock.Verify(
            m => m.TryClaimIssueForExecutionAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var reloaded = await builder.TaskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(status, reloaded.Status);
    }

    [Fact]
    public async Task TaskLifecycleMutationCoordinator_RejectsPromoteAndRestart_ForTriageStatus()
    {
        var builder = new TestOrchestratorBuilder();
        var workspace = await builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        });
        var triageTask = await builder.TaskRepository.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: incoming report",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.Normal,
            BaseBranch = "main"
        });

        var coordinator = builder.BuildLifecycleMutationCoordinator();

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() =>
            coordinator.PromoteSuggestedTaskAsync(triageTask.Id, TaskOverallStatus.Open, TaskPriority.High, MergeMode.ManualReviewRequired));

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() =>
            coordinator.RestartTaskPipelineAsync(triageTask.Id));

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() =>
            coordinator.RejectTaskAsync(triageTask.Id, "Should fail"));

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() =>
            coordinator.RequestChangesAsync(triageTask.Id, "Should fail"));

        await Assert.ThrowsAsync<TaskCannotBeModifiedException>(() =>
            coordinator.SubmitAnswersAsync(triageTask.Id, new Dictionary<int, string> { [1] = "answer" }));
    }

    [Fact]
    public async Task MarkdownSync_IgnoresMissingTriageIdFrontmatter_AndRejectsDependenciesOnTriageTask()
    {
        var repo = new InMemoryTaskRepository();
        var sync = CreateMarkdownSyncService(repo);
        var workspace = new Workspace { Id = 1, Name = "Repo", RepositoryPath = _tempDir, DefaultBaseBranch = "main" };

        var triageTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Triage: incoming webhook failure",
            Description = "Verbatim intake",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage,
            Origin = TaskOrigin.UntrustedIntake,
            Priority = TaskPriority.Normal,
            BaseBranch = "main"
        });

        var openTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Open Backlog Work",
            Description = "Real task",
            Status = TaskOverallStatus.Open,
            Origin = TaskOrigin.Manual,
            Priority = TaskPriority.High,
            BaseBranch = "main"
        });

        var docsTasksDir = Path.Combine(_tempDir, "docs", "tasks");
        Directory.CreateDirectory(docsTasksDir);

        // Non-existent id with status: Triage must be skipped cleanly instead of throwing TaskNotFound / TaskMarkdownSyncException
        var missingIdTriageFile = Path.Combine(docsTasksDir, "task-999-missing-triage.md");
        await File.WriteAllTextAsync(missingIdTriageFile, """
---
id: 999
title: "Triage: external report"
status: Triage
priority: 3
---

Body
""");

        await sync.SyncAllTasksFromDiskAsync(workspace, _tempDir, null, null, AuditActors.FileWatcher, "sync");
        Assert.Null(await repo.GetByIdAsync(999));

        // Explicit numeric dependency on a Triage task must be rejected
        var depByIdPath = Path.Combine(docsTasksDir, $"task-{openTask.Id}-by-id.md");
        var depByIdContent = $"""
---
id: {openTask.Id}
title: {openTask.Title}
status: Open
priority: 2
depends_on: [{triageTask.Id}]
---

Updated body
""";
        var idEx = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(depByIdPath, depByIdContent, workspace.Id));
        Assert.Contains("Triage", idEx.Message);

        // Title dependency on a Triage task must also fail resolution
        var depByTitlePath = Path.Combine(docsTasksDir, $"task-{openTask.Id}-by-title.md");
        var depByTitleContent = $"""
---
id: {openTask.Id}
title: {openTask.Title}
status: Open
priority: 2
depends_on: ["{triageTask.Title}"]
---

Updated body
""";
        await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sync.ProcessFileContentAsync(depByTitlePath, depByTitleContent, workspace.Id));
    }

    [Fact]
    public async Task TaskMarkdownValidator_RejectsDependenciesTargetingTriageSessionTasks()
    {
        var repo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var validator = new TaskMarkdownValidator(repo, depEngine, NullLogger<TaskMarkdownValidator>.Instance);

        var triageTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Triage: incoming error",
            Status = TaskOverallStatus.Triage,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Triage
        });

        var openTask = await repo.CreateAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Valid Open Task",
            Status = TaskOverallStatus.Open
        });

        var editById = $"""
---
id: {openTask.Id}
title: {openTask.Title}
depends_on: [{triageTask.Id}]
---

Content
""";
        var resById = await validator.ValidateAsync(workspaceId: 1, currentTaskId: openTask.Id, path: $"docs/tasks/task-{openTask.Id}.md", content: editById);
        Assert.False(resById.IsValid);
        Assert.Contains("Triage", resById.ErrorMessage);

        var editByTitle = $"""
---
id: {openTask.Id}
title: {openTask.Title}
depends_on: ["{triageTask.Title}"]
---

Content
""";
        var resByTitle = await validator.ValidateAsync(workspaceId: 1, currentTaskId: openTask.Id, path: $"docs/tasks/task-{openTask.Id}.md", content: editByTitle);
        Assert.False(resByTitle.IsValid);
        Assert.Contains("Triage", resByTitle.ErrorMessage);
    }
}
