using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests.Services;

public class TaskRecencyAndRetentionHelperTests
{
    [Fact]
    public void IsRecentTerminal_ReturnsTrueForFailedOrClosedWithin24Hours()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        var recentFailed = new ForgeTask
        {
            Id = 1,
            Status = TaskOverallStatus.Failed,
            UpdatedAt = now.AddHours(-2)
        };

        var recentClosed = new ForgeTask
        {
            Id = 2,
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddHours(-23)
        };

        var oldClosed = new ForgeTask
        {
            Id = 3,
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddHours(-25)
        };

        var recentOpen = new ForgeTask
        {
            Id = 4,
            Status = TaskOverallStatus.Open,
            UpdatedAt = now.AddHours(-1)
        };

        Assert.True(TaskRecencyAndRetentionHelper.IsRecentTerminal(recentFailed, now));
        Assert.True(TaskRecencyAndRetentionHelper.IsRecentTerminal(recentClosed, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsRecentTerminal(oldClosed, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsRecentTerminal(recentOpen, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsRecentTerminal(null, now));
    }

    [Fact]
    public void IsExcludedFromBacklog_ExcludesClosedOlderThan7Days_PreservesUnresolvedFailedTasks()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        var recentClosed = new ForgeTask
        {
            Id = 1,
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddDays(-3)
        };

        var oldClosed = new ForgeTask
        {
            Id = 2,
            Status = TaskOverallStatus.Closed,
            UpdatedAt = now.AddDays(-8)
        };

        var oldRejected = new ForgeTask
        {
            Id = 3,
            Status = TaskOverallStatus.Rejected,
            UpdatedAt = now.AddDays(-10)
        };

        var oldCancelled = new ForgeTask
        {
            Id = 4,
            Status = TaskOverallStatus.Cancelled,
            UpdatedAt = now.AddDays(-14)
        };

        // Unresolved failed tasks must remain visible even if old
        var oldFailed = new ForgeTask
        {
            Id = 5,
            Status = TaskOverallStatus.Failed,
            UpdatedAt = now.AddDays(-20)
        };

        var openTask = new ForgeTask
        {
            Id = 6,
            Status = TaskOverallStatus.Open,
            UpdatedAt = now.AddDays(-30)
        };

        Assert.False(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(recentClosed, now));
        Assert.True(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(oldClosed, now));
        Assert.True(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(oldRejected, now));
        Assert.True(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(oldCancelled, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(oldFailed, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsExcludedFromBacklog(openTask, now));

        Assert.True(TaskRecencyAndRetentionHelper.IsRetainedInBacklog(recentClosed, now));
        Assert.False(TaskRecencyAndRetentionHelper.IsRetainedInBacklog(oldClosed, now));
        Assert.True(TaskRecencyAndRetentionHelper.IsRetainedInBacklog(oldFailed, now));
    }

    [Fact]
    public void OrderClosedColumnTasks_SortsRecentTasksFirst_WithFailedFirst_ThenByUpdatedAtDesc()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

        var tOldClosed = new ForgeTask { Id = 1, Status = TaskOverallStatus.Closed, UpdatedAt = now.AddDays(-3) };
        var tRecentClosed = new ForgeTask { Id = 2, Status = TaskOverallStatus.Closed, UpdatedAt = now.AddHours(-2) };
        var tRecentFailed = new ForgeTask { Id = 3, Status = TaskOverallStatus.Failed, UpdatedAt = now.AddHours(-5) };
        var tRecentFailedNewer = new ForgeTask { Id = 4, Status = TaskOverallStatus.Failed, UpdatedAt = now.AddHours(-1) };

        var tasks = new[] { tOldClosed, tRecentClosed, tRecentFailed, tRecentFailedNewer };

        var ordered = TaskRecencyAndRetentionHelper.OrderClosedColumnTasks(tasks, now).ToList();

        // 1st & 2nd: Recent Failed (newer first) -> Id 4, then Id 3
        Assert.Equal(4, ordered[0].Id);
        Assert.Equal(3, ordered[1].Id);

        // 3rd: Recent Closed -> Id 2
        Assert.Equal(2, ordered[2].Id);

        // 4th: Older Closed -> Id 1
        Assert.Equal(1, ordered[3].Id);
    }
}

