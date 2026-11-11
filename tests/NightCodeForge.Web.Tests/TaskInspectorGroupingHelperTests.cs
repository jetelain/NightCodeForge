using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskInspectorGroupingHelperTests
{
    [Fact]
    public void GroupTasks_CategorizesIntoProperOptgroupsAndOrdersByIdAscending()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 20, Title = "Running Task P1 High ID", Status = TaskOverallStatus.Open, Priority = 1 },
            new() { Id = 5, Title = "Running Task P2 Low ID", Status = TaskOverallStatus.Open, Priority = 2 },
            new() { Id = 3, Title = "Waiting for Human Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true, Priority = 1 },
            new() { Id = 12, Title = "Failed Task 12", Status = TaskOverallStatus.Failed, Priority = 1 },
            new() { Id = 7, Title = "Failed Task 7", Status = TaskOverallStatus.Failed, Priority = 3 },
            new() { Id = 4, Title = "Queued In Flight Task", Status = TaskOverallStatus.Ready, HasExecutionRun = true, Priority = 3 },
            new() { Id = 9, Title = "Open Backlog Task 9", Status = TaskOverallStatus.Open, Priority = 1 },
            new() { Id = 2, Title = "Open Backlog Task 2", Status = TaskOverallStatus.Open, Priority = 2 },
            new() { Id = 15, Title = "Closed Task 15", Status = TaskOverallStatus.Closed, Priority = 1 },
            new() { Id = 6, Title = "Closed Task 6", Status = TaskOverallStatus.Closed, Priority = 2 }
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(tasks, runningTaskIds: new HashSet<int> { 5, 20 });

        Assert.Equal(6, groups.Count);

        // Group 1: Running (ordered by Id ASC: 5, 20)
        var runningGroup = groups[0];
        Assert.Equal(TaskInspectorGroupingHelper.GroupRunning, runningGroup.Label);
        Assert.Equal(2, runningGroup.Tasks.Count);
        Assert.Equal(5, runningGroup.Tasks[0].Id);
        Assert.Equal(20, runningGroup.Tasks[1].Id);

        // Group 2: Waiting for Human
        var humanGroup = groups[1];
        Assert.Equal(TaskInspectorGroupingHelper.GroupWaitingForHuman, humanGroup.Label);
        Assert.Single(humanGroup.Tasks);
        Assert.Equal(3, humanGroup.Tasks[0].Id);

        // Group 3: Failed (ordered by Id ASC: 7, 12)
        var failedGroup = groups[2];
        Assert.Equal(TaskInspectorGroupingHelper.GroupFailed, failedGroup.Label);
        Assert.Equal(2, failedGroup.Tasks.Count);
        Assert.Equal(7, failedGroup.Tasks[0].Id);
        Assert.Equal(12, failedGroup.Tasks[1].Id);

        // Group 4: Waiting for Slot
        var slotGroup = groups[3];
        Assert.Equal(TaskInspectorGroupingHelper.GroupWaitingForSlot, slotGroup.Label);
        Assert.Single(slotGroup.Tasks);
        Assert.Equal(4, slotGroup.Tasks[0].Id);

        // Group 5: Open Backlog (ordered by Id ASC: 2, 9)
        var openGroup = groups[4];
        Assert.Equal(TaskInspectorGroupingHelper.GroupOpen, openGroup.Label);
        Assert.Equal(2, openGroup.Tasks.Count);
        Assert.Equal(2, openGroup.Tasks[0].Id);
        Assert.Equal(9, openGroup.Tasks[1].Id);

        // Group 6: Closed (ordered by Id ASC: 6, 15)
        var closedGroup = groups[5];
        Assert.Equal(TaskInspectorGroupingHelper.GroupClosed, closedGroup.Label);
        Assert.Equal(2, closedGroup.Tasks.Count);
        Assert.Equal(6, closedGroup.Tasks[0].Id);
        Assert.Equal(15, closedGroup.Tasks[1].Id);
    }

    [Fact]
    public void GroupTasks_WithActiveTaskId_CategorizesIntoRunningGroup()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 77, Title = "Direct InProgress Task", Status = TaskOverallStatus.Open, Priority = 1 }
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(tasks, activeTaskId: 77);

        Assert.Single(groups);
        Assert.Equal(TaskInspectorGroupingHelper.GroupRunning, groups[0].Label);
        Assert.Single(groups[0].Tasks);
        Assert.Equal(77, groups[0].Tasks[0].Id);
    }

    [Fact]
    public void GroupTasks_ClosedTaskWithStaleActiveId_StaysInClosedGroup()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 88, Title = "Merged", Status = TaskOverallStatus.Closed, Priority = 1 }
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(tasks, activeTaskId: 88, runningTaskIds: new HashSet<int> { 88 });

        Assert.Single(groups);
        Assert.Equal(TaskInspectorGroupingHelper.GroupClosed, groups[0].Label);
    }

    [Fact]
    public void GroupTasks_FailedTaskWithStaleActiveIdOrWaitingFlag_StaysInFailedGroup()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 99, Title = "Failed with stale flags", Status = TaskOverallStatus.Failed, IsWaitingForHuman = true, Priority = 1 }
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(tasks, activeTaskId: 99, runningTaskIds: new HashSet<int> { 99 });

        Assert.Single(groups);
        Assert.Equal(TaskInspectorGroupingHelper.GroupFailed, groups[0].Label);
        Assert.Equal(99, groups[0].Tasks[0].Id);
    }

    [Fact]
    public void ResolveRunningTaskIds_CombinesSnapshotAndTelemetryActiveTasks()
    {
        var snapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.Running,
            ActiveTaskId: 10,
            ActiveTaskTitle: "Primary Task",
            StageId: "stage-1",
            StageKind: "exec",
            HardwareTarget: "GPU",
            PendingQuestionCount: 0,
            ActiveTasks: new[]
            {
                new NightCodeForge.Core.Workflow.ActiveTaskSummary(10, "Task 10", "stage-1", 1, "test", DateTime.UtcNow, "GPU"),
                new NightCodeForge.Core.Workflow.ActiveTaskSummary(20, "Task 20", "stage-2", 2, "test", DateTime.UtcNow, "GPU")
            });

        var telemetryMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>();
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(new[]
        {
            new NightCodeForge.Core.Workflow.ActiveTaskSummary(20, "Task 20", "stage-2", 2, "test", DateTime.UtcNow, "GPU"),
            new NightCodeForge.Core.Workflow.ActiveTaskSummary(30, "Task 30", "stage-3", 3, "test", DateTime.UtcNow, "GPU")
        });

        var resolved = TaskInspectorGroupingHelper.ResolveRunningTaskIds(snapshot, telemetryMock.Object);

        Assert.Equal(3, resolved.Count);
        Assert.Contains(10, resolved);
        Assert.Contains(20, resolved);
        Assert.Contains(30, resolved);
    }

    [Fact]
    public void ResolveRunningTaskIds_ExcludesNonRunningSnapshotAndIdeationSessions()
    {
        var blockedSnapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.Blocked,
            ActiveTaskId: 44,
            ActiveTaskTitle: "Blocked Task",
            StageId: "stage-1",
            StageKind: "exec",
            HardwareTarget: "GPU",
            PendingQuestionCount: 0,
            ActiveTasks: Array.Empty<NightCodeForge.Core.Workflow.ActiveTaskSummary>());

        var telemetryMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>();
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(new[]
        {
            new NightCodeForge.Core.Workflow.ActiveTaskSummary(0, "Ideation", StandardStageIds.Ideation, 1, "interactive", DateTime.UtcNow, "GPU"),
            new NightCodeForge.Core.Workflow.ActiveTaskSummary(55, "Ideation With Id", StandardStageIds.Ideation, 1, "interactive", DateTime.UtcNow, "GPU"),
            new NightCodeForge.Core.Workflow.ActiveTaskSummary(66, "Genuine Task", "implementation", 1, "test", DateTime.UtcNow, "GPU")
        });

        var resolved = TaskInspectorGroupingHelper.ResolveRunningTaskIds(blockedSnapshot, telemetryMock.Object);

        Assert.Single(resolved);
        Assert.Contains(66, resolved);
        Assert.DoesNotContain(44, resolved);
        Assert.DoesNotContain(55, resolved);
    }
}
