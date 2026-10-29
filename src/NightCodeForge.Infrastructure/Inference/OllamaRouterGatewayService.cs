using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Gateway to an OllamaRouter fleet. Everything is driven by the router's own <c>/api/router/*</c> API:
/// the fleet topology (any number of targets, including the Cloud pseudo-target), model context metadata
/// and fleet VRAM reclaim. No target, hardware or model is assumed client-side.
/// </summary>
public sealed class OllamaRouterGatewayService : OllamaGatewayServiceBase
{
    public OllamaRouterGatewayService(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        ILlmContextWindowCache contextWindowCache,
        ILogger<OllamaRouterGatewayService> logger)
        : base(httpClient, options, contextWindowCache, logger)
    {
    }

    public override LlmProviderType ProviderType => LlmProviderType.OllamaRouter;

    public override async Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var infoTask = TryGetJsonAsync<RouterInfoDto>(OllamaApiPaths.RouterInfo, StatusTimeout, cancellationToken);
            var fleetTask = TryGetJsonAsync<RouterFleetDto>(OllamaApiPaths.RouterFleet, StatusTimeout, cancellationToken);
            var info = await infoTask;
            var fleet = await fleetTask;

            if (info is not { IsRouter: true } || fleet is null)
            {
                Logger.LogWarning("No OllamaRouter answered on /api/router/info and /api/router/fleet at {BaseUrl}.", BaseUrl);
                return new GatewayStatusReport(false, ProviderType, BaseUrl, string.Empty, []);
            }

            var targets = (fleet.Targets ?? []).Where(t => t != null).Select(MapTarget).ToList();
            return new GatewayStatusReport(true, ProviderType, BaseUrl, info.Version ?? string.Empty, targets);
        }
        catch (Exception ex) when (IsRecoverableProbeFailure(ex, cancellationToken))
        {
            Logger.LogWarning(ex, "Failed to parse OllamaRouter status report from {BaseUrl}.", BaseUrl);
            return new GatewayStatusReport(false, ProviderType, BaseUrl, string.Empty, []);
        }
    }

    public override async Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(
                OllamaApiPaths.RouterVramReclaim,
                new RouterVramReclaimRequestDto { Target = string.IsNullOrWhiteSpace(targetName) ? null : targetName },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("OllamaRouter VRAM reclaim failed with HTTP {Status}.", (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<RouterVramReclaimResponseDto>(cancellationToken);
            Logger.LogInformation(
                "OllamaRouter VRAM reclaim (target: {Target}) unloaded {Total} model(s).",
                targetName ?? "all",
                result?.TotalUnloaded ?? 0);
            return result?.Success == true;
        }
        catch (Exception ex) when (IsRecoverableProbeFailure(ex, cancellationToken))
        {
            Logger.LogWarning(ex, "OllamaRouter VRAM reclaim request failed at {BaseAddress}.", Http.BaseAddress);
            return false;
        }
    }

    protected override async Task<int?> ProbeProviderContextWindowAsync(string modelName, CancellationToken cancellationToken)
    {
        var detail = await TryGetJsonAsync<RouterModelDto>(
            $"{OllamaApiPaths.RouterModels}/{Uri.EscapeDataString(modelName)}",
            ProbeTimeout,
            cancellationToken);

        return detail is null ? null : detail.EffectiveContextWindow;
    }

    private static FleetTargetStatus MapTarget(RouterTargetDto target)
    {
        var hw = target.Hardware;
        var exec = target.Execution;

        return new FleetTargetStatus(
            Name: target.Name ?? "unknown",
            Url: target.Url,
            Enabled: target.Enabled,
            Liveness: target.Liveness ?? FleetLiveness.Offline,
            LatencyMs: target.LatencyMs,
            Hardware: new FleetTargetHardware(
                HardwareHint: hw?.HardwareHint,
                GpuVendor: hw?.GpuVendor ?? FleetTargetHardware.UnknownGpuVendor,
                Devices: hw?.Devices ?? [],
                MaxVramMB: hw?.MaxVramMB,
                MaxParallel: hw?.MaxParallel,
                SpeedFactor: hw?.SpeedFactor),
            Execution: new FleetTargetExecution(
                InFlightRequests: exec?.InFlightRequests ?? 0,
                ActiveModel: exec?.ActiveModel,
                ContextAllocation: exec?.ContextAllocation));
    }

    private sealed class RouterInfoDto
    {
        [JsonPropertyName("isRouter")]
        public bool IsRouter { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }
    }

    private sealed class RouterFleetDto
    {
        [JsonPropertyName("targets")]
        public required List<RouterTargetDto> Targets { get; init; }
    }

    private sealed class RouterTargetDto
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("liveness")]
        public string? Liveness { get; init; }

        [JsonPropertyName("latencyMs")]
        public long? LatencyMs { get; init; }

        [JsonPropertyName("hardware")]
        public RouterHardwareDto? Hardware { get; init; }

        [JsonPropertyName("execution")]
        public RouterExecutionDto? Execution { get; init; }
    }

    private sealed class RouterHardwareDto
    {
        [JsonPropertyName("hardwareHint")]
        public string? HardwareHint { get; init; }

        [JsonPropertyName("gpuVendor")]
        public string? GpuVendor { get; init; }

        [JsonPropertyName("devices")]
        public IReadOnlyList<string>? Devices { get; init; }

        [JsonPropertyName("maxVramMB")]
        public int? MaxVramMB { get; init; }

        [JsonPropertyName("maxParallel")]
        public int? MaxParallel { get; init; }

        [JsonPropertyName("speedFactor")]
        public double? SpeedFactor { get; init; }
    }

    private sealed class RouterExecutionDto
    {
        [JsonPropertyName("inFlightRequests")]
        public int InFlightRequests { get; init; }

        [JsonPropertyName("activeModel")]
        public string? ActiveModel { get; init; }

        [JsonPropertyName("contextAllocation")]
        public string? ContextAllocation { get; init; }
    }

    private sealed class RouterModelDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("architecture")]
        public string? Architecture { get; init; }

        [JsonPropertyName("parameterCount")]
        public long? ParameterCount { get; init; }

        [JsonPropertyName("nativeContextWindow")]
        public int NativeContextWindow { get; init; }

        [JsonPropertyName("recommendedContextWindow")]
        public int RecommendedContextWindow { get; init; }

        [JsonPropertyName("cloudOverflowAvailable")]
        public bool CloudOverflowAvailable { get; init; }

        /// <summary>
        /// Sized to the fleet's recommended context window (server-side pre-calculated as max(Targets.contextCeiling)
        /// across capable nodes), falling back to the native context window or null.
        /// </summary>
        public int? EffectiveContextWindow =>
            RecommendedContextWindow > 0 ? RecommendedContextWindow
            : NativeContextWindow > 0 ? NativeContextWindow
            : null;
    }

    private sealed class RouterVramReclaimRequestDto
    {
        [JsonPropertyName("target")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; init; }
    }

    private sealed class RouterVramReclaimResponseDto
    {
        [JsonPropertyName("success")]
        public bool Success { get; init; }

        [JsonPropertyName("totalUnloaded")]
        public int TotalUnloaded { get; init; }
    }
}
