namespace NightCodeForge.Installer.Core.Services;

/// <summary>
/// Controls installed NightCodeForge, OllamaRouter, and NightCodeAssay applications and inspects local bundles.
/// </summary>
public interface IHostAppControlService
{
    bool IsWebServerRunning();

    string GetConfiguredWebUrl(string installDirectory);

    Task<bool> StartWebServerAsync(string installDirectory, CancellationToken cancellationToken = default);

    Task<bool> StopWebServerAsync(CancellationToken cancellationToken = default);

    Task<bool> OpenWebUiInBrowserAsync(string url, CancellationToken cancellationToken = default);

    Task<bool> OpenCliTerminalAsync(string installDirectory, CancellationToken cancellationToken = default);

    Task<bool> LaunchNightCodeAssayAsync(CancellationToken cancellationToken = default);

    Task<bool> OpenOllamaRouterAppAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<string> ScanBundleDirectoryAssets(string? bundleDirectory);
}
