using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Represents a snapshot entry of a single task markdown file in a directory index.
/// </summary>
public sealed class DirectoryTaskEntry
{
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public int? TaskId { get; set; }
    public int? Version { get; set; }
    public required string ContentHash { get; set; }
    public required TaskFrontmatter Frontmatter { get; set; }
    public string? PreferredSlug { get; set; }
    public string Content { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TaskContentHash { get; set; } = string.Empty;
}

/// <summary>
/// In-memory directory index holding parsed task markdown entries for O(1) lookups,
/// duplicate detection, and file hash diffing.
/// </summary>
public sealed class DirectoryTaskIndex
{
    private readonly Dictionary<string, DirectoryTaskEntry> _entriesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DirectoryTaskEntry> _entriesByFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<DirectoryTaskEntry>> _entriesByTaskId = new();

    public string DirectoryPath { get; }

    public DirectoryTaskIndex(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentNullException(nameof(directoryPath));
        }

        DirectoryPath = NormalizePath(directoryPath);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('/', '\\');
        }
        catch (Exception)
        {
            // Justification: Preserve original path string if input contains non-standard path characters in synthetic tests.
            return path.TrimEnd('/', '\\');
        }
    }

    public IReadOnlyCollection<DirectoryTaskEntry> AllEntries => _entriesByPath.Values;

    public IReadOnlyList<DirectoryTaskEntry> GetFilesByTaskId(int taskId)
    {
        if (taskId <= 0) return Array.Empty<DirectoryTaskEntry>();
        return _entriesByTaskId.TryGetValue(taskId, out var list) ? list : Array.Empty<DirectoryTaskEntry>();
    }

    public bool TryGetEntryByFileName(string fileName, [NotNullWhen(true)] out DirectoryTaskEntry? entry)
    {
        return _entriesByFileName.TryGetValue(fileName, out entry);
    }

    public bool TryGetEntryByPath(string filePath, [NotNullWhen(true)] out DirectoryTaskEntry? entry)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            entry = null;
            return false;
        }

        return _entriesByPath.TryGetValue(NormalizePath(filePath), out entry);
    }

    public string? TryGetFileHash(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return _entriesByPath.TryGetValue(NormalizePath(filePath), out var entry) ? entry.ContentHash : null;
    }

    public void UpsertEntry(DirectoryTaskEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var normalizedFilePath = NormalizePath(entry.FilePath);
        entry.FilePath = normalizedFilePath;

        if (_entriesByPath.TryGetValue(normalizedFilePath, out var existing))
        {
            _entriesByFileName.Remove(existing.FileName);
            if (existing.TaskId.HasValue && _entriesByTaskId.TryGetValue(existing.TaskId.Value, out var list))
            {
                list.RemoveAll(e => string.Equals(e.FilePath, existing.FilePath, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) _entriesByTaskId.Remove(existing.TaskId.Value);
            }
        }

        if (_entriesByFileName.TryGetValue(entry.FileName, out var existingFile) &&
            !string.Equals(existingFile.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _entriesByPath.Remove(existingFile.FilePath);
            if (existingFile.TaskId.HasValue && _entriesByTaskId.TryGetValue(existingFile.TaskId.Value, out var list))
            {
                list.RemoveAll(e => string.Equals(e.FilePath, existingFile.FilePath, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) _entriesByTaskId.Remove(existingFile.TaskId.Value);
            }
        }

        _entriesByPath[normalizedFilePath] = entry;
        _entriesByFileName[entry.FileName] = entry;

        if (entry.TaskId.HasValue && entry.TaskId.Value > 0)
        {
            if (!_entriesByTaskId.TryGetValue(entry.TaskId.Value, out var list))
            {
                list = new List<DirectoryTaskEntry>();
                _entriesByTaskId[entry.TaskId.Value] = list;
            }
            list.RemoveAll(e => string.Equals(e.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase));
            list.Add(entry);
        }
    }

    public void RemoveEntry(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        var normalizedFilePath = NormalizePath(filePath);
        if (_entriesByPath.TryGetValue(normalizedFilePath, out var existing))
        {
            _entriesByPath.Remove(normalizedFilePath);
            _entriesByFileName.Remove(existing.FileName);
            if (existing.TaskId.HasValue && _entriesByTaskId.TryGetValue(existing.TaskId.Value, out var list))
            {
                list.RemoveAll(e => string.Equals(e.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) _entriesByTaskId.Remove(existing.TaskId.Value);
            }
        }
    }

    public static async Task<DirectoryTaskIndex> CreateAsync(
        string directoryPath,
        Func<string, string?, (TaskFrontmatter Frontmatter, string Body)> parseMarkdown,
        Func<string, string> computeHash,
        ILogger? logger = null,
        bool isRemoteOrParallel = false,
        bool failFast = true,
        CancellationToken cancellationToken = default)
    {
        var index = new DirectoryTaskIndex(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            return index;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, TaskSyncConstants.MarkdownPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (failFast)
            {
                throw new TaskMarkdownSyncException($"Failed to list files in '{directoryPath}': {ex.Message}", ex);
            }
            logger?.LogDebug(ex, "Failed to list files in directory '{DirectoryPath}'", directoryPath);
            return index;
        }

        if (files.Length == 0)
        {
            return index;
        }

        bool isRemote = isRemoteOrParallel ||
            directoryPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
            directoryPath.StartsWith("//", StringComparison.OrdinalIgnoreCase);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = isRemote ? 16 : 8,
            CancellationToken = cancellationToken
        };

        var entries = new ConcurrentBag<DirectoryTaskEntry>();

        await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
        {
            try
            {
                string content;
                try
                {
                    content = await File.ReadAllTextAsync(file, ct);
                }
                catch (IOException ex)
                {
                    if (failFast)
                    {
                        throw new TaskMarkdownSyncException($"Failed to read task markdown file '{file}': {ex.Message}", ex, file);
                    }
                    logger?.LogDebug(ex, "Ignored unreadable file '{FilePath}' in '{DirectoryPath}'", file, directoryPath);
                    return;
                }
                catch (Exception ex)
                {
                    if (failFast)
                    {
                        throw new TaskMarkdownSyncException($"Failed to read task markdown file '{file}': {ex.Message}", ex, file);
                    }
                    logger?.LogDebug(ex, "Ignored error reading file '{FilePath}' in '{DirectoryPath}'", file, directoryPath);
                    return;
                }

                var (frontmatter, body) = parseMarkdown(content, file);
                var hash = computeHash(content);
                var description = TaskMarkdownSyncService.ExtractDescriptionFromBody(body);
                var taskContentHash = TaskMarkdownSyncService.ComputeTaskContentHash(frontmatter, description);
                var fileName = Path.GetFileName(file);
                var slug = !string.IsNullOrWhiteSpace(frontmatter.Title) ? TaskMarkdownSyncService.GenerateSlug(frontmatter.Title) : null;

                entries.Add(new DirectoryTaskEntry
                {
                    FilePath = file,
                    FileName = fileName,
                    TaskId = frontmatter.Id > 0 ? frontmatter.Id : null,
                    Version = frontmatter.Version,
                    ContentHash = hash,
                    Frontmatter = frontmatter,
                    PreferredSlug = slug,
                    Content = content,
                    Body = body,
                    Description = description,
                    TaskContentHash = taskContentHash
                });
            }
            catch (TaskMarkdownSyncException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (failFast)
                {
                    throw;
                }
                logger?.LogDebug(ex, "Ignored error parsing file '{FilePath}' in '{DirectoryPath}'", file, directoryPath);
            }
        });

        foreach (var entry in entries.OrderBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            index.UpsertEntry(entry);
        }

        return index;
    }
}
