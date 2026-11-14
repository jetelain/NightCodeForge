using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.GitHub;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class GitHubTaskMirrorServiceTests
{
    private readonly Mock<ITaskRepository> _taskRepoMock = new();
    private readonly Mock<ITaskDependencyStore> _depStoreMock = new();
    private readonly Mock<IGitHubClientService> _gitHubClientMock = new();
    private readonly Mock<IGitHubTokenStore> _tokenStoreMock = new();
    private readonly Mock<IGitHubDeviceAuthService> _deviceAuthMock = new();
    private readonly Mock<IAgentActivityStreamer> _streamerMock = new();
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();

    public GitHubTaskMirrorServiceTests()
    {
        _tokenStoreMock
            .Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload
            {
                AccessToken = "gho_test_token",
                AuthenticatedUserLogin = "octocat"
            });

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
    }

    private GitHubTaskMirrorService CreateSut(bool airGappedMode = false) => new(
        _taskRepoMock.Object,
        _depStoreMock.Object,
        _gitHubClientMock.Object,
        _tokenStoreMock.Object,
        _deviceAuthMock.Object,
        _streamerMock.Object,
        _telemetryMock.Object,
        Options.Create(new SecurityOptions { AirGappedMode = airGappedMode }),
        NullLogger<GitHubTaskMirrorService>.Instance);

    private static Workspace CreateConfiguredWorkspace(Action<GitHubIntegrationSettings>? configure = null)
    {
        var settings = new GitHubIntegrationSettings
        {
            Enabled = true,
            RepositoryOwner = "acme",
            RepositoryName = "nightcodeforge",
            TaskMirroringEnabled = true,
            AutoAssignIssueOnStart = true,
            SyncStatusLabels = true,
            PostMilestoneComments = true,
            CloseTaskOnPrMerge = true,
            PullRequestDeliveryEnabled = true,
            CreateGitHubIssuesForLocalTasks = false
        };
        configure?.Invoke(settings);

        return new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main",
            GitHubSettings = settings
        };
    }

    private static ForgeTask CreateLinkedTask(int id = 10, string issueRef = "42", TaskOverallStatus status = TaskOverallStatus.Open) => new()
    {
        Id = id,
        WorkspaceId = 1,
        Title = $"Implement feature #{issueRef}",
        Description = "Detailed task specification",
        Status = status,
        BaseBranch = "main",
        ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
        ExternalReferenceId = issueRef
    };

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        var sec = Options.Create(new SecurityOptions());
        var logger = NullLogger<GitHubTaskMirrorService>.Instance;

        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(null!, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, null!, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, null!, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, null!, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, null!, _streamerMock.Object, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, null!, _telemetryMock.Object, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, null!, sec, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new GitHubTaskMirrorService(_taskRepoMock.Object, _depStoreMock.Object, _gitHubClientMock.Object, _tokenStoreMock.Object, _deviceAuthMock.Object, _streamerMock.Object, _telemetryMock.Object, sec, null!));
    }

    [Fact]
    public async Task AllOperations_WhenAirGappedModeEnabled_SkipAllOutboundGitHubCalls()
    {
        var sut = CreateSut(airGappedMode: true);
        var workspace = CreateConfiguredWorkspace(s => s.CreateGitHubIssuesForLocalTasks = true);
        var task = CreateLinkedTask();
        task.PullRequestNumber = 99;
        var callbackInvoked = false;

        var claimResult = await sut.TryClaimIssueForExecutionAsync(task, workspace);
        await sut.MirrorTaskOpenedAsync(task, workspace);
        await sut.MirrorTaskReviewCompletedAsync(task, workspace, "Plan");
        await sut.MirrorPostImplementationGatesCompletedAsync(task, workspace);
        await sut.MirrorTaskReadyAsync(task, workspace);
        await sut.MirrorTaskClosedAsync(task, workspace);
        var syncedIds = await sut.SyncMergedPullRequestsAsync(workspace, (_, _) =>
        {
            callbackInvoked = true;
            return Task.CompletedTask;
        });

        Assert.True(claimResult);
        Assert.Empty(syncedIds);
        Assert.False(callbackInvoked);
        _gitHubClientMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OutboundApiFailures_LogWarningAndNeverThrow()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask();

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("502 Bad Gateway"));
        _gitHubClientMock
            .Setup(c => c.AddIssueCommentAsync("acme", "nightcodeforge", 42, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Rate limit exceeded"));

        var claimResult = await sut.TryClaimIssueForExecutionAsync(task, workspace);
        await sut.MirrorTaskReviewCompletedAsync(task, workspace, "Spec");

        Assert.True(claimResult);
        _taskRepoMock.Verify(
            r => r.AddExecutionLogAsync(
                It.Is<ExecutionLog>(l => l.TaskId == task.Id && l.Level == LogLevel.Warning),
                It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenIssueClosedExternallyAndNoClosedSibling_CancelsTaskAndReturnsFalse()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        _taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([task]);

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, State = "closed" });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.False(allowed);
        Assert.Equal(TaskOverallStatus.Cancelled, task.Status);
        _taskRepoMock.Verify(
            r => r.UpdateWithHistoryAsync(
                It.Is<ForgeTask>(t => t.Id == 10 && t.Status == TaskOverallStatus.Cancelled),
                null,
                AuditActors.System,
                It.Is<string>(s => s.Contains("closed on GitHub", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _depStoreMock.Verify(d => d.UpdateTaskStatusAsync(10, TaskOverallStatus.Cancelled, It.IsAny<CancellationToken>()), Times.Once);
        _taskRepoMock.Verify(r => r.AddExecutionLogAsync(It.Is<ExecutionLog>(l => l.TaskId == 10 && l.Level == LogLevel.Warning), It.IsAny<CancellationToken>()), Times.Once);
        _streamerMock.Verify(s => s.BroadcastAsync(It.Is<AgentActivityEvent>(e => e.TaskId == 10 && e.Kind == AgentActivityKind.Notice), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenIssueClosedExternallyButLocalSiblingClosed_Proceeds()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var sibling = CreateLinkedTask(9, "42", TaskOverallStatus.Closed);
        var task = CreateLinkedTask(10, "42", TaskOverallStatus.Open);
        _taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([sibling, task]);

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, State = "closed", Labels = [GitHubSyncConstants.LabelClosed] });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        Assert.Equal(TaskOverallStatus.Open, task.Status);
        _depStoreMock.Verify(d => d.UpdateTaskStatusAsync(It.IsAny<int>(), TaskOverallStatus.Cancelled, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenAssignedToAnotherUser_SuspendsWithHumanClarificationAndReturnsFalse()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        _taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([task]);

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Assignees = ["teammate"]
            });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.False(allowed);
        Assert.True(task.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.TaskReview, task.InterruptedStageId);
        Assert.Equal(StandardStageIds.TaskReview, task.CurrentStageId);

        var question = Assert.Single(task.Questions);
        Assert.Equal(QuestionStatus.Pending, question.Status);
        Assert.Equal(QuestionType.SingleChoice, question.QuestionType);
        Assert.StartsWith(GitHubClaimConflictConstants.ClaimConflictBatchPrefix, question.BatchId);
        Assert.Contains(GitHubClaimConflictConstants.OverrideClaimOption, question.OptionsJson);
        Assert.Contains(GitHubClaimConflictConstants.CancelTaskOption, question.OptionsJson);

        _depStoreMock.Verify(d => d.SetWaitingForHumanAsync(10, true, It.IsAny<CancellationToken>()), Times.Once);
        _streamerMock.Verify(
            s => s.BroadcastAsync(
                It.Is<AgentActivityEvent>(e => e.TaskId == 10 && e.Kind == AgentActivityKind.Interrupted),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenUnassignedButCarriesActiveExecutionLabel_SuspendsWithHumanClarification()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        _taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([task]);

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Assignees = Array.Empty<string>(),
                Labels = [GitHubSyncConstants.LabelInProgress, "enhancement"]
            });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.False(allowed);
        Assert.True(task.IsWaitingForHuman);
        Assert.Single(task.Questions);
        _depStoreMock.Verify(d => d.SetWaitingForHumanAsync(10, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenUnclaimedAndMirroringEnabled_AutoAssignsAndAppliesInProgressLabel()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        UpdateGitHubIssueRequest? capturedRequest = null;

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Labels = [GitHubSyncConstants.LabelBacklog, "bug"]
            });
        _gitHubClientMock
            .Setup(c => c.UpdateIssueAsync("acme", "nightcodeforge", 42, It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, int, UpdateGitHubIssueRequest, CancellationToken>((_, _, _, req, _) => capturedRequest = req)
            .ReturnsAsync(new GitHubIssue { Number = 42 });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        Assert.NotNull(capturedRequest);
        Assert.Equal(["octocat"], capturedRequest!.Assignees);
        Assert.Contains("bug", capturedRequest.Labels!);
        Assert.Contains(GitHubSyncConstants.LabelInProgress, capturedRequest.Labels!);
        Assert.DoesNotContain(GitHubSyncConstants.LabelBacklog, capturedRequest.Labels!);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenConflictOverriddenByUser_ProceedsAndClaimsIssue()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        task.Questions.Add(new Question
        {
            Id = 5,
            TaskId = 10,
            BatchId = GitHubClaimConflictConstants.ClaimConflictBatchPrefix + "b1",
            Prompt = "Override?",
            Status = QuestionStatus.Answered,
            Answer = GitHubClaimConflictConstants.OverrideClaimOption,
            AnsweredAt = DateTime.UtcNow
        });

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Assignees = ["teammate"],
                Labels = [GitHubSyncConstants.LabelInProgress]
            });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme",
                "nightcodeforge",
                42,
                It.Is<UpdateGitHubIssueRequest>(r =>
                    r.Assignees != null &&
                    r.Assignees.Contains("octocat") &&
                    r.Labels != null &&
                    r.Labels.Contains(GitHubSyncConstants.LabelInProgress)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TryHandleClaimConflictAnswerAsync_HandlesCancelAndOverrideOptions()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var batchId = GitHubClaimConflictConstants.ClaimConflictBatchPrefix + "b2";

        var cancelTask = CreateLinkedTask(21, "42");
        var cancelQuestion = new Question
        {
            TaskId = 21,
            BatchId = batchId,
            Status = QuestionStatus.Answered,
            Answer = GitHubClaimConflictConstants.CancelTaskOption
        };

        var cancelled = await sut.TryHandleClaimConflictAnswerAsync(cancelTask, workspace, [cancelQuestion]);

        Assert.True(cancelled);
        Assert.Equal(TaskOverallStatus.Cancelled, cancelTask.Status);
        _depStoreMock.Verify(d => d.UpdateTaskStatusAsync(21, TaskOverallStatus.Cancelled, It.IsAny<CancellationToken>()), Times.Once);

        var overrideTask = CreateLinkedTask(22, "42");
        var overrideQuestion = new Question
        {
            TaskId = 22,
            BatchId = batchId,
            Status = QuestionStatus.Answered,
            Answer = GitHubClaimConflictConstants.OverrideClaimOption
        };

        var cancelledOnOverride = await sut.TryHandleClaimConflictAnswerAsync(overrideTask, workspace, [overrideQuestion]);

        Assert.False(cancelledOnOverride);
        Assert.Equal(TaskOverallStatus.Open, overrideTask.Status);
    }

    [Fact]
    public async Task MirrorTaskOpenedAsync_SwapsTriagedToBacklogOrCreatesIssueForLocalTask()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace(s => s.CreateGitHubIssuesForLocalTasks = true);

        // 1. Linked task swaps ncf:triaged -> ncf:backlog
        var linkedTask = CreateLinkedTask(10, "42");
        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                Labels = [GitHubSyncConstants.LabelTriaged, "priority-high"]
            });

        await sut.MirrorTaskOpenedAsync(linkedTask, workspace);

        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme",
                "nightcodeforge",
                42,
                It.Is<UpdateGitHubIssueRequest>(r =>
                    r.Labels != null &&
                    r.Labels.Contains(GitHubSyncConstants.LabelBacklog) &&
                    r.Labels.Contains("priority-high") &&
                    !r.Labels.Contains(GitHubSyncConstants.LabelTriaged)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // 2. Unlinked local task creates issue when CreateGitHubIssuesForLocalTasks == true
        var localTask = new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Add telemetry metrics",
            Description = "Track latency histogram",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        };

        _gitHubClientMock
            .Setup(c => c.CreateIssueAsync("acme", "nightcodeforge", It.IsAny<CreateGitHubIssueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 105, HtmlUrl = "https://github.com/acme/nightcodeforge/issues/105" });

        await sut.MirrorTaskOpenedAsync(localTask, workspace);

        Assert.Equal(GitHubSyncConstants.ExternalSourceGitHub, localTask.ExternalSource);
        Assert.Equal("105", localTask.ExternalReferenceId);
        _gitHubClientMock.Verify(
            c => c.CreateIssueAsync(
                "acme",
                "nightcodeforge",
                It.Is<CreateGitHubIssueRequest>(r =>
                    r.Title == "Add telemetry metrics" &&
                    r.Labels != null && r.Labels.Contains(GitHubSyncConstants.LabelBacklog) &&
                    r.Assignees != null && r.Assignees.Contains("octocat")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StageAndReadyMilestones_ApplyStatusLabelsAndPostFormattedComments()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42", TaskOverallStatus.Ready);
        task.PullRequestNumber = 77;
        task.PullRequestUrl = "https://github.com/acme/nightcodeforge/pull/77";

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 24);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, 24);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 0);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 88.50);
        bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 85.00);
        bag.Set(PipelineDataBagKeys.CoverageDelta, 3.50);
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 1);
        bag.Set(PipelineDataBagKeys.WarningsBaselineTotal, 2);
        bag.Set(PipelineDataBagKeys.WarningsDelta, -1);
        task.PipelineDataJson = bag.ToJson();

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, Labels = [GitHubSyncConstants.LabelBacklog] });

        await sut.MirrorTaskReviewCompletedAsync(task, workspace, "Plan: split service and add unit tests.");
        await sut.MirrorPostImplementationGatesCompletedAsync(task, workspace);
        await sut.MirrorTaskReadyAsync(task, workspace);

        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme", "nightcodeforge", 42,
                It.Is<UpdateGitHubIssueRequest>(r => r.Labels != null && r.Labels.Contains(GitHubSyncConstants.LabelInProgress)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme", "nightcodeforge", 42,
                It.Is<UpdateGitHubIssueRequest>(r => r.Labels != null && r.Labels.Contains(GitHubSyncConstants.LabelGatesPassed)),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme", "nightcodeforge", 42,
                It.Is<UpdateGitHubIssueRequest>(r => r.Labels != null && r.Labels.Contains(GitHubSyncConstants.LabelReadyForReview)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _gitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(
                "acme", "nightcodeforge", 42,
                It.Is<string>(s => s.Contains("Plan: split service and add unit tests.")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(
                "acme", "nightcodeforge", 42,
                It.Is<string>(s => s.Contains("24/24 passed") && s.Contains("88.50%")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(
                "acme", "nightcodeforge", 42,
                It.Is<string>(s => s.Contains("https://github.com/acme/nightcodeforge/pull/77")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task MirrorTaskClosedAsync_EnforcesSiblingGuardUntilLastSiblingCloses()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var finishedTask = CreateLinkedTask(10, "42", TaskOverallStatus.Closed);
        finishedTask.SquashCommitSha = "abc1234";
        var openSibling = CreateLinkedTask(11, "42", TaskOverallStatus.Open);

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([finishedTask, openSibling]);

        // 1. Open sibling exists -> comment posted with note, issue NOT closed
        await sut.MirrorTaskClosedAsync(finishedTask, workspace);

        _gitHubClientMock.Verify(
            c => c.AddIssueCommentAsync(
                "acme", "nightcodeforge", 42,
                It.Is<string>(s => s.Contains("abc1234") && s.Contains("#11")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme", "nightcodeforge", 42,
                It.IsAny<UpdateGitHubIssueRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // 2. Sibling is now Closed -> closes issue and applies ncf:closed
        openSibling.Status = TaskOverallStatus.Closed;
        var cancelledSibling = CreateLinkedTask(12, "42", TaskOverallStatus.Cancelled);
        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([finishedTask, openSibling, cancelledSibling]);
        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, Labels = [GitHubSyncConstants.LabelReadyForReview] });

        await sut.MirrorTaskClosedAsync(openSibling, workspace);

        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme", "nightcodeforge", 42,
                It.Is<UpdateGitHubIssueRequest>(r =>
                    r.State == "closed" &&
                    r.Labels != null &&
                    r.Labels.Contains(GitHubSyncConstants.LabelClosed) &&
                    !r.Labels.Contains(GitHubSyncConstants.LabelReadyForReview)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncMergedPullRequestsAsync_RecordsShaAndClosesOnlyMergedTasks()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var mergedTask = CreateLinkedTask(10, "42", TaskOverallStatus.Ready);
        mergedTask.PullRequestNumber = 70;
        var openPrTask = CreateLinkedTask(11, "43", TaskOverallStatus.Ready);
        openPrTask.PullRequestNumber = 71;

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync([mergedTask, openPrTask]);

        _gitHubClientMock
            .Setup(c => c.GetPullRequestAsync("acme", "nightcodeforge", 70, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest { Number = 70, State = "closed", Merged = true, MergeCommitSha = "sha-70" });
        _gitHubClientMock
            .Setup(c => c.GetPullRequestAsync("acme", "nightcodeforge", 71, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest { Number = 71, State = "open", Merged = false });

        var closedCallbackIds = new List<int>();
        var result = await sut.SyncMergedPullRequestsAsync(workspace, (id, _) =>
        {
            closedCallbackIds.Add(id);
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { 10 }, result);
        Assert.Equal(new[] { 10 }, closedCallbackIds);
        Assert.Equal("sha-70", mergedTask.SquashCommitSha);
        _taskRepoMock.Verify(
            r => r.UpdateWithHistoryAsync(
                It.Is<ForgeTask>(t => t.Id == 10 && t.SquashCommitSha == "sha-70"),
                null,
                AuditActors.System,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void SynchronizeWorkspacePrMergeRecurringJob_RegistersWhenEnabledAndRemovesWhenDisabled()
    {
        var managerMock = new Mock<IRecurringJobManager>();
        var enabledWorkspace = CreateConfiguredWorkspace();
        enabledWorkspace.Id = 15;
        enabledWorkspace.GitHubSettings!.PollingIntervalMinutes = 10;

        GitHubTaskMirrorService.SynchronizeWorkspacePrMergeRecurringJob(managerMock.Object, enabledWorkspace);

        managerMock.Verify(
            m => m.AddOrUpdate(
                GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(15),
                It.Is<Job>(j =>
                    j.Type == typeof(ITaskOrchestrator) &&
                    j.Method.Name == nameof(ITaskOrchestrator.SyncMergedPullRequestsAsync)),
                "*/10 * * * *",
                It.IsAny<RecurringJobOptions>()),
            Times.Once);

        enabledWorkspace.GitHubSettings.CloseTaskOnPrMerge = false;
        GitHubTaskMirrorService.SynchronizeWorkspacePrMergeRecurringJob(managerMock.Object, enabledWorkspace);

        managerMock.Verify(
            m => m.RemoveIfExists(GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(15)),
            Times.Once);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenPendingConflictQuestionAlreadyExists_ReusesWithoutDuplicating()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var existingBatchId = GitHubClaimConflictConstants.ClaimConflictBatchPrefix + "existing";
        var task = CreateLinkedTask(10, "42");
        task.IsWaitingForHuman = true;
        task.Questions.Add(new Question
        {
            Id = 77,
            TaskId = 10,
            StageId = StandardStageIds.TaskReview,
            BatchId = existingBatchId,
            QuestionType = QuestionType.SingleChoice,
            Prompt = "Stale prompt",
            Status = QuestionStatus.Pending,
            AskedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        _taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync([task]);

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Assignees = ["teammate"],
                Labels = [GitHubSyncConstants.LabelInProgress]
            });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.False(allowed);
        var singleQuestion = Assert.Single(task.Questions);
        Assert.Equal(77, singleQuestion.Id);
        Assert.Equal(existingBatchId, singleQuestion.BatchId);
        Assert.Equal(QuestionStatus.Pending, singleQuestion.Status);
        Assert.Contains("@teammate", singleQuestion.Prompt);
        Assert.Contains(GitHubSyncConstants.LabelInProgress, singleQuestion.Prompt);
    }

    [Fact]
    public async Task SafeAddExecutionLogAsync_RedactsEmbeddedCredentialsAndTokens()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42");
        ExecutionLog? capturedLog = null;

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(
                "GET https://x-access-token:ghs_secret123@github.com/acme/nightcodeforge failed with token ghp_anotherSecretToken99"));
        _taskRepoMock
            .Setup(r => r.AddExecutionLogAsync(It.IsAny<ExecutionLog>(), It.IsAny<CancellationToken>()))
            .Callback<ExecutionLog, CancellationToken>((log, _) => capturedLog = log)
            .Returns(Task.CompletedTask);

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        Assert.NotNull(capturedLog);
        Assert.DoesNotContain("ghs_secret123", capturedLog!.Message);
        Assert.DoesNotContain("ghp_anotherSecretToken99", capturedLog.Message);
        Assert.Contains(GitCredentialRedactor.RedactedPlaceholder, capturedLog.Message);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenMirroringDisabled_SkipsClaimCheckAndProceeds()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace(s => s.TaskMirroringEnabled = false);
        var task = CreateLinkedTask(10, "42");

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        _gitHubClientMock.Verify(
            c => c.GetIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryClaimIssueForExecutionAsync_WhenIssueClosedExternallyButReadySiblingHasPullRequest_Proceeds()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var task = CreateLinkedTask(10, "42", TaskOverallStatus.Open);
        var readyPrSibling = CreateLinkedTask(11, "42", TaskOverallStatus.Ready);
        readyPrSibling.PullRequestNumber = 99;

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([task, readyPrSibling]);
        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, State = "closed" });

        var allowed = await sut.TryClaimIssueForExecutionAsync(task, workspace);

        Assert.True(allowed);
        Assert.Equal(TaskOverallStatus.Open, task.Status);
    }

    [Fact]
    public async Task MirrorTaskOpenedAsync_WhenActiveSiblingExists_SkipsBacklogLabelDowngrade()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace();
        var openingTask = CreateLinkedTask(10, "42", TaskOverallStatus.Open);
        openingTask.HasExecutionRun = false;
        var activeSibling = CreateLinkedTask(11, "42", TaskOverallStatus.Open);
        activeSibling.HasExecutionRun = true;

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([openingTask, activeSibling]);

        await sut.MirrorTaskOpenedAsync(openingTask, workspace);

        _gitHubClientMock.Verify(
            c => c.GetIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ApplyStatusLabelSafeAsync_WhenTargetLabelAlreadyMatches_SkipsNoOpUpdateIssueCall()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace(s => s.PostMilestoneComments = false);
        var task = CreateLinkedTask(10, "42");

        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue
            {
                Number = 42,
                State = "open",
                Labels = ["bug", GitHubSyncConstants.LabelInProgress]
            });

        await sut.MirrorTaskReviewCompletedAsync(task, workspace, "Spec");

        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubIssueRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SyncMergedPullRequestsAsync_WhenPullRequestDeliveryDisabled_SkipsPolling()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace(s =>
        {
            s.PullRequestDeliveryEnabled = false;
            s.CloseTaskOnPrMerge = true;
        });

        var closed = await sut.SyncMergedPullRequestsAsync(workspace, (_, _) => Task.FromResult(new CompleteTaskResult(CompleteTaskStatus.Completed)));

        Assert.Empty(closed);
        _gitHubClientMock.Verify(
            c => c.GetPullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MirrorTaskClosedAsync_WhenLastSiblingCancelledAfterCompletedSibling_ClosesGitHubIssue()
    {
        var sut = CreateSut();
        var workspace = CreateConfiguredWorkspace(s => s.PostMilestoneComments = false);
        var cancelledLastSibling = CreateLinkedTask(10, "42", TaskOverallStatus.Cancelled);
        var completedSibling = CreateLinkedTask(11, "42", TaskOverallStatus.Closed);

        _taskRepoMock
            .Setup(r => r.GetByWorkspaceIdAsync(workspace.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([cancelledLastSibling, completedSibling]);
        _gitHubClientMock
            .Setup(c => c.GetIssueAsync("acme", "nightcodeforge", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue { Number = 42, State = "open", Labels = [GitHubSyncConstants.LabelInProgress] });

        await sut.MirrorTaskClosedAsync(cancelledLastSibling, workspace);

        _gitHubClientMock.Verify(
            c => c.UpdateIssueAsync(
                "acme",
                "nightcodeforge",
                42,
                It.Is<UpdateGitHubIssueRequest>(req => req.State == "closed"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void RemoveWorkspacePrMergeRecurringJob_RemovesJobByCanonicalId()
    {
        var managerMock = new Mock<IRecurringJobManager>();

        GitHubTaskMirrorService.RemoveWorkspacePrMergeRecurringJob(managerMock.Object, 15);

        managerMock.Verify(
            m => m.RemoveIfExists(GitHubTaskMirrorService.GetWorkspacePrMergeRecurringJobId(15)),
            Times.Once);
    }
}
