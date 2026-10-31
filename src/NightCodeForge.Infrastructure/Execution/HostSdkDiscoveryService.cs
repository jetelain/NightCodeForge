using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Service responsible for discovering and caching SDK and toolchain directories on the host system (WSL2 or Bare Unix).
/// Probes environment variables (e.g. ANDROID_HOME, JAVA_HOME, DOTNET_ROOT)
/// and standard commands (e.g. which dotnet, node, java) to detect existing host toolchains
/// suitable for mounting read-only into container sandboxes.
/// </summary>
public class HostSdkDiscoveryService : IHostSdkDiscoveryService
{
    internal const string ProbeScript =
@"[ -f /etc/profile ] && . /etc/profile 2>/dev/null
[ -f ""$HOME/.profile"" ] && . ""$HOME/.profile"" 2>/dev/null
[ -f ""$HOME/.bash_profile"" ] && . ""$HOME/.bash_profile"" 2>/dev/null
if [ -f ""$HOME/.bashrc"" ]; then
  . <(sed 's/return/true/g' ""$HOME/.bashrc"" 2>/dev/null) 2>/dev/null || . ""$HOME/.bashrc"" 2>/dev/null
fi
for dir in ""$HOME/.dotnet/tools"" ""$HOME/.cargo/bin"" ""$HOME/.local/bin"" ""$HOME/bin"" ""$HOME/.nvm""; do
  [ -d ""$dir"" ] && PATH=""$dir:$PATH""
done
export PATH
echo ""---ENV---""
echo ""ANDROID_HOME=$ANDROID_HOME""
echo ""ANDROID_SDK_ROOT=$ANDROID_SDK_ROOT""
echo ""JAVA_HOME=$JAVA_HOME""
echo ""DOTNET_ROOT=$DOTNET_ROOT""
echo ""GOROOT=$GOROOT""
echo ""CARGO_HOME=$CARGO_HOME""
echo ""RUSTUP_HOME=$RUSTUP_HOME""
echo ""NVM_DIR=$NVM_DIR""
echo ""NODE_PATH=$NODE_PATH""
echo ""---CMD---""
for c in dotnet node npm java javac go rustc cargo python3 python uv mvn gradle adb sdkmanager csnav mdfetch codecondenser; do
  p=$(which ""$c"" 2>/dev/null || command -v ""$c"" 2>/dev/null)
  if [ -z ""$p"" ] && [ -x ""$HOME/.dotnet/tools/$c"" ]; then
    p=""$HOME/.dotnet/tools/$c""
  fi
  if [ -n ""$p"" ]; then
    real=$(readlink -f ""$p"" 2>/dev/null || realpath ""$p"" 2>/dev/null || echo ""$p"")
    echo ""$c=$p=$real""
  fi
done
echo ""---DIR---""
for d in /usr/share/dotnet /usr/lib/dotnet /usr/lib/jvm /opt/android-sdk /etc/ssl/certs /etc/java-* /usr/lib/x86_64-linux-gnu /usr/local/go ""$HOME/Android/Sdk"" ""$HOME/android-sdk"" ""$HOME/.dotnet"" ""$HOME/.cargo"" ""$HOME/.nvm""; do
  if [ -d ""$d"" ]; then
    if [ ""$d"" = ""/usr/share/dotnet"" ] || [ ""$d"" = ""/usr/lib/dotnet"" ]; then
      ([ -f ""$d/dotnet"" ] || [ -d ""$d/shared"" ]) && echo ""$d""
    else
      echo ""$d""
    fi
  fi
done";

    public static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        "/",
        "/usr",
        "/usr/bin",
        "/usr/sbin",
        "/bin",
        "/sbin",
        "/usr/lib",
        "/usr/local",
        "/usr/local/bin",
        "/usr/local/sbin",
        "/usr/local/lib",
        "/lib",
        "/lib64",
        "/etc",
        "/var",
        "/tmp",
        "/root",
        "/home",
        "/dev",
        "/proc",
        "/sys",
        "/mnt",
        "/opt",
        "/snap",
        "/snap/bin",
        "/usr/lib/snapd"
    };

    private readonly IProcessRunner _processRunner;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly bool? _isWindowsOverride;
    private readonly ILogger<HostSdkDiscoveryService> _logger;

    private readonly ConcurrentDictionary<string, HostSdkDiscoveryDetails> _cachedDetails = new(StringComparer.OrdinalIgnoreCase);

    [ActivatorUtilitiesConstructor]
    public HostSdkDiscoveryService(
        IProcessRunner processRunner,
        IWslMetadataService wslMetadataService,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        ILogger<HostSdkDiscoveryService> logger)
        : this(processRunner, wslMetadataService, options, isWindowsOverride: null, logger)
    {
    }

    public HostSdkDiscoveryService(
        IProcessRunner processRunner,
        IWslMetadataService wslMetadataService,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        bool? isWindowsOverride,
        ILogger<HostSdkDiscoveryService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Options value cannot be null.", nameof(options));
        _isWindowsOverride = isWindowsOverride;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private bool IsWindows => _isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public IReadOnlyList<string> DiscoverSdkMountPaths(string? distribution = null)
    {
        return DiscoverSdkDetails(distribution).MountPaths;
    }

    public HostSdkDiscoveryDetails DiscoverSdkDetails(string? distribution = null)
    {
        var cacheKey = GetCacheKey(distribution);
        if (_cachedDetails.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var spec = BuildProcessSpec(distribution, timeoutSeconds: 10);

        try
        {
            var result = _processRunner.Run(spec);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var parsed = ParseDiscoveryOutput(result.StandardOutput);
                parsed = ApplyCustomOverrides(parsed, cacheKey);
                _logger.LogInformation("Discovered {Count} host SDK mount paths for '{Target}'", parsed.MountPaths.Count, cacheKey);
                _cachedDetails[cacheKey] = parsed;
                return parsed;
            }
            else
            {
                _logger.LogWarning("Host SDK discovery script exited with code {Code}: {Error}", result.ExitCode, result.StandardError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute host SDK discovery script for '{Target}'", cacheKey);
        }

        var fallback = CreateFallbackDetails();
        fallback = ApplyCustomOverrides(fallback, cacheKey);
        _cachedDetails[cacheKey] = fallback;
        return fallback;
    }

    public async Task<IReadOnlyList<string>> DiscoverSdkMountPathsAsync(string? distribution = null, CancellationToken cancellationToken = default)
    {
        var details = await DiscoverSdkDetailsAsync(distribution, cancellationToken);
        return details.MountPaths;
    }

    public async Task<HostSdkDiscoveryDetails> DiscoverSdkDetailsAsync(string? distribution = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = GetCacheKey(distribution);
        if (_cachedDetails.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var spec = BuildProcessSpec(distribution, timeoutSeconds: 10);

        try
        {
            var result = await _processRunner.RunAsync(spec, cancellationToken);
            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var parsed = ParseDiscoveryOutput(result.StandardOutput);
                parsed = ApplyCustomOverrides(parsed, cacheKey);
                _logger.LogInformation("Discovered {Count} host SDK mount paths for '{Target}'", parsed.MountPaths.Count, cacheKey);
                _cachedDetails[cacheKey] = parsed;
                return parsed;
            }
            else
            {
                _logger.LogWarning("Host SDK discovery script exited with code {Code}: {Error}", result.ExitCode, result.StandardError);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute host SDK discovery script for '{Target}'", cacheKey);
        }

        var fallback = CreateFallbackDetails();
        fallback = ApplyCustomOverrides(fallback, cacheKey);
        _cachedDetails[cacheKey] = fallback;
        return fallback;
    }

    public void ClearCache()
    {
        _cachedDetails.Clear();
    }

    private string GetCacheKey(string? distribution)
    {
        if (IsWindows)
        {
            var distro = distribution ?? _wslMetadataService.GetDefaultDistributionName();
            return $"wsl:{distro}";
        }

        return "bare_host";
    }

    internal static readonly string NormalizedProbeScript = ProbeScript
        .TrimStart('\uFEFF', '\r', '\n')
        .Replace("\r\n", "\n") + "\n";

    private ProcessStartSpec BuildProcessSpec(string? distribution, int timeoutSeconds = 10)
    {
        var (fileName, args) = BuildProcessArguments(distribution);
        return new ProcessStartSpec
        {
            FileName = fileName,
            ArgumentList = args,
            StandardInput = NormalizedProbeScript,
            TimeoutSeconds = timeoutSeconds
        };
    }

    private (string FileName, IEnumerable<string> Arguments) BuildProcessArguments(string? distribution)
    {
        if (IsWindows)
        {
            var fileName = !string.IsNullOrWhiteSpace(_options.WslExecutable) ? _options.WslExecutable : "wsl.exe";
            var distro = distribution ?? _wslMetadataService.GetDefaultDistributionName();

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(distro))
            {
                args.Add("-d");
                args.Add(distro);
            }
            args.Add("--");
            args.Add("bash");
            args.Add("-s");

            return (fileName, args);
        }
        else
        {
            var fileName = "bash";
            var args = new[] { "-s" };
            return (fileName, args);
        }
    }

    public static HostSdkDiscoveryDetails ParseDiscoveryOutput(string output)
    {
        var mountPaths = new List<string>();
        var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(output))
        {
            return CreateFallbackDetails();
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var section = "NONE";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line == "---ENV---")
            {
                section = "ENV";
                continue;
            }
            if (line == "---CMD---")
            {
                section = "CMD";
                continue;
            }
            if (line == "---DIR---")
            {
                section = "DIR";
                continue;
            }

            switch (section)
            {
                case "ENV":
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = line[..eqIdx].Trim();
                        var val = line[(eqIdx + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            envVars[key] = val;
                            if (IsValidMountPath(val))
                            {
                                mountPaths.Add(NormalizePath(val));
                            }
                        }
                    }
                    break;

                case "CMD":
                    var cmdParts = line.Split(['='], 3);
                    if (cmdParts.Length >= 2)
                    {
                        var cmdName = cmdParts[0].Trim();
                        var cmdPath = cmdParts[1].Trim();
                        var cmdReal = cmdParts.Length >= 3 ? cmdParts[2].Trim() : cmdPath;

                        if (!string.IsNullOrWhiteSpace(cmdPath))
                        {
                            commands[cmdName] = cmdPath;
                        }

                        var candidate = DeriveSdkRootFromBinary(cmdName, cmdReal);
                        if (!string.IsNullOrWhiteSpace(candidate) && IsValidMountPath(candidate))
                        {
                            mountPaths.Add(NormalizePath(candidate));
                        }
                    }
                    break;

                case "DIR":
                    if (IsValidMountPath(line))
                    {
                        mountPaths.Add(NormalizePath(line));
                    }
                    break;
            }
        }

        var distinctPaths = FilterAndDeduplicate(mountPaths);
        if (distinctPaths.Count == 0)
        {
            distinctPaths = ContainerExecutorOptions.DefaultSdkMountPaths.ToList();
        }

        return new HostSdkDiscoveryDetails(distinctPaths, envVars, commands);
    }

    public static string? DeriveSdkRootFromBinary(string cmdName, string realPath)
    {
        if (string.IsNullOrWhiteSpace(realPath)) return null;

        var normalized = realPath.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/')) return null;

        var dir = GetDirectory(normalized);
        if (string.IsNullOrWhiteSpace(dir) || IsExcludedDirectory(dir))
        {
            return null;
        }

        if (dir.EndsWith("/cmdline-tools/latest/bin", StringComparison.OrdinalIgnoreCase))
        {
            var androidRoot = dir[..^"/cmdline-tools/latest/bin".Length];
            if (!string.IsNullOrWhiteSpace(androidRoot) && !IsExcludedDirectory(androidRoot))
            {
                return androidRoot;
            }
        }

        if (dir.EndsWith("/bin", StringComparison.OrdinalIgnoreCase) ||
            dir.EndsWith("/platform-tools", StringComparison.OrdinalIgnoreCase))
        {
            var parent = GetDirectory(dir);
            if (!string.IsNullOrWhiteSpace(parent) && !IsExcludedDirectory(parent))
            {
                return parent;
            }
        }

        return dir;
    }

    private static string? GetDirectory(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return null;
        return trimmed[..lastSlash];
    }

    public static bool IsExcludedDirectory(string path)
    {
        var normalized = NormalizePath(path);
        return ExcludedDirectories.Contains(normalized);
    }

    public static bool IsValidMountPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith('/') && !normalized.StartsWith("~")) return false;
        return !ExcludedDirectories.Contains(normalized);
    }

    public static string NormalizePath(string path)
    {
        var trimmed = path.Trim().Replace('\\', '/');
        if (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }
        return trimmed;
    }

    public static List<string> FilterAndDeduplicate(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var norm = NormalizePath(path);
            if (!IsValidMountPath(norm)) continue;

            if (seen.Add(norm))
            {
                result.Add(norm);
            }
        }

        return result;
    }

    private HostSdkDiscoveryDetails ApplyCustomOverrides(HostSdkDiscoveryDetails details, string cacheKey)
    {
        var currentMounts = new List<string>(details.MountPaths);

        // 1. Environment variable override (e.g. NCF_SDK_MOUNT_PATHS=/path1:/path2)
        var envOverride = Environment.GetEnvironmentVariable("NCF_SDK_MOUNT_PATHS");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            var parts = envOverride.Split([';', ':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                if (IsValidMountPath(p))
                {
                    currentMounts.Insert(0, NormalizePath(p));
                }
            }
        }

        // 2. Configured additional paths in options
        if (_options.AdditionalSdkMountPaths != null)
        {
            foreach (var p in _options.AdditionalSdkMountPaths)
            {
                if (IsValidMountPath(p))
                {
                    currentMounts.Add(NormalizePath(p));
                }
            }
        }

        var distinct = FilterAndDeduplicate(currentMounts);
        if (distinct.Count != details.MountPaths.Count || !distinct.SequenceEqual(details.MountPaths))
        {
            return new HostSdkDiscoveryDetails(distinct, details.EnvironmentVariables, details.CommandPaths);
        }

        return details;
    }

    private static HostSdkDiscoveryDetails CreateFallbackDetails()
    {
        return new HostSdkDiscoveryDetails(
            ContainerExecutorOptions.DefaultSdkMountPaths,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }
}
