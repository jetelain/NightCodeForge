using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WebNotificationServiceTests
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly ToastService _toastService;
    private readonly TestNavigationManager _nav;
    private readonly Mock<IJSRuntime> _jsMock;
    private readonly OrchestratorHubClientConnection _hubConnection;

    public WebNotificationServiceTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _toastService = TestServices.CreateToastService();
        _nav = new TestNavigationManager("http://localhost/");
        _jsMock = new Mock<IJSRuntime>();
        _hubConnection = TestHubConnectionHelper.Create(nav: _nav);

        // Default JS mock setups
        _jsMock.Setup(j => j.InvokeAsync<NotificationPreferences>(
                "NightCodeForge.notifications.getPreferences",
                It.IsAny<object[]>()))
            .ReturnsAsync(new NotificationPreferences());

        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.isDocumentVisible",
                It.IsAny<object[]>()))
            .ReturnsAsync(false);

        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.showNotification",
                It.IsAny<object[]>()))
            .ReturnsAsync(true);
    }

    private WebNotificationService CreateService()
    {
        return new WebNotificationService(
            _hubConnection,
            _taskRepository,
            _toastService,
            _nav,
            _jsMock.Object,
            NullLogger<WebNotificationService>.Instance);
    }

    [Fact]
    public async Task Preferences_WhenMasterDisabled_DoesNotDispatchNotification()
    {
        var service = CreateService();
        var prefs = new NotificationPreferences { MasterEnabled = false };
        await service.SavePreferencesAsync(prefs);

        var task = new ForgeTask
        {
            Id = 1,
            Title = "Implement Feature",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.False(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
        Assert.Empty(_toastService.Toasts);
    }

    [Fact]
    public async Task Preferences_WhenSubjectDisabled_DoesNotDispatchForThatSubject()
    {
        var service = CreateService();
        var prefs = new NotificationPreferences
        {
            MasterEnabled = true,
            NotifyOnQuestionInbox = false,
            NotifyOnReviewRoom = true,
            NotifyOnTaskMerged = true
        };
        await service.SavePreferencesAsync(prefs);

        var task = new ForgeTask
        {
            Id = 1,
            Title = "Need Input",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.False(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task Dispatch_QuestionInbox_DispatchesExpectedPayload()
    {
        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Refactor Database Schema",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args =>
                args.Length == 2 &&
                args[0].ToString() == "❓ Human Input Needed: Task #42")),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_ReviewRoom_DispatchesExpectedPayload()
    {
        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 7,
            Title = "Add Auth Token Support",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.ReviewRoom, task);

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args =>
                args.Length == 2 &&
                args[0].ToString() == "🔍 Ready for Review: Task #7")),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_TaskMerged_DispatchesExpectedPayload()
    {
        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 15,
            Title = "Fix Race Condition",
            Status = TaskOverallStatus.Closed,
            BaseBranch = "develop",
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.TaskMerged, task);

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args =>
                args.Length == 2 &&
                args[0].ToString() == "🚀 Task Merged: Task #15")),
            Times.Once);
    }

    [Fact]
    public async Task Deduplication_WhenSameSignatureDispatchedAgain_ReturnsFalse()
    {
        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 10,
            Title = "Duplicate Check",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc)
        };

        var first = await service.DispatchNotificationAsync(NotificationSubject.ReviewRoom, task);
        var second = await service.DispatchNotificationAsync(NotificationSubject.ReviewRoom, task);

        Assert.True(first);
        Assert.False(second);
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task ActiveRouteSuppression_WhenUserOnActiveRouteAndDocumentVisible_SuppressesOSNotificationAndShowsToast()
    {
        _nav.NavigateTo("http://localhost/w/1/inbox?taskId=20");
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.isDocumentVisible",
                It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 20,
            Title = "Database Migration Query",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.True(result);
        // OS push notification should be suppressed
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
        // In-app toast should be shown instead
        Assert.Single(_toastService.Toasts);
        Assert.Contains("Human Input Needed: Task #20", _toastService.Toasts[0].Title);
    }

    [Fact]
    public async Task ActiveRouteSuppression_WhenDocumentNotVisible_DispatchesOSNotificationEvenOnActiveRoute()
    {
        _nav.NavigateTo("http://localhost/inbox?taskId=20");
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.isDocumentVisible",
                It.IsAny<object[]>()))
            .ReturnsAsync(false); // Tab in background or screen locked

        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 20,
            Title = "Database Migration Query",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.True(result);
        // Dispatches OS notification since screen is not visible
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Once);
        Assert.Empty(_toastService.Toasts);
    }

    [Fact]
    public async Task ActiveRouteSuppression_WhenUserOnDifferentRoute_DispatchesOSNotification()
    {
        _nav.NavigateTo("http://localhost/w/1/backlog");
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.isDocumentVisible",
                It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 20,
            Title = "Database Migration Query",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Once);
        Assert.Empty(_toastService.Toasts);
    }

    [Fact]
    public async Task SignalRHook_OnStageTransition_WaitingForHuman_DispatchesQuestionNotification()
    {
        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "API Client Refactor",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false,
            UpdatedAt = DateTime.UtcNow
        });

        var service = CreateService();
        await service.InitializeAsync();

        // Simulate task entering WaitingForHuman and incoming stage transition event from SignalR
        task.IsWaitingForHuman = true;
        task.UpdatedAt = task.UpdatedAt.AddSeconds(1);
        await _taskRepository.UpdateAsync(task);

        _hubConnection.TriggerStageTransition(task.Id, "review", "Agentic", StageExecutionStatus.WaitingForHuman.ToString());

        // Allow async handler to execute
        await Task.Delay(50);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{task.Id}")),
            Times.Once);
    }

    [Fact]
    public async Task SignalRHook_OnWorkerStatus_WaitingForHuman_DispatchesQuestionNotification()
    {
        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Worker Clarification",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false,
            UpdatedAt = DateTime.UtcNow
        });

        var service = CreateService();
        await service.InitializeAsync();

        // Simulate incoming worker status event from SignalR
        task.IsWaitingForHuman = true;
        task.UpdatedAt = task.UpdatedAt.AddSeconds(1);
        await _taskRepository.UpdateAsync(task);

        _hubConnection.TriggerWorkerStatus(WorkerStatusConstants.WaitingForHuman, task.Id, "implement", "Local GPU");

        await Task.Delay(50);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{task.Id}")),
            Times.Once);
    }

    [Fact]
    public async Task SignalRHook_OnStageTransition_ReadyWithManualReview_DispatchesReviewNotification()
    {
        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "New UI Feature",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = DateTime.UtcNow
        });

        var service = CreateService();

        _hubConnection.TriggerStageTransition(task.Id, "post-review", "Deterministic", "Completed");

        await Task.Delay(50);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"🔍 Ready for Review: Task #{task.Id}")),
            Times.Once);
    }

    [Fact]
    public async Task SignalRHook_OnStageTransition_Closed_DispatchesMergedNotification()
    {
        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Completed Feature",
            Status = TaskOverallStatus.Closed,
            BaseBranch = "main",
            UpdatedAt = DateTime.UtcNow
        });

        var service = CreateService();

        _hubConnection.TriggerStageTransition(task.Id, "merge", "Deterministic", "Completed");

        await Task.Delay(50);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"🚀 Task Merged: Task #{task.Id}")),
            Times.Once);
    }

    [Fact]
    public async Task WakeupCatchUp_DispatchesMissedTransitions_AndUpdatesBaseline()
    {
        var service = CreateService();
        // Initialize baseline with empty DB
        await service.InitializeAsync();

        // Simulate tasks that transitioned while mobile device was asleep:
        // 1. Task with pending question
        var task1 = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Asleep Question",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow,
            Questions = new List<Question>
            {
                new() { Prompt = "Which branch?", Status = QuestionStatus.Pending }
            }
        });

        // 2. Task became Ready with ManualReviewRequired
        var task2 = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Asleep Ready",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = DateTime.UtcNow
        });

        // 3. Task became Closed (merged)
        var task3 = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Asleep Merged",
            Status = TaskOverallStatus.Closed,
            BaseBranch = "main",
            UpdatedAt = DateTime.UtcNow
        });

        // Trigger tab foregrounding / wakeup
        await service.HandleTabForegroundedAsync();

        // All 3 missed transitions should be dispatched
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{task1.Id}")),
            Times.Once);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"🔍 Ready for Review: Task #{task2.Id}")),
            Times.Once);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"🚀 Task Merged: Task #{task3.Id}")),
            Times.Once);

        // Running catch-up again immediately should NOT dispatch duplicates
        await service.RunCatchUpAsync();

        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Exactly(3));
    }

    [Fact]
    public async Task WakeupCatchUp_DoesNotDispatchForTasksAlreadyAtBaseline()
    {
        // Populate DB with existing Ready task before service initializes
        var existingReady = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Old Ready Task",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = DateTime.UtcNow
        });

        var service = CreateService();
        await service.InitializeAsync();

        // Run catch-up
        await service.RunCatchUpAsync();

        // Should not dispatch for tasks that were already Ready at initial baseline
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_StartsSignalRHubConnection_AndSeedsOpenWaitingForHumanTasks()
    {
        var openWaiting = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Existing Open Waiting Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        });

        var hubMock = new Mock<OrchestratorHubClientConnection>(
            _nav,
            Mock.Of<Microsoft.Extensions.Logging.ILogger<OrchestratorHubClientConnection>>(),
            WorkerStateTestHelper.Create(Mock.Of<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>()));
        hubMock.Setup(h => h.StartAsync()).Returns(Task.CompletedTask);

        var service = new WebNotificationService(
            hubMock.Object,
            _taskRepository,
            _toastService,
            _nav,
            _jsMock.Object,
            NullLogger<WebNotificationService>.Instance);

        await service.InitializeAsync();

        // HubConnection.StartAsync should be called on InitializeAsync so pages without their own HubClient still receive SignalR events
        hubMock.Verify(h => h.StartAsync(), Times.Once);

        // Running catch-up should NOT notify for the already-waiting Open task seeded during InitializeAsync
        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task WakeupCatchUp_WhenQuestionAnsweredAndWaitingCleared_ReNotifiesOnSecondQuestion()
    {
        var service = CreateService();
        await service.InitializeAsync();

        // 1. Task enters WaitingForHuman (Question #1)
        var t1Time = new DateTime(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc);
        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Multi-Question Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = t1Time
        });

        await service.RunCatchUpAsync();

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{task.Id}")),
            Times.Once);

        // 2. Question #1 is answered and agent resumes (IsWaitingForHuman = false)
        task.IsWaitingForHuman = false;
        task.UpdatedAt = t1Time.AddMinutes(5);
        await _taskRepository.UpdateAsync(task);

        await service.RunCatchUpAsync();

        // Still only 1 notification
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Once);

        // 3. Agent asks a second question later while device is asleep (IsWaitingForHuman = true again)
        task.IsWaitingForHuman = true;
        task.UpdatedAt = t1Time.AddMinutes(10);
        await _taskRepository.UpdateAsync(task);

        await service.RunCatchUpAsync();

        // Second question transition must be detected and dispatched!
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{task.Id}")),
            Times.Exactly(2));
    }

    [Fact]
    public void RingBufferDeduplicator_EvictsOldestEntriesWhenCapacityExceeded()
    {
        var deduplicator = new RingBufferDeduplicator(capacity: 2);

        Assert.True(deduplicator.TryAdd("sig-1"));
        Assert.True(deduplicator.TryAdd("sig-2"));
        Assert.False(deduplicator.TryAdd("sig-1")); // Duplicate within capacity

        // Adding 3rd entry evicts "sig-1"
        Assert.True(deduplicator.TryAdd("sig-3"));
        Assert.False(deduplicator.Contains("sig-1"));
        Assert.True(deduplicator.Contains("sig-2"));
        Assert.True(deduplicator.Contains("sig-3"));

        // "sig-1" can now be added again
        Assert.True(deduplicator.TryAdd("sig-1"));
    }

    [Fact]
    public async Task WakeupCatchUp_IncludesReviewedAndImplementedTasks_AndClearsWaitingFlagAcrossRunnableStatuses()
    {
        var existingReviewedWaiting = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Baseline Reviewed Waiting",
            Status = TaskOverallStatus.Reviewed,
            IsWaitingForHuman = true,
            UpdatedAt = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc)
        });

        var service = CreateService();
        await service.InitializeAsync();

        // 1. Existing Reviewed waiting task should already be seeded at InitializeAsync (no duplicate notification)
        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);

        // 2. Question on Reviewed task is answered and waiting flag cleared
        existingReviewedWaiting.IsWaitingForHuman = false;
        existingReviewedWaiting.UpdatedAt = existingReviewedWaiting.UpdatedAt.AddMinutes(5);
        await _taskRepository.UpdateAsync(existingReviewedWaiting);

        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);

        // 3. Reviewed task enters WaitingForHuman again (e.g. during implementation/code-review stage)
        existingReviewedWaiting.IsWaitingForHuman = true;
        existingReviewedWaiting.UpdatedAt = existingReviewedWaiting.UpdatedAt.AddMinutes(5);
        await _taskRepository.UpdateAsync(existingReviewedWaiting);

        // 4. Also a new Implemented task enters WaitingForHuman without a Question record yet
        var implementedWaiting = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Implemented Task Blocked",
            Status = TaskOverallStatus.Implemented,
            IsWaitingForHuman = true,
            UpdatedAt = new DateTime(2026, 11, 8, 10, 15, 0, DateTimeKind.Utc)
        });

        await service.RunCatchUpAsync();

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{existingReviewedWaiting.Id}")),
            Times.Once);

        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args => args[0].ToString() == $"❓ Human Input Needed: Task #{implementedWaiting.Id}")),
            Times.Once);
    }

    [Fact]
    public async Task Dispatch_WhenJsShowNotificationReturnsFalse_DoesNotRecordDeduplication_AllowingRetryOnCatchUp()
    {
        // Initially permission is not granted or JS showNotification returns false
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.showNotification",
                It.IsAny<object[]>()))
            .ReturnsAsync(false);

        var service = CreateService();
        await service.InitializeAsync();

        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Title = "Retryable Ready Task",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired,
            UpdatedAt = new DateTime(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc)
        });

        // First catch-up fails to display notification (returns false)
        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Once);

        // Now user grants permission / browser allows showNotification
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.showNotification",
                It.IsAny<object[]>()))
            .ReturnsAsync(true);

        // Second catch-up retries and succeeds because failed dispatch was not poisoned in deduplicator/snapshot
        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Exactly(2));

        // Third catch-up is deduplicated now that it succeeded
        await service.RunCatchUpAsync();
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ActiveRouteSuppression_WhenViewingTaskInspectorForSameTask_SuppressesQuestionNotificationAndShowsToast()
    {
        _nav.NavigateTo("http://localhost/w/1/inspector/42");
        _jsMock.Setup(j => j.InvokeAsync<bool>(
                "NightCodeForge.notifications.isDocumentVisible",
                It.IsAny<object[]>()))
            .ReturnsAsync(true);

        var service = CreateService();
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Inspector Active Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow
        };

        var result = await service.DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>("NightCodeForge.notifications.showNotification", It.IsAny<object[]>()), Times.Never);
        Assert.Single(_toastService.Toasts);
        Assert.Contains("Human Input Needed: Task #42", _toastService.Toasts[0].Title);
    }

    [Fact]
    public async Task SendTestNotificationAsync_DispatchesTestNotificationViaJS()
    {
        var service = CreateService();

        var result = await service.SendTestNotificationAsync();

        Assert.True(result);
        _jsMock.Verify(j => j.InvokeAsync<bool>(
            "NightCodeForge.notifications.showNotification",
            It.Is<object[]>(args =>
                args[0].ToString() == NotificationConstants.Titles.Test)),
            Times.Once);
    }
}

