using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Concurrency;

/// <summary>
/// Thread-safe active task cancellation manager enabling explicit cooperative cancellation
/// across queue dispatchers, background jobs, and running task orchestrator instances.
/// Each registration is owned by the caller that created it: the manager never cancels or disposes
/// a source it did not just create on behalf of an explicit <see cref="Cancel"/> request.
/// </summary>
public class TaskCancellationManager : ITaskCancellationManager
{
    private sealed class Registration
    {
        public Registration(CancellationTokenSource source)
        {
            Source = source;
        }

        public CancellationTokenSource Source { get; }

        public volatile bool ExplicitCancelRequested;
    }

    private readonly ConcurrentDictionary<int, Registration> _registrations = new();
    private readonly ILogger<TaskCancellationManager> _logger;

    public TaskCancellationManager(ILogger<TaskCancellationManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CancellationTokenSource Register(int taskId, CancellationToken parentToken = default)
    {
        var cts = parentToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(parentToken)
            : new CancellationTokenSource();

        // The previous owner (if any) is still responsible for its own source and removes it conditionally on Unregister.
        _registrations[taskId] = new Registration(cts);

        _logger.LogDebug("Registered cancellation source for Task #{TaskId}", taskId);
        return cts;
    }

    public bool Cancel(int taskId)
    {
        if (_registrations.TryGetValue(taskId, out var registration))
        {
            try
            {
                registration.ExplicitCancelRequested = true;
                registration.Source.Cancel();
                _logger.LogInformation("Signaled cancellation for active Task #{TaskId}", taskId);
                return true;
            }
            catch (ObjectDisposedException)
            {
                // Justification: Target CTS was already disposed upon task completion.
                _logger.LogDebug("Task #{TaskId} cancellation source was already disposed", taskId);
                return false;
            }
        }

        _logger.LogDebug("No active cancellation source registered for Task #{TaskId}", taskId);
        return false;
    }

    public bool IsCancelRequested(int taskId)
    {
        return _registrations.TryGetValue(taskId, out var registration) && registration.ExplicitCancelRequested;
    }

    public void Unregister(int taskId, CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_registrations.TryGetValue(taskId, out var registration)
            && ReferenceEquals(registration.Source, source)
            && _registrations.TryRemove(new KeyValuePair<int, Registration>(taskId, registration)))
        {
            _logger.LogDebug("Unregistered cancellation source for Task #{TaskId}", taskId);
        }
    }
}
