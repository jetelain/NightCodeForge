using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskHistoryTimelineComponentTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly TaskHistoryDiffService _diffService;

    public TaskHistoryTimelineComponentTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _diffService = new TaskHistoryDiffService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskHistoryDiffService>(_diffService);
    }

    [Fact]
    public void TaskHistoryTimeline_WhenNoHistory_RendersEmptyState()
    {
        var cut = Render<TaskHistoryTimeline>(p => p
            .Add(t => t.TaskId, 999)
            .Add(t => t.History, Array.Empty<ForgeTaskHistory>()));

        Assert.Contains("No History Recorded", cut.Markup);
    }

    [Fact]
    public void TaskHistoryTimeline_RendersVersionsDescending_WithBadgesAndActors()
    {
        var now = DateTime.UtcNow;
        var histories = new List<ForgeTaskHistory>
        {
            new()
            {
                Id = 1,
                TaskId = 200,
                Version = 1,
                Title = "Initial Title",
                Description = "Line 1\nLine 2",
                Status = TaskOverallStatus.Suggested,
                Priority = TaskPriority.Normal,
                MergeMode = MergeMode.ManualReviewRequired,
                ChangedAt = now.AddMinutes(-20),
                ChangedBy = "IdeationAgent",
                ChangeReason = "Created from ideation"
            },
            new()
            {
                Id = 2,
                TaskId = 200,
                Version = 2,
                Title = "Updated Title",
                Description = "Line 1\nLine 2\nLine 3 New",
                Status = TaskOverallStatus.Open,
                Priority = TaskPriority.Critical,
                MergeMode = MergeMode.Automatic,
                ChangedAt = now.AddMinutes(-5),
                ChangedBy = "User",
                ChangeReason = "Updated Priority to Critical and clarified acceptance criteria"
            }
        };

        var cut = Render<TaskHistoryTimeline>(p => p
            .Add(t => t.TaskId, 200)
            .Add(t => t.History, histories));

        // Check header count
        Assert.Contains("2 revisions", cut.Markup);

        // Check version rendering (Version 2 first, then Version 1)
        var versionNodes = cut.FindAll(".timeline-node");
        Assert.Equal(2, versionNodes.Count);

        // First node is latest (Version 2)
        Assert.Contains("Version 2", versionNodes[0].TextContent);
        Assert.Contains("Current State", versionNodes[0].TextContent);
        Assert.Contains("User", versionNodes[0].TextContent);
        Assert.Contains("Updated Priority to Critical and clarified acceptance criteria", versionNodes[0].TextContent);

        // Field change badges in Version 2
        Assert.Contains("Priority:", versionNodes[0].TextContent);
        Assert.Contains("P3", versionNodes[0].TextContent);
        Assert.Contains("P1", versionNodes[0].TextContent);

        Assert.Contains("Status:", versionNodes[0].TextContent);
        Assert.Contains("Suggested", versionNodes[0].TextContent);
        Assert.Contains("Open", versionNodes[0].TextContent);

        // Second node is baseline (Version 1)
        Assert.Contains("Version 1", versionNodes[1].TextContent);
        Assert.Contains("IdeationAgent", versionNodes[1].TextContent);
        Assert.Contains("Created from ideation", versionNodes[1].TextContent);
        Assert.Contains("Initial baseline snapshot created", versionNodes[1].TextContent);
    }

    [Fact]
    public void TaskHistoryTimeline_ClickingToggleDiff_ExpandsAndCollapsesDiffSection()
    {
        var now = DateTime.UtcNow;
        var histories = new List<ForgeTaskHistory>
        {
            new()
            {
                Id = 1,
                TaskId = 201,
                Version = 1,
                Title = "Task 201",
                Description = "Old specification body\nLine 2",
                Status = TaskOverallStatus.Open,
                ChangedAt = now.AddHours(-1),
                ChangedBy = "User",
                ChangeReason = "Initial Creation"
            },
            new()
            {
                Id = 2,
                TaskId = 201,
                Version = 2,
                Title = "Task 201",
                Description = "Old specification body\nLine 2\nLine 3 Addition",
                Status = TaskOverallStatus.Open,
                ChangedAt = now.AddMinutes(-10),
                ChangedBy = "User",
                ChangeReason = "Added Line 3"
            }
        };

        var cut = Render<TaskHistoryTimeline>(p => p
            .Add(t => t.TaskId, 201)
            .Add(t => t.History, histories));

        // When only 1 diff exists, it is expanded automatically by default
        Assert.Contains("Description Diff: Version 1 → Version 2", cut.Markup);
        Assert.Contains("Line 3 Addition", cut.Markup);

        // Click button to collapse
        var toggleBtn = cut.Find(".toggle-diff-btn");
        toggleBtn.Click();

        // Diff pane should now be hidden
        Assert.DoesNotContain("Description Diff: Version 1 → Version 2", cut.Markup);

        // Click again to expand
        toggleBtn = cut.Find(".toggle-diff-btn");
        toggleBtn.Click();
        Assert.Contains("Description Diff: Version 1 → Version 2", cut.Markup);
    }

    [Fact]
    public async Task TaskHistoryTimeline_LoadsFromRepository_WhenHistoryParameterIsNull()
    {
        var task = new ForgeTask
        {
            Id = 300,
            WorkspaceId = 1,
            Title = "Repo Loaded Task",
            Description = "Initial description",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Normal
        };

        var created = await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "Created in repo");
        created.Priority = TaskPriority.Critical;
        await _taskRepository.UpdateWithHistoryAsync(created, null, AuditActors.User, "Escalated to P1");

        var cut = Render<TaskHistoryTimeline>(p => p
            .Add(t => t.TaskId, 300));

        Assert.Contains("2 revisions", cut.Markup);
        Assert.Contains("Version 2", cut.Markup);
        Assert.Contains("Version 1", cut.Markup);
        Assert.Contains("Escalated to P1", cut.Markup);
    }
}
