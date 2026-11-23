namespace NightCodeForge.Installer.ViewModels;

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

/// <summary>
/// Abstraction over native desktop window interactions (folder picker dialog and application shutdown).
/// </summary>
public interface IInstallerUiHost
{
    Task<string?> PickBundleFolderAsync(string? initialDirectory, CancellationToken cancellationToken = default);
    void RequestExit(int exitCode);
}

/// <summary>
/// Avalonia desktop host bridging ViewModels to <see cref="IStorageProvider"/> and <c>TryShutdown</c>.
/// </summary>
public sealed class AvaloniaInstallerUiHost : IInstallerUiHost
{
    private readonly Func<Window?> _windowAccessor;
    private readonly Action<int> _shutdownAction;

    public AvaloniaInstallerUiHost(Func<Window?> windowAccessor, Action<int> shutdownAction)
    {
        _windowAccessor = windowAccessor ?? throw new ArgumentNullException(nameof(windowAccessor));
        _shutdownAction = shutdownAction ?? throw new ArgumentNullException(nameof(shutdownAction));
    }

    public Task<string?> PickBundleFolderAsync(string? initialDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var window = _windowAccessor();
            if (window?.StorageProvider is not { CanPickFolder: true } storageProvider)
            {
                return null;
            }

            IStorageFolder? suggestedStart = null;
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            {
                suggestedStart = await storageProvider.TryGetFolderFromPathAsync(initialDirectory).ConfigureAwait(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var selected = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Air-Gapped Local Bundle Directory",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedStart
            }).ConfigureAwait(true);

            var folder = selected.FirstOrDefault();
            return folder?.TryGetLocalPath();
        });
    }

    public void RequestExit(int exitCode)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _shutdownAction(exitCode);
            return;
        }

        Dispatcher.UIThread.Post(() => _shutdownAction(exitCode));
    }
}
