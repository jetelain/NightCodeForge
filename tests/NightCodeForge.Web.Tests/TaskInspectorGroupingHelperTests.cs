using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskInspectorGroupingHelperTests
{
    [Fact]
    public void GroupTasks_CategorizesIntoProperOptgroupsAndOrdersByPriority()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 1, Title = "Running Task P2", Status = TaskOverallStatus.Open, Priority = 2 },
            new() { Id = 2, Title = "Running Task P1", Status = TaskOverallStatus.Open, Priority = 1 },
            new() { Id = 3, Title = "Waiting for Human Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true, Priority = 1 },
            new() { Id = 4, Title = "Queued In Flight Task", Status = TaskOverallStatus.Ready, HasExecutionRun = true, Priority = 3 },
            new() { Id = 5, Title = "Open Backlog Task", Status = TaskOverallStatus.Open, Priority = 2 },
            new() { Id = 6, Title = "Closed Task", Status = TaskOverallStatus.Closed, Priority = 1 }
        };

        var groups = TaskInspectorGroupingHelper.GroupTasks(tasks, runningTaskIds: new HashSet<int> { 1, 2 });

        Assert.Equal(5, groups.Count);

        // Group 1: Running
        var runningGroup = groups.First(g => g.Label == TaskInspectorGroupingHelper.GroupRunning);
        Assert.Equal(2, runningGroup.Tasks.Count);
        Assert.Equal(2, runningGroup.Tasks[0].Id); // P1 first
        Assert.Equal(1, runningGroup.Tasks[1].Id); // P2 second

        // Group 2: Waiting for Human
        var humanGroup = groups.First(g => g.Label == TaskInspectorGroupingHelper.GroupWaitingForHuman);
        Assert.Single(humanGroup.Tasks);
        Assert.Equal(3, humanGroup.Tasks[0].Id);

        // Group 3: Waiting for Slot
        var slotGroup = groups.First(g => g.Label == TaskInspectorGroupingHelper.GroupWaitingForSlot);
        Assert.Single(slotGroup.Tasks);
        Assert.Equal(4, slotGroup.Tasks[0].Id);

        // Group 4: Open Backlog
        var openGroup = groups.First(g => g.Label == TaskInspectorGroupingHelper.GroupOpen);
        Assert.Single(openGroup.Tasks);
        Assert.Equal(5, openGroup.Tasks[0].Id);

        // Group 5: Closed
        var closedGroup = groups.First(g => g.Label == TaskInspectorGroupingHelper.GroupClosed);
        Assert.Single(closedGroup.Tasks);
        Assert.Equal(6, closedGroup.Tasks[0].Id);
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
}
