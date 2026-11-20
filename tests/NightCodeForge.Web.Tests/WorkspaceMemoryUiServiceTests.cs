using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public sealed class WorkspaceMemoryUiServiceTests : IDisposable
{
    private const int WorkspaceId = 7;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-mem-" + Guid.NewGuid().ToString("N"));
    private readonly string _localDir;
    private readonly string _repoDir;
    private readonly InMemoryWorkspaceRepository _workspaces = new();
    private readonly Mock<IIdeationConversationCoordinator> _coordinator = new();
    private readonly Mock<IAgentMemoryMonitorService> _monitor = new();
    private readonly WorkspaceMemoryUiService _service;

    public WorkspaceMemoryUiServiceTests()
    {
        _localDir = Path.Combine(_root, "wks", "memory");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoDir);
        _workspaces.CreateAsync(new Workspace { Id = WorkspaceId, Name = "ws", RepositoryPath = _repoDir }).GetAwaiter().GetResult();

        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.GetWorkspaceMemoryPath(WorkspaceId)).Returns(_localDir);
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);

        _service = new WorkspaceMemoryUiService(
            _workspaces,
            resolver.Object,
            _coordinator.Object,
            _monitor.Object,
            NullLogger<WorkspaceMemoryUiService>.Instance);
    }

    [Fact]
    public async Task SaveLocal_CreatesThenUpdates_AndMarksContextDirty()
    {
        var created = await _service.SaveLocalAsync(WorkspaceId, "topic.md", "# Hello");
        var updated = await _service.SaveLocalAsync(WorkspaceId, "topic.md", "# Hello 2");

        Assert.Equal(AgentMemoryChangeKind.Created, created);
        Assert.Equal(AgentMemoryChangeKind.Updated, updated);
        Assert.Equal("# Hello 2", await File.ReadAllTextAsync(Path.Combine(_localDir, "topic.md")));
        Assert.Empty(Directory.GetFiles(_localDir, "*.tmp"));
        _coordinator.Verify(c => c.MarkWorkspaceContextDirty(WorkspaceId), Times.Exactly(2));
        _monitor.Verify(m => m.RecordWrite(
            WorkspaceId,
            AgentMemoryScope.LocalWorkspace,
            "/workspace/memory/topic.md",
            It.Is<string?>(h => h != null && h == h.ToLowerInvariant() && h.Length == 64)), Times.Exactly(2));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("# Hello 2")));
        _monitor.Verify(m => m.RecordWrite(
            WorkspaceId,
            AgentMemoryScope.LocalWorkspace,
            "/workspace/memory/topic.md",
            expectedHash), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("notes.txt")]
    [InlineData("../evil.md")]
    [InlineData("sub/a.md")]
    [InlineData(".hidden.md")]
    [InlineData(".md")]
    [InlineData("a b.md")]
    public async Task SaveLocal_InvalidNames_Rejected(string name)
    {
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.SaveLocalAsync(WorkspaceId, name, "x"));
        _coordinator.Verify(c => c.MarkWorkspaceContextDirty(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SaveLocal_ConcurrentWrites_NeverCorrupt()
    {
        var tasks = Enumerable.Range(0, 20).Select(i => _service.SaveLocalAsync(WorkspaceId, "race.md", new string((char)('a' + i % 26), 1000)));

        await Task.WhenAll(tasks);

        var text = await File.ReadAllTextAsync(Path.Combine(_localDir, "race.md"));
        Assert.Equal(1000, text.Length);
        Assert.Single(text.Distinct());
    }

    [Fact]
    public async Task SaveLocal_UnknownWorkspace_FailsFast()
    {
        await Assert.ThrowsAsync<MemoryWorkspaceNotFoundException>(() => _service.SaveLocalAsync(999, "a.md", "x"));
    }

    [Fact]
    public async Task SaveLocal_TooLarge_Rejected()
    {
        await Assert.ThrowsAsync<MemoryValidationException>(() =>
            _service.SaveLocalAsync(WorkspaceId, "big.md", new string('x', MemoryUiConstants.MaxMemoryFileBytes + 1)));
    }

    [Fact]
    public async Task Delete_RemovesFile_AndMarksContextDirty()
    {
        await _service.SaveLocalAsync(WorkspaceId, "gone.md", "x");

        await _service.DeleteLocalAsync(WorkspaceId, "gone.md");

        Assert.False(File.Exists(Path.Combine(_localDir, "gone.md")));
        _monitor.Verify(m => m.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/gone.md", null), Times.Once);
        await Assert.ThrowsAsync<MemoryFileNotFoundException>(() => _service.DeleteLocalAsync(WorkspaceId, "gone.md"));
    }

    [Fact]
    public async Task ListLocal_PinnedFirst_IgnoresNonMarkdownAndTempFiles()
    {
        await _service.SaveLocalAsync(WorkspaceId, "zeta.md", "---\ndescription: Zeta topic\n---\nbody");
        await _service.SaveLocalAsync(WorkspaceId, AgentMemoryConstants.PinnedMemoryFileName, "pinned");
        await File.WriteAllTextAsync(Path.Combine(_localDir, "ignored.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_localDir, ".tmp.md"), "x");

        var list = await _service.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace);

        Assert.Equal([AgentMemoryConstants.PinnedMemoryFileName, "zeta.md"], list.Select(f => f.RelativePath));
        Assert.Equal("Zeta topic", list[1].Summary);
    }

    [Fact]
    public async Task ListLocal_NoDirectory_ReturnsEmpty()
    {
        Assert.Empty(await _service.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace));
    }

    [Fact]
    public async Task ListShared_ReadsFromRepositoryPath()
    {
        Directory.CreateDirectory(Path.Combine(_repoDir, ".agents", "memory"));
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "AGENTS.md"), "# Rules");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, ".agents", "memory", "arch.md"), "Architecture");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, ".agents", "memory", "notes.txt"), "no");

        var list = await _service.ListAsync(WorkspaceId, AgentMemoryScope.SharedRepository);

        Assert.Equal([".agents/memory/arch.md", "AGENTS.md"], list.Select(f => f.RelativePath));
        var read = await _service.ReadAsync(WorkspaceId, AgentMemoryScope.SharedRepository, ".agents/memory/arch.md");
        Assert.Equal("Architecture", read.Content);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("../outside.md")]
    [InlineData(".agents/memory/../../secret.md")]
    public async Task ReadShared_NonMemoryPaths_Rejected(string path)
    {
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.ReadAsync(WorkspaceId, AgentMemoryScope.SharedRepository, path));
    }

    [Fact]
    public async Task Local_SymlinkedFile_IsRejectedAndHidden()
    {
        Directory.CreateDirectory(_localDir);
        var secret = Path.Combine(_root, "secret.md");
        await File.WriteAllTextAsync(secret, "secret");
        var link = Path.Combine(_localDir, "link.md");
        if (!TryCreateSymlink(link, secret))
        {
            return; // symlink creation not permitted on this machine
        }

        Assert.Empty(await _service.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, "link.md"));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.SaveLocalAsync(WorkspaceId, "link.md", "overwrite"));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.DeleteLocalAsync(WorkspaceId, "link.md"));
        Assert.Equal("secret", await File.ReadAllTextAsync(secret));
    }

    [Fact]
    public async Task ListLocal_OversizedFile_ShowsOversizedSummaryWhileReadAsyncRejects()
    {
        await _service.SaveLocalAsync(WorkspaceId, "small.md", "small note");
        var oversizedPath = Path.Combine(_localDir, "huge.md");
        await File.WriteAllTextAsync(oversizedPath, new string('a', MemoryUiConstants.MaxMemoryFileBytes + 1));

        var list = await _service.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace);

        Assert.Equal(["huge.md", "small.md"], list.Select(f => f.RelativePath));
        Assert.Equal(MemoryUiConstants.OversizedFileSummary, list[0].Summary);
        Assert.Equal("small note", list[1].Summary);
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, "huge.md"));
    }

    [Fact]
    public async Task ReadAndDeleteLocal_AllowAgentCreatedFlatFilesWithSpacesAndUnicode()
    {
        Directory.CreateDirectory(_localDir);
        const string agentCreatedName = "my notes é.md";
        await File.WriteAllTextAsync(Path.Combine(_localDir, agentCreatedName), "Agent note content");

        var content = await _service.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, agentCreatedName);
        Assert.Equal("Agent note content", content.Content);

        await _service.DeleteLocalAsync(WorkspaceId, agentCreatedName);
        Assert.False(File.Exists(Path.Combine(_localDir, agentCreatedName)));
    }

    [Theory]
    [InlineData("workspace/clone/AGENTS.md")]
    [InlineData("workspace/task-1/AGENTS.md")]
    [InlineData("/AGENTS.md")]
    public async Task ReadShared_VirtualAndLeadingSlashPaths_Rejected(string path)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "AGENTS.md"), "# Rules");

        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.ReadAsync(WorkspaceId, AgentMemoryScope.SharedRepository, path));
    }

    [Fact]
    public async Task Local_DanglingSymlink_IsSkippedByListAndRejectedOnReadSaveDelete()
    {
        Directory.CreateDirectory(_localDir);
        var danglingLink = Path.Combine(_localDir, "dangling.md");
        if (!TryCreateSymlink(danglingLink, Path.Combine(_root, "nonexistent-target.md")))
        {
            return; // symlink creation not permitted on this machine
        }

        Assert.Empty(await _service.ListAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.ReadAsync(WorkspaceId, AgentMemoryScope.LocalWorkspace, "dangling.md"));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.SaveLocalAsync(WorkspaceId, "dangling.md", "overwrite"));
        await Assert.ThrowsAsync<MemoryValidationException>(() => _service.DeleteLocalAsync(WorkspaceId, "dangling.md"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing-subdir")]
    public async Task ListShared_MissingOrEmptyRepositoryPath_ReturnsEmpty(string repoSubPath)
    {
        var missingRepoWs = await _workspaces.CreateAsync(new Workspace
        {
            Name = "missing-repo",
            RepositoryPath = string.IsNullOrEmpty(repoSubPath) ? string.Empty : Path.Combine(_root, repoSubPath)
        });

        Assert.Empty(await _service.ListAsync(missingRepoWs.Id, AgentMemoryScope.SharedRepository));
    }

    private static bool TryCreateSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

