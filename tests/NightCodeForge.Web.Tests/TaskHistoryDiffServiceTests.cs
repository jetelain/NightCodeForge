using DiffPlex.DiffBuilder.Model;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskHistoryDiffServiceTests
{
    private readonly TaskHistoryDiffService _service = new();

    [Fact]
    public void BuildTimeline_EmptyList_ReturnsEmpty()
    {
        var result = _service.BuildTimeline(Array.Empty<ForgeTaskHistory>());
        Assert.Empty(result);
    }

    [Fact]
    public void BuildTimeline_SingleVersion_ReturnsBaselineWithoutDiffs()
    {
        var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 100,
                Version = 1,
                Title = "Initial Task",
                Description = "# Task Spec\nLine 1\nLine 2",
                Status = TaskOverallStatus.Open,
                Priority = TaskPriority.Normal,
                MergeMode = MergeMode.ManualReviewRequired,
                BaseBranch = "main",
                FeatureBranch = "feature/task-100",
                CurrentStageId = "stage-1",
                SerializedDependencies = "[10, 20]",
                ChangedAt = now.AddMinutes(-30),
                ChangedBy = "User",
                ChangeReason = "Initial Task Creation"
            }
        };

        var timeline = _service.BuildTimeline(histories, now);

        Assert.Single(timeline);
        var rev = timeline[0];
        Assert.Equal(1, rev.Version);
        Assert.Equal("User", rev.ChangedBy);
        Assert.Equal("Initial Task Creation", rev.ChangeReason);
        Assert.Equal("Initial Task", rev.Title);
        Assert.Equal(new[] { 10, 20 }, rev.Dependencies);
        Assert.Equal("30 minutes ago", rev.TimeAgo);
        Assert.Empty(rev.FieldChanges);
        Assert.False(rev.HasDescriptionDiff);
        Assert.Empty(rev.DiffLines);
    }

    [Fact]
    public void BuildTimeline_MultipleVersions_ComputesFieldChangesAndDiffs()
    {
        var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 101,
                Version = 1,
                Title = "Old Title",
                Description = "Line A\nLine B",
                Status = TaskOverallStatus.Suggested,
                Priority = TaskPriority.Normal,
                MergeMode = MergeMode.ManualReviewRequired,
                BaseBranch = "main",
                FeatureBranch = null,
                CurrentStageId = null,
                SerializedDependencies = "[]",
                ChangedAt = now.AddHours(-2),
                ChangedBy = "IdeationAgent",
                ChangeReason = "Suggestion created"
            },
            new ForgeTaskHistory
            {
                Id = 2,
                TaskId = 101,
                Version = 2,
                Title = "New Title",
                Description = "Line A\nLine B Modified\nLine C",
                Status = TaskOverallStatus.Open,
                Priority = TaskPriority.Critical,
                MergeMode = MergeMode.Automatic,
                BaseBranch = "develop",
                FeatureBranch = "feature/task-101",
                CurrentStageId = "stage-1",
                SerializedDependencies = "[42]",
                ChangedAt = now.AddMinutes(-10),
                ChangedBy = "User",
                ChangeReason = "Refined and promoted"
            }
        };

        var timeline = _service.BuildTimeline(histories, now);

        // Timeline is ordered descending by version (newest first)
        Assert.Equal(2, timeline.Count);
        var rev2 = timeline[0];
        var rev1 = timeline[1];

        Assert.Equal(2, rev2.Version);
        Assert.Equal(1, rev1.Version);

        // Version 2 should have field changes compared to Version 1
        Assert.True(rev2.FieldChanges.Count > 0);

        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "Title" && c.OldValue == "Old Title" && c.NewValue == "New Title");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "Status" && c.OldValue == "Suggested" && c.NewValue == "Open");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "Priority" && c.OldValue == "P3" && c.NewValue == "P1");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "MergeMode" && c.OldValue == "ManualReviewRequired" && c.NewValue == "Automatic");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "BaseBranch" && c.OldValue == "main" && c.NewValue == "develop");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "FeatureBranch" && c.OldValue == "None" && c.NewValue == "feature/task-101");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "Stage" && c.OldValue == "None" && c.NewValue == "stage-1");
        Assert.Contains(rev2.FieldChanges, c => c.FieldName == "Dependencies" && c.OldValue == "None" && c.NewValue == "[#42]");

        // Description diff check
        Assert.True(rev2.HasDescriptionDiff);
        Assert.True(rev2.DescriptionLinesAdded > 0);
        Assert.NotEmpty(rev2.DiffLines);
        Assert.Contains(rev2.DiffLines, l => l.Type == ChangeType.Inserted || l.Type == ChangeType.Modified);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(120, "2 minutes ago")]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(86400, "yesterday")]
    [InlineData(172800, "2 days ago")]
    public void FormatRelativeTime_ReturnsExpectedLabels(int secondsAgo, string expectedLabel)
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var past = now.AddSeconds(-secondsAgo);
        var label = TaskHistoryDiffService.FormatRelativeTime(past, now);
        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public void BuildTimeline_MalformedDependenciesJson_HandlesGracefullyWithoutThrowing()
    {
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 102,
                Version = 1,
                Title = "Task with bad JSON",
                SerializedDependencies = "{not a json array}",
                ChangedAt = DateTime.UtcNow,
                ChangedBy = "system"
            }
        };

        var timeline = _service.BuildTimeline(histories);
        Assert.Single(timeline);
        Assert.Empty(timeline[0].Dependencies);
    }

    [Theory]
    [InlineData("User", "badge-indigo", "👤 User")]
    [InlineData("user", "badge-indigo", "👤 User")]
    [InlineData("TaskOrchestrator", "badge-teal", "🤖 TaskOrchestrator")]
    [InlineData("IdeationAgent", "badge-purple", "💡 IdeationAgent")]
    [InlineData("agent:task-review", "badge-amber", "🤖 agent:task-review")]
    [InlineData("system", "badge-gray", "⚙️ system")]
    [InlineData("", "badge-gray", "⚙️ system")]
    public void ActorHelpers_ReturnExpectedBadgesAndDisplayLabels(string actor, string expectedBadge, string expectedDisplay)
    {
        var badge = TaskHistoryDiffService.GetActorBadgeClass(actor);
        var display = TaskHistoryDiffService.GetActorDisplay(actor);

        Assert.Equal(expectedBadge, badge);
        Assert.Equal(expectedDisplay, display);
    }

    [Fact]
    public void TaskHistoryDiffLine_FormattingProperties_ReturnExpectedPrefixAndStyles()
    {
        var inserted = new TaskHistoryDiffLine(null, 1, ChangeType.Inserted, "new line");
        Assert.Equal("+", inserted.Prefix);
        Assert.Contains("rgba(16, 185, 129", inserted.BackgroundStyle);
        Assert.Equal("#6ee7b7", inserted.TextColor);

        var deleted = new TaskHistoryDiffLine(1, null, ChangeType.Deleted, "deleted line");
        Assert.Equal("-", deleted.Prefix);
        Assert.Contains("rgba(239, 68, 68", deleted.BackgroundStyle);
        Assert.Equal("#fca5a5", deleted.TextColor);

        var modified = new TaskHistoryDiffLine(1, 1, ChangeType.Modified, "modified line");
        Assert.Equal("~", modified.Prefix);
        Assert.Contains("rgba(245, 158, 11", modified.BackgroundStyle);
        Assert.Equal("#fde68a", modified.TextColor);

        var unchanged = new TaskHistoryDiffLine(1, 1, ChangeType.Unchanged, "same line");
        Assert.Equal(" ", unchanged.Prefix);
        Assert.Equal("transparent", unchanged.BackgroundStyle);
    }

    [Fact]
    public void BuildTimeline_PopulatesActorDisplayAndBadgeClassOnModel()
    {
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 105,
                Version = 1,
                Title = "Task with actor",
                ChangedAt = DateTime.UtcNow,
                ChangedBy = AuditActors.IdeationAgent
            }
        };

        var timeline = _service.BuildTimeline(histories);
        Assert.Single(timeline);
        Assert.Equal("badge-purple", timeline[0].ActorBadgeClass);
        Assert.Equal("💡 IdeationAgent", timeline[0].ActorDisplay);
    }

    [Fact]
    public void BuildTimeline_NormalizesLineEndings_DoesNotReportFalseDiff()
    {
        var now = DateTime.UtcNow;
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 106,
                Version = 1,
                Title = "Task with CRLF",
                Description = "Line 1\r\nLine 2\r\nLine 3",
                ChangedAt = now.AddMinutes(-5),
                ChangedBy = "User"
            },
            new ForgeTaskHistory
            {
                Id = 2,
                TaskId = 106,
                Version = 2,
                Title = "Task with CRLF",
                Description = "Line 1\nLine 2\nLine 3",
                ChangedAt = now,
                ChangedBy = "User"
            }
        };

        var timeline = _service.BuildTimeline(histories, now);
        Assert.Equal(2, timeline.Count);
        Assert.False(timeline[0].HasDescriptionDiff);
        Assert.Equal(1, timeline[0].PreviousVersion);
    }

    [Fact]
    public void BuildTimeline_SetsPreviousVersionCorrectly_EvenWhenVersionNumbersAreNonConsecutive()
    {
        var now = DateTime.UtcNow;
        var histories = new[]
        {
            new ForgeTaskHistory { Id = 1, TaskId = 107, Version = 1, Title = "V1", ChangedAt = now.AddHours(-1) },
            new ForgeTaskHistory { Id = 2, TaskId = 107, Version = 4, Title = "V4", ChangedAt = now.AddMinutes(-30) },
            new ForgeTaskHistory { Id = 3, TaskId = 107, Version = 9, Title = "V9", ChangedAt = now }
        };

        var timeline = _service.BuildTimeline(histories, now);
        Assert.Equal(3, timeline.Count);
        Assert.Equal(9, timeline[0].Version);
        Assert.Equal(4, timeline[0].PreviousVersion);
        Assert.Equal(4, timeline[1].Version);
        Assert.Equal(1, timeline[1].PreviousVersion);
        Assert.Equal(1, timeline[2].Version);
        Assert.Equal(0, timeline[2].PreviousVersion);
    }

    [Fact]
    public void FormatRelativeTime_HandlesUnspecifiedDateTimeKind_WithoutThrowing()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var unspecifiedPast = new DateTime(2026, 10, 20, 11, 0, 0, DateTimeKind.Unspecified);
        var label = TaskHistoryDiffService.FormatRelativeTime(unspecifiedPast, now);
        Assert.Equal("1 hour ago", label);
    }

    [Fact]
    public void ActorHelpers_HandlesFileWatcherActor()
    {
        Assert.Equal("badge-blue", TaskHistoryDiffService.GetActorBadgeClass(AuditActors.FileWatcher));
        Assert.Equal("📁 file-watcher", TaskHistoryDiffService.GetActorDisplay(AuditActors.FileWatcher));
    }

    [Fact]
    public void BuildTimeline_DependenciesDifferentOrder_DoesNotReportFieldChange()
    {
        var now = DateTime.UtcNow;
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 110,
                Version = 1,
                Title = "Task",
                SerializedDependencies = "[10, 20]",
                ChangedAt = now.AddMinutes(-10),
                ChangedBy = "User"
            },
            new ForgeTaskHistory
            {
                Id = 2,
                TaskId = 110,
                Version = 2,
                Title = "Task",
                SerializedDependencies = "[20, 10]",
                ChangedAt = now,
                ChangedBy = "User"
            }
        };

        var timeline = _service.BuildTimeline(histories, now);
        Assert.Equal(2, timeline.Count);
        Assert.DoesNotContain(timeline[0].FieldChanges, c => c.FieldName == "Dependencies");
    }

    [Fact]
    public void BuildTimeline_DependenciesChanged_SortsBadgeText()
    {
        var now = DateTime.UtcNow;
        var histories = new[]
        {
            new ForgeTaskHistory
            {
                Id = 1,
                TaskId = 111,
                Version = 1,
                Title = "Task",
                SerializedDependencies = "[30, 10]",
                ChangedAt = now.AddMinutes(-10),
                ChangedBy = "User"
            },
            new ForgeTaskHistory
            {
                Id = 2,
                TaskId = 111,
                Version = 2,
                Title = "Task",
                SerializedDependencies = "[50, 20]",
                ChangedAt = now,
                ChangedBy = "User"
            }
        };

        var timeline = _service.BuildTimeline(histories, now);
        Assert.Equal(2, timeline.Count);
        var depChange = timeline[0].FieldChanges.FirstOrDefault(c => c.FieldName == "Dependencies");
        Assert.NotNull(depChange);
        Assert.Equal("[#10, #30]", depChange.OldValue);
        Assert.Equal("[#20, #50]", depChange.NewValue);
    }
}
