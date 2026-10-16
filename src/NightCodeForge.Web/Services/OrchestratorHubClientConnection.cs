using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Client-side connection manager for the <see cref="NightCodeForge.Infrastructure.Hubs.OrchestratorHub"/>.
/// Encapsulates connection lifecycle, exponential auto-reconnect, and typed C# event delegates for Blazor components.
/// </summary>
public class OrchestratorHubClientConnection : IAsyncDisposable
{
    private readonly NavigationManager? _navigationManager;
    private readonly string? _configuredHubUrl;
    private HubConnection? _hubConnection;

    public HubConnection? HubConnection => _hubConnection;
    public HubConnectionState State => _hubConnection?.State ?? HubConnectionState.Disconnected;

    public event Action<int, string, bool>? OnTokenDeltaReceived;
    public event Action<int, string, string, string, int?>? OnToolExecutionReceived;
    public event Action<int, string, string>? OnGitProgressReceived;
    public event Action<string, int?, string?, string>? OnWorkerStatusReceived;
    public event Action<int, string, string, string>? OnStageTransitionReceived;
    public event Action<int, string, bool, string>? OnDeterministicGateResultReceived;
    public event Action<int, string>? OnHostDirtyAlertReceived;
    public event Action<int, string, string, string, DateTime>? OnExecutionLogReceived;
    public event Action<int, string, string, string, int, DateTime>? OnOptimizationActionReceived;
    public event Action<HubConnectionState>? OnConnectionStateChanged;

    public OrchestratorHubClientConnection(NavigationManager navigationManager)
    {
        _navigationManager = navigationManager;
    }

    public OrchestratorHubClientConnection(string hubUrl)
    {
        _configuredHubUrl = hubUrl;
    }

    public OrchestratorHubClientConnection(HubConnection hubConnection)
    {
        _hubConnection = hubConnection;
        RegisterHandlers(_hubConnection);
        _hubConnection.Reconnecting += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting); return Task.CompletedTask; };
        _hubConnection.Reconnected += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Connected); return Task.CompletedTask; };
        _hubConnection.Closed += _ => { OnConnectionStateChanged?.Invoke(HubConnectionState.Disconnected); return Task.CompletedTask; };
    }

    public async Task StartAsync()
    {
        if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
        {
            return;
        }

        if (_hubConnection == null)
        {
            Uri hubUri;
            if (!string.IsNullOrEmpty(_configuredHubUrl))
            {
                hubUri = new Uri(_configuredHubUrl, UriKind.RelativeOrAbsolute);
            }
            else if (_navigationManager != null)
            {
                hubUri = _navigationManager.ToAbsoluteUri("/hubs/orchestrator");
            }
            else
            {
                hubUri = new Uri("/hubs/orchestrator", UriKind.Relative);
            }

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(hubUri)
                .WithAutomaticReconnect(new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10)
                })
                .Build();

            RegisterHandlers(_hubConnection);

            _hubConnection.Reconnecting += _ =>
            {
                OnConnectionStateChanged?.Invoke(HubConnectionState.Reconnecting);
                return Task.CompletedTask;
            };

            _hubConnection.Reconnected += _ =>
            {
                OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
                return Task.CompletedTask;
            };

            _hubConnection.Closed += _ =>
            {
                OnConnectionStateChanged?.Invoke(HubConnectionState.Disconnected);
                return Task.CompletedTask;
            };
        }

        await _hubConnection.StartAsync();
        OnConnectionStateChanged?.Invoke(HubConnectionState.Connected);
    }

    public async Task JoinTaskGroupAsync(int taskId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("JoinTaskGroup", taskId);
        }
    }

    public async Task LeaveTaskGroupAsync(int taskId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            await _hubConnection.InvokeAsync("LeaveTaskGroup", taskId);
        }
    }

    public void RegisterHandlers(HubConnection hub)
    {
        hub.On<int, string, bool>("ReceiveTokenDelta", TriggerTokenDelta);
        hub.On<int, string, string, string, int?>("ReceiveToolExecution", TriggerToolExecution);
        hub.On<int, string, string>("ReceiveGitProgress", TriggerGitProgress);
        hub.On<string, int?, string?, string>("ReceiveWorkerStatus", TriggerWorkerStatus);
        hub.On<int, string, string, string>("ReceiveStageTransition", TriggerStageTransition);
        hub.On<int, string, bool, string>("ReceiveDeterministicGateResult", TriggerDeterministicGateResult);
        hub.On<int, string>("ReceiveHostDirtyAlert", TriggerHostDirtyAlert);
        hub.On<int, string, string, string, DateTime>("ReceiveExecutionLog", TriggerExecutionLog);
        hub.On<int, string, string, string, int, DateTime>("ReceiveOptimizationAction", TriggerOptimizationAction);
    }

    public void TriggerTokenDelta(int taskId, string token, bool isThinking) =>
        OnTokenDeltaReceived?.Invoke(taskId, token, isThinking);

    public void TriggerToolExecution(int taskId, string toolName, string arguments, string status, int? durationMs) =>
        OnToolExecutionReceived?.Invoke(taskId, toolName, arguments, status, durationMs);

    public void TriggerGitProgress(int taskId, string operation, string message) =>
        OnGitProgressReceived?.Invoke(taskId, operation, message);

    public void TriggerWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget) =>
        OnWorkerStatusReceived?.Invoke(status, activeTaskId, stageId, hardwareTarget);

    public void TriggerStageTransition(int taskId, string stageId, string stageKind, string status) =>
        OnStageTransitionReceived?.Invoke(taskId, stageId, stageKind, status);

    public void TriggerDeterministicGateResult(int taskId, string gateName, bool passed, string metricsSummary) =>
        OnDeterministicGateResultReceived?.Invoke(taskId, gateName, passed, metricsSummary);

    public void TriggerHostDirtyAlert(int taskId, string branchName) =>
        OnHostDirtyAlertReceived?.Invoke(taskId, branchName);

    public void TriggerExecutionLog(int taskId, string stageId, string level, string message, DateTime timestamp) =>
        OnExecutionLogReceived?.Invoke(taskId, stageId, level, message, timestamp);

    public void TriggerOptimizationAction(int taskId, string stageId, string actionType, string description, int tokensOrBytesSaved, DateTime timestamp) =>
        OnOptimizationActionReceived?.Invoke(taskId, stageId, actionType, description, tokensOrBytesSaved, timestamp);

    public void TriggerConnectionStateChanged(HubConnectionState state) =>
        OnConnectionStateChanged?.Invoke(state);

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
        }
    }
}
