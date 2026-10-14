using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NightCodeForge.Core.Streaming;

/// <summary>
/// Thread-safe in-memory activity broadcaster with fan-out channels for real-time UI streaming.
/// </summary>
public class InMemoryAgentActivityStreamer : IAgentActivityStreamer
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscriptions = new();

    public int ActiveSubscriptionsCount => _subscriptions.Count;

    public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
    {
        foreach (var (id, sub) in _subscriptions)
        {
            if (sub.TaskId == null || sub.TaskId == activityEvent.TaskId)
            {
                sub.Channel.Writer.TryWrite(activityEvent);
            }
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(
        int? taskId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<AgentActivityEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var subscription = new Subscription(taskId, channel);
        _subscriptions[subId] = subscription;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                AgentActivityEvent evt;
                try
                {
                    evt = await channel.Reader.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                yield return evt;
            }
        }
        finally
        {
            _subscriptions.TryRemove(subId, out _);
            channel.Writer.TryComplete();
        }
    }

    private record Subscription(int? TaskId, Channel<AgentActivityEvent> Channel);
}
