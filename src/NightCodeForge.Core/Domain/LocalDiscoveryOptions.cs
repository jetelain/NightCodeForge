namespace NightCodeForge.Core.Domain;

/// <summary>
/// Configuration options controlling local Git repository discovery and caching.
/// </summary>
public class LocalDiscoveryOptions
{
    public const string SectionName = "LocalDiscovery";

    public bool ScanOnStartup { get; set; } = false;
    public int MaxDepth { get; set; } = 2;
    public int CacheDurationMinutes { get; set; } = 10;
    public List<string> AdditionalScanPaths { get; set; } = new();
}
