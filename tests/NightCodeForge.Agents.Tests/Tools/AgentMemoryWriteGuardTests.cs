using System.Security.Cryptography;
using Moq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;

namespace NightCodeForge.Agents.Tests;

/// <summary>Step 12.39 §2.3: Markdown-only memory write guard in read-only mode, locking and the memory write seam.</summary>
public sealed class AgentMemoryWriteGuardTests : IDisposable
{
    private readonly string _root;
    private readonly string _clone;
    private readonly string _memory;

    public AgentMemoryWriteGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ncf-memguard-{Guid.NewGuid():N}");
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

    private WorkspaceFileToolModule CreateModule(SandboxSecurityTier tier, bool memoryAllowWriteInReadOnlyScope = true)
    {
        var isReadOnly = tier == SandboxSecurityTier.RestrictedReadOnly;
        var table = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _clone,
            isReadOnly: isReadOnly,
            hostCloneDirectory: _clone,
            additionalEntries:
            [
                new VirtualMountEntry(
                    AgentMemoryConstants.LocalMemoryDirectoryName,
                    AgentMemoryConstants.VirtualLocalMemoryPath,
                    _memory,
                    string.Empty,
                    isReadOnly: false,
                    allowWriteInReadOnlyScope: memoryAllowWriteInReadOnlyScope)
            ]);
        var sandboxScope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.BareHost,
            tier,
            table,
            workspaceId: 7,
            defaultWorkingDirectory: "/workspace/clone");
        var resolver = new SandboxPathResolver(sandboxScope);
        var toolScope = new CanonicalToolScope(
            _clone,
            WorkspaceId: 7,
            IsReadOnly: isReadOnly,
            SandboxScope: sandboxScope,
            SandboxPathResolver: resolver);

        return new WorkspaceFileToolModule(
            toolScope,
            resolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);
    }

    [Fact]
    public async Task ReadOnlyMode_WritesMarkdownIntoWorkspaceMemory()
    {
        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly);

        var result = await module.WriteAsync("/workspace/memory/ideation-vision.md", "# Vision\n");

        Assert.StartsWith("Successfully wrote", result);
        Assert.True(File.Exists(Path.Combine(_memory, "ideation-vision.md")));
        Assert.Empty(Directory.GetFiles(_memory, ".*"));
    }

    [Theory]
    [InlineData("/workspace/memory/run.sh")]
    [InlineData("/workspace/memory/.hidden.md")]
    [InlineData("/workspace/memory/notes.txt")]
    [InlineData("/workspace/memory/sub/nested.md")]
    public async Task ReadOnlyMode_RejectsNonMarkdownHiddenOrNestedMemoryTargets(string path)
    {
        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly);

        var result = await module.WriteAsync(path, "content");

        Assert.StartsWith("Error: Access denied", result);
        Assert.Empty(Directory.GetFileSystemEntries(_memory));
    }

    [Theory]
    [InlineData("/workspace/clone/README.md")]
    [InlineData("/workspace/clone/.agents/memory/rules.md")]
    [InlineData("/workspace/clone/AGENTS.md")]
    [InlineData("/workspace/clone/docs/tasks/001-task.md")]
    [InlineData("docs/notes.md")]
    public async Task ReadOnlyMode_RejectsWritesOutsideMemoryWithActionableError(string path)
    {
        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly);

        var write = await module.WriteAsync(path, "content");
        var edit = await module.EditAsync(path, "a", "b");

        Assert.StartsWith("Error: Access denied: Mount '/workspace/clone' is read-only.", write);
        Assert.Contains("only '/workspace/memory/*.md' is writable", write);
        Assert.StartsWith("Error: Access denied", edit);
        Assert.False(File.Exists(Path.Combine(_clone, "README.md")));
    }

    [Fact]
    public async Task ReadOnlyMode_WithoutSelectiveWritableMount_KeepsFailFastException()
    {
        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly, memoryAllowWriteInReadOnlyScope: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => module.WriteAsync("/workspace/memory/x.md", "x"));
        Assert.Equal("Write operations are not permitted in read-only mode.", ex.Message);
        var editEx = await Assert.ThrowsAsync<InvalidOperationException>(() => module.EditAsync("/workspace/memory/x.md", "a", "b"));
        Assert.Equal("Edit operations are not permitted in read-only mode.", editEx.Message);
    }

    [Fact]
    public async Task ReadOnlyMode_EditsExistingMemoryFile()
    {
        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly);
        await module.WriteAsync("/workspace/memory/MEMORY.md", "alpha beta");

        var result = await module.EditAsync("/workspace/memory/MEMORY.md", "beta", "gamma");

        Assert.StartsWith("Successfully applied", result);
        Assert.Equal("alpha gamma", File.ReadAllText(Path.Combine(_memory, "MEMORY.md")).TrimEnd());
    }

    [Fact]
    public async Task ReadOnlyMode_RejectsSymlinkedMemoryTarget()
    {
        var secret = Path.Combine(_root, "secret.md");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(_memory, "leak.md");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation requires privileges on some Windows hosts: nothing to verify there.
            return;
        }

        var module = CreateModule(SandboxSecurityTier.RestrictedReadOnly);

        var result = await module.WriteAsync("/workspace/memory/leak.md", "overwrite");

        Assert.StartsWith("Error: Access denied", result);
        Assert.Equal("secret", File.ReadAllText(secret));
    }

    [Fact]
    public async Task MemoryWrites_InvokeSingleObserverSeamWithCreatedThenUpdated()
    {
        var module = CreateModule(SandboxSecurityTier.Standard);
        var events = new List<AgentMemoryWriteEvent>();
        module.MemoryWriteObserver = (e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        };

        await module.WriteAsync("/workspace/memory/topic.md", "one");
        await module.WriteAsync("/workspace/memory/topic.md", "two");
        await module.EditAsync("/workspace/memory/topic.md", "two", "three");
        await module.WriteAsync("/workspace/clone/src/code.cs", "class A {}");

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(AgentMemoryScope.LocalWorkspace, e.Scope));
        Assert.Equal(AgentMemoryChangeKind.Created, events[0].Kind);
        Assert.Equal(AgentMemoryChangeKind.Updated, events[1].Kind);
        Assert.Equal(AgentMemoryChangeKind.Updated, events[2].Kind);
        Assert.Equal("/workspace/memory/topic.md", events[0].RequestedPath);
        Assert.Equal(Path.Combine(_memory, "topic.md"), events[0].HostFilePath);
    }

    [Fact]
    public async Task StandardMode_AllowsAnyFileInMemory_AndSharedMemoryWritesNotifyAsSharedRepository()
    {
        var module = CreateModule(SandboxSecurityTier.Standard);
        var events = new List<AgentMemoryWriteEvent>();
        module.MemoryWriteObserver = (e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        };

        var script = await module.WriteAsync("/workspace/memory/helper.sh", "echo hi");
        var shared = await module.WriteAsync("/workspace/clone/.agents/memory/architecture.md", "# Architecture");

        Assert.StartsWith("Successfully wrote", script);
        Assert.StartsWith("Successfully wrote", shared);
        var sharedEvent = Assert.Single(events);
        Assert.Equal(AgentMemoryScope.SharedRepository, sharedEvent.Scope);
    }

    [Fact]
    public async Task ConcurrentMemoryWrites_NeverCorruptFileNorLeaveTempFiles()
    {
        var module = CreateModule(SandboxSecurityTier.Standard);
        var payloads = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i % 26), 2000 + i)).ToList();

        await Task.WhenAll(payloads.Select(p => module.WriteAsync("/workspace/memory/shared.md", p)));

        var finalContent = File.ReadAllText(Path.Combine(_memory, "shared.md")).TrimEnd();
        Assert.Contains(finalContent, payloads);
        Assert.Equal("shared.md", Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(_memory))));
    }

    [Fact]
    public async Task ConcurrentMemoryEdits_AreSerializedWithoutLostUpdates()
    {
        var module = CreateModule(SandboxSecurityTier.Standard);
        await module.WriteAsync("/workspace/memory/counter.md", "0");
        const int iterations = 15;

        await Task.WhenAll(Enumerable.Range(0, iterations).Select(i =>
            module.EditAsync("/workspace/memory/counter.md", "0", $"0|{i}")));

        // Only the first edit finds '0' unchanged at the start of the text; every other attempt must still be serialized safely.
        var content = File.ReadAllText(Path.Combine(_memory, "counter.md"));
        Assert.StartsWith("0|", content);
        Assert.Single(Directory.GetFileSystemEntries(_memory));
    }

    private WorkspacePathResolverSandboxAdapter CreateSandboxAdapter(bool isReadOnly = true, bool allowLocalMemoryWriteInReadOnlyScope = true)
    {
        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.GetWorkspaceMemoryPath(7)).Returns(_memory);
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        resolver.Setup(r => r.GetWslDistributionName()).Returns(string.Empty);
        resolver.Setup(r => r.ResolveCruciblePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns<string, string, int?>((root, rel, _) =>
            {
                var clean = (rel ?? string.Empty).Trim().Replace('\\', '/');
                if (clean.Equals("/tmp", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(_root, "tmp");
                }

                if (clean.Equals(AttachmentConstants.VirtualAttachmentsMountPath, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(_root, "attachments");
                }

                if (clean.StartsWith("/workspace/clone/", StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean["/workspace/clone/".Length..];
                }
                else if (clean.Equals("/workspace/clone", StringComparison.OrdinalIgnoreCase))
                {
                    clean = string.Empty;
                }
                else if (clean.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean["/workspace/".Length..];
                }

                return string.IsNullOrEmpty(clean) ? root : Path.GetFullPath(Path.Combine(root, clean));
            });
        resolver.Setup(r => r.ResolveCruciblePath(7, It.IsAny<string>(), It.IsAny<int?>()))
            .Returns<int, string, int?>((_, rel, _) =>
            {
                var clean = (rel ?? string.Empty).Trim().Replace('\\', '/');
                if (clean.StartsWith("./", StringComparison.Ordinal))
                {
                    clean = clean[2..].TrimStart('/');
                }

                const string marker = ".nightcodeforge/wks/7/";
                var markerIdx = clean.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIdx >= 0)
                {
                    clean = "/workspace/" + clean[(markerIdx + marker.Length)..].TrimStart('/');
                }

                if (clean.Equals("/workspace/memory", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("workspace/memory", StringComparison.OrdinalIgnoreCase))
                {
                    return _memory;
                }

                if (clean.StartsWith("/workspace/memory/", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(_memory, clean["/workspace/memory/".Length..]);
                }

                if (clean.StartsWith("workspace/memory/", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(_memory, clean["workspace/memory/".Length..]);
                }

                if (clean.Equals("/workspace/clone", StringComparison.OrdinalIgnoreCase))
                {
                    return _clone;
                }

                if (clean.StartsWith("/workspace/clone/", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(_clone, clean["/workspace/clone/".Length..]);
                }

                return string.IsNullOrEmpty(clean) ? _clone : Path.GetFullPath(Path.Combine(_clone, clean.TrimStart('/')));
            });

        var toolScope = new CanonicalToolScope(
            _clone,
            WorkspaceId: 7,
            IsReadOnly: isReadOnly,
            SandboxScope: null)
        {
            AllowLocalMemoryWriteInReadOnlyScope = allowLocalMemoryWriteInReadOnlyScope
        };

        return new WorkspacePathResolverSandboxAdapter(toolScope, resolver.Object, NullLogger.Instance);
    }

    [Theory]
    [InlineData("workspace/memory/vision.md")]
    [InlineData(".nightcodeforge/wks/7/memory/vision.md")]
    public void SandboxAdapter_ResolvesLocalMemoryVariantsIntoMemoryRoot(string requestedPath)
    {
        var adapter = CreateSandboxAdapter(isReadOnly: true, allowLocalMemoryWriteInReadOnlyScope: true);

        var resolved = adapter.ResolveHostPath(requestedPath, forWrite: true);

        Assert.Equal(Path.GetFullPath(Path.Combine(_memory, "vision.md")), Path.GetFullPath(resolved));
    }

    [Theory]
    [InlineData("/workspace/clone/AGENTS.md", "AGENTS.md")]
    [InlineData("/workspace/clone/.agents/AGENTS.md", ".agents/AGENTS.md")]
    public void SandboxAdapter_RejectsSymlinkedSharedRulesFiles(string requestedPath, string relativeFile)
    {
        var secret = Path.Combine(_root, "secret-rules.md");
        File.WriteAllText(secret, "secret");
        var link = Path.Combine(_clone, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var adapter = CreateSandboxAdapter(isReadOnly: false);

        Assert.Throws<UnauthorizedAccessException>(() => adapter.ResolveHostPath(requestedPath, forWrite: false));
        Assert.Throws<UnauthorizedAccessException>(() => adapter.ResolveHostPath(requestedPath, forWrite: true));
    }

    [Fact]
    public async Task BareHostModule_AbsoluteSharedMemoryWrites_NotifySharedRepositoryWithContentSha256()
    {
        var table = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _clone,
            isReadOnly: false,
            hostCloneDirectory: _clone,
            additionalEntries:
            [
                new VirtualMountEntry(
                    AgentMemoryConstants.LocalMemoryDirectoryName,
                    AgentMemoryConstants.VirtualLocalMemoryPath,
                    _memory,
                    string.Empty,
                    isReadOnly: false,
                    allowWriteInReadOnlyScope: true)
            ]);
        var sandboxScope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.BareHost,
            SandboxSecurityTier.Standard,
            table,
            workspaceId: 7,
            defaultWorkingDirectory: "/workspace/clone",
            repositoryPath: _clone);
        var resolver = new SandboxPathResolver(sandboxScope);
        var toolScope = new CanonicalToolScope(
            _clone,
            WorkspaceId: 7,
            IsReadOnly: false,
            SandboxScope: sandboxScope,
            SandboxPathResolver: resolver);
        var module = new WorkspaceFileToolModule(
            toolScope,
            resolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        var events = new List<AgentMemoryWriteEvent>();
        module.MemoryWriteObserver = (e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        };

        var archHostPath = Path.Combine(_clone, ".agents", "memory", "arch.md");
        var rootAgentsHostPath = Path.Combine(_clone, "AGENTS.md");

        Assert.StartsWith("Successfully wrote", await module.WriteAsync(archHostPath, "# Arch\n"));
        Assert.StartsWith("Successfully wrote", await module.WriteAsync(rootAgentsHostPath, "# Rules\n"));

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(AgentMemoryScope.SharedRepository, e.Scope));

        var expectedArchSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archHostPath)));
        var expectedAgentsSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(rootAgentsHostPath)));
        Assert.Equal(expectedArchSha, events[0].ContentSha256);
        Assert.Equal(expectedAgentsSha, events[1].ContentSha256);
    }
}
