using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Core.Tests;

/// <summary>Selective <c>:rw</c> /workspace/memory mount enforcement in RestrictedReadOnly scopes (Step 12.39 §2).</summary>
public class AgentMemorySandboxResolverTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly string _memory;

    public AgentMemorySandboxResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ncf-memory-test-{Guid.NewGuid():N}");
        _clone = Path.Combine(_root, "clone");
        _memory = Path.Combine(_root, "memory");
        Directory.CreateDirectory(_clone);
        Directory.CreateDirectory(_memory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SandboxPathResolver CreateResolver(SandboxSecurityTier tier, bool memoryAllowWriteInReadOnly = true, bool memoryReadOnly = false)
    {
        var isReadOnly = tier == SandboxSecurityTier.RestrictedReadOnly;
        var table = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _clone,
            isReadOnly: isReadOnly,
            hostCloneDirectory: _clone,
            additionalEntries:
            [
                new VirtualMountEntry(
                    virtualSubPath: AgentMemoryConstants.LocalMemoryDirectoryName,
                    virtualMountPath: AgentMemoryConstants.VirtualLocalMemoryPath,
                    hostDirectoryPath: _memory,
                    wslTargetDirectoryPath: string.Empty,
                    isReadOnly: memoryReadOnly,
                    allowWriteInReadOnlyScope: memoryAllowWriteInReadOnly)
            ]);

        var scope = new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.WorkspaceClone,
            executionMode: ExecutionMode.BareHost,
            securityTier: tier,
            mountTable: table,
            workspaceId: 7,
            defaultWorkingDirectory: "/workspace/clone");
        return new SandboxPathResolver(scope);
    }

    [Fact]
    public void ReadOnlyScope_AllowsWriteInsideSelectiveWritableMemoryMount()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        var resolved = resolver.ResolveHostPath("/workspace/memory/ideation-vision.md", forWrite: true);

        Assert.Equal(Path.Combine(_memory, "ideation-vision.md"), resolved);
    }

    [Theory]
    [InlineData("/workspace/clone/src/Program.cs")]
    [InlineData("/workspace/clone/.agents/memory/rules.md")]
    [InlineData("/workspace/clone/AGENTS.md")]
    [InlineData("src/Program.cs")]
    public void ReadOnlyScope_RejectsWritesOutsideMemoryMount(string path)
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        var ex = Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath(path, forWrite: true));

        Assert.Contains("is read-only", ex.Message);
        Assert.Contains("/workspace/memory/*.md", ex.Message);
    }

    [Fact]
    public void ReadOnlyScope_RejectsWriteWhenMemoryMountDoesNotAllowWriteInReadOnlyScope()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly, memoryAllowWriteInReadOnly: false);

        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/memory/MEMORY.md", forWrite: true));
    }

    [Fact]
    public void ReadOnlyScope_RejectsWriteWhenMemoryMountIsReadOnly()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly, memoryReadOnly: true);

        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/memory/MEMORY.md", forWrite: true));
    }

    [Fact]
    public void ReadOnlyScope_AllowsReadsEverywhere()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        Assert.Equal(Path.Combine(_clone, "src", "a.cs"), resolver.ResolveHostPath("/workspace/clone/src/a.cs", forWrite: false));
        Assert.Equal(Path.Combine(_memory, "MEMORY.md"), resolver.ResolveHostPath("/workspace/memory/MEMORY.md", forWrite: false));
    }

    [Fact]
    public void StandardScope_AllowsWritesToMemoryAndClone()
    {
        var resolver = CreateResolver(SandboxSecurityTier.Standard);

        Assert.Equal(Path.Combine(_memory, "notes.md"), resolver.ResolveHostPath("/workspace/memory/notes.md", forWrite: true));
        Assert.Equal(Path.Combine(_clone, "a.cs"), resolver.ResolveHostPath("/workspace/clone/a.cs", forWrite: true));
    }

    [Fact]
    public void MemoryMount_RejectsTraversalOutsideMemoryDirectory()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/memory/../clone/a.cs", forWrite: true));
    }

    [Fact]
    public void MemoryMount_RejectsSymlinkedMemoryFile()
    {
        var secret = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(_memory, "leak.md");
        if (!TryCreateFileSymlink(link, secret))
        {
            return;
        }

        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        var ex = Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/memory/leak.md", forWrite: false));
        Assert.Contains("symbolic link", ex.Message);
        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/memory/leak.md", forWrite: true));
    }

    [Fact]
    public void MemoryMount_RejectsSymlinkedSubdirectory()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(_memory, "sub");
        if (!TryCreateDirectorySymlink(link, outside))
        {
            return;
        }

        var resolver = CreateResolver(SandboxSecurityTier.Standard);

        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/memory/sub/x.md", forWrite: true));
    }

    [Fact]
    public void SharedAgentsMemory_RejectsSymlinkedDirectory()
    {
        var outside = Path.Combine(_root, "outside-agents");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_clone, ".agents"));
        var link = Path.Combine(_clone, ".agents", "memory");
        if (!TryCreateDirectorySymlink(link, outside))
        {
            return;
        }

        var resolver = CreateResolver(SandboxSecurityTier.Standard);

        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/clone/.agents/memory/rules.md", forWrite: false));
    }

    [Theory]
    [InlineData("/workspace/clone/AGENTS.md", "AGENTS.md")]
    [InlineData("/workspace/clone/.agents/AGENTS.md", ".agents/AGENTS.md")]
    public void SharedRulesFiles_RejectSymlinkedTargets(string virtualPath, string relativeFile)
    {
        var secret = Path.Combine(_root, "secret-rules.md");
        File.WriteAllText(secret, "secret");
        var linkPath = Path.Combine(_clone, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        if (!TryCreateFileSymlink(linkPath, secret))
        {
            return;
        }

        var resolver = CreateResolver(SandboxSecurityTier.Standard);

        var readEx = Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath(virtualPath, forWrite: false));
        Assert.Contains("symbolic link", readEx.Message);
        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath(virtualPath, forWrite: true));
    }

    [Fact]
    public void MemoryMount_RejectsDanglingSymlinkOnReadAndWrite()
    {
        var nonExistentTarget = Path.Combine(_root, "does-not-exist.md");
        var danglingLink = Path.Combine(_memory, "dangling.md");
        if (!TryCreateFileSymlink(danglingLink, nonExistentTarget))
        {
            return;
        }

        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        var readEx = Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/memory/dangling.md", forWrite: false));
        Assert.Contains("symbolic link", readEx.Message);
        Assert.Throws<UnauthorizedAccessException>(() => resolver.ResolveHostPath("/workspace/memory/dangling.md", forWrite: true));
    }

    [Fact]
    public void ForNightwatch_PinsAllowWriteInReadOnlyScopeToFalse()
    {
        var table = SandboxMountTable.ForNightwatch(
            hostNcfLogsPath: Path.Combine(_root, "logs"),
            wslNcfLogsPath: null,
            hostSkillsPath: null,
            wslSkillsPath: null,
            additionalMounts:
            [
                new VirtualMountEntry("memory", "/nightcodeforge/memory", _memory, string.Empty, isReadOnly: false, allowWriteInReadOnlyScope: true)
            ]);

        Assert.All(table.Entries, e =>
        {
            Assert.True(e.IsReadOnly);
            Assert.False(e.AllowWriteInReadOnlyScope);
        });
    }

    [Fact]
    public void AttachmentsMount_AlwaysPinnedReadOnlyWithoutSelectiveWrite()
    {
        var table = new SandboxMountTable(
            SandboxMountTable.WorkspaceVirtualRoot,
            [new VirtualMountEntry("attachments", AttachmentConstants.VirtualAttachmentsMountPath, _root, string.Empty, isReadOnly: false, allowWriteInReadOnlyScope: true)]);

        var entry = Assert.Single(table.Entries);
        Assert.True(entry.IsReadOnly);
        Assert.False(entry.AllowWriteInReadOnlyScope);
    }

    [Fact]
    public void TranslateBashCommand_RewritesMemoryPrefixOnBareHost()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        var translated = resolver.TranslateBashCommand("cat /workspace/memory/MEMORY.md", ExecutionTier.BareHost);

        Assert.Equal($"cat {_memory.Replace('\\', '/')}/MEMORY.md", translated);
    }

    [Fact]
    public void ResolveBashWorkingDirectory_AcceptsMemoryInContainer()
    {
        var resolver = CreateResolver(SandboxSecurityTier.RestrictedReadOnly);

        Assert.Equal("/workspace/memory", resolver.ResolveBashWorkingDirectory("/workspace/memory", ExecutionTier.ContainerSandbox));
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation requires privileges on some Windows hosts: the symlink cases are skipped there.
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation requires privileges on some Windows hosts: the symlink cases are skipped there.
            return false;
        }
    }
}
