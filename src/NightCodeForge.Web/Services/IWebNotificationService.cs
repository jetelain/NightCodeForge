using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Scoped service managing background browser notifications, SignalR telemetry triggers,
/// wakeup catch-up, active route suppression, and device-local preferences.
/// </summary>
public interface IWebNotificationService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string> GetPermissionStateAsync();
    Task<string> RequestPermissionAsync();
    Task<NotificationPreferences> GetPreferencesAsync();
    Task SavePreferencesAsync(NotificationPreferences preferences);
    Task<bool> SendTestNotificationAsync();
    Task<bool> DispatchNotificationAsync(NotificationSubject subject, ForgeTask task);
    Task RunCatchUpAsync(CancellationToken cancellationToken = default);
    Task HandleTabForegroundedAsync(CancellationToken cancellationToken = default);
    Task<bool> IsSupportedAsync();
}

