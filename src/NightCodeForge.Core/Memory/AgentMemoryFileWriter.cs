using System.Collections.Concurrent;

namespace NightCodeForge.Core.Memory;

/// <summary>
/// Describes a successful host-side write to an agent memory file. Raised by host write channels
/// (Crucible <c>write</c>/<c>edit</c> tools, ACP host RPC, UI drawer) so notification / monitoring hooks can react.
/// </summary>
/// <param name="RequestedPath">The path as requested by the caller (e.g. <c>/workspace/memory/x.md</c>).</param>
/// <param name="HostFilePath">The resolved host file path that was written.</param>
/// <param name="Scope">The memory tier of the written file.</param>
/// <param name="Kind">Whether the file was created or updated.</param>
/// <param name="ContentSha256">Optional precomputed lowercase hex SHA-256 of the bytes written under lock.</param>
public sealed record AgentMemoryWriteEvent(
    string RequestedPath,
    string HostFilePath,
    AgentMemoryScope Scope,
    AgentMemoryChangeKind Kind,
    string? ContentSha256 = null);

/// <summary>
/// Per-workspace write coordination and atomic temp-file-and-replace writes for agent memory files,
/// so concurrent Stage 1–3 tasks, Ideation and the UI drawer never suffer sharing violations or lost edits.
/// </summary>
public static class AgentMemoryFileWriter
{
    private const int MaxReplaceAttempts = 5;
    private const int ReplaceRetryDelayMilliseconds = 50;
    private const string TempFilePrefix = ".";
    private const string TempFileSuffix = ".tmp";

    private static readonly ConcurrentDictionary<int, SemaphoreSlim> WorkspaceLocks = new();

    /// <summary>
    /// Acquires the per-workspace memory write lock. Dispose the returned handle to release it.
    /// The lock is not re-entrant: use <see cref="WriteAtomicAsync"/> (not <see cref="WriteLockedAsync"/>) while holding it.
    /// </summary>
    public static async Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var semaphore = WorkspaceLocks.GetOrAdd(workspaceId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    /// <summary>Acquires the workspace lock, then performs <see cref="WriteAtomicAsync"/>. Returns true when the file was created.</summary>
    public static async Task<bool> WriteLockedAsync(
        int workspaceId,
        string hostFilePath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        using var _ = await AcquireLockAsync(workspaceId, cancellationToken);
        return await WriteAtomicAsync(hostFilePath, content, cancellationToken);
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a hidden temp file next to the target, then atomically replaces the target.
    /// Callers must hold the workspace lock when read-modify-write consistency matters. Returns true when the file was created.
    /// </summary>
    public static async Task<bool> WriteAtomicAsync(string hostFilePath, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFilePath);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(hostFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var existed = File.Exists(hostFilePath);
        var tempPath = Path.Combine(
            directory ?? string.Empty,
            $"{TempFilePrefix}{Path.GetFileName(hostFilePath)}.{Guid.NewGuid():N}{TempFileSuffix}");

        try
        {
            await File.WriteAllBytesAsync(tempPath, content, cancellationToken);

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, hostFilePath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < MaxReplaceAttempts)
                {
                    // Transient sharing violation (Windows / 9P): another reader holds the target; retry shortly.
                    await Task.Delay(ReplaceRetryDelayMilliseconds, cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return !existed;
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
