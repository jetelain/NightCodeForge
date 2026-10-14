using Microsoft.AspNetCore.SignalR;

namespace NightCodeForge.Infrastructure.Hubs;

/// <summary>
/// SignalR hub providing real-time telemetry streaming for task execution, Git events, and worker state.
/// Blazor interactive clients can subscribe to specific task groups to receive fine-grained token and tool streams.
/// </summary>
public class OrchestratorHub : Hub<IOrchestratorHubClient>
{
    public const string HubUrl = "/hubs/orchestrator";

    /// <summary>
    /// Gets the SignalR group name for a specific task.
    /// </summary>
    public static string GetTaskGroupName(int taskId) => $"task-{taskId}";

    /// <summary>
    /// Subscribes the current connection to telemetry events for the specified task.
    /// </summary>
    public async Task JoinTaskGroup(int taskId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GetTaskGroupName(taskId));
    }

    /// <summary>
    /// Unsubscribes the current connection from telemetry events for the specified task.
    /// </summary>
    public async Task LeaveTaskGroup(int taskId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetTaskGroupName(taskId));
    }
}
