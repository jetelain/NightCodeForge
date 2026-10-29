using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Layout;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TopNavBarTests : BunitContext
{
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly WorkspaceStateService _workspaceState = new();
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Mock<IOperationsControlService> _operationsControlMock = new();
    private readonly Mock<IOllamaRouterGatewayService> _gatewayServiceMock = new();

    public TopNavBarTests()
    {
        _gatewayServiceMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepo);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(new WorkerStateService(_telemetryMock.Object));
        Services.AddSingleton(_operationsControlMock.Object);
        Services.AddSingleton(_gatewayServiceMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<TopNavBar>.Instance);
        _operationsControlMock.Setup(o => o.GetDeferredTaskCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
    }

    [Fact]
    public void TopNavBar_WhenOperationsRunning_RendersPauseButton()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);

        var cut = Render<TopNavBar>();

        var pauseBtn = cut.Find("#nav-btn-pause-operations");
        Assert.NotNull(pauseBtn);
        Assert.Contains("Pause Operations", pauseBtn.TextContent);
        Assert.Contains("forge-btn-secondary", pauseBtn.ClassName);
        Assert.DoesNotContain("btn-pulse-amber", pauseBtn.ClassName);

        Assert.Empty(cut.FindAll("#nav-btn-resume-operations"));
    }

    [Fact]
    public async Task TopNavBar_WhenRunningWithDeferredTasks_RendersRetryDeferredButton()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);
        _operationsControlMock.Setup(o => o.GetDeferredTaskCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        _operationsControlMock.Setup(o => o.ResumeOperationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var cut = Render<TopNavBar>();

        var retryBtn = cut.Find("#nav-btn-retry-deferred");
        Assert.NotNull(retryBtn);
        Assert.Contains("Retry Deferred (3)", retryBtn.TextContent);
        Assert.Contains("forge-btn-warning", retryBtn.ClassName);

        // Clicking retry invokes ResumeOperationsAsync
        await retryBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        _operationsControlMock.Verify(o => o.ResumeOperationsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TopNavBar_WhenOperationsPaused_RendersResumeButtonWithAmberPulse()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        _operationsControlMock.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Evicted);

        var cut = Render<TopNavBar>();

        var resumeBtn = cut.Find("#nav-btn-resume-operations");
        Assert.NotNull(resumeBtn);
        Assert.Contains("Resume Operations", resumeBtn.TextContent);
        Assert.Contains("forge-btn-warning", resumeBtn.ClassName);
        Assert.Contains("btn-pulse-amber", resumeBtn.ClassName);

        Assert.Empty(cut.FindAll("#nav-btn-pause-operations"));
    }

    [Fact]
    public void TopNavBar_WhenReclaimFails_RendersGpuReclaimIncompleteBadge()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        _operationsControlMock.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Failed);
        _operationsControlMock.Setup(o => o.LastReclaimError).Returns("Connection refused to OllamaRouter");

        var cut = Render<TopNavBar>();

        var badge = cut.Find("span.badge.bg-danger-subtle");
        Assert.NotNull(badge);
        Assert.Contains("GPU Reclaim Incomplete", badge.TextContent);
        Assert.Equal("Connection refused to OllamaRouter", badge.GetAttribute("title"));
    }

    [Fact]
    public void TopNavBar_WhenProviderIsOpenAiCompatible_SuppressesGpuReclaimIncompleteBadge()
    {
        _gatewayServiceMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OpenAiCompatible);
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        _operationsControlMock.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Failed);
        _operationsControlMock.Setup(o => o.LastReclaimError).Returns("VRAM reclaim failed");

        var cut = Render<TopNavBar>();

        Assert.Empty(cut.FindAll("span.badge.bg-danger-subtle"));
        Assert.DoesNotContain("GPU Reclaim Incomplete", cut.Markup);
    }

    [Fact]
    public void TopNavBar_WhenProviderIsOpenAiCompatible_UsesCloudFriendlyPauseTooltips()
    {
        _gatewayServiceMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OpenAiCompatible);
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);

        var cut = Render<TopNavBar>();

        var pauseBtn = cut.Find("#nav-btn-pause-operations");
        Assert.Equal("Pause background agent execution & operations", pauseBtn.GetAttribute("title"));

        // Now test paused state tooltip
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        var cutPaused = Render<TopNavBar>();

        var resumeBtn = cutPaused.Find("#nav-btn-resume-operations");
        Assert.Equal("Operations paused. Click to resume operations.", resumeBtn.GetAttribute("title"));
    }

    [Fact]
    public async Task TopNavBar_ClickingPause_CallsPauseOperationsAsync()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);
        _operationsControlMock.Setup(o => o.PauseOperationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = Render<TopNavBar>();

        var pauseBtn = cut.Find("#nav-btn-pause-operations");
        await pauseBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _operationsControlMock.Verify(o => o.PauseOperationsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TopNavBar_ClickingResume_CallsResumeOperationsAsync()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        _operationsControlMock.Setup(o => o.ResumeOperationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = Render<TopNavBar>();

        var resumeBtn = cut.Find("#nav-btn-resume-operations");
        await resumeBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _operationsControlMock.Verify(o => o.ResumeOperationsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TopNavBar_SubscribesAndUnsubscribes_OnOperationsStateChanged()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);

        var cut = Render<TopNavBar>();

        // Verify event subscription was made
        _operationsControlMock.VerifyAdd(o => o.OnOperationsStateChanged += It.IsAny<Action<bool>>(), Times.Once);

        // Dispose component
        cut.Instance.Dispose();

        // Verify event unsubscription was made
        _operationsControlMock.VerifyRemove(o => o.OnOperationsStateChanged -= It.IsAny<Action<bool>>(), Times.Once);
    }
}
