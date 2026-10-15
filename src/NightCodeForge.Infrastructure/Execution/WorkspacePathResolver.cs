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

    public string GetAgentWorktreePath(int workspaceId, int taskId)
    {
        return $"{GetAgentWorkspaceRootPath(workspaceId)}/task-{taskId}";
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

    public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath, int? taskId)
    {
        var raw = (relativeOrContainerPath ?? string.Empty).Trim();
        var normalized = raw.Replace('\\', '/');

        if (normalized.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\\\wsl.localhost\\", StringComparison.OrdinalIgnoreCase))
        {
            return raw.Replace('/', '\\');
        }

        int? effectiveTaskId = taskId;
        var agentWorkingPrefix = $".nightcodeforge/wks/{workspaceId}/";
        int wksIdx = normalized.IndexOf(agentWorkingPrefix, StringComparison.OrdinalIgnoreCase);
        if (wksIdx >= 0)
        {
            var afterPrefix = normalized[(wksIdx + agentWorkingPrefix.Length)..];
            raw = "/workspace/" + afterPrefix;
        }

        string baseWorkingRoot;
        if (IsWindows)
        {
            baseWorkingRoot = effectiveTaskId.HasValue
                ? $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}\task-{effectiveTaskId.Value}"
                : $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}\clone";
        }
        else
        {
            baseWorkingRoot = effectiveTaskId.HasValue
                ? GetAgentWorktreePath(workspaceId, effectiveTaskId.Value)
                : GetAgentWorkingClonePath(workspaceId);
        }

        return ResolveCruciblePath(baseWorkingRoot, raw, effectiveTaskId);
    }

    public string ResolveCruciblePath(string workspaceRoot, string relativeOrContainerPath, int? taskId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var raw = (relativeOrContainerPath ?? string.Empty).Trim();
        var normalized = raw.Replace('\\', '/');

        if (normalized.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\\\wsl.localhost\\", StringComparison.OrdinalIgnoreCase))
        {
            return raw.Replace('/', '\\');
        }

        if (Path.IsPathRooted(raw) &&
            !normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/clone", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(raw);
        }

        bool isClonePrefixed =
            normalized.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "/clone", StringComparison.OrdinalIgnoreCase);

        if (isClonePrefixed)
        {
            string cloneRel = normalized.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase)
                ? normalized["/clone/".Length..]
                : string.Empty;

            cloneRel = cloneRel.TrimStart('/', '\\');

            var normRoot = workspaceRoot.Replace('\\', '/').TrimEnd('/');
            var rootLeaf = Path.GetFileName(normRoot);
            string parentDir;
            if (rootLeaf.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.Equals("mirror", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.StartsWith("task-", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.StartsWith("wt-", StringComparison.OrdinalIgnoreCase))
            {
                parentDir = Path.GetDirectoryName(normRoot) ?? normRoot;
            }
            else
            {
                parentDir = normRoot;
            }

            bool isUnix = !IsWindows || (normRoot.StartsWith('/') && !normRoot.StartsWith("//")) || normRoot.StartsWith('~');
            bool isUnc = normRoot.StartsWith("//") || normRoot.StartsWith(@"\\");

            string FormatPath(string baseDir, string subPath)
            {
                if (string.IsNullOrEmpty(subPath))
                {
                    if (isUnc) return baseDir.Replace('/', '\\');
                    return isUnix ? baseDir.Replace('\\', '/') : Path.GetFullPath(baseDir);
                }

                if (isUnc)
                {
                    var cleanBase = baseDir.Replace('/', '\\').TrimEnd('\\');
                    var cleanSub = subPath.Replace('/', '\\').TrimStart('\\');
                    return $@"{cleanBase}\{cleanSub}";
                }

                if (isUnix)
                {
                    var cleanBase = baseDir.Replace('\\', '/').TrimEnd('/');
                    var cleanSub = subPath.Replace('\\', '/').TrimStart('/');
                    return $"{cleanBase}/{cleanSub}";
                }

                return Path.GetFullPath(Path.Combine(baseDir, subPath));
            }

            var cloneDir = Path.Combine(parentDir, "clone");
            if (Directory.Exists(cloneDir))
            {
                return FormatPath(cloneDir, cloneRel);
            }
            return FormatPath(workspaceRoot, cloneRel);
        }

        // Only remap "/workspace" or "workspace" virtual prefixes
        bool isWorkspacePrefixed =
            normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "workspace", StringComparison.OrdinalIgnoreCase);

        if (isWorkspacePrefixed)
        {
            string rel;
            if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
            {
                rel = normalized["/workspace/".Length..];
            }
            else if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
            {
                rel = normalized["workspace/".Length..];
            }
            else
            {
                rel = string.Empty;
            }

            rel = rel.TrimStart('/', '\\');

            // Find parentDir (the directory mounted to /workspace in container)
            var normRoot = workspaceRoot.Replace('\\', '/').TrimEnd('/');
            var rootLeaf = Path.GetFileName(normRoot);
            string parentDir;
            if (rootLeaf.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.Equals("mirror", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.StartsWith("task-", StringComparison.OrdinalIgnoreCase) ||
                rootLeaf.StartsWith("wt-", StringComparison.OrdinalIgnoreCase))
            {
                parentDir = Path.GetDirectoryName(normRoot) ?? normRoot;
            }
            else
            {
                parentDir = normRoot;
            }

            bool isUnix = !IsWindows || (normRoot.StartsWith('/') && !normRoot.StartsWith("//")) || normRoot.StartsWith('~');
            bool isUnc = normRoot.StartsWith("//") || normRoot.StartsWith(@"\\");

            string FormatPath(string baseDir, string subPath)
            {
                if (string.IsNullOrEmpty(subPath))
                {
                    if (isUnc) return baseDir.Replace('/', '\\');
                    return isUnix ? baseDir.Replace('\\', '/') : Path.GetFullPath(baseDir);
                }

                if (isUnc)
                {
                    var cleanBase = baseDir.Replace('/', '\\').TrimEnd('\\');
                    var cleanSub = subPath.Replace('/', '\\').TrimStart('\\');
                    return $@"{cleanBase}\{cleanSub}";
                }

                if (isUnix)
                {
                    var cleanBase = baseDir.Replace('\\', '/').TrimEnd('/');
                    var cleanSub = subPath.Replace('\\', '/').TrimStart('/');
                    return $"{cleanBase}/{cleanSub}";
                }

                return Path.GetFullPath(Path.Combine(baseDir, subPath));
            }

            if (string.IsNullOrEmpty(rel))
            {
                return FormatPath(workspaceRoot, string.Empty);
            }

            // In container, agent has "/workspace/clone" and "/workspace/task-nnn"
            if (rel.StartsWith("clone/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel, "clone", StringComparison.OrdinalIgnoreCase))
            {
                var cloneRel = rel.Length > 5 ? rel[6..].TrimStart('/') : string.Empty;
                var cloneDir = Path.Combine(parentDir, "clone");
                if (Directory.Exists(cloneDir))
                {
                    return FormatPath(cloneDir, cloneRel);
                }
                return FormatPath(workspaceRoot, cloneRel);
            }

            var taskMatch = System.Text.RegularExpressions.Regex.Match(rel, @"^(?:wt/)?task-(\d+)(?:/(.*)|$)");
            if (taskMatch.Success)
            {
                var taskNum = taskMatch.Groups[1].Value;
                var taskRel = taskMatch.Groups[2].Success ? taskMatch.Groups[2].Value.TrimStart('/') : string.Empty;
                var taskDir = Path.Combine(parentDir, $"task-{taskNum}");
                return FormatPath(taskDir, taskRel);
            }

            // If not clone or task-nnn, it's relative to the target working directory (worktree if taskId, else clone)
            if (taskId.HasValue && taskId.Value > 0)
            {
                var taskDir = Path.Combine(parentDir, $"task-{taskId.Value}");
                if (Directory.Exists(taskDir))
                {
                    return FormatPath(taskDir, rel);
                }
            }

            return FormatPath(workspaceRoot, rel);
        }

        // Relative path without /workspace prefix: resolve directly against workspaceRoot
        bool isRootUnix = !IsWindows || (workspaceRoot.StartsWith('/') && !workspaceRoot.StartsWith("//")) || workspaceRoot.StartsWith('~');
        bool isRootUnc = workspaceRoot.StartsWith("//") || workspaceRoot.StartsWith(@"\\");

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (isRootUnc) return workspaceRoot.Replace('/', '\\');
            return isRootUnix ? workspaceRoot.Replace('\\', '/') : Path.GetFullPath(workspaceRoot);
        }

        if (isRootUnc)
        {
            var cleanBase = workspaceRoot.Replace('/', '\\').TrimEnd('\\');
            var cleanSub = raw.Replace('/', '\\').TrimStart('\\');
            return $@"{cleanBase}\{cleanSub}";
        }

        if (isRootUnix)
        {
            return $"{workspaceRoot.Replace('\\', '/').TrimEnd('/')}/{raw.Replace('\\', '/').TrimStart('/')}";
        }

        return Path.GetFullPath(Path.Combine(workspaceRoot, raw));
    }
}
