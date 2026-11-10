using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Model currently resident in Ollama / OllamaRouter GPU VRAM queried via GET /api/ps.
/// </summary>
public record ResidentModelInfo(
    string Name,
    string Model,
    long Size,
    long SizeVram,
    DateTimeOffset? ExpiresAt,
    string? Digest = null)
{
    public string SizeVramFormatted => SizeVram > 0 ? $"{SizeVram / (1024.0 * 1024 * 1024):F1} GB" : "0 GB";
    public string SizeTotalFormatted => Size > 0 ? $"{Size / (1024.0 * 1024 * 1024):F1} GB" : "0 GB";
    public string ExpiresAtFormatted => ExpiresAt.HasValue ? ExpiresAt.Value.LocalDateTime.ToString("HH:mm:ss") : "Indefinite";
    public double VramPercentage => Size > 0 ? Math.Clamp(Math.Round((double)SizeVram / Size * 100.0, 1), 0.0, 100.0) : (SizeVram > 0 ? 100.0 : 0.0);
    public string VramPercentageFormatted => $"{VramPercentage:F0}%";
}

/// <summary>
/// Liveness wire values reported by OllamaRouter for a fleet target (<c>GET /api/router/fleet</c>),
/// also produced by the standalone Ollama gateway for its single host.
/// </summary>
public static class FleetLiveness
{
    public const string Online = "Online";
    public const string Offline = "Offline";
    public const string Probing = "Probing";
}

public enum TargetBackendState
{
    /// <summary>Online and idle (ready to serve).</summary>
    Active,

    /// <summary>Being probed by the router; not yet confirmed online.</summary>
    Available,

    /// <summary>Online and currently executing at least one inference request.</summary>
    Busy,

    /// <summary>Administratively disabled in the router routing configuration.</summary>
    Disabled,

    /// <summary>Unreachable.</summary>
    Offline
}

public record FleetTargetHardware(
    string? HardwareHint,
    string GpuVendor,
    IReadOnlyList<string> Devices,
    int? MaxVramMB,
    int? MaxParallel,
    double? SpeedFactor = null)
{
    /// <summary>GPU vendor reported when the endpoint does not expose hardware information.</summary>
    public const string UnknownGpuVendor = "Unknown";
}

public record FleetTargetExecution(
    int InFlightRequests,
    string? ActiveModel,
    string? ContextAllocation);

public record FleetTargetStatus(
    string Name,
    string? Url,
    bool Enabled,
    string Liveness,
    long? LatencyMs,
    FleetTargetHardware Hardware,
    FleetTargetExecution Execution)
{
    public TargetBackendState State
    {
        get
        {
            if (!Enabled)
            {
                return TargetBackendState.Disabled;
            }

            if (string.Equals(Liveness, FleetLiveness.Probing, StringComparison.OrdinalIgnoreCase))
            {
                return TargetBackendState.Available;
            }

            if (!string.Equals(Liveness, FleetLiveness.Online, StringComparison.OrdinalIgnoreCase))
            {
                return TargetBackendState.Offline;
            }

            return Execution.InFlightRequests > 0 ? TargetBackendState.Busy : TargetBackendState.Active;
        }
    }
}

public record GatewayStatusReport(
    bool IsConnected,
    LlmProviderType ProviderType,
    string BaseUrl,
    string Version,
    IReadOnlyList<FleetTargetStatus> FleetTargets)
{
    public bool IsRouterProxy => IsConnected && ProviderType == LlmProviderType.OllamaRouter;
}

public record RouterSessionUsageTargetItem(
    string Target,
    int Requests,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    double ElapsedMs,
    double? CloudValue,
    double? ElectricityCost,
    double? Savings);

public record RouterSessionUsage(
    string SessionId,
    int Requests,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    double ElapsedMs,
    DateTimeOffset? LastSeenAt,
    double? CloudValue,
    double? ElectricityCost,
    double? Savings,
    double? ActualCloudCost,
    double? Cost,
    string? Currency,
    IReadOnlyList<RouterSessionUsageTargetItem>? PerTarget);

public record RouterSessionAffinityReleaseResult(
    string SessionId,
    bool Released);

public record SessionUsageDelta(
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    double? Cost,
    double? ActualCloudCost,
    double? ElectricityCost,
    double? CloudSavings,
    string? Currency)
{
    public static SessionUsageDelta? Compute(RouterSessionUsage? finalUsage, RouterSessionUsage? baselineUsage)
    {
        if (finalUsage == null)
        {
            return null;
        }

        var baseline = baselineUsage;
        var inputTokens = Math.Max(0, finalUsage.InputTokens - (baseline?.InputTokens ?? 0));
        var outputTokens = Math.Max(0, finalUsage.OutputTokens - (baseline?.OutputTokens ?? 0));
        var cachedInputTokens = Math.Max(0, finalUsage.CachedInputTokens - (baseline?.CachedInputTokens ?? 0));

        if (!finalUsage.Cost.HasValue)
        {
            return new SessionUsageDelta(
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CachedInputTokens: cachedInputTokens,
                Cost: null,
                ActualCloudCost: null,
                ElectricityCost: null,
                CloudSavings: null,
                Currency: null);
        }

        var cost = Math.Max(0.0, finalUsage.Cost.Value - (baseline?.Cost ?? 0.0));
        var actualCloudCost = Math.Max(0.0, (finalUsage.ActualCloudCost ?? 0.0) - (baseline?.ActualCloudCost ?? 0.0));
        var electricityCost = Math.Max(0.0, (finalUsage.ElectricityCost ?? 0.0) - (baseline?.ElectricityCost ?? 0.0));
        var cloudSavings = Math.Max(0.0, (finalUsage.Savings ?? 0.0) - (baseline?.Savings ?? 0.0));
        var currency = finalUsage.Currency ?? baseline?.Currency;

        return new SessionUsageDelta(
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CachedInputTokens: cachedInputTokens,
            Cost: cost,
            ActualCloudCost: actualCloudCost,
            ElectricityCost: electricityCost,
            CloudSavings: cloudSavings,
            Currency: currency);
    }

    public void ApplyTo(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        conversation.InputTokens = (conversation.InputTokens ?? 0) + InputTokens;
        conversation.OutputTokens = (conversation.OutputTokens ?? 0) + OutputTokens;
        if (CachedInputTokens > 0 || conversation.CachedInputTokens != null)
        {
            conversation.CachedInputTokens = (conversation.CachedInputTokens ?? 0) + CachedInputTokens;
        }

        if (Cost.HasValue)
        {
            conversation.Cost = (conversation.Cost ?? 0.0) + Cost.Value;
        }
        if (ActualCloudCost.HasValue)
        {
            conversation.ActualCloudCost = (conversation.ActualCloudCost ?? 0.0) + ActualCloudCost.Value;
        }
        if (ElectricityCost.HasValue)
        {
            conversation.ElectricityCost = (conversation.ElectricityCost ?? 0.0) + ElectricityCost.Value;
        }
        if (CloudSavings.HasValue)
        {
            conversation.CloudSavings = (conversation.CloudSavings ?? 0.0) + CloudSavings.Value;
        }
        if (!string.IsNullOrWhiteSpace(Currency))
        {
            conversation.Currency = Currency;
        }
    }
}

/// <summary>
/// Gateway to the configured LLM inference backend (Ollama, OllamaRouter, OpenAI-Compatible).
/// The implementation is selected once at startup; consumers never branch on the provider.
/// </summary>
public interface ILlmGatewayService
{
    /// <summary>The provider this implementation talks to (fixed at startup).</summary>
    LlmProviderType ProviderType { get; }

    /// <summary>
    /// Probes the configured endpoint to discover which provider actually answers there
    /// (diagnostic only: it does not switch the active implementation).
    /// </summary>
    Task<LlmProviderType?> DetectProviderAsync(CancellationToken cancellationToken = default);

    Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default);
    Task<int> ResolveContextWindowAsync(string modelName, CancellationToken cancellationToken = default);
    Task<ModelThinkingCapability?> ResolveThinkingCapabilityAsync(string modelName, CancellationToken cancellationToken = default);
    Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default);
    Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default);
    Task<RouterSessionUsage?> GetSessionUsageAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<bool> ReleaseSessionAffinityAsync(string sessionId, CancellationToken cancellationToken = default);
}
