using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Web.Components.Layout;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class NavMenuShellTests : BunitContext
{
    public NavMenuShellTests()
    {
        Services.AddSingleton(new WorkerStateService(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object));
        Services.AddSingleton(new WorkspaceStateService());
    }

    [Fact]
    public void NavMenu_RendersSvgFavicon_InsteadOfEmoji()
    {
        var cut = Render<NavMenu>();

        var faviconImg = cut.Find("img.brand-favicon");
        Assert.NotNull(faviconImg);
        Assert.Equal("favicon.svg", faviconImg.GetAttribute("src"));
        Assert.Equal("NightCodeForge", faviconImg.GetAttribute("alt"));
        Assert.DoesNotContain("🌙", cut.Markup);
    }

    [Fact]
    public void NavMenu_WhenExpanded_RendersCollapseToggleState()
    {
        var cut = Render<NavMenu>(parameters => parameters
            .Add(p => p.IsCollapsed, false));

        var toggleBtn = cut.Find("button.sidebar-toggle-btn");
        Assert.NotNull(toggleBtn);
        Assert.Equal("Collapse Sidebar", toggleBtn.GetAttribute("title"));
        Assert.Equal("true", toggleBtn.GetAttribute("aria-expanded"));
        Assert.Contains("«", toggleBtn.TextContent);
    }

    [Fact]
    public void NavMenu_WhenCollapsed_RendersExpandToggleState()
    {
        var cut = Render<NavMenu>(parameters => parameters
            .Add(p => p.IsCollapsed, true));

        var toggleBtn = cut.Find("button.sidebar-toggle-btn");
        Assert.NotNull(toggleBtn);
        Assert.Equal("Expand Sidebar", toggleBtn.GetAttribute("title"));
        Assert.Equal("false", toggleBtn.GetAttribute("aria-expanded"));
        Assert.Contains("»", toggleBtn.TextContent);
    }

    [Fact]
    public async Task NavMenu_ClickingToggle_InvokesOnToggleSidebar()
    {
        bool toggled = false;
        var cut = Render<NavMenu>(parameters => parameters
            .Add(p => p.OnToggleSidebar, () => { toggled = true; }));

        var toggleBtn = cut.Find("button.sidebar-toggle-btn");
        await toggleBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(toggled);
    }

    private void RegisterMainLayoutServices()
    {
        Services.AddSingleton<NightCodeForge.Core.Repositories.IWorkspaceRepository>(new NightCodeForge.Core.Repositories.InMemoryWorkspaceRepository());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        var mockGateway = new Moq.Mock<NightCodeForge.Core.Interfaces.ILlmGatewayService>();
        mockGateway.Setup(g => g.ProviderType).Returns(NightCodeForge.Core.Domain.LlmProviderType.OllamaRouter);
        Services.AddSingleton<NightCodeForge.Core.Interfaces.ILlmGatewayService>(mockGateway.Object);
        Services.AddSingleton(new Moq.Mock<IWorkspaceProvisioningService>().Object);
        Services.AddSingleton(Microsoft.Extensions.Logging.Abstractions.NullLogger<TopNavBar>.Instance);
        JSInterop.SetupVoid(MainLayoutForegroundRegisterFunction, _ => true);
        JSInterop.SetupVoid(MainLayoutForegroundUnregisterFunction);
    }

    private const string MainLayoutForegroundRegisterFunction = "NightCodeForge.registerForegroundSync";
    private const string MainLayoutForegroundUnregisterFunction = "NightCodeForge.unregisterForegroundSync";

    [Fact]
    public async Task MainLayout_ClickingSidebarToggle_TogglesCollapsedClassOnForgeLayout()
    {
        RegisterMainLayoutServices();

        var cut = Render<MainLayout>();

        var layoutDiv = cut.Find("div.forge-layout");
        Assert.DoesNotContain("sidebar-collapsed", layoutDiv.ClassName);

        var toggleBtn = cut.Find("button.sidebar-toggle-btn");
        Assert.Equal("«", toggleBtn.TextContent.Trim());

        await toggleBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        layoutDiv = cut.Find("div.forge-layout");
        Assert.Contains("sidebar-collapsed", layoutDiv.ClassName);

        toggleBtn = cut.Find("button.sidebar-toggle-btn");
        Assert.Equal("»", toggleBtn.TextContent.Trim());
    }

    [Fact]
    public void NavMenu_WhenMobileOpen_RendersMobileOpenClass()
    {
        var closed = Render<NavMenu>();
        Assert.DoesNotContain("mobile-open", closed.Find("aside.forge-sidebar").ClassName);

        var open = Render<NavMenu>(parameters => parameters.Add(p => p.IsMobileOpen, true));
        Assert.Contains("mobile-open", open.Find("aside.forge-sidebar").ClassName);
    }

    [Fact]
    public async Task NavMenu_ClickingNavLink_InvokesOnNavigate()
    {
        var navigated = false;
        var cut = Render<NavMenu>(parameters => parameters
            .Add(p => p.OnNavigate, () => { navigated = true; }));

        await cut.Find("a.forge-nav-link").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(navigated);
    }

    [Fact]
    public async Task MainLayout_HamburgerToggle_OpensDrawerWithBackdrop_AndBackdropClickCloses()
    {
        RegisterMainLayoutServices();
        var cut = Render<MainLayout>();

        Assert.Empty(cut.FindAll("#mobile-nav-backdrop"));
        Assert.DoesNotContain("mobile-nav-open", cut.Find("div.forge-layout").ClassName);

        var hamburger = cut.Find("#mobile-nav-toggle");
        Assert.Equal("false", hamburger.GetAttribute("aria-expanded"));
        await hamburger.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("mobile-nav-open", cut.Find("div.forge-layout").ClassName);
        Assert.Contains("mobile-open", cut.Find("aside.forge-sidebar").ClassName);
        Assert.Equal("true", cut.Find("#mobile-nav-toggle").GetAttribute("aria-expanded"));

        await cut.Find("#mobile-nav-backdrop").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Empty(cut.FindAll("#mobile-nav-backdrop"));
        Assert.DoesNotContain("mobile-open", cut.Find("aside.forge-sidebar").ClassName);
    }

    [Fact]
    public async Task MainLayout_ClickingNavLinkInOpenDrawer_ClosesDrawer()
    {
        RegisterMainLayoutServices();
        var cut = Render<MainLayout>();

        await cut.Find("#mobile-nav-toggle").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Contains("mobile-open", cut.Find("aside.forge-sidebar").ClassName);

        await cut.Find("a.forge-nav-link").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.DoesNotContain("mobile-open", cut.Find("aside.forge-sidebar").ClassName);
        Assert.Empty(cut.FindAll("#mobile-nav-backdrop"));
    }

    [Fact]
    public void MainLayout_RendersReconnectBanner_AndRegistersForegroundSync()
    {
        RegisterMainLayoutServices();
        var cut = Render<MainLayout>();

        var banner = cut.Find("#components-reconnect-modal");
        Assert.Contains("Reconnecting to Forge...", banner.TextContent);
        JSInterop.VerifyInvoke(MainLayoutForegroundRegisterFunction);
    }

    [Fact]
    public void WorkerState_RefreshFromTelemetry_RaisesStateChanged()
    {
        var workerState = new WorkerStateService(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object);
        var raised = 0;
        workerState.OnWorkerStateChanged += () => raised++;

        workerState.RefreshFromTelemetry();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void WorkerState_RefreshFromTelemetry_ClearsStaleRunningTaskWhenTelemetryIsIdle()
    {
        var telemetryMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>();
        telemetryMock.Setup(t => t.GetActiveTask()).Returns((NightCodeForge.Core.Workflow.ActiveTaskSummary?)null);
        var workerState = new WorkerStateService(telemetryMock.Object);

        workerState.HandleWorkerStatus("Running", 42, "Implementation", "Local GPU", "Task 42");
        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);
        Assert.Equal(42, workerState.Current.ActiveTaskId);

        // When telemetry reports no active task (e.g. finished while mobile tab was backgrounded),
        // RefreshFromTelemetry / Current reconciles back to Idle.
        workerState.RefreshFromTelemetry();

        Assert.Equal(WorkerExecutionStatus.Idle, workerState.Current.Status);
        Assert.Null(workerState.Current.ActiveTaskId);
    }

    [Fact]
    public void TopNavBar_MobileWorkerPill_UsesWorkspaceScopedHref()
    {
        RegisterMainLayoutServices();
        var workspaceState = Services.GetRequiredService<WorkspaceStateService>();
        var ws = new NightCodeForge.Core.Domain.Workspace { Id = 7, Name = "Mobile WS", RepositoryPath = "/repos/mobile" };
        workspaceState.SetKnownWorkspaces(new[] { ws });
        workspaceState.SetActiveWorkspace(ws);

        var workerState = Services.GetRequiredService<WorkerStateService>();
        workerState.UpdateStatus(new WorkerStatusSnapshot(
            WorkerExecutionStatus.Running, 42, "Task 42", "Implementation", null, "Local GPU", 0));

        var cut = Render<TopNavBar>();

        var pill = cut.Find("#mobile-worker-pill");
        Assert.Equal("w/7/inspector/42", pill.GetAttribute("href"));
    }
}