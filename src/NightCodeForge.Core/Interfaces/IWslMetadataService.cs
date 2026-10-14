namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Provides cached metadata about the local WSL2 environment, including default distribution name and Linux username.
/// </summary>
public interface IWslMetadataService
{
    /// <summary>
    /// Gets the default WSL2 distribution name (e.g. "Ubuntu"). Cached across invocations.
    /// </summary>
    string GetDefaultDistributionName();

    /// <summary>
    /// Gets the default Linux username within the target WSL2 distribution. Cached across invocations.
    /// </summary>
    string GetDefaultUserName();

    /// <summary>
    /// Clears any cached metadata, forcing subsequent queries to re-evaluate from the system.
    /// </summary>
    void ClearCache();
}
