using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Implements cross-platform path resolution between the host and Unix agent execution environments (WSL2 / ext4 / container).
/// </summary>
public partial class WorkspacePathResolver : IWorkspacePathResolver
{
    [GeneratedRegex(@"^([a-zA-Z]):[\\/]")]
    private static partial Regex WindowsDrivePrefixRegex();

    private readonly IWslMetadataService _wslMetadataService;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly bool? _isWindowsOverride;
    private readonly string? _distributionOverride;
    private readonly string? _userOverride;

    [ActivatorUtilitiesConstructor]
    public WorkspacePathResolver(
        IWslMetadataService wslMetadataService,
        IOptions<ExecutionEnvironmentProviderOptions> options)
        : this(wslMetadataService, options, isWindowsOverride: null, distributionOverride: null, userOverride: null)
    {
    }

    public WorkspacePathResolver(
        IWslMetadataService wslMetadataService,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        bool isWindowsOverride)
        : this(wslMetadataService, options, isWindowsOverride: isWindowsOverride, distributionOverride: null, userOverride: null)
    {
    }

    public WorkspacePathResolver(
        IWslMetadataService wslMetadataService,
        IOptions<ExecutionEnvironmentProviderOptions> options,
        bool? isWindowsOverride,
        string? distributionOverride,
        string? userOverride)
    {
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentException("Options value cannot be null.", nameof(options));
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

        return _wslMetadataService.GetDefaultDistributionName();
    }

    public string GetWslUserName()
    {
        if (!string.IsNullOrWhiteSpace(_userOverride))
        {
            return _userOverride;
        }

        return _wslMetadataService.GetDefaultUserName();
    }

    public string ToHostPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (IsWindows && (path.StartsWith('/') || path.StartsWith('~')) && !path.StartsWith("//"))
        {
            var distro = GetWslDistributionName();
            var user = GetWslUserName();
            if (string.IsNullOrWhiteSpace(distro) || string.IsNullOrWhiteSpace(user))
            {
                return path;
            }
            return WslPathConverter.ToWslUncPath(path, distro, user);
        }

        return path;
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

    public string GetAttachmentsPath(int workspaceId)
    {
        return $"{GetAgentWorkspaceRootPath(workspaceId)}/attachments";
    }

    public string GetWorkspaceMemoryPath(int workspaceId)
    {
        return $"{GetAgentWorkspaceRootPath(workspaceId)}/{AgentMemoryConstants.LocalMemoryDirectoryName}";
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

        int? effectiveTaskId = (taskId.HasValue && taskId.Value > 0) ? taskId.Value : null;
        var agentWorkingPrefix = $".nightcodeforge/wks/{workspaceId}/";
        int wksIdx = normalized.IndexOf(agentWorkingPrefix, StringComparison.OrdinalIgnoreCase);
        if (wksIdx >= 0)
        {
            var afterPrefix = normalized[(wksIdx + agentWorkingPrefix.Length)..].TrimStart('/');
            if (afterPrefix.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
                afterPrefix.StartsWith("tmp/", StringComparison.OrdinalIgnoreCase))
            {
                raw = "/" + afterPrefix;
            }
            else
            {
                raw = "/workspace/" + afterPrefix;
            }
        }

        if (workspaceId > 0 && TryGetLocalMemoryRelativePath(raw, out var memoryRel))
        {
            return IsWindows
                ? JoinMemoryPath(
                    $@"\\wsl.localhost\{GetWslDistributionName()}\home\{GetWslUserName()}\.nightcodeforge\wks\{workspaceId}\{AgentMemoryConstants.LocalMemoryDirectoryName}",
                    memoryRel,
                    '\\')
                : JoinMemoryPath(GetWorkspaceMemoryPath(workspaceId), memoryRel, '/');
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

        if (IsWindows && (workspaceRoot.StartsWith('/') || workspaceRoot.StartsWith('~')))
        {
            var distro = GetWslDistributionName();
            var user = GetWslUserName();
            workspaceRoot = WslPathConverter.ToWslUncPath(workspaceRoot, distro, user);
        }

        var raw = (relativeOrContainerPath ?? string.Empty).Trim();
        var normalized = raw.Replace('\\', '/');

        if (normalized.StartsWith("./") || normalized.StartsWith(".\\"))
        {
            raw = raw[2..].TrimStart('/', '\\');
            normalized = raw.Replace('\\', '/');
        }

        if (normalized.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("\\\\wsl.localhost\\", StringComparison.OrdinalIgnoreCase))
        {
            return raw.Replace('/', '\\');
        }

        var normRoot = workspaceRoot.Replace('\\', '/').TrimEnd('/');
        var rootLeaf = Path.GetFileName(normRoot);
        string parentDir;
        if (rootLeaf.Equals("clone", StringComparison.OrdinalIgnoreCase) ||
            rootLeaf.Equals("repo", StringComparison.OrdinalIgnoreCase) ||
            rootLeaf.Equals("mirror", StringComparison.OrdinalIgnoreCase) ||
            rootLeaf.StartsWith("task-", StringComparison.OrdinalIgnoreCase) ||
            rootLeaf.StartsWith("wt-", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = normRoot.LastIndexOf('/');
            parentDir = lastSlash > 0 ? normRoot[..lastSlash] : normRoot;
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
                var combined = $"{cleanBase}/{cleanSub}";
                if (!OperatingSystem.IsWindows() && cleanBase.StartsWith('/') && !cleanBase.StartsWith("//"))
                {
                    try
                    {
                        return Path.GetFullPath(combined);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        // Retain unnormalized path when non-standard characters prevent Path.GetFullPath
                    }
                }
                return combined;
            }

            return Path.GetFullPath(Path.Combine(baseDir, subPath));
        }

        var normParent = parentDir.Replace('\\', '/').TrimEnd('/');
        bool isAlreadyUnderParent =
            !string.IsNullOrEmpty(normParent) &&
            (normalized.Equals(normParent, StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(normParent + "/", StringComparison.OrdinalIgnoreCase));

        if (!isAlreadyUnderParent)
        {
            var embeddedWksMatch = Regex.Match(
                normalized,
                @"\.nightcodeforge/wks/(\d+)/((?:attachments|memory)(?:/.*)?)$",
                RegexOptions.IgnoreCase);
            if (embeddedWksMatch.Success &&
                normParent.EndsWith($".nightcodeforge/wks/{embeddedWksMatch.Groups[1].Value}", StringComparison.OrdinalIgnoreCase))
            {
                raw = "/workspace/" + embeddedWksMatch.Groups[2].Value;
                normalized = raw;
            }
        }

        if (!isAlreadyUnderParent && TryResolveTmpSubPath(raw, out var tmpSubPath))
        {
            var tmpRel = string.IsNullOrEmpty(tmpSubPath) ? "tmp" : $"tmp/{tmpSubPath}";
            return FormatPath(parentDir, tmpRel);
        }

        if (!isAlreadyUnderParent && TryGetLocalMemoryRelativePath(raw, out var siblingMemoryRel))
        {
            // Memory is a sibling of clone/ and task-{id}/. Never resolve it inside a repository path (BareHost):
            // without a wks/{id} layout the caller must use the workspace-id overload.
            if (string.Equals(parentDir, normRoot, StringComparison.Ordinal) &&
                !Regex.IsMatch(normRoot, @"\.nightcodeforge/wks/\d+$", RegexOptions.IgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    $"Access denied: '{AgentMemoryConstants.VirtualLocalMemoryPath}' cannot be resolved from workspace root '{workspaceRoot}'; use the workspace-id based resolution.");
            }

            var siblingMemoryDir = FormatPath(parentDir, AgentMemoryConstants.LocalMemoryDirectoryName);
            return string.IsNullOrEmpty(siblingMemoryRel)
                ? siblingMemoryDir
                : FormatPath(siblingMemoryDir, siblingMemoryRel);
        }

        if (Path.IsPathRooted(raw) &&
            !normalized.StartsWith("/workspace", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/clone", StringComparison.OrdinalIgnoreCase))
        {
            return isUnix ? normalized : Path.GetFullPath(raw);
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

            if (string.IsNullOrEmpty(rel))
            {
                return FormatPath(workspaceRoot, string.Empty);
            }

            // In container, agent has "/workspace/clone", "/workspace/attachments", and "/workspace/task-nnn"
            if (rel.StartsWith("attachments/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(rel, "attachments", StringComparison.OrdinalIgnoreCase))
            {
                var attachRel = rel.Length > "attachments".Length
                    ? rel[("attachments".Length + 1)..].TrimStart('/')
                    : string.Empty;
                var attachSub = string.IsNullOrEmpty(attachRel)
                    ? "attachments"
                    : $"attachments/{attachRel}";
                return FormatPath(parentDir, attachSub);
            }

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

    private static string JoinMemoryPath(string baseDir, string relative, char separator)
    {
        var cleanBase = baseDir.TrimEnd('/', '\\');
        return string.IsNullOrEmpty(relative)
            ? cleanBase
            : $"{cleanBase}{separator}{relative.Replace('/', separator)}";
    }

    /// <summary>
    /// Detects <c>/workspace/memory[/...]</c> and returns the traversal-checked relative remainder.
    /// </summary>
    private static bool TryGetLocalMemoryRelativePath(string? path, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/').TrimStart('/');
        var memoryPrefix = $"workspace/{AgentMemoryConstants.LocalMemoryDirectoryName}";
        if (!normalized.Equals(memoryPrefix, StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(memoryPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stack = new List<string>();
        foreach (var segment in normalized[memoryPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied: Path '{path}' resolves outside the allowed workspace sandbox.");
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(segment);
        }

        relative = string.Join('/', stack);
        return true;
    }

    /// <summary>
    /// Checks if the given path targets the virtual /tmp directory without escaping via '..' traversal,
    /// and outputs the normalized relative path within /tmp (empty string if targeting /tmp itself).
    /// </summary>
    public static bool TryResolveTmpSubPath(string? path, out string subPath)
    {
        subPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.Equals("/tmp", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("/tmp/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = normalized.Length > 4 ? normalized[4..].TrimStart('/') : string.Empty;
        if (string.IsNullOrEmpty(remainder))
        {
            return true;
        }

        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (stack.Count == 0)
                {
                    return false;
                }
                stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(seg);
        }

        subPath = string.Join("/", stack);
        return true;
    }
}
