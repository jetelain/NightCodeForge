namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Resolves the host or WSL user identity (UID:GID) to maintain consistent file ownership inside containers.
/// </summary>
public interface IHostUserIdentityResolver
{
    /// <summary>
    /// Resolves the host user UID and GID formatted as 'uid:gid' (e.g. '1000:1000').
    /// </summary>
    string ResolveUserIdentity(bool useWsl, string? wslDistribution, string? wslExecutable);
}
