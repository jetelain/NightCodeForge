using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Resolves workspace attachment file paths across host OSes and WSL2/Unix agent environments.
/// Ensures symlinked attachments are read directly from their host target path on Windows rather than
/// traversing POSIX symlinks over <c>\\wsl.localhost</c>.
/// </summary>
public static class AttachmentHostPathResolver
{
    public static bool TryConvertMntDriveToWindowsPath(string? posixPath, out string windowsPath)
    {
        windowsPath = string.Empty;
        if (string.IsNullOrWhiteSpace(posixPath))
        {
            return false;
        }

        var normalized = posixPath.Trim().Replace('\\', '/');
        if (normalized.Length >= 6 &&
            normalized.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
            char.IsAsciiLetter(normalized[5]) &&
            (normalized.Length == 6 || normalized[6] == '/'))
        {
            var drive = char.ToUpperInvariant(normalized[5]);
            var rest = normalized.Length > 7 ? normalized[7..].Replace('/', '\\') : string.Empty;
            windowsPath = $"{drive}:\\{rest}";
            return true;
        }

        return false;
    }

    public static string ExpandLeadingTilde(string path, IWorkspacePathResolver? pathResolver)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)))
        {
            return path;
        }

        var relativePart = path.Length > 1
            ? path[1..].TrimStart('/', '\\').Replace('\\', '/')
            : string.Empty;

        var baseDirectory = ResolveHomeBaseDirectory(pathResolver);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return path;
        }

        return string.IsNullOrEmpty(relativePart)
            ? baseDirectory
            : $"{baseDirectory}/{relativePart}";
    }

    public static string? ResolveHostReadableSourceRef(string? sourceRef, IWorkspacePathResolver? pathResolver)
    {
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return null;
        }

        var expanded = ExpandLeadingTilde(sourceRef.Trim(), pathResolver);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (TryConvertMntDriveToWindowsPath(expanded, out var windowsDrivePath))
            {
                return windowsDrivePath;
            }

            if (IsWindowsDrivePath(expanded) ||
                expanded.StartsWith("//", StringComparison.Ordinal) ||
                expanded.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return expanded.Replace('/', '\\');
            }

            var mappedOnWindows = pathResolver?.ToHostPath(expanded);
            return !string.IsNullOrWhiteSpace(mappedOnWindows) ? mappedOnWindows : expanded;
        }

        if (File.Exists(expanded))
        {
            return expanded;
        }

        var mapped = pathResolver?.ToHostPath(expanded);
        return !string.IsNullOrWhiteSpace(mapped) && File.Exists(mapped) ? mapped : expanded;
    }

    public static string ResolveHostAttachmentsDirectory(int workspaceId, IWorkspacePathResolver pathResolver)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);

        var rawAttachmentsDir = pathResolver.GetAttachmentsPath(workspaceId);
        if (string.IsNullOrWhiteSpace(rawAttachmentsDir))
        {
            return string.Empty;
        }

        var expanded = ExpandLeadingTilde(rawAttachmentsDir, pathResolver);
        var hostPath = pathResolver.ToHostPath(expanded);
        return string.IsNullOrWhiteSpace(hostPath) ? expanded : hostPath;
    }

    public static IReadOnlyList<string> EnumerateReadableAttachmentPaths(
        TaskAttachment attachment,
        string? hostAttachmentsDir,
        IWorkspacePathResolver? pathResolver,
        bool readPrimaryFile = false)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        var targetRelPath = readPrimaryFile || string.IsNullOrWhiteSpace(attachment.MarkdownPath)
            ? attachment.RelativePath
            : attachment.MarkdownPath;

        var containedCandidate = ResolveContainedAttachmentPath(hostAttachmentsDir, targetRelPath);
        var isReadingSymlinkEntry = attachment.IsSymlink &&
            (readPrimaryFile ||
             attachment.ContentKind is TaskAttachmentContentKind.PlainText or TaskAttachmentContentKind.Image ||
             string.Equals(
                 (targetRelPath ?? string.Empty).Replace('\\', '/'),
                 (attachment.RelativePath ?? string.Empty).Replace('\\', '/'),
                 StringComparison.OrdinalIgnoreCase));

        var candidates = new List<string>(2);
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        void AddIfReadable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            if (!candidates.Any(existing => string.Equals(existing, path, comparison)))
            {
                candidates.Add(path);
            }
        }

        if (isReadingSymlinkEntry)
        {
            if (!string.IsNullOrWhiteSpace(attachment.SourceRef))
            {
                AddIfReadable(ResolveHostReadableSourceRef(attachment.SourceRef, pathResolver));
            }
            else
            {
                AddIfReadable(containedCandidate);
            }
        }
        else
        {
            AddIfReadable(containedCandidate);
        }

        return candidates;
    }

    public static string? TryResolveManifestSymlinkHostTarget(
        string attachmentsRoot,
        string relativeAttachmentPath,
        IWorkspacePathResolver? pathResolver,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(attachmentsRoot) || string.IsNullOrWhiteSpace(relativeAttachmentPath))
        {
            return null;
        }

        var manifestPath = Path.Combine(attachmentsRoot, AttachmentConstants.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<AttachmentManifest>(json);
            if (manifest?.Attachments == null)
            {
                return null;
            }

            var normRel = NormalizeSlash(relativeAttachmentPath);
            var entry = manifest.Attachments.FirstOrDefault(a =>
                string.Equals(NormalizeSlash(a.RelativePath), normRel, StringComparison.OrdinalIgnoreCase));
            if (entry == null || !entry.IsSymlink || string.IsNullOrWhiteSpace(entry.SourceRef))
            {
                return null;
            }

            return ResolveHostReadableSourceRef(entry.SourceRef, pathResolver);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Return null so callers fall back to standard attachment file lookup if the manifest is unreadable or corrupt.
            logger?.LogWarning(
                ex,
                "Failed to read or parse attachment manifest '{ManifestPath}' while resolving symlink target for '{RelativePath}'.",
                manifestPath,
                relativeAttachmentPath);
            return null;
        }
    }

    private static string? ResolveContainedAttachmentPath(string? hostAttachmentsDir, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(hostAttachmentsDir) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var fullBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostAttachmentsDir))
            + Path.DirectorySeparatorChar;
        var cleanRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(Path.Combine(hostAttachmentsDir, cleanRel));
        return fullCandidate.StartsWith(fullBase, comparison) ? fullCandidate : null;
    }

    private static string? ResolveHomeBaseDirectory(IWorkspacePathResolver? pathResolver)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            pathResolver?.GetWslUserName() is { Length: > 0 } wslUser &&
            !string.IsNullOrWhiteSpace(wslUser))
        {
            return $"/home/{wslUser}";
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : userProfile.Replace('\\', '/').TrimEnd('/');
    }

    public static bool IsWindowsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    private static string NormalizeSlash(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
}
