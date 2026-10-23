using System.Globalization;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DagLayoutEngineTests
{
    private readonly DagLayoutEngine _engine = new();

    [Fact]
    public void ComputeLayout_WhenNoTasks_ReturnsDefaultDimensions()
    {
        var layout = _engine.ComputeLayout(Array.Empty<ForgeTask>(), Array.Empty<(int, int)>());

        Assert.NotNull(layout);
        Assert.Equal(920, layout.Width);
        Assert.Equal(480, layout.Height);
        Assert.Empty(layout.Nodes);
        Assert.Empty(layout.Edges);
    }

    [Fact]
    public void ComputeLayout_CultureInvariance_GeneratesPeriodDecimalsUnderFrenchCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // Explicitly set French culture with comma decimal separator
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            var task1 = new ForgeTask { Id = 1, Title = "Root Architecture", Status = TaskOverallStatus.Closed };
            var task2 = new ForgeTask { Id = 2, Title = "Feature Module", Status = TaskOverallStatus.Open };
            var tasks = new List<ForgeTask> { task1, task2 };
            var deps = new List<(int TaskId, int DependsOnTaskId)> { (TaskId: 2, DependsOnTaskId: 1) };

            var layout = _engine.ComputeLayout(tasks, deps);

            Assert.Equal(2, layout.Nodes.Count);
            Assert.Single(layout.Edges);

            var edge = layout.Edges[0];
            // Path data must use '.' and never ',' for decimal numbers
            Assert.Contains("M ", edge.PathData);
            Assert.Contains(" C ", edge.PathData);
            Assert.DoesNotContain(",0", edge.PathData); // No French-style 120,0 decimals

            // Arrow points must have valid polygon points format
            Assert.Contains(" ", edge.ArrowPoints);
            Assert.DoesNotContain(",0 ", edge.ArrowPoints);

            // Node transforms must have '.' and no ',' decimals
            foreach (var node in layout.Nodes)
            {
                Assert.StartsWith("translate(", node.Transform);
                Assert.DoesNotContain(",0)", node.Transform);
            }

            // Zoomed strings must use '.' and never ','
            var zoomedWidth = layout.GetZoomedWidthInvariant(1.5f);
            Assert.EndsWith("px", zoomedWidth);
            Assert.DoesNotContain(",", zoomedWidth);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void ComputeLayout_ConnectedAndIndependentTasks_ArrangesBothGracefully()
    {
        var t1 = new ForgeTask { Id = 1, Title = "Pipeline 1", Status = TaskOverallStatus.Closed };
        var t2 = new ForgeTask { Id = 2, Title = "Pipeline 2", Status = TaskOverallStatus.Open };
        var t3 = new ForgeTask { Id = 3, Title = "Independent 1", Status = TaskOverallStatus.Open };
        var t4 = new ForgeTask { Id = 4, Title = "Independent 2", Status = TaskOverallStatus.Open };

        var tasks = new List<ForgeTask> { t1, t2, t3, t4 };
        var deps = new List<(int TaskId, int DependsOnTaskId)> { (TaskId: 2, DependsOnTaskId: 1) };

        var layout = _engine.ComputeLayout(tasks, deps);

        Assert.Equal(4, layout.Nodes.Count);
        Assert.Single(layout.Edges);

        var node1 = layout.Nodes.First(n => n.Task.Id == 1);
        var node2 = layout.Nodes.First(n => n.Task.Id == 2);
        var node3 = layout.Nodes.First(n => n.Task.Id == 3);
        var node4 = layout.Nodes.First(n => n.Task.Id == 4);

        // Connected tasks have ranks 0 and 1
        Assert.Equal(0, node1.Rank);
        Assert.Equal(1, node2.Rank);

        // Target X of edge is destination node X
        Assert.Equal(node1.X + DagLayoutEngine.NodeWidth, layout.Edges[0].SourceX);
        Assert.Equal(node2.X, layout.Edges[0].TargetX);

        // Independent tasks are placed without overlap
        Assert.NotEqual(node3.X, node4.X);
    }

    [Fact]
    public void ComputeLayout_CycleSafety_DoesNotLoopInfinitely()
    {
        var t1 = new ForgeTask { Id = 1, Title = "Cycle A", Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { Id = 2, Title = "Cycle B", Status = TaskOverallStatus.Open };

        var tasks = new List<ForgeTask> { t1, t2 };
        // Mutual cycle
        var deps = new List<(int TaskId, int DependsOnTaskId)>
        {
            (TaskId: 1, DependsOnTaskId: 2),
            (TaskId: 2, DependsOnTaskId: 1)
        };

        var layout = _engine.ComputeLayout(tasks, deps);

        Assert.Equal(2, layout.Nodes.Count);
        Assert.Equal(2, layout.Edges.Count);
    }

    [Fact]
    public void ComputeLayout_UnstartedOpenTask_WithPendingPrerequisite_IsBlockedAndNotInFlight()
    {
        var prereq = new ForgeTask { Id = 1, Title = "Prereq Open", Status = TaskOverallStatus.Open };
        var task4 = new ForgeTask
        {
            Id = 4,
            Title = "Promoted Task 4",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            CurrentStageId = "task-review"
        };

        var tasks = new List<ForgeTask> { prereq, task4 };
        var deps = new List<(int TaskId, int DependsOnTaskId)> { (TaskId: 4, DependsOnTaskId: 1) };

        var layout = _engine.ComputeLayout(tasks, deps);
        var node4 = layout.Nodes.First(n => n.Task.Id == 4);

        Assert.True(node4.IsBlocked);
        Assert.False(node4.IsInFlight);
        Assert.False(node4.IsReadyToExecute);
        Assert.Equal("🔒 Blocked (1)", node4.StatusLabel);
    }

    [Fact]
    public void ComputeLayout_UnstartedOpenTask_WithNoBlockers_IsReadyAndNotInFlight()
    {
        var task4 = new ForgeTask
        {
            Id = 4,
            Title = "Unblocked Open Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            CurrentStageId = "task-review"
        };

        var layout = _engine.ComputeLayout(new[] { task4 }, Array.Empty<(int, int)>());
        var node4 = layout.Nodes.First(n => n.Task.Id == 4);

        Assert.False(node4.IsBlocked);
        Assert.False(node4.IsInFlight);
        Assert.True(node4.IsReadyToExecute);
        Assert.Equal("🟢 Ready", node4.StatusLabel);
    }

    [Fact]
    public void ComputeLayout_ExecutingOpenTask_IsInFlight()
    {
        var task4 = new ForgeTask
        {
            Id = 4,
            Title = "Executing Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            CurrentStageId = "task-review"
        };

        var layout = _engine.ComputeLayout(new[] { task4 }, Array.Empty<(int, int)>());
        var node4 = layout.Nodes.First(n => n.Task.Id == 4);

        Assert.True(node4.IsInFlight);
        Assert.Equal("In-Flight: task-review", node4.StatusLabel);
    }

    [Fact]
    public void ComputeLayout_RejectedAndFailedTasks_HaveAppropriateLabelsAndColors()
    {
        var rejected = new ForgeTask { Id = 10, Title = "Rejected Task", Status = TaskOverallStatus.Rejected };
        var failed = new ForgeTask { Id = 11, Title = "Failed Task", Status = TaskOverallStatus.Failed };

        var layout = _engine.ComputeLayout(new[] { rejected, failed }, Array.Empty<(int, int)>());

        var nodeRejected = layout.Nodes.First(n => n.Task.Id == 10);
        Assert.Equal("🗑️ Rejected", nodeRejected.StatusLabel);
        Assert.Equal("#2a151b", nodeRejected.FillColor);
        Assert.Equal("#ef4444", nodeRejected.StrokeColor);

        var nodeFailed = layout.Nodes.First(n => n.Task.Id == 11);
        Assert.Equal("❌ Failed", nodeFailed.StatusLabel);
        Assert.Equal("#2a151b", nodeFailed.FillColor);
        Assert.Equal("#ef4444", nodeFailed.StrokeColor);
    }

    [Fact]
    public void ComputeLayout_WhenActiveTaskIdMatches_NodeIsRunningAndHighlighted()
    {
        var task5 = new ForgeTask { Id = 5, Title = "Active Task", Status = TaskOverallStatus.Open };
        var layout = _engine.ComputeLayout(new[] { task5 }, Array.Empty<(int, int)>(), activeTaskId: 5);

        var node5 = layout.Nodes.First(n => n.Task.Id == 5);
        Assert.True(node5.IsRunning);
        Assert.True(node5.IsInFlight);
        Assert.Equal("🚀 Running", node5.StatusLabel);
        Assert.Equal("#064e3b", node5.FillColor);
        Assert.Equal("#10b981", node5.StrokeColor);
    }
}
