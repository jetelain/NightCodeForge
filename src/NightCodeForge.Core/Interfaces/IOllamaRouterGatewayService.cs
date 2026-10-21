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

public enum TargetBackendState
{
    Active,
    Available,
    Busy,
    Disabled,
    Offline
}

public record TargetBackendStatus(
    string TargetName,
    TargetBackendState State,
    string Description,
    long? LatencyMs = null,
    string? CurrentModel = null);

public record OllamaRouterStatus(
    bool IsConnected,
    bool IsRouterProxy,
    string BaseUrl,
    IReadOnlyList<TargetBackendStatus> Targets);

/// <summary>
/// Service for querying OllamaRouter gateway status, resident GPU VRAM processes (/api/ps),
/// and triggering non-destructive memory reclaim (/monitor/reclaim-vram).
/// </summary>
public interface IOllamaRouterGatewayService
{
    Task<OllamaRouterStatus> GetGatewayStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default);
    Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default);
}
