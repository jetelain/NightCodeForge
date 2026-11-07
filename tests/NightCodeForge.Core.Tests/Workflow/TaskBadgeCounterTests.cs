using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class TaskBadgeCounterTests
{
    [Theory]
    [InlineData(TaskOverallStatus.Ready, true, true)]
    [InlineData(TaskOverallStatus.Ready, false, false)]
    [InlineData(TaskOverallStatus.Open, true, false)]
    [InlineData(TaskOverallStatus.Closed, true, false)]
    [InlineData(TaskOverallStatus.Rejected, true, false)]
    [InlineData(TaskOverallStatus.Cancelled, true, false)]
    [InlineData(TaskOverallStatus.Draft, true, false)]
    public void IsPendingHumanReview_ReturnsExpected(TaskOverallStatus status, bool hasExecutionRun, bool expected)
    {
        var task = new ForgeTask
        {
            Id = 1,
            Status = status,
            HasExecutionRun = hasExecutionRun
        };

        Assert.Equal(expected, TaskBadgeCounter.IsPendingHumanReview(task));
    }

    [Theory]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Open, true)]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Ready, true)]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Draft, true)]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Closed, false)]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Rejected, false)]
    [InlineData(QuestionStatus.Pending, TaskOverallStatus.Cancelled, false)]
    [InlineData(QuestionStatus.Answered, TaskOverallStatus.Open, false)]
    [InlineData(QuestionStatus.Skipped, TaskOverallStatus.Open, false)]
    public void IsPendingQuestion_ReturnsExpected(QuestionStatus questionStatus, TaskOverallStatus taskStatus, bool expected)
    {
        var question = new Question { Id = 10, Status = questionStatus };
        var parentTask = new ForgeTask { Id = 1, Status = taskStatus };

        Assert.Equal(expected, TaskBadgeCounter.IsPendingQuestion(question, parentTask));
    }

    [Fact]
    public async Task ComputeCountsAsync_ComputesCorrectPendingQuestionsAndReviews()
    {
        var repo = new InMemoryTaskRepository();

        var openTask = await repo.CreateAsync(new ForgeTask
        {
            Title = "Open with Pending Q",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            Questions = new List<Question>
            {
                new() { Id = 1, TaskId = 1, Prompt = "Q1", Status = QuestionStatus.Pending },
                new() { Id = 2, TaskId = 1, Prompt = "Q2", Status = QuestionStatus.Answered }
            }
        });

        var readyTaskWithRun = await repo.CreateAsync(new ForgeTask
        {
            Title = "Ready with run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            Questions = new List<Question>()
        });

        var readyTaskWithoutRun = await repo.CreateAsync(new ForgeTask
        {
            Title = "Ready without run",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = false,
            Questions = new List<Question>()
        });

        var closedTaskWithPendingQ = await repo.CreateAsync(new ForgeTask
        {
            Title = "Closed with pending Q",
            Status = TaskOverallStatus.Closed,
            HasExecutionRun = true,
            Questions = new List<Question>
            {
                new() { Id = 3, TaskId = 4, Prompt = "Q3", Status = QuestionStatus.Pending }
            }
        });

        var (questionCount, reviewCount) = await TaskBadgeCounter.ComputeCountsAsync(repo);

        // Q1 is pending on Open task; Q3 is pending on Closed task (excluded).
        Assert.Equal(1, questionCount);
        // readyTaskWithRun is pending review; readyTaskWithoutRun has no execution run (excluded).
        Assert.Equal(1, reviewCount);
    }

    [Fact]
    public async Task SynchronizeBadgeCountsAsync_RecordsPendingCountsOnTelemetry()
    {
        var repo = new InMemoryTaskRepository();
        await repo.CreateAsync(new ForgeTask
        {
            Title = "Ready Task",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        });

        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await TaskBadgeCounter.SynchronizeBadgeCountsAsync(repo, telemetryMock.Object);

        telemetryMock.Verify(t => t.RecordPendingCounts(0, 1), Times.Once);
    }
}

