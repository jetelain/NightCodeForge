using NightCodeForge.Core.Execution;

namespace NightCodeForge.Web.Services;

public enum ToastLevel
{
    Info,
    Success,
    Warning,
    Error
}

public record ToastMessage(Guid Id, string Title, string Message, ToastLevel Level, DateTime Timestamp);

/// <summary>
/// Scoped service for publishing and dismissing non-blocking, asynchronous toast notifications.
/// </summary>
public class ToastService : IDisposable
{
    private readonly IBackgroundTaskRunner _backgroundTaskRunner;
    private readonly List<ToastMessage> _toasts = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public ToastService(IBackgroundTaskRunner backgroundTaskRunner)
    {
        _backgroundTaskRunner = backgroundTaskRunner ?? throw new ArgumentNullException(nameof(backgroundTaskRunner));
    }

    public IReadOnlyList<ToastMessage> Toasts
    {
        get
        {
            lock (_lock)
            {
                return _toasts.ToList();
            }
        }
    }

    public event Action? OnToastsChanged;

    public void Show(string title, string message, ToastLevel level = ToastLevel.Info)
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        var toast = new ToastMessage(Guid.NewGuid(), title, message, level, DateTime.UtcNow);
        lock (_lock)
        {
            if (_disposed) return;
            _toasts.Add(toast);
        }
        OnToastsChanged?.Invoke();

        try
        {
            _backgroundTaskRunner.RunTracked(async ct =>
            {
                await Task.Delay(5000, ct).ConfigureAwait(false);
                Remove(toast.Id);
            }, $"AutoDismissToast-{toast.Id}", _cts.Token);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _toasts.RemoveAll(t => t.Id == id);
        }
        OnToastsChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
    }
}
