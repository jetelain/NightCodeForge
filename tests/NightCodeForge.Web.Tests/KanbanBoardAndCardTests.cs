using Bunit;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Backlog;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class KanbanBoardAndCardTests : BunitContext
{
    [Fact]
    public void KanbanCard_RendersPriorityAndMergeMode()
    {
        var task = new ForgeTask
        {
            Id = 7,
            Title = "Implement Test Gates",
            Priority = 5,
            MergeMode = MergeMode.Automatic,
            Status = TaskOverallStatus.Open
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task));

        Assert.Contains("#7", cut.Find(".card-id").TextContent);
        Assert.Contains("P5", cut.Find(".badge-priority-5").TextContent);
        Assert.Contains("Auto", cut.Markup);
    }

    [Fact]
    public void KanbanCard_WhenWaitingForHuman_RendersWaitingBadge()
    {
        var task = new ForgeTask
        {
            Id = 8,
            Title = "Database Migration",
            Status = TaskOverallStatus.Implemented,
            IsWaitingForHuman = true
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task));

        Assert.NotEmpty(cut.FindAll(".badge-waiting-human"));
        Assert.Contains("Waiting for Human", cut.Find(".badge-waiting-human").TextContent);
    }

    [Fact]
    public void KanbanCard_WhenOpenAndPrerequisiteNotClosed_RendersBlockedBadge()
    {
        var prereq = new ForgeTask { Id = 1, Title = "Prereq Task", Status = TaskOverallStatus.Open };
        var task = new ForgeTask { Id = 2, Title = "Dependent Task", Status = TaskOverallStatus.Open };

        var allTasks = new List<ForgeTask> { prereq, task };
        var prereqIds = new List<int> { 1 };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.AllTasks, allTasks)
            .Add(c => c.PrerequisiteIds, prereqIds));

        var blockedBadge = cut.Find(".badge-blocked");
        Assert.Contains("Blocked (1)", blockedBadge.TextContent);
    }

    [Fact]
    public void KanbanCard_WhenOpenAndPrerequisiteClosed_RendersReadyBadge()
    {
        var prereq = new ForgeTask { Id = 1, Title = "Prereq Task", Status = TaskOverallStatus.Closed };
        var task = new ForgeTask { Id = 2, Title = "Dependent Task", Status = TaskOverallStatus.Open };

        var allTasks = new List<ForgeTask> { prereq, task };
        var prereqIds = new List<int> { 1 };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.AllTasks, allTasks)
            .Add(c => c.PrerequisiteIds, prereqIds));

        var readyBadge = cut.Find(".badge-ready");
        Assert.Contains("Ready for Inference", readyBadge.TextContent);
    }

    [Fact]
    public void KanbanCard_WhenSuggested_RendersTriageButton()
    {
        var task = new ForgeTask { Id = 3, Title = "Proposed Item", Status = TaskOverallStatus.Suggested };
        bool promoteInvoked = false;

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.OnPromote, (t) => { promoteInvoked = true; }));

        var triageBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Triage"));
        triageBtn.Click();

        Assert.True(promoteInvoked);
    }

    [Fact]
    public void KanbanBoard_RendersAllFiveColumnsWithCorrectTaskCounts()
    {
        var tasks = new List<ForgeTask>
        {
            new ForgeTask { Id = 1, Title = "Item 1", Status = TaskOverallStatus.Suggested },
            new ForgeTask { Id = 2, Title = "Item 2", Status = TaskOverallStatus.Open },
            new ForgeTask { Id = 3, Title = "Item 3", Status = TaskOverallStatus.Reviewed },
            new ForgeTask { Id = 4, Title = "Item 4", Status = TaskOverallStatus.Ready },
            new ForgeTask { Id = 5, Title = "Item 5", Status = TaskOverallStatus.Closed }
        };

        var cut = Render<KanbanBoard>(p => p
            .Add(b => b.Tasks, tasks));

        var columns = cut.FindAll(".kanban-column");
        Assert.Equal(5, columns.Count);

        Assert.Contains("Suggested (Triage)", columns[0].TextContent);
        Assert.Contains("Open (Backlog)", columns[1].TextContent);
        Assert.Contains("In-Flight", columns[2].TextContent);
        Assert.Contains("Ready for Review", columns[3].TextContent);
        Assert.Contains("Closed & Archived", columns[4].TextContent);

        // Each column should have count of 1
        Assert.Equal("1", columns[0].QuerySelector(".column-count-badge")?.TextContent);
        Assert.Equal("1", columns[1].QuerySelector(".column-count-badge")?.TextContent);
        Assert.Equal("1", columns[2].QuerySelector(".column-count-badge")?.TextContent);
        Assert.Equal("1", columns[3].QuerySelector(".column-count-badge")?.TextContent);
        Assert.Equal("1", columns[4].QuerySelector(".column-count-badge")?.TextContent);
    }

    [Fact]
    public void KanbanCard_WhenOpenAndActiveStageExecuting_RendersCurrentStageBadge()
    {
        var task = new ForgeTask
        {
            Id = 9,
            Title = "Task In Stage 1",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task));

        var stageBadge = cut.Find(".badge-purple");
        Assert.Contains("task-review", stageBadge.TextContent);
        Assert.Empty(cut.FindAll(".badge-ready"));
    }

    [Fact]
    public void KanbanBoard_WhenOpenWithActiveStage_PlacesInInFlightColumn()
    {
        var tasks = new List<ForgeTask>
        {
            new ForgeTask { Id = 1, Title = "Waiting Item", Status = TaskOverallStatus.Open, CurrentStageId = "" },
            new ForgeTask { Id = 2, Title = "Executing Item", Status = TaskOverallStatus.Open, CurrentStageId = "task-review", HasExecutionRun = true }
        };

        var cut = Render<KanbanBoard>(p => p
            .Add(b => b.Tasks, tasks));

        var columns = cut.FindAll(".kanban-column");
        // Open column has 1 task (Waiting Item)
        Assert.Equal("1", columns[1].QuerySelector(".column-count-badge")?.TextContent);
        // In-Flight column has 1 task (Executing Item)
        Assert.Equal("1", columns[2].QuerySelector(".column-count-badge")?.TextContent);
    }
}
