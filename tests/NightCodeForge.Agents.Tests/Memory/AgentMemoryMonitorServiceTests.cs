using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Tests.Memory;

public sealed class AgentMemoryMonitorServiceTests : IDisposable
{
    private const int WorkspaceId = 7;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-mem-" + Guid.NewGuid().ToString("N"));
    private readonly string _memoryDir;
    private readonly string _repoDir;
    private readonly AgentMemoryMonitorService _monitor;

    public AgentMemoryMonitorServiceTests()
    {
        _memoryDir = Path.Combine(_root, "wks", WorkspaceId.ToString(), "memory");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_memoryDir);
        Directory.CreateDirectory(Path.Combine(_repoDir, ".agents", "memory"));

        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.GetWorkspaceMemoryPath(WorkspaceId)).Returns(_memoryDir);
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        _monitor = new AgentMemoryMonitorService(resolver.Object, TimeProvider.System, NullLogger<AgentMemoryMonitorService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task<AgentMemorySnapshot> SnapshotAsync() => _monitor.CaptureSnapshotAsync(WorkspaceId, _repoDir, CancellationToken.None);

    [Fact]
    public async Task CaptureSnapshot_ListsLocalAndSharedMemoryOnly()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "pinned");
        File.WriteAllText(Path.Combine(_memoryDir, "topic.md"), "topic");
        File.WriteAllText(Path.Combine(_memoryDir, "script.sh"), "ignored");
        File.WriteAllText(Path.Combine(_memoryDir, ".hidden.md"), "ignored");
        File.WriteAllText(Path.Combine(_repoDir, "AGENTS.md"), "rules");
        File.WriteAllText(Path.Combine(_repoDir, ".agents", "memory", "arch.md"), "arch");
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "ignored");

        var snapshot = await SnapshotAsync();

        var keys = snapshot.Files.Keys.Select(k => (k.Scope, k.RelativePath)).ToHashSet();
        Assert.Equal(4, keys.Count);
        Assert.Contains((AgentMemoryScope.LocalWorkspace, "/workspace/memory/MEMORY.md"), keys);
        Assert.Contains((AgentMemoryScope.LocalWorkspace, "/workspace/memory/topic.md"), keys);
        Assert.Contains((AgentMemoryScope.SharedRepository, "AGENTS.md"), keys);
        Assert.Contains((AgentMemoryScope.SharedRepository, ".agents/memory/arch.md"), keys);
    }

    [Fact]
    public async Task Diff_ReportsCreatedUpdatedAndDeleted()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "keep.md"), "v1");
        File.WriteAllText(Path.Combine(_memoryDir, "gone.md"), "v1");
        var before = await SnapshotAsync();

        File.WriteAllText(Path.Combine(_memoryDir, "keep.md"), "v2");
        File.Delete(Path.Combine(_memoryDir, "gone.md"));
        File.WriteAllText(Path.Combine(_memoryDir, "new.md"), "v1");
        var changes = _monitor.Diff(before, await SnapshotAsync());

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.RelativePath.EndsWith("keep.md") && c.Kind == AgentMemoryChangeKind.Updated && c.Sha256 != null);
        Assert.Contains(changes, c => c.RelativePath.EndsWith("gone.md") && c.Kind == AgentMemoryChangeKind.Deleted && c.Sha256 == null);
        Assert.Contains(changes, c => c.RelativePath.EndsWith("new.md") && c.Kind == AgentMemoryChangeKind.Created);
    }

    [Fact]
    public async Task Diff_ExcludesWritesAlreadyAttributedInLedger_SoConcurrentTaskIsNotBlamed()
    {
        // Task #42 snapshots, then Ideation (or the UI drawer) writes a file and records it in the ledger.
        var before = await SnapshotAsync();
        var file = Path.Combine(_memoryDir, "ideation-vision.md");
        File.WriteAllText(file, "vision");
        var sha = await AgentMemoryFileSystem.ComputeSha256Async(file, CancellationToken.None);
        Assert.True(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/ideation-vision.md", sha));

        var changes = _monitor.Diff(before, await SnapshotAsync());

        Assert.Empty(changes);
    }

    [Fact]
    public async Task Diff_StillReportsWhenFileChangedAfterLedgerEntry()
    {
        var before = await SnapshotAsync();
        var file = Path.Combine(_memoryDir, "topic.md");
        File.WriteAllText(file, "first");
        var sha = await AgentMemoryFileSystem.ComputeSha256Async(file, CancellationToken.None);
        _monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/topic.md", sha);
        File.WriteAllText(file, "second written by bash");

        var changes = _monitor.Diff(before, await SnapshotAsync());

        Assert.Single(changes);
    }

    [Fact]
    public void RecordWrite_SuppressesDuplicateOfSameHashOnly()
    {
        Assert.True(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", "h1"));
        Assert.False(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", "h1"));
        Assert.True(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", "h2"));
        Assert.True(_monitor.RecordWrite(WorkspaceId + 1, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", "h2"));
    }

    [Fact]
    public void RecordWrite_RejectsNoneScope()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _monitor.RecordWrite(WorkspaceId, AgentMemoryScope.None, "x.md", "h"));
    }

    [Fact]
    public async Task RecordWrite_IsAtomicAcrossConcurrentCallers()
    {
        var winners = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            if (_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/race.md", "same"))
            {
                Interlocked.Increment(ref winners);
            }
        })));

        Assert.Equal(1, winners);
    }

    [Fact]
    public async Task CaptureSnapshot_SkipsSymlinkedFiles()
    {
        var secret = Path.Combine(_root, "secret.md");
        File.WriteAllText(secret, "secret");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_memoryDir, "link.md"), secret);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var snapshot = await SnapshotAsync();

        Assert.DoesNotContain(snapshot.Files.Keys, k => k.RelativePath.EndsWith("link.md"));
    }

    [Fact]
    public async Task Ledger_IsCaseInsensitiveOnRelativePath()
    {
        var memFile = Path.Combine(_memoryDir, "MEMORY.md");
        var agentsFile = Path.Combine(_repoDir, "AGENTS.md");
        File.WriteAllText(memFile, "v1");
        File.WriteAllText(agentsFile, "rules");
        var memSha = await AgentMemoryFileSystem.ComputeSha256Async(memFile, CancellationToken.None);

        var before = await _monitor.CaptureSnapshotAsync(WorkspaceId, _repoDir, CancellationToken.None);
        File.WriteAllText(memFile, "v2");
        var newMemSha = await AgentMemoryFileSystem.ComputeSha256Async(memFile, CancellationToken.None);

        Assert.True(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/memory.md", newMemSha));
        Assert.False(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/MEMORY.md", newMemSha));

        Assert.True(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.SharedRepository, "agents.md", "same-sha"));
        Assert.False(_monitor.RecordWrite(WorkspaceId, AgentMemoryScope.SharedRepository, "AGENTS.md", "same-sha"));

        var diff = _monitor.Diff(before, await _monitor.CaptureSnapshotAsync(WorkspaceId, _repoDir, CancellationToken.None));
        Assert.Empty(diff);
        Assert.NotEqual(memSha, newMemSha);
    }

    [Fact]
    public void HasReparsePointInChain_ReturnsTrueForDanglingSymlink()
    {
        var wksRoot = Path.GetDirectoryName(_memoryDir)!;
        var dangling = Path.Combine(_memoryDir, "dangling.md");
        try
        {
            File.CreateSymbolicLink(dangling, Path.Combine(_root, "does-not-exist.md"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        Assert.True(AgentMemoryFileSystem.HasReparsePointInChain(wksRoot, dangling));
    }
}
