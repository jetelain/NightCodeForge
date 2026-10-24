using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskReviewServiceTests
{
    [Fact]
    public void ParseGitDiff_ParsesModifiedFileDiffCorrectly()
    {
        var rawDiff = @"diff --git a/src/Services/Math.cs b/src/Services/Math.cs
index 1234567..89abcdef 100644
--- a/src/Services/Math.cs
+++ b/src/Services/Math.cs
@@ -1,3 +1,4 @@
 public class Math
-    public int Add(int a, int b) => a;
+    public int Add(int a, int b) => a + b;
+    public int Subtract(int a, int b) => a - b;
 }";

        var files = TaskReviewService.ParseGitDiff(rawDiff);

        Assert.Single(files);
        var file = files[0];
        Assert.Equal("src/Services/Math.cs", file.Path);
        Assert.Equal(FileChangeType.Modified, file.ChangeType);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Contains("public int Add(int a, int b) => a;", file.OldContent);
        Assert.Contains("public int Add(int a, int b) => a + b;", file.NewContent);
        Assert.Contains("public int Subtract(int a, int b) => a - b;", file.NewContent);
    }

    [Fact]
    public void ParseGitDiff_ParsesMultipleFiles()
    {
        var rawDiff = @"diff --git a/src/FileA.cs b/src/FileA.cs
new file mode 100644
--- /dev/null
+++ b/src/FileA.cs
@@ -0,0 +1,2 @@
+public class FileA
+{
+}
diff --git a/src/FileB.cs b/src/FileB.cs
deleted file mode 100644
--- a/src/FileB.cs
+++ /dev/null
@@ -1,2 +0,0 @@
-public class FileB
-{
-}";

        var files = TaskReviewService.ParseGitDiff(rawDiff);

        Assert.Equal(2, files.Count);
        Assert.Equal("src/FileA.cs", files[0].Path);
        Assert.Equal(FileChangeType.Added, files[0].ChangeType);
        Assert.Equal(3, files[0].Additions);
        Assert.Equal(0, files[0].Deletions);

        Assert.Equal("src/FileB.cs", files[1].Path);
        Assert.Equal(FileChangeType.Deleted, files[1].ChangeType);
        Assert.Equal(0, files[1].Additions);
        Assert.Equal(3, files[1].Deletions);
    }

    [Fact]
    public async Task GetSnapshotAsync_ExtractsGateMetricsAndTaskData()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager();
        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.80);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.855);
        bag.Set(PipelineDataBagKeys.WarningsBaselineTotal, 5);
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 3);
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 42);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, 42);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 0);
        bag.Set(PipelineDataBagKeys.TestsCurrentDurationMs, 1250);

        var sampleFiles = new List<FileDiffSummary>
        {
            new("src/Calculator.cs", FileChangeType.Modified, 10, 2, "old code", "new code")
        };
        bag.Set(PipelineDataBagKeys.ReviewFiles, sampleFiles);

        var task = new ForgeTask
        {
            Id = 101,
            Title = "Implement Calculator",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = "main",
            FeatureBranch = "feature/task-101",
            PipelineDataJson = bag.ToJson()
        };
        await taskRepo.CreateAsync(task);

        var snapshot = await service.GetSnapshotAsync(101);

        Assert.NotNull(snapshot);
        Assert.Equal(101, snapshot.TaskId);
        Assert.Equal("Implement Calculator", snapshot.Title);
        Assert.Equal("feature/task-101", snapshot.FeatureBranch);
        Assert.Equal("main", snapshot.BaseBranch);
        Assert.Equal(TaskOverallStatus.Ready, snapshot.Status);
        Assert.Equal(MergeMode.ManualReviewRequired, snapshot.MergeMode);

        Assert.Equal(85.5, snapshot.GateMetrics.CurrentCoverage);
        Assert.Equal(80.0, snapshot.GateMetrics.BaselineCoverage);
        Assert.Equal(5.5, snapshot.GateMetrics.CoverageDelta);
        Assert.Equal(3, snapshot.GateMetrics.CurrentWarnings);
        Assert.Equal(5, snapshot.GateMetrics.BaselineWarnings);
        Assert.Equal(-2, snapshot.GateMetrics.WarningDelta);
        Assert.Equal(42, snapshot.GateMetrics.TotalTests);
        Assert.Equal(42, snapshot.GateMetrics.PassedTests);
        Assert.Equal(0, snapshot.GateMetrics.FailedTests);
        Assert.Equal(1250, snapshot.GateMetrics.DurationMs);

        Assert.Single(snapshot.Files);
        Assert.Equal("src/Calculator.cs", snapshot.Files[0].Path);
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenPassedTestsMissing_CalculatesPassedAsTotalMinusFailed()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager();
        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 10);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 2);
        // Notice: TestsCurrentPassed is intentionally not set

        var task = new ForgeTask
        {
            Id = 102,
            Title = "Fallback Calculator",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            PipelineDataJson = bag.ToJson()
        };
        await taskRepo.CreateAsync(task);

        var snapshot = await service.GetSnapshotAsync(102);

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.GateMetrics.TotalTests);
        Assert.Equal(2, snapshot.GateMetrics.FailedTests);
        Assert.Equal(8, snapshot.GateMetrics.PassedTests);
    }

    [Fact]
    public async Task GetTaskDiffFilesAsync_WhenTaskClosedWithSquashSha_UsesCommitDiff()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager
        {
            CommitDiffResult = @"diff --git a/src/ClosedFeature.cs b/src/ClosedFeature.cs
new file mode 100644
--- /dev/null
+++ b/src/ClosedFeature.cs
@@ -0,0 +1,3 @@
+public class ClosedFeature
+{
+}"
        };

        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Main Repo",
            RepositoryPath = "/fake/repo"
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = new ForgeTask
        {
            Id = 200,
            WorkspaceId = 1,
            Title = "Closed Feature Task",
            Status = TaskOverallStatus.Closed,
            SquashCommitSha = "c0ffee1234567890abcdef1234567890abcdef12",
            BaseBranch = "main",
            FeatureBranch = "feature/task-200"
        };
        await taskRepo.CreateAsync(task);

        var diffs = await service.GetTaskDiffFilesAsync(200);

        Assert.True(gitManager.GetCommitDiffCalled);
        Assert.Equal("c0ffee1234567890abcdef1234567890abcdef12", gitManager.LastCommitSha);
        Assert.False(gitManager.GenerateLiveDiffCalled);
        Assert.Single(diffs);
        Assert.Equal("src/ClosedFeature.cs", diffs[0].Path);
        Assert.Equal(FileChangeType.Added, diffs[0].ChangeType);
    }

    [Fact]
    public async Task GetTaskDiffFilesAsync_WhenTaskNotClosed_IgnoresSquashShaAndUsesLiveDiff()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager
        {
            LiveDiffResult = @"diff --git a/src/WipFeature.cs b/src/WipFeature.cs
new file mode 100644
--- /dev/null
+++ b/src/WipFeature.cs
@@ -0,0 +1,3 @@
+public class WipFeature
+{
+}"
        };

        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Main Repo",
            RepositoryPath = "/fake/repo"
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = new ForgeTask
        {
            Id = 201,
            WorkspaceId = 1,
            Title = "Active Ready Task with Stale Squash SHA",
            Status = TaskOverallStatus.Ready,
            SquashCommitSha = "stale_sha_1234567890abcdef1234567890abcdef",
            BaseBranch = "main",
            FeatureBranch = "feature/task-201"
        };
        await taskRepo.CreateAsync(task);

        var diffs = await service.GetTaskDiffFilesAsync(201);

        Assert.False(gitManager.GetCommitDiffCalled);
        Assert.True(gitManager.GenerateLiveDiffCalled);
        Assert.Single(diffs);
        Assert.Equal("src/WipFeature.cs", diffs[0].Path);
        Assert.Equal(FileChangeType.Added, diffs[0].ChangeType);
    }

    [Fact]
    public void ParseGitDiff_HandlesWindowsCrlfLineEndings()
    {
        var rawDiff = "diff --git a/src/File.cs b/src/File.cs\r\nindex 1234..5678 100644\r\n--- a/src/File.cs\r\n+++ b/src/File.cs\r\n@@ -1,2 +1,3 @@\r\n line1\r\n-line2\r\n+line2_mod\r\n+line3\r\n";

        var files = TaskReviewService.ParseGitDiff(rawDiff);

        Assert.Single(files);
        Assert.Equal("src/File.cs", files[0].Path);
        Assert.False(files[0].Path.EndsWith('\r'));
        Assert.Equal(2, files[0].Additions);
        Assert.Equal(1, files[0].Deletions);
        Assert.Contains("line2_mod", files[0].NewContent);
        Assert.Contains("line3", files[0].NewContent);
        Assert.Contains("line2", files[0].OldContent);
    }

    [Fact]
    public async Task GetTaskDiffFilesAsync_ClosedTaskWithSquashSha_CallsGetCommitDiffAsync()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager
        {
            CommitDiffResult = "diff --git a/src/Calculator.cs b/src/Calculator.cs\n--- a/src/Calculator.cs\n+++ b/src/Calculator.cs\n@@ -1,1 +1,2 @@\n+public int Multiply(int a, int b) => a * b;\n"
        };
        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Main Repo",
            RepositoryPath = "/fake/repo"
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Title = "Closed Task",
            Status = TaskOverallStatus.Closed,
            SquashCommitSha = "26c69f5a7a48891ee7fc4b834b119b0221c58f56",
            BaseBranch = "main"
        };
        await taskRepo.CreateAsync(task);

        var diffs = await service.GetTaskDiffFilesAsync(301);

        Assert.True(gitManager.GetCommitDiffCalled);
        Assert.Equal("26c69f5a7a48891ee7fc4b834b119b0221c58f56", gitManager.LastCommitSha);
        Assert.Single(diffs);
        Assert.Equal("src/Calculator.cs", diffs[0].Path);
        Assert.Equal(1, diffs[0].Additions);
    }

    [Fact]
    public async Task GetPendingReviewsAsync_ExcludesImplementedAndReviewed_ReturnsOnlyReadyTasksWithExecutionRun()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager();
        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var readyWithRun = new ForgeTask
        {
            Id = 401,
            WorkspaceId = 1,
            Title = "Ready with Run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        };
        var readyWithoutRun = new ForgeTask
        {
            Id = 402,
            WorkspaceId = 1,
            Title = "Ready without Run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = false
        };
        var implementedWithRun = new ForgeTask
        {
            Id = 403,
            WorkspaceId = 1,
            Title = "Implemented Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true
        };
        var reviewedWithRun = new ForgeTask
        {
            Id = 404,
            WorkspaceId = 1,
            Title = "Reviewed Task",
            Status = TaskOverallStatus.Reviewed,
            HasExecutionRun = true
        };
        var openTask = new ForgeTask
        {
            Id = 405,
            WorkspaceId = 1,
            Title = "Open Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        };

        await taskRepo.CreateAsync(readyWithRun);
        await taskRepo.CreateAsync(readyWithoutRun);
        await taskRepo.CreateAsync(implementedWithRun);
        await taskRepo.CreateAsync(reviewedWithRun);
        await taskRepo.CreateAsync(openTask);

        var pending = await service.GetPendingReviewsAsync();

        Assert.Single(pending);
        Assert.Equal(401, pending[0].TaskId);
    }

    [Fact]
    public async Task GetPendingReviewsAsync_WithWorkspaceId_ReturnsOnlyReadyTasksForSpecifiedWorkspace()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var gitManager = new TestGitWorkspaceManager();
        var service = new TaskReviewService(taskRepo, workspaceRepo, gitManager, NullLogger<TaskReviewService>.Instance);

        var taskWs1 = new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Workspace 1 Task",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        };
        var taskWs2 = new ForgeTask
        {
            Id = 502,
            WorkspaceId = 2,
            Title = "Workspace 2 Task",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        };

        await taskRepo.CreateAsync(taskWs1);
        await taskRepo.CreateAsync(taskWs2);

        var pendingWs1 = await service.GetPendingReviewsAsync(workspaceId: 1);

        Assert.Single(pendingWs1);
        Assert.Equal(501, pendingWs1[0].TaskId);
    }

    [Fact]
    public void TaskReviewService_ReviewReadinessPredicates_BehaveCorrectly()
    {
        var emptyFiles = Array.Empty<FileDiffSummary>();
        var metrics = new QualityGateMetrics(80, 85, 5, 2, 1, -1, 10, 10, 0, 100);

        var readyWithRun = new TaskReviewSnapshot(1, "Ready Task", "feat", "main", MergeMode.ManualReviewRequired, TaskOverallStatus.Ready, emptyFiles, metrics, HasResolvedConflict: true, HasExecutionRun: true);
        var readyWithoutRun = new TaskReviewSnapshot(2, "Ready No Run", "feat", "main", MergeMode.ManualReviewRequired, TaskOverallStatus.Ready, emptyFiles, metrics, HasResolvedConflict: true, HasExecutionRun: false);
        var implemented = new TaskReviewSnapshot(3, "Implemented Task", "feat", "main", MergeMode.ManualReviewRequired, TaskOverallStatus.Implemented, emptyFiles, metrics, HasResolvedConflict: true, HasExecutionRun: true);
        var readyNoConflict = new TaskReviewSnapshot(4, "Ready Clean", "feat", "main", MergeMode.ManualReviewRequired, TaskOverallStatus.Ready, emptyFiles, metrics, HasResolvedConflict: false, HasExecutionRun: true);

        Assert.True(TaskReviewService.IsPendingReview(readyWithRun));
        Assert.False(TaskReviewService.IsPendingReview(readyWithoutRun));
        Assert.False(TaskReviewService.IsPendingReview(implemented));

        Assert.True(TaskReviewService.ShouldDisplayConflictResolvedBanner(readyWithRun));
        Assert.False(TaskReviewService.ShouldDisplayConflictResolvedBanner(implemented));
        Assert.False(TaskReviewService.ShouldDisplayConflictResolvedBanner(readyNoConflict));
    }

    private class TestGitWorkspaceManager : NightCodeForge.Core.Interfaces.IGitWorkspaceManager
    {
        public bool GetCommitDiffCalled { get; private set; }
        public string? LastCommitSha { get; private set; }
        public bool GenerateLiveDiffCalled { get; private set; }
        public string CommitDiffResult { get; set; } = string.Empty;
        public string LiveDiffResult { get; set; } = string.Empty;

        public Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string summary, CancellationToken cancellationToken = default) => Task.FromResult("wip-commit-sha");
        public Task<string> GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task<string> GenerateDiffStatAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult("");

        public Task<string> GenerateLiveDiffAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default)
        {
            GenerateLiveDiffCalled = true;
            return Task.FromResult(LiveDiffResult);
        }

        public Task<string> GenerateLiveDiffStatAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default) => Task.FromResult("");

        public Task<string> GetCommitDiffAsync(Workspace workspace, string commitSha, CancellationToken cancellationToken = default)
        {
            GetCommitDiffCalled = true;
            LastCommitSha = commitSha;
            return Task.FromResult(CommitDiffResult);
        }

        public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default) => Task.FromResult("squash-commit-sha");
        public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, bool allowCreateBaseBranch, CancellationToken cancellationToken = default) => Task.FromResult("squash-commit-sha");
        public Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult($"/wks/task-{taskId}");
        public Task EnsureWorktreeRelativePathsAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string GetWorktreePath(Workspace workspace, int taskId) => $"/wks/task-{taskId}";
        public Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateGitVersionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult(new MergeSyncResult(true, false, Array.Empty<string>(), "sha"));
        public Task<MergeabilityCheckResult> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult(new MergeabilityCheckResult(true, Array.Empty<string>(), "base", "feat"));
        public Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default) => Task.FromResult("sha");
        public Task ResetCanonicalBranchAsync(Workspace workspace, string branchName, string targetSha, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetTaskBranchToBaseAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
