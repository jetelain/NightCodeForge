using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Service responsible for discovering and caching host and WSL user identities (UID:GID)
/// so that container file generation matches the host user credentials and avoids root ownership conflicts.
/// </summary>
public partial class HostUserIdentityResolver : IHostUserIdentityResolver
{
    public const string DefaultUserUidGid = "1000:1000";

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<HostUserIdentityResolver> _logger;
    private readonly ConcurrentDictionary<string, string> _cachedIdentities = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*(\d+:\d+)\s*$")]
    private static partial Regex UidGidRegex();

    public HostUserIdentityResolver(
        IProcessRunner processRunner,
        ILogger<HostUserIdentityResolver> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string ResolveUserIdentity(bool useWsl, string? wslDistribution, string? wslExecutable)
    {
        var cacheKey = useWsl ? $"wsl:{wslDistribution ?? "default"}" : "host";
        if (_cachedIdentities.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = DiscoverUserIdentity(useWsl, wslDistribution, wslExecutable);
        if (resolved == null)
        {
            // Discovery failed (possibly transiently): return the fallback without caching it so a later call can retry.
            return DefaultUserUidGid;
        }

        _cachedIdentities[cacheKey] = resolved;
        return resolved;
    }

    private string? DiscoverUserIdentity(bool useWsl, string? wslDistribution, string? wslExecutable)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (!useWsl)
                {
                    return DefaultUserUidGid;
                }

                var exe = !string.IsNullOrWhiteSpace(wslExecutable) ? wslExecutable : "wsl.exe";
                var args = new List<string>();
                if (!string.IsNullOrWhiteSpace(wslDistribution))
                {
                    args.AddRange(["-d", wslDistribution]);
                }
                args.AddRange(["--", "sh", "-c", "echo $(id -u):$(id -g)"]);

                var spec = new ProcessStartSpec
                {
                    FileName = exe,
                    ArgumentList = args,
                    TimeoutSeconds = 5
                };

                var result = _processRunner.Run(spec);
                if (result.ExitCode == 0 && TryParseUidGid(result.StandardOutput, out var uidGid))
                {
                    _logger.LogInformation("Discovered WSL user identity: {UidGid} (distro: {Distro})", uidGid, wslDistribution ?? "default");
                    return uidGid;
                }

                _logger.LogWarning("Failed to resolve WSL user identity (exit code {ExitCode}, stderr: {Error}). Using fallback {Fallback}",
                    result.ExitCode, result.StandardError, DefaultUserUidGid);
                return null;
            }
            else
            {
                var spec = new ProcessStartSpec
                {
                    FileName = "sh",
                    ArgumentList = ["-c", "echo $(id -u):$(id -g)"],
                    TimeoutSeconds = 5
                };

                var result = _processRunner.Run(spec);
                if (result.ExitCode == 0 && TryParseUidGid(result.StandardOutput, out var uidGid))
                {
                    _logger.LogInformation("Discovered host user identity: {UidGid}", uidGid);
                    return uidGid;
                }

                _logger.LogWarning("Failed to resolve host user identity (exit code {ExitCode}, stderr: {Error}). Using fallback {Fallback}",
                    result.ExitCode, result.StandardError, DefaultUserUidGid);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while discovering user identity. Using fallback {Fallback}", DefaultUserUidGid);
            return null;
        }
    }

    private static bool TryParseUidGid(string output, out string uidGid)
    {
        uidGid = string.Empty;
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = UidGidRegex().Match(output.Trim());
        if (match.Success)
        {
            uidGid = match.Groups[1].Value;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Extension methods for resolving container user identity against <see cref="ContainerExecutorOptions"/>.
/// </summary>
public static class HostUserIdentityResolverExtensions
{
    public static string ResolveUserIdentity(this IHostUserIdentityResolver resolver, ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.UserUidGid))
        {
            return options.UserUidGid;
        }

        var identity = resolver.ResolveUserIdentity(options.UseWsl, options.WslDistribution, options.WslExecutable);
        options.UserUidGid = identity;
        return identity;
    }
}
