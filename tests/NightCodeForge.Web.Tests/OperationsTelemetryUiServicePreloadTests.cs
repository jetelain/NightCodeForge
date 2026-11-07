using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class OperationsTelemetryUiServicePreloadTests : BunitContext
{
    [Fact]
    public void OperationsTelemetryUiService_FormattingAndBadges_ProduceExpectedOutputs()
    {
        // 1. Null
        Assert.Equal("badge-gray", OperationsTelemetryUiService.GetPreloadBadgeClass(null));
        Assert.Equal("Idle", OperationsTelemetryUiService.GetPreloadStatusText(null));
        Assert.Equal(string.Empty, OperationsTelemetryUiService.FormatPreloadBadgeLabel(null));
        Assert.Equal("No active preload", OperationsTelemetryUiService.FormatPreloadSummary(null));
        Assert.False(OperationsTelemetryUiService.IsPreloadActive(null));

        // 2. Active Warmup
        var activeRecord = new ModelPreloadTelemetryRecord(
            Model: "qwen2.5-coder:32b",
            SelectedTarget: "Server-Rack-1",
            Success: false,
            NumCtx: 65536,
            KeepAliveSeconds: -1,
            DurationMs: null,
            Reason: "Warmup initiated",
            Timestamp: DateTime.UtcNow,
            IsActive: true);

        Assert.Equal("badge-amber", OperationsTelemetryUiService.GetPreloadBadgeClass(activeRecord));
        Assert.Equal("⚡ JIT Warmup", OperationsTelemetryUiService.GetPreloadStatusText(activeRecord));
        Assert.Equal("⚡ JIT Warmup: qwen2.5-coder:32b (Server-Rack-1)", OperationsTelemetryUiService.FormatPreloadBadgeLabel(activeRecord));
        Assert.Equal("Warming up 'qwen2.5-coder:32b' → Server-Rack-1...", OperationsTelemetryUiService.FormatPreloadSummary(activeRecord));
        Assert.True(OperationsTelemetryUiService.IsPreloadActive(activeRecord));

        // 3. Resident / Success
        var residentRecord = new ModelPreloadTelemetryRecord(
            Model: "qwen2.5-coder:32b",
            SelectedTarget: "Server-Rack-1",
            Success: true,
            NumCtx: 65536,
            KeepAliveSeconds: -1,
            DurationMs: 1420,
            Reason: "Preload complete",
            Timestamp: DateTime.UtcNow,
            IsActive: false);

        Assert.Equal("badge-emerald", OperationsTelemetryUiService.GetPreloadBadgeClass(residentRecord));
        Assert.Equal("● Resident", OperationsTelemetryUiService.GetPreloadStatusText(residentRecord));
        Assert.Equal("● Resident: qwen2.5-coder:32b (Server-Rack-1)", OperationsTelemetryUiService.FormatPreloadBadgeLabel(residentRecord));
        Assert.Equal("'qwen2.5-coder:32b' on Server-Rack-1 (65,536 ctx) in 1,420ms", OperationsTelemetryUiService.FormatPreloadSummary(residentRecord));
        Assert.False(OperationsTelemetryUiService.IsPreloadActive(residentRecord));

        // 4. Failed / Warning
        var failedRecord = new ModelPreloadTelemetryRecord(
            Model: "deepseek-r1:70b",
            SelectedTarget: null,
            Success: false,
            NumCtx: null,
            KeepAliveSeconds: null,
            DurationMs: null,
            Reason: "No eligible node in fleet",
            Timestamp: DateTime.UtcNow,
            IsActive: false);

        Assert.Equal("badge-crimson", OperationsTelemetryUiService.GetPreloadBadgeClass(failedRecord));
        Assert.Equal("⚠️ Warmup Issue", OperationsTelemetryUiService.GetPreloadStatusText(failedRecord));
        Assert.Equal("⚠️ Warmup Issue: deepseek-r1:70b", OperationsTelemetryUiService.FormatPreloadBadgeLabel(failedRecord));
        Assert.Equal("No eligible node in fleet", OperationsTelemetryUiService.FormatPreloadSummary(failedRecord));
        Assert.False(OperationsTelemetryUiService.IsPreloadActive(failedRecord));
    }

    [Fact]
    public void ActiveOperationsHero_WhenPreloadResident_RendersJitWarmupBadge()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var opsControlMock = new Mock<IOperationsControlService>();
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        opsControlMock.Setup(o => o.IsPaused).Returns(false);
        opsControlMock.Setup(o => o.GetDeferredTaskCountAsync(default)).ReturnsAsync(0);

        var residentRecord = new ModelPreloadTelemetryRecord(
            Model: "qwen2.5-coder:32b",
            SelectedTarget: "Server-Rack-1",
            Success: true,
            NumCtx: 65536,
            KeepAliveSeconds: -1,
            DurationMs: 1420,
            Reason: "Preload complete",
            Timestamp: DateTime.UtcNow,
            IsActive: false);

        telemetryMock.Setup(t => t.GetLatestModelPreload()).Returns(residentRecord);
        telemetryMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(Array.Empty<LlmCallRecord>());
        telemetryMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(Array.Empty<ShellCommandRecord>());

        var workerState = new WorkerStateService(telemetryMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var uiService = new OperationsTelemetryUiService(hubConnection, telemetryMock.Object);

        Services.AddSingleton(workerState);
        Services.AddSingleton(uiService);
        Services.AddSingleton(opsControlMock.Object);
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<ActiveOperationsHero>();

        var badge = cut.Find("#badge-jit-warmup");
        Assert.NotNull(badge);
        Assert.Contains("● Resident", badge.TextContent);
        Assert.Contains("qwen2.5-coder:32b", badge.TextContent);
        Assert.Contains("Server-Rack-1", badge.TextContent);
    }

    [Fact]
    public void ActiveOperationsHero_And_InferenceInspectionModal_RenderRetryAttemptBadgeAndReason()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var opsControlMock = new Mock<IOperationsControlService>();
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        opsControlMock.Setup(o => o.IsPaused).Returns(false);
        opsControlMock.Setup(o => o.GetDeferredTaskCountAsync(default)).ReturnsAsync(0);

        var retryingCall = new LlmCallRecord(
            CallId: "call-retry-1",
            Model: "Qwen3.8-27B:latest",
            TargetBackend: "Local",
            StartTime: DateTime.UtcNow.AddSeconds(-12),
            DurationMs: 12000,
            PromptTokens: 48500,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: "[Tool Result] Passed! 42 tests",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            RetryAttempt: 2,
            MaxRetries: 3,
            LastRetryReason: "The request timed out after 600 seconds.");

        Assert.True(OperationsTelemetryUiService.HasRetryAttempt(retryingCall));
        Assert.Equal("Retry 2/3", OperationsTelemetryUiService.FormatRetryBadgeLabel(retryingCall));
        Assert.Equal("Retry 2/3 — The request timed out after 600 seconds.", OperationsTelemetryUiService.FormatRetryNotice(retryingCall));

        telemetryMock.Setup(t => t.GetActiveLlmCall()).Returns(retryingCall);
        telemetryMock.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new[] { retryingCall });
        telemetryMock.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(Array.Empty<ShellCommandRecord>());

        var workerState = new WorkerStateService(telemetryMock.Object);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var uiService = new OperationsTelemetryUiService(hubConnection, telemetryMock.Object);

        Services.AddSingleton(workerState);
        Services.AddSingleton(uiService);
        Services.AddSingleton(opsControlMock.Object);
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var heroCut = Render<ActiveOperationsHero>();
        var retryBadge = heroCut.Find("#badge-llm-retry-attempt");
        Assert.Contains("Retry 2/3", retryBadge.TextContent);

        var retryReason = heroCut.Find("#active-llm-retry-reason");
        Assert.Contains("The request timed out after 600 seconds.", retryReason.TextContent);

        var modalCut = Render<InferenceInspectionModal>(parameters => parameters.Add(p => p.Call, retryingCall));
        Assert.Contains("Retry 2/3", modalCut.Markup);
        Assert.Contains("The request timed out after 600 seconds.", modalCut.Markup);
    }
}
