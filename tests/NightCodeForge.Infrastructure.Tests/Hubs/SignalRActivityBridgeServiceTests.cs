using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Hubs;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Hubs;

public class SignalRActivityBridgeServiceTests
{
    [Fact]
    public async Task DispatchActivityEventAsync_ThinkingDelta_RoutesToTaskGroupWithThinkingFlag()
    {
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            new InMemoryAgentActivityStreamer(),
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        var evt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 42,
            StageId: "impl",
            Kind: AgentActivityKind.ThinkingDelta,
            Content: "Let me check the diff...",
            Timestamp: DateTime.UtcNow);

        await bridge.DispatchActivityEventAsync(evt);

        var groupClient = fakeHubContext.GetGroupClient("task-42");
        Assert.NotNull(groupClient);
        Assert.Single(groupClient.TokenDeltas);
        Assert.Equal(42, groupClient.TokenDeltas[0].TaskId);
        Assert.Equal("Let me check the diff...", groupClient.TokenDeltas[0].Token);
        Assert.True(groupClient.TokenDeltas[0].IsThinking);
    }

    [Fact]
    public async Task DispatchActivityEventAsync_MessageDelta_RoutesToTaskGroup()
    {
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            new InMemoryAgentActivityStreamer(),
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        var evt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 42,
            StageId: "impl",
            Kind: AgentActivityKind.MessageDelta,
            Content: "Updated the file.",
            Timestamp: DateTime.UtcNow);

        await bridge.DispatchActivityEventAsync(evt);

        var groupClient = fakeHubContext.GetGroupClient("task-42");
        Assert.NotNull(groupClient);
        Assert.Single(groupClient.TokenDeltas);
        Assert.Equal("Updated the file.", groupClient.TokenDeltas[0].Token);
        Assert.False(groupClient.TokenDeltas[0].IsThinking);
    }

    [Fact]
    public async Task DispatchActivityEventAsync_ToolStartedAndCompleted_RoutesWithMetadata()
    {
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            new InMemoryAgentActivityStreamer(),
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        var startedEvt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 99,
            StageId: "impl",
            Kind: AgentActivityKind.ToolStarted,
            Content: "{\"path\":\"file.cs\"}",
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?> { ["ToolName"] = "edit_file" });

        var completedEvt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 99,
            StageId: "impl",
            Kind: AgentActivityKind.ToolCompleted,
            Content: "Success",
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?>
            {
                ["ToolName"] = "edit_file",
                ["DurationMs"] = 142
            });

        await bridge.DispatchActivityEventAsync(startedEvt);
        await bridge.DispatchActivityEventAsync(completedEvt);

        var groupClient = fakeHubContext.GetGroupClient("task-99");
        Assert.NotNull(groupClient);
        Assert.Equal(2, groupClient.ToolExecutions.Count);

        var startCall = groupClient.ToolExecutions[0];
        Assert.Equal("edit_file", startCall.ToolName);
        Assert.Equal("Running", startCall.Status);
        Assert.Null(startCall.DurationMs);

        var endCall = groupClient.ToolExecutions[1];
        Assert.Equal("edit_file", endCall.ToolName);
        Assert.Equal("Completed", endCall.Status);
        Assert.Equal(142, endCall.DurationMs);
    }

    [Fact]
    public async Task DispatchActivityEventAsync_StageTransitionsAndInterrupted_BroadcastsToAll()
    {
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            new InMemoryAgentActivityStreamer(),
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        var stageEvt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 7,
            StageId: "test-stage",
            Kind: AgentActivityKind.StageStarted,
            Content: "Stage starting",
            Timestamp: DateTime.UtcNow,
            Metadata: new Dictionary<string, object?> { ["StageKind"] = "Deterministic" });

        var interruptedEvt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 7,
            StageId: "human-gate",
            Kind: AgentActivityKind.Interrupted,
            Content: "Need human approval",
            Timestamp: DateTime.UtcNow);

        await bridge.DispatchActivityEventAsync(stageEvt);
        await bridge.DispatchActivityEventAsync(interruptedEvt);

        var allClient = fakeHubContext.AllClient;
        Assert.Single(allClient.StageTransitions);
        Assert.Equal("test-stage", allClient.StageTransitions[0].StageId);
        Assert.Equal("Deterministic", allClient.StageTransitions[0].StageKind);
        Assert.Equal("Running", allClient.StageTransitions[0].Status);

        Assert.Single(allClient.WorkerStatuses);
        Assert.Equal("WaitingForHuman", allClient.WorkerStatuses[0].Status);
        Assert.Equal(7, allClient.WorkerStatuses[0].ActiveTaskId);
    }

    [Fact]
    public async Task DispatchActivityEventAsync_ErrorEvent_RoutesExecutionLogToTaskGroup()
    {
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            new InMemoryAgentActivityStreamer(),
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        var now = DateTime.UtcNow;
        var errorEvt = new AgentActivityEvent(
            SessionId: "sess-1",
            TaskId: 55,
            StageId: "build",
            Kind: AgentActivityKind.Error,
            Content: "Compiler error CS0103",
            Timestamp: now);

        await bridge.DispatchActivityEventAsync(errorEvt);

        var groupClient = fakeHubContext.GetGroupClient("task-55");
        Assert.NotNull(groupClient);
        Assert.Single(groupClient.ExecutionLogs);
        Assert.Equal("Error", groupClient.ExecutionLogs[0].Level);
        Assert.Equal("Compiler error CS0103", groupClient.ExecutionLogs[0].Message);
    }

    [Fact]
    public async Task SignalRActivityBridgeService_RunsHostedExecutionLoop_AndProcessesStreamerEvents()
    {
        var streamer = new InMemoryAgentActivityStreamer();
        var fakeHubContext = new FakeHubContext();
        var bridge = new SignalRActivityBridgeService(
            streamer,
            fakeHubContext,
            NullLogger<SignalRActivityBridgeService>.Instance);

        using var cts = new CancellationTokenSource();
        var bridgeTask = bridge.StartAsync(cts.Token);

        // Wait until bridge has subscribed to streamer
        for (int i = 0; i < 50 && streamer.ActiveSubscriptionsCount == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, streamer.ActiveSubscriptionsCount);

        // Broadcast through in-memory channel
        await streamer.BroadcastAsync(new AgentActivityEvent(
            SessionId: "session-abc",
            TaskId: 10,
            StageId: "impl",
            Kind: AgentActivityKind.MessageDelta,
            Content: "Streamed message through channel",
            Timestamp: DateTime.UtcNow));

        // Allow loop to process
        FakeOrchestratorHubClient? groupClient = null;
        for (int i = 0; i < 50; i++)
        {
            groupClient = fakeHubContext.GetGroupClient("task-10");
            if (groupClient?.TokenDeltas.Count > 0)
            {
                break;
            }
            await Task.Delay(20);
        }

        Assert.NotNull(groupClient);
        Assert.Single(groupClient.TokenDeltas);
        Assert.Equal("Streamed message through channel", groupClient.TokenDeltas[0].Token);

        await cts.CancelAsync();
        await bridge.StopAsync(CancellationToken.None);
        await bridgeTask;
    }

    private class FakeHubContext : IHubContext<OrchestratorHub, IOrchestratorHubClient>
    {
        private readonly FakeHubClients _clients = new();

        public IHubClients<IOrchestratorHubClient> Clients => _clients;
        public IGroupManager Groups => throw new NotImplementedException();

        public FakeOrchestratorHubClient AllClient => _clients.AllRecorded;
        public FakeOrchestratorHubClient? GetGroupClient(string groupName) =>
            _clients.GroupClients.TryGetValue(groupName, out var client) ? client : null;
    }

    private class FakeHubClients : IHubClients<IOrchestratorHubClient>
    {
        public FakeOrchestratorHubClient AllRecorded { get; } = new();
        public Dictionary<string, FakeOrchestratorHubClient> GroupClients { get; } = new();

        public IOrchestratorHubClient All => AllRecorded;

        public IOrchestratorHubClient Group(string groupName)
        {
            if (!GroupClients.TryGetValue(groupName, out var client))
            {
                client = new FakeOrchestratorHubClient();
                GroupClients[groupName] = client;
            }
            return client;
        }

        public IOrchestratorHubClient Client(string connectionId) => throw new NotImplementedException();
        public IOrchestratorHubClient User(string userId) => throw new NotImplementedException();
        public IOrchestratorHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
        public IOrchestratorHubClient Clients(IReadOnlyList<string> connectionIds) => throw new NotImplementedException();
        public IOrchestratorHubClient Groups(IReadOnlyList<string> groupNames) => throw new NotImplementedException();
        public IOrchestratorHubClient Users(IReadOnlyList<string> userIds) => throw new NotImplementedException();
        public IOrchestratorHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotImplementedException();
    }

    private class FakeOrchestratorHubClient : IOrchestratorHubClient
    {
        public List<(int TaskId, string Token, bool IsThinking)> TokenDeltas { get; } = new();
        public List<(int TaskId, string ToolName, string Arguments, string Status, int? DurationMs)> ToolExecutions { get; } = new();
        public List<(int TaskId, string Operation, string Message)> GitProgress { get; } = new();
        public List<(string Status, int? ActiveTaskId, string? StageId, string HardwareTarget)> WorkerStatuses { get; } = new();
        public List<(int TaskId, string StageId, string StageKind, string Status)> StageTransitions { get; } = new();
        public List<(int TaskId, string GateName, bool Passed, string MetricsSummary)> GateResults { get; } = new();
        public List<(int TaskId, string BranchName)> HostDirtyAlerts { get; } = new();
        public List<(int TaskId, string StageId, string Level, string Message, DateTime Timestamp)> ExecutionLogs { get; } = new();

        public Task ReceiveTokenDelta(int taskId, string token, bool isThinking)
        {
            TokenDeltas.Add((taskId, token, isThinking));
            return Task.CompletedTask;
        }

        public Task ReceiveToolExecution(int taskId, string toolName, string arguments, string status, int? durationMs)
        {
            ToolExecutions.Add((taskId, toolName, arguments, status, durationMs));
            return Task.CompletedTask;
        }

        public Task ReceiveGitProgress(int taskId, string operation, string message)
        {
            GitProgress.Add((taskId, operation, message));
            return Task.CompletedTask;
        }

        public Task ReceiveWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget)
        {
            WorkerStatuses.Add((status, activeTaskId, stageId, hardwareTarget));
            return Task.CompletedTask;
        }

        public Task ReceiveStageTransition(int taskId, string stageId, string stageKind, string status)
        {
            StageTransitions.Add((taskId, stageId, stageKind, status));
            return Task.CompletedTask;
        }

        public Task ReceiveDeterministicGateResult(int taskId, string gateName, bool passed, string metricsSummary)
        {
            GateResults.Add((taskId, gateName, passed, metricsSummary));
            return Task.CompletedTask;
        }

        public Task ReceiveHostDirtyAlert(int taskId, string branchName)
        {
            HostDirtyAlerts.Add((taskId, branchName));
            return Task.CompletedTask;
        }

        public Task ReceiveExecutionLog(int taskId, string stageId, string level, string message, DateTime timestamp)
        {
            ExecutionLogs.Add((taskId, stageId, level, message, timestamp));
            return Task.CompletedTask;
        }
    }
}
