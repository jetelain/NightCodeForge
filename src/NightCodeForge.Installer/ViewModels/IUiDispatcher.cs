namespace NightCodeForge.Installer.ViewModels;

using Avalonia.Threading;

/// <summary>
/// Abstraction over UI-thread marshaling so ViewModels execute deterministically under both Avalonia and xUnit.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action);
}

/// <summary>
/// Production UI dispatcher marshaling updates onto Avalonia's <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }
}

/// <summary>
/// Synchronous UI dispatcher for headless tests without a running Avalonia application loop.
/// </summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="IProgress{T}"/> adapter that forwards reports directly through <see cref="IUiDispatcher"/>,
/// avoiding <see cref="Progress{T}"/>'s asynchronous ThreadPool capture when no <see cref="SynchronizationContext"/> is active.
/// </summary>
internal sealed class UiDispatchedProgress<T>(IUiDispatcher dispatcher, Action<T> handler) : IProgress<T>
{
    public void Report(T value) => dispatcher.Post(() => handler(value));
}
