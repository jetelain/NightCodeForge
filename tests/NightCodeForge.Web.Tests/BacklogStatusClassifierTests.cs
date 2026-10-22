using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class BacklogStatusClassifierTests
{
    [Fact]
    public void GetColumn_WhenOpenAndNotExecuted_ReturnsOpen_EvenWithCurrentStageId()
    {
        var task = new ForgeTask
        {
            Id = 4,
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            CurrentStageId = "task-review"
        };

        var col = BacklogStatusClassifier.GetColumn(task);

        Assert.Equal(BacklogKanbanColumn.Open, col);
        Assert.True(BacklogStatusClassifier.IsOpenBacklog(task));
        Assert.False(BacklogStatusClassifier.IsInFlight(task));
    }

    [Fact]
    public void GetColumn_WhenOpenAndHasExecutionRun_ReturnsInFlight()
    {
        var task = new ForgeTask
        {
            Id = 4,
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            CurrentStageId = "task-review"
        };

        var col = BacklogStatusClassifier.GetColumn(task);

        Assert.Equal(BacklogKanbanColumn.InFlight, col);
        Assert.True(BacklogStatusClassifier.IsInFlight(task));
        Assert.False(BacklogStatusClassifier.IsOpenBacklog(task));
    }

    [Fact]
    public void GetColumn_WhenReviewedOrImplemented_ReturnsInFlight()
    {
        var reviewedTask = new ForgeTask { Id = 1, Status = TaskOverallStatus.Reviewed };
        var implementedTask = new ForgeTask { Id = 2, Status = TaskOverallStatus.Implemented };

        Assert.Equal(BacklogKanbanColumn.InFlight, BacklogStatusClassifier.GetColumn(reviewedTask));
        Assert.Equal(BacklogKanbanColumn.InFlight, BacklogStatusClassifier.GetColumn(implementedTask));
    }

    [Fact]
    public void GetColumn_WhenSuggested_ReturnsSuggested()
    {
        var task = new ForgeTask { Id = 3, Status = TaskOverallStatus.Suggested };

        Assert.Equal(BacklogKanbanColumn.Suggested, BacklogStatusClassifier.GetColumn(task));
    }

    [Fact]
    public void GetColumn_WhenReady_ReturnsReadyForReview()
    {
        var task = new ForgeTask { Id = 5, Status = TaskOverallStatus.Ready };

        Assert.Equal(BacklogKanbanColumn.ReadyForReview, BacklogStatusClassifier.GetColumn(task));
    }

    [Fact]
    public void GetColumn_WhenClosedOrRejectedOrFailedOrCancelled_ReturnsClosed()
    {
        var closed = new ForgeTask { Id = 6, Status = TaskOverallStatus.Closed };
        var rejected = new ForgeTask { Id = 7, Status = TaskOverallStatus.Rejected };
        var failed = new ForgeTask { Id = 8, Status = TaskOverallStatus.Failed };
        var cancelled = new ForgeTask { Id = 9, Status = TaskOverallStatus.Cancelled };

        Assert.Equal(BacklogKanbanColumn.Closed, BacklogStatusClassifier.GetColumn(closed));
        Assert.Equal(BacklogKanbanColumn.Closed, BacklogStatusClassifier.GetColumn(rejected));
        Assert.Equal(BacklogKanbanColumn.Closed, BacklogStatusClassifier.GetColumn(failed));
        Assert.Equal(BacklogKanbanColumn.Closed, BacklogStatusClassifier.GetColumn(cancelled));
        Assert.False(BacklogStatusClassifier.IsOpenBacklog(cancelled));
        Assert.False(BacklogStatusClassifier.IsInFlight(cancelled));
    }
}
