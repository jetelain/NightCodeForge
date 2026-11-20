using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Memory;

/// <summary>Maps requested local memory paths to host files of the sibling <c>memory/</c> directory, enforcing the security boundary.</summary>
public static class AgentMemoryPathResolution
{
    private const string HostMarker = ".nightcodeforge/wks/";
    private const string VirtualPrefix = AgentMemoryConstants.VirtualLocalMemoryPath + "/";

    /// <summary>True when <paramref name="requestedPath"/> addresses <c>/workspace/memory</c> (or its host form).</summary>
    public static bool IsLocalMemoryPath(string? requestedPath, out string relativeName)
    {
        relativeName = string.Empty;
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return false;
        }

        var p = requestedPath.Trim().Replace('\\', '/');
        if (string.Equals(p, AgentMemoryConstants.VirtualLocalMemoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (p.StartsWith(VirtualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            relativeName = p[VirtualPrefix.Length..];
            return true;
        }

        var idx = p.IndexOf(HostMarker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = p[(idx + HostMarker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length >= 2 && int.TryParse(rest[0], out _) &&
                string.Equals(rest[1], AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                relativeName = string.Join('/', rest.Skip(2));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a local memory request to a host path under <paramref name="memoryDirectory"/>.
    /// Returns null when the path is not a local memory path. Throws <see cref="UnauthorizedAccessException"/> for
    /// nested paths, non-markdown or hidden files, other workspaces' memory and symlink / reparse point traversal.
    /// </summary>
    public static string? ResolveLocalMemoryFile(string? requestedPath, int workspaceId, string memoryDirectory)
    {
        if (!IsLocalMemoryPath(requestedPath, out var name))
        {
            return null;
        }

        if (workspaceId <= 0)
        {
            throw new UnauthorizedAccessException($"Access denied: Local memory path '{requestedPath}' requires a workspace.");
        }

        var normalized = requestedPath!.Trim().Replace('\\', '/');
        var markerIdx = normalized.IndexOf(HostMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            var idSegment = normalized[(markerIdx + HostMarker.Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            if (!int.TryParse(idSegment, out var pathWorkspaceId) || pathWorkspaceId != workspaceId)
            {
                throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' targets the memory of another workspace.");
            }
        }

        var fullMemoryDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(memoryDirectory));
        var wksRoot = Path.GetDirectoryName(fullMemoryDir)
            ?? throw new UnauthorizedAccessException($"Access denied: Memory directory '{memoryDirectory}' has no workspace root.");

        if (string.IsNullOrEmpty(name))
        {
            if (AgentMemoryFileSystem.HasReparsePointInChain(wksRoot, fullMemoryDir))
            {
                throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' traverses a symbolic link or reparse point inside agent memory.");
            }

            return memoryDirectory;
        }

        if (name.Contains('/') || name.StartsWith('.') ||
            !name.EndsWith(AgentMemoryConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase) ||
            name.Length <= AgentMemoryConstants.MarkdownExtension.Length)
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Path '{requestedPath}' is not a flat markdown note. Only '/workspace/memory/*.md' is available.");
        }

        var hostFile = Path.Combine(fullMemoryDir, name);
        if (AgentMemoryFileSystem.HasReparsePointInChain(wksRoot, hostFile))
        {
            throw new UnauthorizedAccessException($"Access denied: Path '{requestedPath}' traverses a symbolic link or reparse point inside agent memory.");
        }

        return hostFile;
    }
}
