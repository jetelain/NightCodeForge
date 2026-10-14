using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Implements cross-platform path resolution between the host and Unix agent execution environments (WSL2 / ext4 / container).
/// </summary>
public partial class WorkspacePathResolver : IWorkspacePathResolver
{
    [GeneratedRegex(@"^([a-zA-Z]):[\\/]")]
    private static partial Regex WindowsDrivePrefixRegex();

    private readonly IWslMetadataService? _wslMetadataService;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly bool? _isWindowsOverride;
    private readonly string? _distributionOverride;
    private readonly string? _userOverride;

    public WorkspacePathResolver(
        IWslMetadataService? wslMetadataService = null,
        IOptions<ExecutionEnvironmentProviderOptions>? options = null,
        bool? isWindowsOverride = null,
        string? distributionOverride = null,
        string? userOverride = null)
    {
        _wslMetadataService = wslMetadataService;
        _options = options?.Value ?? new ExecutionEnvironmentProviderOptions();
        _isWindowsOverride = isWindowsOverride;
        _distributionOverride = distributionOverride;
        _userOverride = userOverride;
    }

    private bool IsWindows => _isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public string GetWslDistributionName()
    {
        if (!string.IsNullOrWhiteSpace(_distributionOverride))
        {
            return _distributionOverride;
        }

        if (_wslMetadataService != null)
        {
            return _wslMetadataService.GetDefaultDistributionName();
        }

        var envDistro = Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION");
        if (!string.IsNullOrWhiteSpace(envDistro))
        {
            return envDistro;
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultWslDistribution))
        {
            return _options.DefaultWslDistribution;
        }

        return "Ubuntu";
    }

    public string GetWslUserName()
    {
        if (!string.IsNullOrWhiteSpace(_userOverride))
        {
            return _userOverride;
        }

        if (_wslMetadataService != null)
        {
            return _wslMetadataService.GetDefaultUserName();
        }

        var envUser = Environment.GetEnvironmentVariable("NCF_WSL_USER");
        if (!string.IsNullOrWhiteSpace(envUser))
        {
            return envUser;
        }

        var username = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username.ToLowerInvariant();
        }

        return "user";
    }

    public string GetAgentWorkingClonePath(int workspaceId)
    {
        if (IsWindows)
        {
            return $"/home/{GetWslUserName()}/.nightcodeforge/wks/{workspaceId}";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return $"~/.nightcodeforge/wks/{workspaceId}";
        }

        return $"{userProfile.Replace('\\', '/')}/.nightcodeforge/wks/{workspaceId}";
    }

    public string GetHostRemoteUrl(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        // If Windows or path starts with a Windows drive (e.g. C:\)
        if (IsWindows || WindowsDrivePrefixRegex().IsMatch(repositoryPath))
        {
            return WslPathConverter.ToWslPath(repositoryPath);
        }

        return repositoryPath.Replace('\\', '/');
    }

    public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath)
    {
        var raw = (relativeOrContainerPath ?? string.Empty).Trim();
        var normalized = raw.Replace('\\', '/');

        if (normalized.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\\\wsl.localhost\\", StringComparison.OrdinalIgnoreCase))
        {
            return raw.Replace('/', '\\');
        }

        string rel;
        var agentWorkingPrefix = $".nightcodeforge/wks/{workspaceId}/";
        var agentIdx = normalized.IndexOf(agentWorkingPrefix, StringComparison.OrdinalIgnoreCase);
        if (agentIdx >= 0)
        {
            rel = normalized.Substring(agentIdx + agentWorkingPrefix.Length);
        }
        else if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized.Substring("/workspace/".Length);
        }
        else if (string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalized, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            rel = string.Empty;
        }
        else if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized.Substring("workspace/".Length);
        }
        else if (normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized.Substring("/workspace".Length);
        }
        else
        {
            rel = normalized;
        }

        rel = rel.TrimStart('/', '\\');

        if (IsWindows)
        {
            var baseUnc = $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}";
            if (string.IsNullOrEmpty(rel))
            {
                return baseUnc;
            }

            return $@"{baseUnc}\{rel.Replace('/', '\\')}";
        }
        else
        {
            var basePath = GetAgentWorkingClonePath(workspaceId);
            if (string.IsNullOrEmpty(rel))
            {
                return basePath;
            }

            return $"{basePath}/{rel.Replace('\\', '/')}";
        }
    }
}
