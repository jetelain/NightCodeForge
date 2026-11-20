using System.Security.Cryptography;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Memory;

/// <summary>Symlink-safe file helpers shared by agent memory ingestion, monitoring and notification.</summary>
public static class AgentMemoryFileSystem
{
    private const int HashBufferSize = 4096;

    /// <summary>
    /// True when <paramref name="path"/> or any directory between <paramref name="root"/> (exclusive) and the path
    /// (inclusive) is a symbolic link or reparse point. Paths outside <paramref name="root"/> are always rejected.
    /// </summary>
    public static bool HasReparsePointInChain(string root, string path) =>
        AgentMemoryReparseGuard.HasReparsePointInChain(root, path);

    /// <summary>Lists flat non-hidden <c>*.md</c> files of a directory sorted alphabetically (ordinal, case-insensitive).</summary>
    public static IReadOnlyList<string> ListMarkdownFiles(string root, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || HasReparsePointInChain(root, directory))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(directory, "*" + AgentMemoryConstants.MarkdownExtension, SearchOption.TopDirectoryOnly)
            .Where(f => !Path.GetFileName(f).StartsWith('.') && !HasReparsePointInChain(root, f))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            HashBufferSize,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Reads a file allowing concurrent writers (agents and the UI may write memory at any time).</summary>
    public static async Task<string> ReadAllTextSharedAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            HashBufferSize,
            useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>Reads at most <paramref name="maxChars"/> characters of a file while allowing concurrent writers.</summary>
    public static async Task<string> ReadPrefixSharedAsync(string filePath, int maxChars, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            HashBufferSize,
            useAsync: true);
        using var reader = new StreamReader(stream);
        var buffer = new char[maxChars];
        var totalRead = 0;
        while (totalRead < maxChars)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(totalRead, maxChars - totalRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return new string(buffer, 0, totalRead);
    }
}
