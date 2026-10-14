namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Detailed result of SDK and toolchain discovery on a host environment.
/// </summary>
/// <param name="MountPaths">Deduplicated list of host directories suitable for read-only volume mounting.</param>
/// <param name="EnvironmentVariables">Map of discovered SDK environment variables (e.g. ANDROID_HOME, JAVA_HOME).</param>
/// <param name="CommandPaths">Map of discovered tool command paths (e.g. dotnet, node, java).</param>
public record HostSdkDiscoveryDetails(
    IReadOnlyList<string> MountPaths,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyDictionary<string, string> CommandPaths);

/// <summary>
/// Service responsible for discovering and caching SDK and toolchain directories on the host system (WSL2 or Bare Unix).
/// Probes environment variables (e.g. ANDROID_HOME, JAVA_HOME, DOTNET_ROOT)
/// and resolves standard command locations (e.g. which dotnet, node, java).
/// </summary>
public interface IHostSdkDiscoveryService
{
    /// <summary>
    /// Discovers and returns SDK directories to mount read-only into container sandboxes.
    /// Results are cached across invocations.
    /// </summary>
    /// <param name="distribution">Optional WSL2 distribution name when probing WSL2 on Windows. Null uses the default host/distribution.</param>
    /// <returns>A deduplicated list of valid existing host SDK mount directories.</returns>
    IReadOnlyList<string> DiscoverSdkMountPaths(string? distribution = null);

    /// <summary>
    /// Discovers detailed SDK and toolchain information, including detected environment variables, command binaries, and mount paths.
    /// Results are cached across invocations.
    /// </summary>
    /// <param name="distribution">Optional WSL2 distribution name when probing WSL2 on Windows. Null uses the default host/distribution.</param>
    HostSdkDiscoveryDetails DiscoverSdkDetails(string? distribution = null);

    /// <summary>
    /// Asynchronously discovers and returns SDK directories to mount read-only into container sandboxes.
    /// Results are cached across invocations.
    /// </summary>
    /// <param name="distribution">Optional WSL2 distribution name when probing WSL2 on Windows. Null uses the default host/distribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<string>> DiscoverSdkMountPathsAsync(string? distribution = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously discovers detailed SDK and toolchain information.
    /// Results are cached across invocations.
    /// </summary>
    /// <param name="distribution">Optional WSL2 distribution name when probing WSL2 on Windows. Null uses the default host/distribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<HostSdkDiscoveryDetails> DiscoverSdkDetailsAsync(string? distribution = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears any cached SDK discovery results, forcing subsequent calls to re-probe the host environment.
    /// </summary>
    void ClearCache();
}
