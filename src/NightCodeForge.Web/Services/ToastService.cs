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
public class ToastService
{
    private readonly List<ToastMessage> _toasts = new();
    private readonly object _lock = new();

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
        var toast = new ToastMessage(Guid.NewGuid(), title, message, level, DateTime.UtcNow);
        lock (_lock)
        {
            _toasts.Add(toast);
        }
        OnToastsChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            Remove(toast.Id);
        });
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            _toasts.RemoveAll(t => t.Id == id);
        }
        OnToastsChanged?.Invoke();
    }
}
