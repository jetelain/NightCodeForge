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
        var service = new TaskReviewService(taskRepo, workspaceRepo, NullLogger<TaskReviewService>.Instance);

        var bag = new PipelineDataBag();
        bag.Set("coverage.baseline.line_rate", 0.80);
        bag.Set("coverage.current.line_rate", 0.855);
        bag.Set("warnings.baseline.total", 5);
        bag.Set("warnings.current.total", 3);
        bag.Set("tests.current.total", 42);
        bag.Set("tests.current.passed", 42);
        bag.Set("tests.current.failed", 0);
        bag.Set("tests.current.duration_ms", 1250);

        var sampleFiles = new List<FileDiffSummary>
        {
            new("src/Calculator.cs", FileChangeType.Modified, 10, 2, "old code", "new code")
        };
        bag.Set("ReviewFiles", sampleFiles);

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
}
