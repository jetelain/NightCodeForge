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
    Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default);
    Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Legacy interface alias forwarding to <see cref="ILlmGatewayService"/>.
/// </summary>
public interface IOllamaRouterGatewayService : ILlmGatewayService
{
}

