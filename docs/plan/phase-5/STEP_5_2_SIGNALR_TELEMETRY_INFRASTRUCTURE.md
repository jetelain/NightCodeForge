# ⚡ Step 5.2: SignalR Real-Time Telemetry Hub & Streaming Infrastructure

> **Phase 5 Implementation Step**  
> *Parent Plan:* [Phase 5 Master Plan](../05_BLAZOR_WEB_UI_AND_TELEMETRY.md) | *Preceding Step:* [Step 5.1: Design System & Layout](STEP_5_1_DESIGN_SYSTEM_AND_LAYOUT.md) | *Next Step:* [Step 5.3: Ideation & Discovery Chat](STEP_5_3_IDEATION_AND_DISCOVERY_CHAT.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/Hubs` & `src/NightCodeForge.Web/Services` | *Status:* Ready for Implementation

---

## 1. Executive Objective

Provide a robust, low-latency, bidirectional real-time communication pipeline connecting the backend orchestrator, Git runner, quality gate evaluators, and LLM agent harnesses directly to the Blazor interactive client components.

### Core Capabilities:
1. **Strongly Typed Hub Contract (`IOrchestratorHubClient`):** Eliminates stringly-typed client invocation mistakes. All telemetry events are typed records.
2. **Channel-to-SignalR Reactive Bridge:** A background hosted bridge that consumes events from `IAgentActivityStreamer` (the in-memory fan-out channel) and pushes them to SignalR clients and targeted issue groups.
3. **Targeted Group Subscriptions:** Blazor pages subscribe to specific issue streams (`JoinIssueGroup(issueId)`) to avoid inundating inactive views with high-frequency LLM token deltas, while broadcasting global worker state changes globally.
4. **Resilient Client Connection Wrapper (`OrchestratorHubClientConnection`):** Provides automated reconnection with exponential backoff, circuit breaking, connection state tracking (`Connected`, `Reconnecting`, `Disconnected`), and easy C# event delegates for Blazor components.

---

## 2. Event Flow & Architecture

```mermaid
flowchart TD
    subgraph Producers ["Backend Event Producers"]
        AgentHarness["Crucible / Pi / ACP Harness\n(Tokens, Tool Calls)"]
        GitRunner["GitCliRunner / GitWorkspaceManager\n(Branching, Commits, Sync)"]
        Workflow["IssueOrchestrator & Stages\n(Stage Transitions, Quality Gates)"]
        Hangfire["Job Queue / Worker Monitor\n(Worker Status, Idle/Running)"]
    end

    subgraph CoreStreamer ["In-Memory Streamer"]
        ActivityStreamer["IAgentActivityStreamer\n(InMemoryAgentActivityStreamer Channel)"]
    end

    subgraph HubLayer ["SignalR Infrastructure"]
        Bridge["SignalRActivityBridgeService\n(Background Hosted Service)"]
        Hub["OrchestratorHub (/hubs/orchestrator)\n(IOrchestratorHubClient)"]
    end

    subgraph ClientLayer ["Blazor Interactive Client"]
        ClientConn["OrchestratorHubClientConnection\n(Scoped Client Service)"]
        MonitoringView["Monitoring Console\n(Token Stream, Tool Badges)"]
        BacklogView["Backlog / DAG Page\n(Status Updates)"]
        TopNavView["TopNavBar\n(GPU Status Gauge)"]
        ReviewView["Review Room\n(Quality Gate Badges)"]
    end

    AgentHarness --> ActivityStreamer
    GitRunner --> Hub
    Workflow --> Hub
    Hangfire --> Hub
    ActivityStreamer --> Bridge
    Bridge --> Hub
    Hub --> ClientConn
    ClientConn --> MonitoringView
    ClientConn --> BacklogView
    ClientConn --> TopNavView
    ClientConn --> ReviewView
```

---

## 3. Data Contracts & Interfaces

### 3.1 Hub Client Contract (`NightCodeForge.Infrastructure/Hubs/IOrchestratorHubClient.cs`)

```csharp
namespace NightCodeForge.Infrastructure.Hubs;

public interface IOrchestratorHubClient
{
    /// <summary>
    /// Live streaming of model reasoning thoughts (<think>) or answer text.
    /// </summary>
    Task ReceiveTokenDelta(int issueId, string token, bool isThinking);

    /// <summary>
    /// Tool execution notification (e.g. read, write, edit, bash, ask).
    /// </summary>
    Task ReceiveToolExecution(int issueId, string toolName, string arguments, string status, int? durationMs);

    /// <summary>
    /// Git operation telemetry (e.g. branch creation, WIP commit, fetch, push).
    /// </summary>
    Task ReceiveGitProgress(int issueId, string operation, string message);

    /// <summary>
    /// Global worker state change (Idle, Running, WaitingForHuman, Blocked).
    /// </summary>
    Task ReceiveWorkerStatus(string status, int? activeIssueId, string? stageId, string hardwareTarget);

    /// <summary>
    /// Pipeline advancement across agentic and deterministic stages.
    /// </summary>
    Task ReceiveStageTransition(int issueId, string stageId, string stageKind, string status);

    /// <summary>
    /// Differential quality gate outcomes (Coverage delta, Warning ratchet delta, Test assertions).
    /// </summary>
    Task ReceiveDeterministicGateResult(int issueId, string gateName, bool passed, string metricsSummary);

    /// <summary>
    /// Developer host tree dirty alert preventing push/merge.
    /// </summary>
    Task ReceiveHostDirtyAlert(int issueId, string branchName);

    /// <summary>
    /// Execution log line for real-time terminal streaming.
    /// </summary>
    Task ReceiveExecutionLog(int issueId, string stageId, string level, string message, DateTime timestamp);
}
```

### 3.2 SignalR Hub Implementation (`NightCodeForge.Infrastructure/Hubs/OrchestratorHub.cs`)

```csharp
using Microsoft.AspNetCore.SignalR;

namespace NightCodeForge.Infrastructure.Hubs;

public class OrchestratorHub : Hub<IOrchestratorHubClient>
{
    public static string GetIssueGroupName(int issueId) => $"issue-{issueId}";

    public async Task JoinIssueGroup(int issueId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetIssueGroupName(issueId));
    }

    public async Task LeaveIssueGroup(int issueId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetIssueGroupName(issueId));
    }
}
```

### 3.3 Activity Channel to SignalR Bridge (`NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs`)

A background `IHostedService` that subscribes to `IAgentActivityStreamer` and distributes events to `IHubContext<OrchestratorHub, IOrchestratorHubClient>`:

```csharp
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Infrastructure.Hubs;

public class SignalRActivityBridgeService : BackgroundService
{
    private readonly IAgentActivityStreamer _streamer;
    private readonly IHubContext<OrchestratorHub, IOrchestratorHubClient> _hubContext;
    private readonly ILogger<SignalRActivityBridgeService> _logger;

    public SignalRActivityBridgeService(
        IAgentActivityStreamer streamer,
        IHubContext<OrchestratorHub, IOrchestratorHubClient> hubContext,
        ILogger<SignalRActivityBridgeService> logger)
    {
        _streamer = streamer;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalRActivityBridgeService starting event subscription loop.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in _streamer.SubscribeAsync(cancellationToken: stoppingToken))
                {
                    var groupName = OrchestratorHub.GetIssueGroupName(evt.IssueId);

                    switch (evt.Kind)
                    {
                        case AgentActivityKind.ThinkingDelta:
                            await _hubContext.Clients.Group(groupName)
                                .ReceiveTokenDelta(evt.IssueId, evt.Content, isThinking: true);
                            break;

                        case AgentActivityKind.MessageDelta:
                            await _hubContext.Clients.Group(groupName)
                                .ReceiveTokenDelta(evt.IssueId, evt.Content, isThinking: false);
                            break;

                        case AgentActivityKind.ToolStarted:
                            var toolName = evt.Metadata?.GetValueOrDefault("ToolName")?.ToString() ?? "tool";
                            await _hubContext.Clients.Group(groupName)
                                .ReceiveToolExecution(evt.IssueId, toolName, evt.Content, "Running", null);
                            break;

                        case AgentActivityKind.ToolCompleted:
                            var completedTool = evt.Metadata?.GetValueOrDefault("ToolName")?.ToString() ?? "tool";
                            var dur = evt.Metadata?.GetValueOrDefault("DurationMs") as int?;
                            await _hubContext.Clients.Group(groupName)
                                .ReceiveToolExecution(evt.IssueId, completedTool, evt.Content, "Completed", dur);
                            break;

                        case AgentActivityKind.StageStarted:
                        case AgentActivityKind.StageCompleted:
                            var status = evt.Kind == AgentActivityKind.StageStarted ? "Running" : "Completed";
                            var kind = evt.Metadata?.GetValueOrDefault("StageKind")?.ToString() ?? "Agentic";
                            await _hubContext.Clients.All
                                .ReceiveStageTransition(evt.IssueId, evt.StageId, kind, status);
                            break;

                        case AgentActivityKind.Interrupted:
                            await _hubContext.Clients.All
                                .ReceiveWorkerStatus("WaitingForHuman", evt.IssueId, evt.StageId, "Local GPU");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in SignalRActivityBridgeService; retrying in 2 seconds.");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
```

---

## 4. Client-Side Blazor SignalR Service (`NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs`)

Manages the SignalR connection lifecycle in the Blazor server app:

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace NightCodeForge.Web.Services;

public class OrchestratorHubClientConnection : IAsyncDisposable
{
    private readonly NavigationManager _navigationManager;
    private HubConnection? _hubConnection;

    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public event Action<int, string, bool>? OnTokenDeltaReceived;
    public event Action<int, string, string, string, int?>? OnToolExecutionReceived;
    public event Action<int, string, string>? OnGitProgressReceived;
    public event Action<string, int?, string?, string>? OnWorkerStatusReceived;
    public event Action<int, string, string, string>? OnStageTransitionReceived;
    public event Action<int, string, bool, string>? OnDeterministicGateResultReceived;
    public event Action<int, string>? OnHostDirtyAlertReceived;
    public event Action<int, string, string, string, DateTime>? OnExecutionLogReceived;
    public event Action<HubConnectionState>? OnConnectionStateChanged;

    public OrchestratorHubClientConnection(NavigationManager navigationManager)
    {
        _navigationManager = navigationManager;
    }

    public async Task StartAsync()
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            return;

        var hubUrl = _navigationManager.ToAbsoluteUri("/hubs/orchestrator");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        RegisterHandlers(_hubConnection);

        _hubConnection.Reconnecting += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _hubConnection.Reconnected += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Connected); return Task.CompletedTask; };
        _hubConnection.Closed += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };

        await _hubConnection.StartAsync();
        OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
    }

    public async Task JoinIssueGroupAsync(int issueId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinIssueGroup", issueId);
        }
    }

    public async Task LeaveIssueGroupAsync(int issueId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("LeaveIssueGroup", issueId);
        }
    }

    private void RegisterHandlers(HubConnection hub)
    {
        hub.On<int, string, bool>("ReceiveTokenDelta", (issueId, token, isThinking) =>
            OnTokenDeltaReceived?.Invoke(issueId, token, isThinking));

        hub.On<int, string, string, string, int?>("ReceiveToolExecution", (issueId, tool, args, status, dur) =>
            OnToolExecutionReceived?.Invoke(issueId, tool, args, status, dur));

        hub.On<int, string, string>("ReceiveGitProgress", (issueId, op, msg) =>
            OnGitProgressReceived?.Invoke(issueId, op, msg));

        hub.On<string, int?, string?, string>("ReceiveWorkerStatus", (status, issueId, stageId, target) =>
            OnWorkerStatusReceived?.Invoke(status, issueId, stageId, target));

        hub.On<int, string, string, string>("ReceiveStageTransition", (issueId, stageId, kind, status) =>
            OnStageTransitionReceived?.Invoke(issueId, stageId, kind, status));

        hub.On<int, string, bool, string>("ReceiveDeterministicGateResult", (issueId, gate, passed, summary) =>
            OnDeterministicGateResultReceived?.Invoke(issueId, gate, passed, summary));

        hub.On<int, string>("ReceiveHostDirtyAlert", (issueId, branch) =>
            OnHostDirtyAlertReceived?.Invoke(issueId, branch));

        hub.On<int, string, string, string, DateTime>("ReceiveExecutionLog", (issueId, stage, lvl, msg, ts) =>
            OnExecutionLogReceived?.Invoke(issueId, stage, lvl, msg, ts));
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}
```

---

## 5. Wiring & Program.cs Setup

In `NightCodeForge.Web/Program.cs`:
```csharp
// Register SignalR
builder.Services.AddSignalR();
builder.Services.AddHostedService<SignalRActivityBridgeService>();
builder.Services.AddScoped<OrchestratorHubClientConnection>();

// In middleware configuration:
app.MapHub<OrchestratorHub>("/hubs/orchestrator");
```

---

## 6. Verification & Test Plan

1. **Hub Connectivity & Reconnect:**
   - Client connects to `/hubs/orchestrator` and receives `HubConnectionState.Connected`.
   - Temporary server restart triggers automatic reconnect with state transitions.
2. **Channel to SignalR Bridge Broadcasting:**
   - Publish `AgentActivityEvent` to `IAgentActivityStreamer`.
   - Verify client receives `ReceiveTokenDelta` and `ReceiveToolExecution` for joined group.
3. **Group Isolation:**
   - Client A joined to `issue-1` does NOT receive tokens for `issue-2`.
   - Global status events (`ReceiveWorkerStatus`) are broadcast to all clients regardless of group.
4. **Unit & Integration Tests:**
   - `OrchestratorHubTests`: verify group management methods.
   - `SignalRActivityBridgeServiceTests`: verify event mapping from `AgentActivityEvent` to SignalR client calls using mock `IHubContext`.

---

## 7. Definition of Done (DoD) Checklist

- [ ] `IOrchestratorHubClient` interface defined with all 8 real-time callback methods.
- [ ] `OrchestratorHub` created and mapped to `/hubs/orchestrator`.
- [ ] `SignalRActivityBridgeService` hosted service running and forwarding `IAgentActivityStreamer` channel events.
- [ ] `OrchestratorHubClientConnection` implemented with auto-reconnection and typed event delegates.
- [ ] DI configuration in `NightCodeForge.Infrastructure` and `NightCodeForge.Web` wired.
- [ ] Integration tests verify message dispatch and group filtering.
