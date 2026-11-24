using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Web.Services;

/// <summary>Raised when a memory UI operation violates a rule (name, extension, size, symlink, read-only scope...).</summary>
public class MemoryValidationException : InvalidOperationException
{
    public MemoryValidationException(string message) : base(message) { }
}

/// <summary>Raised when the workspace targeted by a memory UI operation does not exist.</summary>
public class MemoryWorkspaceNotFoundException : InvalidOperationException
{
    public MemoryWorkspaceNotFoundException(int workspaceId)
        : base($"Workspace {workspaceId} was not found.")
    {
        WorkspaceId = workspaceId;
    }

    public int WorkspaceId { get; }
}

/// <summary>Raised when a memory file does not exist.</summary>
public class MemoryFileNotFoundException : InvalidOperationException
{
    public MemoryFileNotFoundException(string relativePath)
        : base($"Memory file '{relativePath}' was not found.")
    {
    }
}

public sealed record MemoryFileEntry(
    AgentMemoryScope Scope,
    string RelativePath,
    long SizeBytes,
    int LineCount,
    DateTime ModifiedAtUtc,
    string Summary);

public sealed record MemoryFileContent(MemoryFileEntry Entry, string Content);

public interface IWorkspaceMemoryUiService
{
    Task<IReadOnlyList<MemoryFileEntry>> ListAsync(int workspaceId, AgentMemoryScope scope, CancellationToken cancellationToken = default);

    Task<MemoryFileContent> ReadAsync(int workspaceId, AgentMemoryScope scope, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Creates or atomically replaces a local memory file. Returns whether it was created or updated.</summary>
    Task<AgentMemoryChangeKind> SaveLocalAsync(int workspaceId, string fileName, string content, CancellationToken cancellationToken = default);

    Task DeleteLocalAsync(int workspaceId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Validates a local memory file name (flat, markdown-only). Throws <see cref="MemoryValidationException"/>.</summary>
    void ValidateLocalFileName(string? fileName);
}

/// <summary>
/// File listing, reading and atomic CRUD for the Agent Memory page. Local memory is read-write;
/// committed repository memory (read from <see cref="Workspace.RepositoryPath"/>) is read-only.
/// </summary>
public class WorkspaceMemoryUiService : IWorkspaceMemoryUiService
{
    private const int ReadBufferSize = 4096;
    private static readonly Regex FileNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IIdeationConversationCoordinator _ideationCoordinator;
    private readonly IAgentMemoryMonitorService _memoryMonitor;
    private readonly ILogger<WorkspaceMemoryUiService> _logger;

    public WorkspaceMemoryUiService(
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IIdeationConversationCoordinator ideationCoordinator,
        IAgentMemoryMonitorService memoryMonitor,
        ILogger<WorkspaceMemoryUiService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _ideationCoordinator = ideationCoordinator ?? throw new ArgumentNullException(nameof(ideationCoordinator));
        _memoryMonitor = memoryMonitor ?? throw new ArgumentNullException(nameof(memoryMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<MemoryFileEntry>> ListAsync(int workspaceId, AgentMemoryScope scope, CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceAsync(workspaceId, cancellationToken);
        var entries = new List<MemoryFileEntry>();

        switch (scope)
        {
            case AgentMemoryScope.LocalWorkspace:
            {
                var directory = GetLocalDirectory(workspaceId);
                RejectReparsePoint(directory);
                if (!Directory.Exists(directory))
                {
                    return entries;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*" + AgentMemoryConstants.MarkdownExtension))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith('.') || AgentMemoryReparseGuard.IsReparsePoint(file))
                    {
                        continue;
                    }

                    var entry = await TryBuildEntryAsync(scope, name, file, cancellationToken);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }

                break;
            }
            case AgentMemoryScope.SharedRepository:
            {
                var root = workspace.RepositoryPath;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return entries;
                }

                foreach (var rootFile in new[] { AgentMemoryConstants.RootRulesFileName, AgentMemoryConstants.AgentsRulesFilePath })
                {
                    var full = Path.Combine(root, rootFile);
                    if (File.Exists(full) && !AgentMemoryReparseGuard.HasReparsePointInChain(root, full))
                    {
                        var entry = await TryBuildEntryAsync(scope, rootFile, full, cancellationToken);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                }

                var memoryDirectory = Path.Combine(root, AgentMemoryConstants.SharedMemoryDirectory);
                if (Directory.Exists(memoryDirectory) && !AgentMemoryReparseGuard.HasReparsePointInChain(root, memoryDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(memoryDirectory, "*" + AgentMemoryConstants.MarkdownExtension))
                    {
                        var name = Path.GetFileName(file);
                        if (name.StartsWith('.') || AgentMemoryReparseGuard.IsReparsePoint(file))
                        {
                            continue;
                        }

                        var entry = await TryBuildEntryAsync(scope, $"{AgentMemoryConstants.SharedMemoryDirectory}/{name}", file, cancellationToken);
                        if (entry != null)
                        {
                            entries.Add(entry);
                        }
                    }
                }

                break;
            }
            default:
                throw new MemoryValidationException($"Memory scope '{scope}' cannot be listed.");
        }

        return entries
            .OrderByDescending(e => MemoryPresentationHelper.IsPinnedMemoryFile(e.RelativePath))
            .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MemoryFileContent> ReadAsync(int workspaceId, AgentMemoryScope scope, string relativePath, CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceAsync(workspaceId, cancellationToken);
        string full;
        switch (scope)
        {
            case AgentMemoryScope.LocalWorkspace:
                ValidateExistingLocalFileName(relativePath);
                var localDirectory = GetLocalDirectory(workspaceId);
                RejectReparsePoint(localDirectory);
                full = Path.Combine(localDirectory, relativePath);
                if (AgentMemoryReparseGuard.IsReparsePoint(full))
                {
                    throw new MemoryValidationException($"'{relativePath}' is a symbolic link and cannot be accessed.");
                }
                break;
            case AgentMemoryScope.SharedRepository:
                full = ResolveSharedPath(workspace, relativePath);
                break;
            default:
                throw new MemoryValidationException($"Memory scope '{scope}' cannot be read.");
        }

        if (!File.Exists(full))
        {
            throw new MemoryFileNotFoundException(relativePath);
        }

        var content = await ReadBoundedAsync(full, relativePath, cancellationToken);
        var entry = new MemoryFileEntry(
            scope,
            relativePath,
            new FileInfo(full).Length,
            CountLines(content),
            File.GetLastWriteTimeUtc(full),
            MemoryPresentationHelper.ExtractSummary(content));
        return new MemoryFileContent(entry, content);
    }

    public async Task<AgentMemoryChangeKind> SaveLocalAsync(int workspaceId, string fileName, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateLocalFileName(fileName);
        var bytes = new UTF8Encoding(false).GetBytes(content);
        if (bytes.Length > MemoryUiConstants.MaxMemoryFileBytes)
        {
            throw new MemoryValidationException($"Memory file exceeds the maximum size of {MemoryUiConstants.MaxMemoryFileBytes} bytes.");
        }

        await GetWorkspaceAsync(workspaceId, cancellationToken);
        var directory = GetLocalDirectory(workspaceId);
        var target = Path.Combine(directory, fileName);

        using var memoryLock = await AgentMemoryFileWriter.AcquireLockAsync(workspaceId, cancellationToken);
        RejectReparsePoint(directory);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            RejectReparsePoint(directory);
        }

        if (AgentMemoryReparseGuard.IsReparsePoint(target))
        {
            throw new MemoryValidationException($"'{fileName}' is a symbolic link and cannot be overwritten.");
        }

        var created = await AgentMemoryFileWriter.WriteAtomicAsync(target, bytes, cancellationToken);

        _memoryMonitor.RecordWrite(
            workspaceId,
            AgentMemoryScope.LocalWorkspace,
            ToLocalMemoryLedgerPath(fileName),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
        _logger.LogInformation("Workspace {WorkspaceId} local memory file {FileName} {Kind} from UI.", workspaceId, fileName, created ? "created" : "updated");
        return created ? AgentMemoryChangeKind.Created : AgentMemoryChangeKind.Updated;
    }

    public async Task DeleteLocalAsync(int workspaceId, string fileName, CancellationToken cancellationToken = default)
    {
        ValidateExistingLocalFileName(fileName);
        await GetWorkspaceAsync(workspaceId, cancellationToken);
        var directory = GetLocalDirectory(workspaceId);
        var target = Path.Combine(directory, fileName);

        using var memoryLock = await AgentMemoryFileWriter.AcquireLockAsync(workspaceId, cancellationToken);
        RejectReparsePoint(directory);
        if (AgentMemoryReparseGuard.IsReparsePoint(target))
        {
            throw new MemoryValidationException($"'{fileName}' is a symbolic link and cannot be deleted.");
        }

        if (!File.Exists(target))
        {
            throw new MemoryFileNotFoundException(fileName);
        }

        File.Delete(target);
        _memoryMonitor.RecordWrite(workspaceId, AgentMemoryScope.LocalWorkspace, ToLocalMemoryLedgerPath(fileName), null);
        _ideationCoordinator.MarkWorkspaceContextDirty(workspaceId);
        _logger.LogInformation("Workspace {WorkspaceId} local memory file {FileName} deleted from UI.", workspaceId, fileName);
    }

    public void ValidateLocalFileName(string? fileName)
    {
        ValidateCommonLocalFileNameRules(fileName);
        if (!FileNamePattern.IsMatch(fileName!))
        {
            throw new MemoryValidationException("Memory file names must be flat and may only contain letters, digits, '.', '_' and '-'.");
        }
    }

    private static void ValidateExistingLocalFileName(string? fileName)
    {
        ValidateCommonLocalFileNameRules(fileName);
        if (fileName!.StartsWith('.') ||
            fileName.Contains('/') ||
            fileName.Contains('\\') ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new MemoryValidationException("Invalid local memory file name.");
        }
    }

    private static void ValidateCommonLocalFileNameRules(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new MemoryValidationException("Memory file name is required.");
        }

        if (fileName.Length > MemoryUiConstants.MaxFileNameLength)
        {
            throw new MemoryValidationException($"Memory file name exceeds {MemoryUiConstants.MaxFileNameLength} characters.");
        }

        if (!fileName.EndsWith(AgentMemoryConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length <= AgentMemoryConstants.MarkdownExtension.Length)
        {
            throw new MemoryValidationException($"Memory files must be markdown ({AgentMemoryConstants.MarkdownExtension}).");
        }
    }

    private async Task<Workspace> GetWorkspaceAsync(int workspaceId, CancellationToken cancellationToken) =>
        await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken)
        ?? throw new MemoryWorkspaceNotFoundException(workspaceId);

    private string GetLocalDirectory(int workspaceId) =>
        _pathResolver.ToHostPath(_pathResolver.GetWorkspaceMemoryPath(workspaceId));

    private static string ToLocalMemoryLedgerPath(string fileName) =>
        $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{fileName}";

    private static string ResolveSharedPath(Workspace workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            throw new MemoryValidationException("Workspace repository path is not configured.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MemoryValidationException("Memory file path is required.");
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Contains("..", StringComparison.Ordinal) ||
            normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            throw new MemoryValidationException($"'{relativePath}' is not a committed memory file.");
        }

        if (!AgentMemoryPathClassifier.TryGetCanonicalSharedRelativePath(normalized, repositoryRoot: null, out var canonical) ||
            !string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new MemoryValidationException($"'{relativePath}' is not a committed memory file.");
        }

        var full = Path.Combine(workspace.RepositoryPath, canonical);
        if (AgentMemoryReparseGuard.HasReparsePointInChain(workspace.RepositoryPath, full))
        {
            throw new MemoryValidationException($"'{relativePath}' traverses a symbolic link and cannot be accessed.");
        }

        return full;
    }

    private async Task<MemoryFileEntry?> TryBuildEntryAsync(AgentMemoryScope scope, string relativePath, string fullPath, CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return null;
            }

            if (fileInfo.Length > MemoryUiConstants.MaxMemoryFileBytes)
            {
                return new MemoryFileEntry(
                    scope,
                    relativePath,
                    fileInfo.Length,
                    0,
                    fileInfo.LastWriteTimeUtc,
                    MemoryUiConstants.OversizedFileSummary);
            }

            var content = await ReadBoundedAsync(fullPath, relativePath, cancellationToken);
            return new MemoryFileEntry(
                scope,
                relativePath,
                fileInfo.Length,
                CountLines(content),
                fileInfo.LastWriteTimeUtc,
                MemoryPresentationHelper.ExtractSummary(content));
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogDebug(ex, "Memory file '{Path}' disappeared during listing.", fullPath);
            return null;
        }
    }

    private static async Task<string> ReadBoundedAsync(string fullPath, string relativePath, CancellationToken cancellationToken)
    {
        if (new FileInfo(fullPath).Length > MemoryUiConstants.MaxMemoryFileBytes)
        {
            throw new MemoryValidationException($"'{relativePath}' exceeds the maximum viewable size of {MemoryUiConstants.MaxMemoryFileBytes} bytes.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            useAsync: true);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static int CountLines(string content) => MemoryPresentationHelper.ComputeBudget(content).Lines;

    private static void RejectReparsePoint(string directory)
    {
        if (AgentMemoryReparseGuard.IsReparsePoint(directory))
        {
            throw new MemoryValidationException("The memory directory is a symbolic link and cannot be used.");
        }
    }
}
