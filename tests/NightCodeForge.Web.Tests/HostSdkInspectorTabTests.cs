using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class HostSdkInspectorTabTests : BunitContext
{
    private readonly Mock<IHostSdkDiscoveryService> _sdkDiscoveryMock = new();
    private readonly Mock<ICompanionToolInstaller> _companionInstallerMock = new();
    private readonly Mock<IWslMetadataService> _wslServiceMock = new();
    private readonly ToastService _toastService;

    public HostSdkInspectorTabTests()
    {
        _toastService = TestServices.CreateToastService();
        _wslServiceMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu-22.04");

        var knownTools = new List<CompanionToolSpec>
        {
            new("csnav", "Pmad.CSharpNav", new[] { "dotnet" }),
            new("mdfetch", "Pmad.MarkdownFetch", new[] { "universal" }),
            new("codecondenser", "Pmad.CodeCondenser", new[] { "dotnet" })
        };
        _companionInstallerMock.SetupGet(c => c.KnownTools).Returns(knownTools);

        var details = new HostSdkDiscoveryDetails(
            MountPaths: new List<string> { "/usr/bin/dotnet" },
            EnvironmentVariables: new Dictionary<string, string> { ["DOTNET_ROOT"] = "/usr/share/dotnet" },
            CommandPaths: new Dictionary<string, string>
            {
                ["dotnet"] = "/usr/bin/dotnet",
                ["csnav"] = "/home/user/.dotnet/tools/csnav"
                // mdfetch and codecondenser are missing
            });

        _sdkDiscoveryMock.Setup(s => s.DiscoverSdkDetailsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);

        Services.AddSingleton(_sdkDiscoveryMock.Object);
        Services.AddSingleton(_companionInstallerMock.Object);
        Services.AddSingleton(_wslServiceMock.Object);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void HostSdkInspectorTab_RendersCompanionToolsSection_AndStatuses()
    {
        var cut = Render<HostSdkInspectorTab>();

        Assert.Contains("Stack Companion Tools (csnav, mdfetch, codecondenser)", cut.Markup);

        // csnav is present in CommandPaths -> Installed
        Assert.Contains("csnav", cut.Markup);
        Assert.Contains("🟢 Installed", cut.Markup);
        Assert.Contains("/home/user/.dotnet/tools/csnav", cut.Markup);

        // mdfetch and codecondenser are missing -> Missing
        Assert.Contains("mdfetch", cut.Markup);
        Assert.Contains("codecondenser", cut.Markup);
        Assert.Contains("🟡 Missing", cut.Markup);
    }

    [Fact]
    public void HostSdkInspectorTab_ClickingAutoInstall_InvokesCompanionInstaller()
    {
        var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        _companionInstallerMock.Setup(c => c.EnsureToolsInstalledAsync("dotnet", It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cut = Render<HostSdkInspectorTab>();

        var installBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Install Missing Tools"));
        installBtn.Click();

        if (isWindows)
        {
            _companionInstallerMock.Verify(c => c.EnsureToolsInstalledAsync("dotnet", true, "Ubuntu-22.04", It.IsAny<CancellationToken>()), Times.Once);
            _companionInstallerMock.Verify(c => c.EnsureToolsInstalledAsync("dotnet", false, null, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            _companionInstallerMock.Verify(c => c.EnsureToolsInstalledAsync("dotnet", false, null, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
