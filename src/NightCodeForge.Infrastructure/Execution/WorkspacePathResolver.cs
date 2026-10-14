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

    public string GetAgentWorkspaceRootPath(int workspaceId)
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

    public string GetAgentWorkingClonePath(int workspaceId)
    {
        return $"{GetAgentWorkspaceRootPath(workspaceId)}/clone";
    }

    public string GetAgentWorktreePath(int workspaceId, int issueId)
    {
        return $"{GetAgentWorkspaceRootPath(workspaceId)}/issue-{issueId}";
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
        => ResolveCruciblePath(workspaceId, relativeOrContainerPath, null);

    public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath, int? issueId)
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

        if (normalized.IndexOf(agentWorkingPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var idx = normalized.IndexOf(agentWorkingPrefix, StringComparison.OrdinalIgnoreCase);
            rel = normalized[(idx + agentWorkingPrefix.Length)..];
        }
        else if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized["/workspace/".Length..];
        }
        else if (string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalized, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            rel = string.Empty;
        }
        else if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized["workspace/".Length..];
        }
        else if (normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase))
        {
            rel = normalized["/workspace".Length..];
        }
        else
        {
            rel = normalized;
        }

        rel = rel.TrimStart('/', '\\');

        int? effectiveIssueId = issueId;
        if (effectiveIssueId.HasValue)
        {
            if (rel.StartsWith($"issue-{effectiveIssueId.Value}/", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[($"issue-{effectiveIssueId.Value}/".Length)..];
            }
            else if (rel.StartsWith($"wt/issue-{effectiveIssueId.Value}/", StringComparison.OrdinalIgnoreCase))
            {
                rel = rel[($"wt/issue-{effectiveIssueId.Value}/".Length)..];
            }
            else if (string.Equals(rel, $"issue-{effectiveIssueId.Value}", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(rel, $"wt/issue-{effectiveIssueId.Value}", StringComparison.OrdinalIgnoreCase))
            {
                rel = string.Empty;
            }
        }
        else
        {
            var match = System.Text.RegularExpressions.Regex.Match(rel, @"^(?:wt/)?issue-(\d+)(?:/(.*)|$)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var parsedId))
            {
                effectiveIssueId = parsedId;
                rel = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            }
        }

        if (rel.StartsWith("clone/", StringComparison.OrdinalIgnoreCase))
        {
            rel = rel["clone/".Length..];
        }
        else if (string.Equals(rel, "clone", StringComparison.OrdinalIgnoreCase))
        {
            rel = string.Empty;
        }

        rel = rel.TrimStart('/', '\\');

        if (IsWindows)
        {
            var baseUnc = effectiveIssueId.HasValue
                ? $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}\issue-{effectiveIssueId.Value}"
                : $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}\clone";

            if (string.IsNullOrEmpty(rel))
            {
                return baseUnc;
            }

            return $@"{baseUnc}\{rel.Replace('/', '\\')}";
        }
        else
        {
            var basePath = effectiveIssueId.HasValue
                ? GetAgentWorktreePath(workspaceId, effectiveIssueId.Value)
                : GetAgentWorkingClonePath(workspaceId);

            if (string.IsNullOrEmpty(rel))
            {
                return basePath;
            }

            return $"{basePath}/{rel.Replace('\\', '/')}";
        }
    }
}
