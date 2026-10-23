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

    [Fact]
    public async Task MainLayout_ClickingSidebarToggle_TogglesCollapsedClassOnForgeLayout()
    {
        Services.AddSingleton<NightCodeForge.Core.Repositories.IWorkspaceRepository>(new NightCodeForge.Core.Repositories.InMemoryWorkspaceRepository());
        Services.AddSingleton(new WorkspaceStateService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(new Moq.Mock<IWorkspaceProvisioningService>().Object);
        Services.AddSingleton(Microsoft.Extensions.Logging.Abstractions.NullLogger<TopNavBar>.Instance);

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
}
