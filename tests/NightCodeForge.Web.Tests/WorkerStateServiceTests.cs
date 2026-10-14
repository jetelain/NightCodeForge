using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkerStateServiceTests
{
    [Fact]
    public void InitialState_ShouldBeIdleWithZeroPendingQuestions()
    {
        var service = new WorkerStateService();

        Assert.Equal(WorkerExecutionStatus.Idle, service.Current.Status);
        Assert.Null(service.Current.ActiveTaskId);
        Assert.Null(service.Current.ActiveTaskTitle);
        Assert.Null(service.Current.StageId);
        Assert.Null(service.Current.StageKind);
        Assert.Equal("Local GPU", service.Current.HardwareTarget);
        Assert.Equal(0, service.Current.PendingQuestionCount);
    }

    [Fact]
    public void UpdateStatus_ShouldUpdateSnapshotAndNotify()
    {
        var service = new WorkerStateService();
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
        var service = new WorkerStateService();

        Assert.Throws<ArgumentNullException>(() => service.UpdateStatus(null!));
    }

    [Fact]
    public void UpdatePendingQuestions_ShouldPreserveSnapshotAndNotify()
    {
        var service = new WorkerStateService();
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
}
