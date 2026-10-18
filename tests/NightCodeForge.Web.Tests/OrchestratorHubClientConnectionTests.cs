using Microsoft.AspNetCore.SignalR.Client;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class OrchestratorHubClientConnectionTests
{
    [Fact]
    public void ConnectionState_DefaultsToDisconnected()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void TriggerTokenDelta_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedToken = "";
        bool receivedIsThinking = false;

        connection.OnTokenDeltaReceived += (taskId, token, isThinking) =>
        {
            receivedTaskId = taskId;
            receivedToken = token;
            receivedIsThinking = isThinking;
        };

        connection.TriggerTokenDelta(12, "delta token", true);

        Assert.Equal(12, receivedTaskId);
        Assert.Equal("delta token", receivedToken);
        Assert.True(receivedIsThinking);
    }

    [Fact]
    public void TriggerToolExecution_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedTool = "";
        string receivedArgs = "";
        string receivedStatus = "";
        int? receivedDuration = null;

        connection.OnToolExecutionReceived += (taskId, tool, args, status, dur) =>
        {
            receivedTaskId = taskId;
            receivedTool = tool;
            receivedArgs = args;
            receivedStatus = status;
            receivedDuration = dur;
        };

        connection.TriggerToolExecution(42, "grep_search", "{\"Query\":\"foo\"}", "Completed", 85);

        Assert.Equal(42, receivedTaskId);
        Assert.Equal("grep_search", receivedTool);
        Assert.Equal("{\"Query\":\"foo\"}", receivedArgs);
        Assert.Equal("Completed", receivedStatus);
        Assert.Equal(85, receivedDuration);
    }

    [Fact]
    public void TriggerGitProgress_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedOp = "";
        string receivedMsg = "";

        connection.OnGitProgressReceived += (taskId, op, msg) =>
        {
            receivedTaskId = taskId;
            receivedOp = op;
            receivedMsg = msg;
        };

        connection.TriggerGitProgress(3, "checkout", "Switched to branch feature/task-3");

        Assert.Equal(3, receivedTaskId);
        Assert.Equal("checkout", receivedOp);
        Assert.Equal("Switched to branch feature/task-3", receivedMsg);
    }

    [Fact]
    public void TriggerWorkerStatus_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        string receivedStatus = "";
        int? receivedTaskId = null;
        string? receivedStageId = null;
        string receivedTarget = "";

        connection.OnWorkerStatusReceived += (status, taskId, stageId, target) =>
        {
            receivedStatus = status;
            receivedTaskId = taskId;
            receivedStageId = stageId;
            receivedTarget = target;
        };

        connection.TriggerWorkerStatus("Running", 99, "impl-agent", "Ollama GPU 0");

        Assert.Equal("Running", receivedStatus);
        Assert.Equal(99, receivedTaskId);
        Assert.Equal("impl-agent", receivedStageId);
        Assert.Equal("Ollama GPU 0", receivedTarget);
    }

    [Fact]
    public void TriggerStageTransition_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedStage = "";
        string receivedKind = "";
        string receivedStatus = "";

        connection.OnStageTransitionReceived += (taskId, stage, kind, status) =>
        {
            receivedTaskId = taskId;
            receivedStage = stage;
            receivedKind = kind;
            receivedStatus = status;
        };

        connection.TriggerStageTransition(15, "test-gate", "Deterministic", "Passed");

        Assert.Equal(15, receivedTaskId);
        Assert.Equal("test-gate", receivedStage);
        Assert.Equal("Deterministic", receivedKind);
        Assert.Equal("Passed", receivedStatus);
    }

    [Fact]
    public void TriggerDeterministicGateResult_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedGate = "";
        bool receivedPassed = false;
        string receivedSummary = "";

        connection.OnDeterministicGateResultReceived += (taskId, gate, passed, summary) =>
        {
            receivedTaskId = taskId;
            receivedGate = gate;
            receivedPassed = passed;
            receivedSummary = summary;
        };

        connection.TriggerDeterministicGateResult(21, "CoverageGate", true, "Coverage +2.3% (84.1%)");

        Assert.Equal(21, receivedTaskId);
        Assert.Equal("CoverageGate", receivedGate);
        Assert.True(receivedPassed);
        Assert.Equal("Coverage +2.3% (84.1%)", receivedSummary);
    }

    [Fact]
    public void TriggerHostDirtyAlert_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedBranch = "";

        connection.OnHostDirtyAlertReceived += (taskId, branch) =>
        {
            receivedTaskId = taskId;
            receivedBranch = branch;
        };

        connection.TriggerHostDirtyAlert(88, "feature/task-88-rescue");

        Assert.Equal(88, receivedTaskId);
        Assert.Equal("feature/task-88-rescue", receivedBranch);
    }

    [Fact]
    public void TriggerExecutionLog_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        int receivedTaskId = 0;
        string receivedStage = "";
        string receivedLvl = "";
        string receivedMsg = "";
        DateTime receivedTs = default;

        connection.OnExecutionLogReceived += (taskId, stage, lvl, msg, ts) =>
        {
            receivedTaskId = taskId;
            receivedStage = stage;
            receivedLvl = lvl;
            receivedMsg = msg;
            receivedTs = ts;
        };

        var now = DateTime.UtcNow;
        connection.TriggerExecutionLog(5, "build", "Info", "Build succeeded", now);

        Assert.Equal(5, receivedTaskId);
        Assert.Equal("build", receivedStage);
        Assert.Equal("Info", receivedLvl);
        Assert.Equal("Build succeeded", receivedMsg);
        Assert.Equal(now, receivedTs);
    }

    [Fact]
    public void TriggerConnectionStateChanged_InvokesEventDelegate()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        HubConnectionState receivedState = HubConnectionState.Disconnected;

        connection.OnConnectionStateChanged += state => receivedState = state;

        connection.TriggerConnectionStateChanged(HubConnectionState.Connected);

        Assert.Equal(HubConnectionState.Connected, receivedState);
    }

    [Fact]
    public async Task JoinAndLeaveTaskGroup_WhenDisconnected_DoNotThrow()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");

        var exJoin = await Record.ExceptionAsync(() => connection.JoinTaskGroupAsync(10));
        var exLeave = await Record.ExceptionAsync(() => connection.LeaveTaskGroupAsync(10));

        Assert.Null(exJoin);
        Assert.Null(exLeave);
    }

    [Fact]
    public async Task JoinTaskGroupAsync_TracksActiveTaskGroup()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        Assert.Empty(connection.ActiveTaskGroups);

        await connection.JoinTaskGroupAsync(42);
        await connection.JoinTaskGroupAsync(99);

        Assert.Contains(42, connection.ActiveTaskGroups);
        Assert.Contains(99, connection.ActiveTaskGroups);
        Assert.Equal(2, connection.ActiveTaskGroups.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task JoinTaskGroupAsync_WithNonPositiveTaskId_DoesNotTrackGroup(int invalidTaskId)
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        await connection.JoinTaskGroupAsync(invalidTaskId);

        Assert.Empty(connection.ActiveTaskGroups);
    }

    [Fact]
    public async Task LeaveTaskGroupAsync_RemovesActiveTaskGroup()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        await connection.JoinTaskGroupAsync(42);
        await connection.JoinTaskGroupAsync(99);

        await connection.LeaveTaskGroupAsync(42);

        Assert.DoesNotContain(42, connection.ActiveTaskGroups);
        Assert.Contains(99, connection.ActiveTaskGroups);
        Assert.Single(connection.ActiveTaskGroups);
    }

    [Fact]
    public async Task DisposeAsync_WhenUninitialized_DoesNotThrow()
    {
        var connection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        var ex = await Record.ExceptionAsync(async () => await connection.DisposeAsync());
        Assert.Null(ex);
    }
}
