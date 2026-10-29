using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Gateway to a standalone Ollama daemon (single host, no router): the fleet is the single configured
/// host, and VRAM is reclaimed by unloading the resident models (<c>keep_alive: 0</c>).
/// </summary>
public sealed class OllamaGatewayService : OllamaGatewayServiceBase
{
    private const int UnloadKeepAlive = 0;

    public OllamaGatewayService(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        ILlmContextWindowCache contextWindowCache,
        ILogger<OllamaGatewayService> logger)
        : base(httpClient, options, contextWindowCache, logger)
    {
    }

    public override LlmProviderType ProviderType => LlmProviderType.Ollama;

    public override async Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var version = await TryGetOllamaVersionAsync(cancellationToken);
        stopwatch.Stop();

        if (version is null)
        {
            return new GatewayStatusReport(false, ProviderType, BaseUrl, string.Empty, [BuildHostTarget(online: false, latencyMs: null, activeModel: null)]);
        }

        var resident = await GetResidentModelsAsync(cancellationToken);
        var target = BuildHostTarget(online: true, stopwatch.ElapsedMilliseconds, resident.FirstOrDefault()?.Name);
        return new GatewayStatusReport(true, ProviderType, BaseUrl, version, [target]);
    }

    /// <summary>The daemon is the only target: <paramref name="targetName"/> is irrelevant and all resident models are unloaded.</summary>
    public override async Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default)
    {
        var version = await TryGetOllamaVersionAsync(cancellationToken);
        if (version is null)
        {
            Logger.LogWarning("Cannot reclaim VRAM: Ollama daemon is unreachable at {BaseAddress}.", Http.BaseAddress);
            return false;
        }

        var resident = await GetResidentModelsAsync(cancellationToken);
        var allUnloaded = true;

        foreach (var model in resident)
        {
            try
            {
                using var response = await Http.PostAsJsonAsync(
                    OllamaApiPaths.Generate,
                    new { model = model.Name, keep_alive = UnloadKeepAlive },
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning("Failed to unload '{Model}' (HTTP {Status}).", model.Name, (int)response.StatusCode);
                    allUnloaded = false;
                }
            }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "Failed to unload '{Model}' from {BaseAddress}.", model.Name, Http.BaseAddress);
                allUnloaded = false;
            }
        }

        return allUnloaded;
    }

    private FleetTargetStatus BuildHostTarget(bool online, long? latencyMs, string? activeModel) => new(
        Name: new Uri(BaseUrl).Authority,
        Url: BaseUrl,
        Enabled: true,
        Liveness: online ? FleetLiveness.Online : FleetLiveness.Offline,
        LatencyMs: online ? latencyMs : null,
        Hardware: new FleetTargetHardware(
            HardwareHint: null,
            GpuVendor: FleetTargetHardware.UnknownGpuVendor,
            Devices: [],
            MaxVramMB: null,
            MaxParallel: null),
        Execution: new FleetTargetExecution(
            InFlightRequests: 0,
            ActiveModel: activeModel,
            ContextAllocation: null));
}
