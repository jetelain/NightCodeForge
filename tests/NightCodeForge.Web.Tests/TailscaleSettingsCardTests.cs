using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using NightCodeForge.Web.Tailscale;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TailscaleSettingsCardTests : BunitContext
{
    private readonly Mock<ITailscaleServeService> _serveServiceMock = new();
    private readonly Mock<ITailscaleConfigurationWriter> _configWriterMock = new();
    private readonly Mock<IOptionsMonitor<TailscaleOptions>> _optionsMonitorMock = new();
    private readonly ToastService _toastService;
    private TailscaleOptions _options = new();

    public TailscaleSettingsCardTests()
    {
        _toastService = TestServices.CreateToastService();

        _optionsMonitorMock.SetupGet(m => m.CurrentValue).Returns(() => _options);

        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.Inactive,
                "tailscale 1.76.0",
                "mybox.tail1234.ts.net",
                "tail1234.ts.net",
                "100.101.102.103",
                "127.0.0.1:5270",
                null));

        Services.AddSingleton<ITailscaleServeService>(_serveServiceMock.Object);
        Services.AddSingleton<ITailscaleConfigurationWriter>(_configWriterMock.Object);
        Services.AddSingleton<IOptionsMonitor<TailscaleOptions>>(_optionsMonitorMock.Object);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void RendersInactiveStatus_WhenDaemonInactive()
    {
        var cut = Render<TailscaleSettingsCard>();

        var statusPill = cut.Find("[data-testid='tailscale-status-inactive']");
        Assert.Contains("Inactive", statusPill.TextContent);

        Assert.Equal("tailscale 1.76.0", cut.Find("[data-testid='telemetry-cli-version']").TextContent.Trim());
        Assert.Equal("tail1234.ts.net", cut.Find("[data-testid='telemetry-tailnet-domain']").TextContent.Trim());
        Assert.Equal("100.101.102.103", cut.Find("[data-testid='telemetry-wireguard-ip']").TextContent.Trim());
        Assert.Equal("127.0.0.1:5270", cut.Find("[data-testid='telemetry-loopback-target']").TextContent.Trim());

        Assert.Contains("Remote URL and onboarding QR code will appear here", cut.Markup);
    }

    [Fact]
    public void RendersActiveStatusAndQrCode_WhenActiveSnapshotProvided()
    {
        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.Active,
                "tailscale 1.76.0",
                "mybox.tail1234.ts.net",
                "tail1234.ts.net",
                "100.101.102.103",
                "127.0.0.1:5270",
                "https://ncf.tail1234.ts.net"));

        var cut = Render<TailscaleSettingsCard>();

        var activePill = cut.Find("[data-testid='tailscale-status-active']");
        Assert.Contains("Active", activePill.TextContent);

        var urlLink = cut.Find("[data-testid='tailscale-magicdns-url']");
        Assert.Equal("https://ncf.tail1234.ts.net", urlLink.TextContent.Trim());

        var qrContainer = cut.Find("[data-testid='tailscale-qr-code']");
        Assert.Contains("<svg", qrContainer.InnerHtml);
    }

    [Fact]
    public void RendersApplyingStatus_WhenDaemonApplying()
    {
        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.Applying,
                null,
                null,
                null,
                null,
                "127.0.0.1:5270",
                null));

        var cut = Render<TailscaleSettingsCard>();

        var applyingPill = cut.Find("[data-testid='tailscale-status-applying']");
        Assert.Contains("Draining / Applying", applyingPill.TextContent);
    }

    [Fact]
    public void RendersOfflineStatus_WhenDaemonOfflineOrUnauthenticated()
    {
        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.DaemonOfflineOrUnauthenticated,
                null,
                null,
                null,
                null,
                "127.0.0.1:5270",
                null,
                "Tailscale CLI not found"));

        var cut = Render<TailscaleSettingsCard>();

        var offlinePill = cut.Find("[data-testid='tailscale-status-offline']");
        Assert.Contains("Daemon Offline", offlinePill.TextContent);

        var errorAlert = cut.Find("[data-testid='tailscale-daemon-error-alert']");
        Assert.Contains("Tailscale CLI not found", errorAlert.TextContent);
    }

    [Fact]
    public async Task ClearingServiceNameInput_InServiceMode_KeepsInputMounted_AndShowsValidationErrorOnSave()
    {
        _options = new TailscaleOptions { ServiceName = "svc:ncf", ListenPort = 443 };
        var cut = Render<TailscaleSettingsCard>();

        var serviceNameInput = cut.Find("[data-testid='tailscale-service-name-input']");
        serviceNameInput.Input("");

        // Input must stay mounted in Service Mode even when cleared
        Assert.NotNull(cut.Find("[data-testid='tailscale-service-name-input']"));
        Assert.NotNull(cut.Find("[data-testid='tailscale-advertise-toggle']"));

        // Clicking Save & Apply with an empty service name in Service Mode shows a validation error
        var saveBtn = cut.Find("[data-testid='tailscale-save-btn']");
        await cut.InvokeAsync(() => saveBtn.Click());

        var errorAlert = cut.Find("[data-testid='tailscale-error-alert']");
        Assert.Contains("Service name is required", errorAlert.TextContent);
        _configWriterMock.Verify(
            w => w.SaveTailscaleOptionsAsync(It.IsAny<TailscaleOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidListenPort_ShowsValidationError_WithoutSaving()
    {
        _options = new TailscaleOptions { ServiceName = "svc:ncf", ListenPort = 443 };
        var cut = Render<TailscaleSettingsCard>();

        var portInput = cut.Find("[data-testid='tailscale-listen-port-input']");
        portInput.Change(0);

        var saveBtn = cut.Find("[data-testid='tailscale-save-btn']");
        await cut.InvokeAsync(() => saveBtn.Click());

        var errorAlert = cut.Find("[data-testid='tailscale-error-alert']");
        Assert.Contains("Listen port must be between 1 and 65535", errorAlert.TextContent);
        _configWriterMock.Verify(
            w => w.SaveTailscaleOptionsAsync(It.IsAny<TailscaleOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SaveAndApply_WhenDaemonOfflineAfterApply_ShowsWarningToast()
    {
        _options = new TailscaleOptions { AutoServe = false, ServiceName = "svc:ncf", ListenPort = 443 };
        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.DaemonOfflineOrUnauthenticated,
                null,
                null,
                null,
                null,
                "127.0.0.1:5270",
                null,
                "Tailscale is not connected to a tailnet (run 'tailscale up')."));

        var cut = Render<TailscaleSettingsCard>();

        cut.Find("[data-testid='tailscale-autoserve-toggle']").Change(true);
        var saveBtn = cut.Find("[data-testid='tailscale-save-btn']");
        await cut.InvokeAsync(() => saveBtn.Click());

        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning && t.Title.Contains("Daemon Offline"));
    }

    [Fact]
    public void OnInitialized_PrefersServeServiceEffectiveOptions_OverStaleOptionsMonitor()
    {
        _options = new TailscaleOptions { AutoServe = false, ServiceName = "svc:ncf", ListenPort = 443 };
        _serveServiceMock.Setup(s => s.GetCurrentOptions())
            .Returns(new TailscaleOptions { AutoServe = true, ServiceName = "svc:custom", ListenPort = 8443 });

        var cut = Render<TailscaleSettingsCard>();

        Assert.Equal("svc:custom", cut.Find("[data-testid='tailscale-service-name-input']").GetAttribute("value"));
        Assert.Equal("8443", cut.Find("[data-testid='tailscale-listen-port-input']").GetAttribute("value"));
    }

    [Fact]
    public void RendersFirstTimeSetupNotice_WithIncomingTrafficAndAdminConsoleServiceLinks()
    {
        var cut = Render<TailscaleSettingsCard>();

        var notice = cut.Find("[data-testid='tailscale-setup-notice']");
        Assert.Contains("Allow incoming connections", notice.TextContent);
        Assert.Contains("Tailscale Tray Icon", notice.TextContent);

        var adminLink = cut.Find("[data-testid='tailscale-admin-services-link']");
        Assert.Equal(TailscaleOptions.AdminConsoleServicesUrl, adminLink.GetAttribute("href"));

        var docsLink = cut.Find("[data-testid='tailscale-services-docs-link']");
        Assert.Equal(TailscaleOptions.TailscaleServicesDocsUrl, docsLink.GetAttribute("href"));
    }

    [Fact]
    public void ModeSwitching_TogglesBetweenServiceAndDeviceMode()
    {
        _options = new TailscaleOptions { ServiceName = "svc:ncf" };
        var cut = Render<TailscaleSettingsCard>();

        // Initially in Service Mode
        Assert.NotNull(cut.Find("[data-testid='tailscale-service-name-input']"));
        Assert.NotNull(cut.Find("[data-testid='tailscale-advertise-toggle']"));

        // Switch to Device Node Mode
        var deviceCard = cut.Find("[data-testid='tailscale-mode-device']");
        deviceCard.Click();

        // Service name input and advertise toggle should be hidden
        Assert.Empty(cut.FindAll("[data-testid='tailscale-service-name-input']"));
        Assert.Empty(cut.FindAll("[data-testid='tailscale-advertise-toggle']"));

        // Switch back to Service Mode
        var serviceCard = cut.Find("[data-testid='tailscale-mode-service']");
        serviceCard.Click();

        Assert.NotNull(cut.Find("[data-testid='tailscale-service-name-input']"));
        Assert.NotNull(cut.Find("[data-testid='tailscale-advertise-toggle']"));
    }

    [Fact]
    public async Task SaveAndApply_CallsConfigurationWriterAndServeService()
    {
        _options = new TailscaleOptions
        {
            AutoServe = false,
            ServiceName = "svc:ncf",
            ListenPort = 443,
            Advertise = true
        };

        var cut = Render<TailscaleSettingsCard>();

        // Toggle AutoServe on
        var autoServeToggle = cut.Find("[data-testid='tailscale-autoserve-toggle']");
        autoServeToggle.Change(true);

        // Click Save & Apply
        var saveBtn = cut.Find("[data-testid='tailscale-save-btn']");
        await cut.InvokeAsync(() => saveBtn.Click());

        _configWriterMock.Verify(
            w => w.SaveTailscaleOptionsAsync(
                It.Is<TailscaleOptions>(o => o.AutoServe && o.ServiceName == "svc:ncf" && o.ListenPort == 443),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _serveServiceMock.Verify(
            s => s.ApplyConfigurationAsync(
                It.Is<TailscaleOptions>(o => o.AutoServe && o.ServiceName == "svc:ncf" && o.ListenPort == 443),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains(_toastService.Toasts, t => t.Title.Contains("Tailscale Configuration Applied"));
    }

    [Fact]
    public async Task CopyMagicDnsUrl_InvokesClipboard()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupVoid("navigator.clipboard.writeText", "https://ncf.tail1234.ts.net").SetVoidResult();

        _serveServiceMock.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.Active,
                "tailscale 1.76.0",
                "mybox.tail1234.ts.net",
                "tail1234.ts.net",
                "100.101.102.103",
                "127.0.0.1:5270",
                "https://ncf.tail1234.ts.net"));

        var cut = Render<TailscaleSettingsCard>();

        var copyBtn = cut.Find("[data-testid='tailscale-copy-url-btn']");
        await cut.InvokeAsync(() => copyBtn.Click());

        JSInterop.VerifyInvoke("navigator.clipboard.writeText", 1);
        Assert.Contains(_toastService.Toasts, t => t.Title.Contains("Copied to Clipboard"));
    }
}
