using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WebNotificationsSettingsTabTests : BunitContext
{
    private readonly Mock<IWebNotificationService> _serviceMock;
    private readonly ToastService _toastService;
    private NotificationPreferences _preferences;

    public WebNotificationsSettingsTabTests()
    {
        _serviceMock = new Mock<IWebNotificationService>();
        _toastService = TestServices.CreateToastService();
        _preferences = new NotificationPreferences();

        _serviceMock.Setup(s => s.GetPreferencesAsync()).ReturnsAsync(() => _preferences);
        _serviceMock.Setup(s => s.SavePreferencesAsync(It.IsAny<NotificationPreferences>()))
            .Returns<NotificationPreferences>(p =>
            {
                _preferences = p;
                return Task.CompletedTask;
            });
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Default);
        _serviceMock.Setup(s => s.IsSupportedAsync()).ReturnsAsync(true);
        _serviceMock.Setup(s => s.SendTestNotificationAsync()).ReturnsAsync(true);
        _serviceMock.Setup(s => s.RequestPermissionAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Granted);

        Services.AddSingleton<IWebNotificationService>(_serviceMock.Object);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void RendersPermissionState_Default_ShowsRequestButton()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Default);

        var cut = Render<WebNotificationsSettingsTab>();

        var badge = cut.Find("[data-testid='notification-permission-badge']");
        Assert.Contains("Permission Prompt Required", badge.TextContent);

        var requestBtn = cut.Find("[data-testid='request-permission-btn']");
        Assert.NotNull(requestBtn);
        Assert.Contains("Request Notification Permission", requestBtn.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='send-test-notification-btn']"));
    }

    [Fact]
    public void RendersPermissionState_Granted_ShowsTestButton()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Granted);

        var cut = Render<WebNotificationsSettingsTab>();

        var badge = cut.Find("[data-testid='notification-permission-badge']");
        Assert.Contains("Notifications Allowed", badge.TextContent);

        var testBtn = cut.Find("[data-testid='send-test-notification-btn']");
        Assert.NotNull(testBtn);
        Assert.Contains("Send Test Notification", testBtn.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='request-permission-btn']"));
    }

    [Fact]
    public void RendersPermissionState_Denied_ShowsAdvisory()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Denied);

        var cut = Render<WebNotificationsSettingsTab>();

        var badge = cut.Find("[data-testid='notification-permission-badge']");
        Assert.Contains("Notifications Blocked", badge.TextContent);

        var advisory = cut.Find("[data-testid='permission-denied-advisory']");
        Assert.NotNull(advisory);
        Assert.Contains("Notifications Are Blocked", advisory.TextContent);
    }

    [Fact]
    public void RendersPermissionState_Unsupported_ShowsIosGuidance()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Unsupported);

        var cut = Render<WebNotificationsSettingsTab>();

        var badge = cut.Find("[data-testid='notification-permission-badge']");
        Assert.Contains("Push Notifications Unsupported", badge.TextContent);

        var guidance = cut.Find("[data-testid='ios-safari-guidance']");
        Assert.NotNull(guidance);
        Assert.Contains("Add to Home Screen", guidance.TextContent);
    }

    [Fact]
    public async Task ClickingRequestPermission_CallsService_AndUpdatesState()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Default);
        _serviceMock.Setup(s => s.RequestPermissionAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Granted);

        var cut = Render<WebNotificationsSettingsTab>();

        var btn = cut.Find("[data-testid='request-permission-btn']");
        await btn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _serviceMock.Verify(s => s.RequestPermissionAsync(), Times.Once);
        Assert.Single(_toastService.Toasts);
        Assert.Contains("Notifications Enabled", _toastService.Toasts[0].Title);

        // UI updates to Granted state with Send Test Notification button
        var testBtn = cut.Find("[data-testid='send-test-notification-btn']");
        Assert.NotNull(testBtn);
    }

    [Fact]
    public async Task ClickingSendTestNotification_CallsService()
    {
        _serviceMock.Setup(s => s.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Granted);

        var cut = Render<WebNotificationsSettingsTab>();

        var btn = cut.Find("[data-testid='send-test-notification-btn']");
        await btn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _serviceMock.Verify(s => s.SendTestNotificationAsync(), Times.Once);
        Assert.Single(_toastService.Toasts);
        Assert.Contains("Test Notification Dispatched", _toastService.Toasts[0].Title);
    }

    [Fact]
    public void TogglingMasterSwitch_UpdatesPreferenceAndPersists()
    {
        var cut = Render<WebNotificationsSettingsTab>();

        var toggle = cut.Find("[data-testid='toggle-master-enabled']");
        Assert.True(toggle.HasAttribute("checked") || (toggle as AngleSharp.Html.Dom.IHtmlInputElement)?.IsChecked == true);

        toggle.Change(false);

        _serviceMock.Verify(s => s.SavePreferencesAsync(It.Is<NotificationPreferences>(p => !p.MasterEnabled)), Times.Once);
    }

    [Fact]
    public void WhenMasterDisabled_SubjectSwitchesAreDisabled()
    {
        _preferences.MasterEnabled = false;

        var cut = Render<WebNotificationsSettingsTab>();

        var qToggle = cut.Find("[data-testid='toggle-question-inbox']");
        var rToggle = cut.Find("[data-testid='toggle-review-room']");
        var mToggle = cut.Find("[data-testid='toggle-task-merged']");

        Assert.True(qToggle.HasAttribute("disabled"));
        Assert.True(rToggle.HasAttribute("disabled"));
        Assert.True(mToggle.HasAttribute("disabled"));
    }

    [Fact]
    public void TogglingSubjectSwitch_UpdatesPreferenceAndPersists()
    {
        var cut = Render<WebNotificationsSettingsTab>();

        var qToggle = cut.Find("[data-testid='toggle-question-inbox']");
        qToggle.Change(false);

        _serviceMock.Verify(s => s.SavePreferencesAsync(It.Is<NotificationPreferences>(p => !p.NotifyOnQuestionInbox)), Times.Once);
    }
}

