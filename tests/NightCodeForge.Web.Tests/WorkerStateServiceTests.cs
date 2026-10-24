using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkerStateServiceTests
{
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();

    [Fact]
    public void InitialState_WhenNoActiveTask_ShouldBeIdleWithZeroPendingQuestions()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns((ActiveTaskSummary?)null);
        var service = new WorkerStateService(_telemetryMock.Object);

        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
        Assert.Null(service.Current.ActiveTaskTitle);
        Assert.Null(service.Current.StageId);
        Assert.Null(service.Current.StageKind);
        Assert.Equal("Local GPU", service.Current.HardwareTarget);
        Assert.Equal(0, service.Current.PendingQuestionCount);
    }

    [Fact]
    public void InitialState_WhenActiveTaskInTelemetry_ShouldHydrateRunningState()
    {
        var started = DateTime.UtcNow.AddMinutes(-5);
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(42, "Refactor Parser", "execution-stage", 1, "default", started));
        var service = new WorkerStateService(_telemetryMock.Object);

        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(42, service.Current.ActiveTaskId);
        Assert.Equal("Refactor Parser", service.Current.ActiveTaskTitle);
        Assert.Equal("execution-stage", service.Current.StageId);
        Assert.Equal(started, service.Current.StartedAt);
    }

    [Fact]
    public void Current_WhenInitialIdleButActiveTaskAppears_ShouldHydrateFromTelemetry()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns((ActiveTaskSummary?)null);
        var service = new WorkerStateService(_telemetryMock.Object);
        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);

        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(99, "Late Task", "stage-1", 1, "default", DateTime.UtcNow));
        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(99, service.Current.ActiveTaskId);
        Assert.Equal("Late Task", service.Current.ActiveTaskTitle);
    }

    [Fact]
    public void UpdateStatus_ShouldUpdateSnapshotAndNotify()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        var snapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.Running,
            101,
            "Refactor GitCliRunner",
            "stage-3",
            "ImplementationStage",
            "RTX 4090",
            3);

        var eventFired = false;
        service.OnWorkerStateChanged += () => eventFired = true;

        service.UpdateStatus(snapshot);

        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(101, service.Current.ActiveTaskId);
        Assert.Equal("Refactor GitCliRunner", service.Current.ActiveTaskTitle);
        Assert.Equal("stage-3", service.Current.StageId);
        Assert.Equal("ImplementationStage", service.Current.StageKind);
        Assert.Equal("RTX 4090", service.Current.HardwareTarget);
        Assert.Equal(3, service.Current.PendingQuestionCount);
        Assert.True(eventFired);
    }

    [Fact]
    public void UpdateStatus_WhenNull_ShouldThrowArgumentNullException()
    {
        var service = new WorkerStateService(_telemetryMock.Object);

        Assert.Throws<ArgumentNullException>(() => service.UpdateStatus(null!));
    }

    [Fact]
    public void UpdatePendingQuestions_ShouldPreserveSnapshotAndNotify()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        var initialSnapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.WaitingForHuman,
            50,
            "Approval Needed",
            "stage-gate",
            "HumanClarificationGate",
            "RTX 3090",
            1);
        service.UpdateStatus(initialSnapshot);

        var eventFired = false;
        service.OnWorkerStateChanged += () => eventFired = true;

        service.UpdatePendingQuestions(5);

        Assert.Equal(5, service.Current.PendingQuestionCount);
        Assert.Equal(WorkerExecutionStatus.WaitingForHuman, service.Current.Status);
        Assert.Equal(50, service.Current.ActiveTaskId);
        Assert.Equal("HumanClarificationGate", service.Current.StageKind);
        Assert.True(eventFired);
    }

    [Fact]
    public void HandleWorkerStatus_Running_ShouldUpdateExecutionAndFireEvent()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        var eventFired = false;
        service.OnWorkerStateChanged += () => eventFired = true;

        service.HandleWorkerStatus("running", 5, "stage-exec", "A100", "Run task 5", "task/5");

        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(5, service.Current.ActiveTaskId);
        Assert.Equal("Run task 5", service.Current.ActiveTaskTitle);
        Assert.Equal("stage-exec", service.Current.StageId);
        Assert.Equal("A100", service.Current.HardwareTarget);
        Assert.Equal("task/5", service.Current.BranchName);
        Assert.NotNull(service.Current.StartedAt);
        Assert.True(eventFired);
    }

    [Fact]
    public void HandleWorkerStatus_Idle_ShouldResetActiveTask()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        service.HandleWorkerStatus("running", 5, "stage-exec", "A100", "Run task 5", "task/5");
        Assert.Equal(5, service.Current.ActiveTaskId);

        service.HandleWorkerStatus("idle", null, null, "A100");

        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
        Assert.Null(service.Current.ActiveTaskTitle);
        Assert.Null(service.Current.StageId);
    }

    [Fact]
    public void Current_AfterExplicitIdle_ShouldNotResurrectStaleTelemetryTask()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(5, "Stale", "s", 1, "q", DateTime.UtcNow));
        var service = new WorkerStateService(_telemetryMock.Object);

        service.HandleWorkerStatus("idle", null, null, "Local GPU");

        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
    }

    [Fact]
    public void HandleWorkerStatus_DifferentTask_ShouldNotCarryOverPreviousTitleOrBranch()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        service.HandleWorkerStatus("running", 5, "stage-a", "Local GPU", "Task five", "task/5");

        service.HandleWorkerStatus("running", 6, "stage-a", "Local GPU");

        Assert.Equal(6, service.Current.ActiveTaskId);
        Assert.Null(service.Current.ActiveTaskTitle);
        Assert.Null(service.Current.BranchName);
    }

    [Fact]
    public void HandleWorkerStatus_SameTaskNextStage_ShouldKeepTitleAndStartTime()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        service.HandleWorkerStatus("running", 5, "stage-a", "Local GPU", "Task five", "task/5");
        var started = service.Current.StartedAt;

        service.HandleWorkerStatus("running", 5, "stage-b", "Local GPU");

        Assert.Equal("Task five", service.Current.ActiveTaskTitle);
        Assert.Equal(started, service.Current.StartedAt);
        Assert.Equal("stage-b", service.Current.StageId);
    }

    [Fact]
    public void Current_WhenNewActiveTaskStartsInTelemetryAfterPreviousIdle_HydratesNewActiveTask()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(5, "Stale", "s", 1, "q", DateTime.UtcNow));
        var service = new WorkerStateService(_telemetryMock.Object);

        service.HandleWorkerStatus("idle", null, null, "Local GPU");
        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);

        // A new task #7 is now active in telemetry
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(7, "New Task", "impl", 1, "q", DateTime.UtcNow));

        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(7, service.Current.ActiveTaskId);
        Assert.Equal("New Task", service.Current.ActiveTaskTitle);
    }

    [Fact]
    public void HandleTelemetryActiveTaskChanged_UpdatesStatusAndRaisesEvent()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        bool eventFired = false;
        service.OnWorkerStateChanged += () => eventFired = true;

        _telemetryMock.Raise(t => t.OnActiveTaskChanged += null, new ActiveTaskSummary(7, "Event Task", "impl", 1, "q", DateTime.UtcNow));

        Assert.True(eventFired);
        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(7, service.Current.ActiveTaskId);
        Assert.Equal("Event Task", service.Current.ActiveTaskTitle);

        eventFired = false;
        _telemetryMock.Raise(t => t.OnActiveTaskChanged += null, (ActiveTaskSummary)null!);

        Assert.True(eventFired);
        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
    }

    [Fact]
    public void HardwareTarget_WhenActiveTaskProvidesDeterministicCpu_PropagatesToCurrent()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(
            12, "Deterministic Task", "gate-1", 1, "cpu-deterministic", DateTime.UtcNow, "Deterministic CPU"));

        var service = new WorkerStateService(_telemetryMock.Object);
        Assert.Equal("Deterministic CPU", service.Current.HardwareTarget);

        var task13 = new ActiveTaskSummary(
            13, "GPU Task", "impl", 1, "llm-inference", DateTime.UtcNow, "Local GPU");
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(task13);
        _telemetryMock.Raise(t => t.OnActiveTaskChanged += null, task13);

        Assert.Equal("Local GPU", service.Current.HardwareTarget);
    }

    [Fact]
    public void Current_WhenActiveTaskAdvancesStageInTelemetry_UpdatesCurrentStageId()
    {
        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(
            15, "Advancing Task", "stage-1", 1, "llm-inference", DateTime.UtcNow, "Local GPU"));

        var service = new WorkerStateService(_telemetryMock.Object);
        Assert.Equal("stage-1", service.Current.StageId);

        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(
            15, "Advancing Task", "stage-2", 1, "cpu-deterministic", DateTime.UtcNow, "Deterministic CPU"));

        Assert.Equal("stage-2", service.Current.StageId);
        Assert.Equal("Deterministic CPU", service.Current.HardwareTarget);
    }

    [Fact]
    public void Current_WhenActiveTaskIdChangesInTelemetry_ReconcilesToNewTask()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        service.HandleWorkerStatus("running", 10, "stage-1", "Local GPU", "Task 10", "task/10");
        Assert.Equal(10, service.Current.ActiveTaskId);

        _telemetryMock.Setup(t => t.GetActiveTask()).Returns(new ActiveTaskSummary(
            12, "Task 12", "stage-review", 1, "llm-inference", DateTime.UtcNow, "Local GPU"));

        Assert.Equal(12, service.Current.ActiveTaskId);
        Assert.Equal("Task 12", service.Current.ActiveTaskTitle);
        Assert.Equal("stage-review", service.Current.StageId);
        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
    }

    [Fact]
    public void WhenActiveTaskClearsInTelemetry_ReconcilesToIdle()
    {
        var service = new WorkerStateService(_telemetryMock.Object);
        service.HandleWorkerStatus("running", 10, "stage-1", "Local GPU", "Task 10", "task/10");
        Assert.Equal(WorkerExecutionStatus.Running, service.Current.Status);
        Assert.Equal(10, service.Current.ActiveTaskId);

        _telemetryMock.Setup(t => t.GetActiveTask()).Returns((ActiveTaskSummary)null!);
        _telemetryMock.Raise(t => t.OnActiveTaskChanged += null, (ActiveTaskSummary)null!);

        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
        Assert.Null(service.Current.ActiveTaskTitle);
        Assert.Null(service.Current.StageId);
    }
}

