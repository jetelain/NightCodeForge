using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Discovers and caches WSL2 environment metadata (default distribution name and Linux username)
/// by executing native WSL commands (wsl.exe --list --verbose and wsl.exe bash -c "echo $USER").
/// </summary>
public partial class WslMetadataService : IWslMetadataService
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private readonly IProcessRunner _processRunner;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly bool? _isWindowsOverride;
    private readonly ILogger<WslMetadataService> _logger;

    private readonly object _lock = new();
    private string? _cachedDistributionName;
    private string? _cachedUserName;

    [ActivatorUtilitiesConstructor]
    public WslMetadataService(
        IProcessRunner processRunner,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        ILogger<WslMetadataService> logger)
        : this(processRunner, options, isWindowsOverride: null, logger)
    {
    }

    public WslMetadataService(
        IProcessRunner processRunner,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        bool? isWindowsOverride,
        ILogger<WslMetadataService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Options value cannot be null.", nameof(options));
        _isWindowsOverride = isWindowsOverride;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private bool IsWindows => _isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string GetDefaultDistributionName()
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedDistributionName))
            {
                return _cachedDistributionName;
            }

            // 1. Environment variable override
            var envDistro = Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION");
            if (!string.IsNullOrWhiteSpace(envDistro))
            {
                _cachedDistributionName = envDistro.Trim();
                return _cachedDistributionName;
            }

            // 2. Discover via wsl.exe --list --verbose on Windows
            if (IsWindows)
            {
                try
                {
                    var result = _processRunner.Run("wsl.exe", ["--list", "--verbose"], timeoutSeconds: 5);
                    if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                    {
                        var parsed = ParseDefaultDistribution(result.StandardOutput);
                        if (!string.IsNullOrWhiteSpace(parsed))
                        {
                            _logger.LogInformation("Discovered default WSL distribution '{DistroName}' from wsl.exe --list --verbose", parsed);
                            _cachedDistributionName = parsed;
                            return _cachedDistributionName;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("wsl.exe --list --verbose exited with code {Code}: {Error}", result.ExitCode, result.StandardError);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query wsl.exe --list --verbose");
                }
            }

            // 3. Fallback to configured option or Ubuntu default
            _cachedDistributionName = !string.IsNullOrWhiteSpace(_options.DefaultWslDistribution)
                ? _options.DefaultWslDistribution.Trim()
                : "Ubuntu";

            return _cachedDistributionName;
        }
    }

    public string GetDefaultUserName()
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedUserName))
            {
                return _cachedUserName;
            }

            // 1. Environment variable override
            var envUser = Environment.GetEnvironmentVariable("NCF_WSL_USER");
            if (!string.IsNullOrWhiteSpace(envUser))
            {
                _cachedUserName = envUser.Trim();
                return _cachedUserName;
            }

            // 2. Discover via wsl.exe bash -c "echo $USER" on Windows
            if (IsWindows)
            {
                try
                {
                    var result = _processRunner.Run("wsl.exe", ["bash", "-c", "echo $USER"], timeoutSeconds: 5);
                    if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                    {
                        var username = result.StandardOutput.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                        if (!string.IsNullOrWhiteSpace(username))
                        {
                            _logger.LogInformation("Discovered WSL username '{UserName}' from wsl.exe bash -c \"echo $USER\"", username);
                            _cachedUserName = username;
                            return _cachedUserName;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("wsl.exe bash -c \"echo $USER\" exited with code {Code}: {Error}", result.ExitCode, result.StandardError);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query wsl.exe bash -c \"echo $USER\"");
                }
            }

            // 3. Fallback to Windows username or "user"
            var hostUser = Environment.UserName;
            _cachedUserName = !string.IsNullOrWhiteSpace(hostUser)
                ? hostUser.Trim().ToLowerInvariant()
                : "user";

            return _cachedUserName;
        }
    }

    public void ClearCache()
    {
        lock (_lock)
        {
            _cachedDistributionName = null;
            _cachedUserName = null;
        }
    }

    public static string? ParseDefaultDistribution(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? firstAvailableDistro = null;

        foreach (var line in lines)
        {
            // Skip table header
            if (line.Contains("NAME", StringComparison.OrdinalIgnoreCase) && line.Contains("STATE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Check if marked with asterisk (*) as default
            bool isDefault = trimmed.StartsWith('*');
            var cleanLine = isDefault ? trimmed.TrimStart('*').Trim() : trimmed;

            var tokens = WhitespaceRegex().Split(cleanLine);
            if (tokens.Length > 0 && !string.IsNullOrWhiteSpace(tokens[0]))
            {
                var distroName = tokens[0].Trim();
                if (isDefault)
                {
                    return distroName;
                }

                firstAvailableDistro ??= distroName;
            }
        }

        return firstAvailableDistro;
    }
}
