using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Components.DevQueue;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DevQueueComponentTests : BunitContext
{
    private readonly MockDevQueueService _queueService;
    private readonly ToastService _toastService;

    public DevQueueComponentTests()
    {
        _queueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<IDevQueueService>(_queueService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IBackgroundTaskRunner>(new BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundTaskRunner>.Instance));
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DevQueue>>(Microsoft.Extensions.Logging.Abstractions.NullLogger<DevQueue>.Instance);
    }

    [Fact]
    public void DevQueuePage_RendersHeader_AndHangfireDashboardLink()
    {
        var cut = Render<DevQueue>();

        Assert.Contains("Dev-Queue &amp; Concurrency Operations", cut.Markup);
        var hangfireLink = cut.Find("#open-hangfire-link");
        Assert.NotNull(hangfireLink);
        Assert.Equal("/hangfire", hangfireLink.GetAttribute("href"));
        Assert.Equal("_blank", hangfireLink.GetAttribute("target"));
    }

    [Fact]
    public void DevQueuePage_RendersQueueMetricCards_WithCorrectCounts()
    {
        _queueService.Snapshot = new DevQueueSnapshot(
            UrgentJobs: new List<QueuedJobItem>
            {
                new("u-1", "llm-urgent", 101, "Urgent Task 1", "Stage1", DateTime.UtcNow, "Urgent"),
                new("u-2", "llm-urgent", 102, "Urgent Task 2", "Stage2", DateTime.UtcNow, "Urgent")
            },
            InferenceJobs: new List<QueuedJobItem>
            {
                new("i-1", "llm-inference", 201, "Inference Task 1", "Stage3", DateTime.UtcNow, "Queued"),
                new("i-2", "llm-inference", 202, "Inference Task 2", "Stage3", DateTime.UtcNow, "Queued"),
                new("i-3", "llm-inference", 203, "Inference Task 3", "Stage3", DateTime.UtcNow, "Queued")
            },
            UtilityJobs: new List<QueuedJobItem>
            {
                new("d-1", "default", 0, "Git Fetch", "Utility", DateTime.UtcNow, "Queued")
            },
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>(),
            ActiveLocks: new List<ActiveLockSnapshot>());

        var cut = Render<DevQueue>();

        Assert.Contains("llm-urgent (Priority 1)", cut.Markup);
        Assert.Contains("llm-inference (Priority 2)", cut.Markup);
        Assert.Contains("default (Utility)", cut.Markup);

        Assert.Contains("Worker: 1", cut.Markup);
        Assert.Contains("Workers: 4", cut.Markup);

        // Counts
        Assert.Contains(">2<", cut.Markup);
        Assert.Contains(">3<", cut.Markup);
        Assert.Contains(">1<", cut.Markup);
    }

    [Fact]
    public void DevQueuePage_RendersActiveLocks_AndAllowsEmergencyRelease()
    {
        _queueService.Snapshot = new DevQueueSnapshot(
            UrgentJobs: new List<QueuedJobItem>(),
            InferenceJobs: new List<QueuedJobItem>(),
            UtilityJobs: new List<QueuedJobItem>(),
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>(),
            ActiveLocks: new List<ActiveLockSnapshot>
            {
                new(WorkspaceId: 42, TaskId: 105, LockType: "TaskExclusive", AcquiredAt: DateTime.UtcNow.AddMinutes(-3), DurationHeld: TimeSpan.FromMinutes(3))
            });

        var cut = Render<DevQueue>();

        Assert.Contains("Workspace #42", cut.Markup);
        Assert.Contains("ForgeTask #105", cut.Markup);
        Assert.Contains("TaskExclusive", cut.Markup);

        var releaseBtn = cut.Find("#emergency-release-btn-42");
        Assert.NotNull(releaseBtn);
        releaseBtn.Click();

        Assert.Equal(42, _queueService.LastReleasedWorkspaceId);
        Assert.Equal(105, _queueService.LastReleasedTaskId);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Lock Released");
    }

    [Fact]
    public void DevQueuePage_RendersSequentialTable_AndAllowsPrioritizing()
    {
        _queueService.Snapshot = new DevQueueSnapshot(
            UrgentJobs: new List<QueuedJobItem>
            {
                new("u-1", "llm-urgent", 101, "Clarification Fix", "Stage 1", DateTime.UtcNow, "Urgent")
            },
            InferenceJobs: new List<QueuedJobItem>
            {
                new("i-1", "llm-inference", 201, "Implement Auth Feature", "Stage 3", DateTime.UtcNow, "Queued")
            },
            UtilityJobs: new List<QueuedJobItem>(),
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>(),
            ActiveLocks: new List<ActiveLockSnapshot>());

        var cut = Render<DevQueue>();

        Assert.Contains("URGENT", cut.Markup);
        Assert.Contains("NORMAL", cut.Markup);
        Assert.Contains("Clarification Fix", cut.Markup);
        Assert.Contains("Implement Auth Feature", cut.Markup);

        var prioritizeBtn = cut.Find("#prioritize-btn-i-1");
        Assert.NotNull(prioritizeBtn);
        prioritizeBtn.Click();

        Assert.Equal("i-1", _queueService.LastPromotedJobId);
        Assert.Equal(201, _queueService.LastPromotedTaskId);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Prioritized");
    }

    [Fact]
    public void DevQueuePage_AllowsCancellingJob()
    {
        _queueService.Snapshot = new DevQueueSnapshot(
            UrgentJobs: new List<QueuedJobItem>
            {
                new("u-10", "llm-urgent", 999, "Task to Cancel", "Stage 2", DateTime.UtcNow, "Urgent")
            },
            InferenceJobs: new List<QueuedJobItem>(),
            UtilityJobs: new List<QueuedJobItem>(),
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>(),
            ActiveLocks: new List<ActiveLockSnapshot>());

        var cut = Render<DevQueue>();

        var cancelBtn = cut.Find("#cancel-btn-u-10");
        Assert.NotNull(cancelBtn);
        cancelBtn.Click();

        Assert.Equal("u-10", _queueService.LastCancelledJobId);
        Assert.Equal(999, _queueService.LastCancelledTaskId);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Job Cancelled");
    }

    [Fact]
    public void DevQueuePage_RendersFailedJobs_AndAllowsRequeue()
    {
        _queueService.Snapshot = new DevQueueSnapshot(
            UrgentJobs: new List<QueuedJobItem>(),
            InferenceJobs: new List<QueuedJobItem>(),
            UtilityJobs: new List<QueuedJobItem>(),
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>
            {
                new("failed-1", "failed", 333, "Failed LLM Reasoning", "Implementation", DateTime.UtcNow, "Failed")
            },
            ActiveLocks: new List<ActiveLockSnapshot>());

        var cut = Render<DevQueue>();

        Assert.Contains("Failed Background Jobs", cut.Markup);
        Assert.Contains("Failed LLM Reasoning", cut.Markup);

        var requeueBtn = cut.Find("#requeue-btn-failed-1");
        Assert.NotNull(requeueBtn);
        requeueBtn.Click();

        Assert.Equal("failed-1", _queueService.LastRequeuedJobId);
        Assert.Equal(333, _queueService.LastRequeuedTaskId);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Job Requeued");
    }

    [Fact]
    public void ActiveLocksGrid_RendersEmptyMessage_WhenNoLocksHeld()
    {
        var cut = Render<ActiveLocksGrid>(p => p.Add(x => x.Locks, Array.Empty<ActiveLockSnapshot>()));

        Assert.Contains("No active locks held", cut.Markup);
        Assert.Empty(cut.FindAll("#active-locks-table"));
    }

    [Fact]
    public void DevQueueTable_RendersEmptyMessage_WhenNoJobsQueued()
    {
        var cut = Render<DevQueueTable>(p => p.Add(x => x.Jobs, Array.Empty<QueuedJobItem>()));

        Assert.Contains("No pending tasks in GPU queue", cut.Markup);
        Assert.Empty(cut.FindAll("#sequential-queue-table"));
    }

    [Fact]
    public async Task DevQueuePage_WhenRefreshThrowsException_DoesNotCrashAndShowsToast()
    {
        _queueService.ShouldThrowOnGetSnapshot = true;
        var cut = Render<DevQueue>();

        await cut.Instance.RefreshQueueStateAsync();

        Assert.Contains("Dev-Queue &amp; Concurrency Operations", cut.Markup);
        Assert.Contains(_toastService.Toasts, t => t.Title == "Refresh Failed");
    }

    #region Mock DevQueueService

    private class MockDevQueueService : IDevQueueService
    {
        public bool ShouldThrowOnGetSnapshot { get; set; }
        public DevQueueSnapshot Snapshot { get; set; } = new(
            UrgentJobs: new List<QueuedJobItem>(),
            InferenceJobs: new List<QueuedJobItem>(),
            UtilityJobs: new List<QueuedJobItem>(),
            ProcessingJobs: new List<QueuedJobItem>(),
            FailedJobs: new List<QueuedJobItem>(),
            ActiveLocks: new List<ActiveLockSnapshot>());

        public string? LastPromotedJobId { get; private set; }
        public int? LastPromotedTaskId { get; private set; }
        public string? LastCancelledJobId { get; private set; }
        public int? LastCancelledTaskId { get; private set; }
        public string? LastRequeuedJobId { get; private set; }
        public int? LastRequeuedTaskId { get; private set; }
        public int? LastReleasedWorkspaceId { get; private set; }
        public int? LastReleasedTaskId { get; private set; }

        public Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (ShouldThrowOnGetSnapshot)
            {
                throw new InvalidOperationException("Simulated transient database error");
            }
            return Task.FromResult(Snapshot);
        }

        public Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default)
        {
            LastPromotedJobId = jobId;
            LastPromotedTaskId = taskId;
            return Task.FromResult(true);
        }

        public Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
        {
            LastCancelledJobId = jobId;
            LastCancelledTaskId = taskId;
            return Task.FromResult(true);
        }

        public Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
        {
            LastRequeuedJobId = jobId;
            LastRequeuedTaskId = taskId;
            return Task.FromResult(true);
        }

        public Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default)
        {
            LastReleasedWorkspaceId = workspaceId;
            LastReleasedTaskId = taskId;
            return Task.FromResult(true);
        }

        public int? LastRescheduledTaskId { get; private set; }
        public bool? LastRescheduledRestartPipeline { get; private set; }

        public Task<bool> RescheduleTaskAsync(int taskId, bool restartPipeline = false, CancellationToken cancellationToken = default)
        {
            LastRescheduledTaskId = taskId;
            LastRescheduledRestartPipeline = restartPipeline;
            return Task.FromResult(true);
        }
    }

    #endregion
}
