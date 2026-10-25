using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void KanbanCard_WhenRunning_RendersRunningBadgeAndGlowingBorder()
    {
        var task = new ForgeTask
        {
            Id = 99,
            Title = "Active GPU Task",
            Status = TaskOverallStatus.Open
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.IsRunning, true));

        // Check running card class
        var card = cut.Find(".kanban-card");
        Assert.Contains("card-running", card.ClassName);

        // Check running badge and pulse dot
        var badge = cut.Find(".badge-running");
        Assert.NotNull(badge);
        Assert.Contains("Running", badge.TextContent);
        Assert.NotNull(badge.QuerySelector(".pulse-dot"));
    }

    [Fact]
    public void KanbanCard_WhenRunning_RendersRunningBadgeStageNameAndBorder()
    {
        var task = new ForgeTask
        {
            Id = 101,
            Title = "In-Flight Implementation Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-codegen"
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.IsRunning, true));

        var card = cut.Find(".kanban-card");
        Assert.Contains("card-running", card.ClassName);

        var badge = cut.Find(".badge-running");
        Assert.NotNull(badge);
        Assert.Contains("Running", badge.TextContent);

        // Stage badge rendered
        Assert.Contains("Stage: stage-codegen", card.TextContent);
    }

    [Fact]
    public void KanbanCard_WhenRunning_ClickingRunningBadge_NavigatesToInspectorAndStopsPropagation()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var task = new ForgeTask
        {
            Id = 55,
            Title = "Active Inference Task",
            Status = TaskOverallStatus.Open
        };

        bool cardClicked = false;

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.IsRunning, true)
            .Add(c => c.OnClick, EventCallback.Factory.Create<ForgeTask>(this, _ => cardClicked = true)));

        var badge = cut.Find(".badge-running");
        Assert.Equal("/inspector/55", badge.GetAttribute("href"));

        badge.Click();

        Assert.EndsWith("/inspector/55", nav.Uri);
        Assert.False(cardClicked, "Clicking the running badge must not trigger card details OnClick due to stopPropagation");
    }

    [Fact]
    public void KanbanCard_WhenRunning_ClickingRunningBadge_WithCustomOnInspect_InvokesCallback()
    {
        var task = new ForgeTask
        {
            Id = 77,
            Title = "Active Testing Task",
            Status = TaskOverallStatus.Open
        };

        ForgeTask? inspectedTask = null;
        bool cardClicked = false;

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.IsRunning, true)
            .Add(c => c.OnInspect, EventCallback.Factory.Create<ForgeTask>(this, t => inspectedTask = t))
            .Add(c => c.OnClick, EventCallback.Factory.Create<ForgeTask>(this, _ => cardClicked = true)));

        var badge = cut.Find(".badge-running");
        badge.Click();

        Assert.NotNull(inspectedTask);
        Assert.Equal(77, inspectedTask.Id);
        Assert.False(cardClicked, "Clicking the running badge must not trigger card details OnClick");
    }

    [Fact]
    public void KanbanBoard_WhenRunningTask_ClickingRunningBadge_InvokesOnInspectTask()
    {
        var runningTask = new ForgeTask
        {
            Id = 88,
            Title = "Executing In-Flight Task",
            Status = TaskOverallStatus.Open
        };

        ForgeTask? inspectedTask = null;
        bool cardSelected = false;

        var cut = Render<KanbanBoard>(p => p
            .Add(b => b.Tasks, new[] { runningTask })
            .Add(b => b.ActiveTaskId, 88)
            .Add(b => b.OnInspectTask, EventCallback.Factory.Create<ForgeTask>(this, t => inspectedTask = t))
            .Add(b => b.OnTaskSelected, EventCallback.Factory.Create<ForgeTask>(this, _ => cardSelected = true)));

        var badge = cut.Find(".badge-running");
        badge.Click();

        Assert.NotNull(inspectedTask);
        Assert.Equal(88, inspectedTask.Id);
        Assert.False(cardSelected, "Clicking the running badge on the Kanban board must not trigger OnTaskSelected");
    }
}

