namespace NightCodeForge.Core.Domain;

/// <summary>
/// Well-known ACP provider catalog identifiers.
/// </summary>
public static class KnownAcpCatalogIds
{
    public const string Antigravity = "antigravity";
    public const string Custom = "custom";
}

/// <summary>
/// Metadata describing an inference model and its supported thinking levels exposed by an ACP provider.
/// </summary>
public record AcpModelOption(
    string ModelId,
    string DisplayName,
    IReadOnlyList<string> SupportedThoughtLevels);

/// <summary>
/// Discovery, authentication, subscription tier, and model catalog status for an ACP harness provider.
/// </summary>
public record AcpCatalogProviderStatus(
    string CatalogId,
    string DisplayName,
    bool IsInstalled,
    bool IsAuthenticated,
    string? DetectedExecutablePath,
    string? ExecutionTargetDescription,
    string? ServerVersion,
    string? AccountTierName,
    string? DiagnosticMessage,
    IReadOnlyList<AcpModelOption> AvailableModels);
