using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests.Memory;

public sealed class AcpMemoryTests : IDisposable
{
    private const int WorkspaceId = 9;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-acpmem-" + Guid.NewGuid().ToString("N"));
    private readonly string _memoryDir;
    private readonly string _repoDir;
    private readonly Mock<IWorkspacePathResolver> _resolver = new();
    private readonly Mock<IAgentMemoryWriteNotifier> _notifier = new();

    public AcpMemoryTests()
    {
        _memoryDir = Path.Combine(_root, "wks", WorkspaceId.ToString(), "memory");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_memoryDir);
        Directory.CreateDirectory(_repoDir);
        _resolver.Setup(r => r.GetWorkspaceMemoryPath(WorkspaceId)).Returns(_memoryDir);
        _resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        _resolver.Setup(r => r.ResolveCruciblePath(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns<string, string, int?>((root, rel, _) =>
            {
                var clean = (rel ?? string.Empty).Trim().Replace('\\', '/');
                if (clean.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
                {
                    clean = clean["/workspace/".Length..];
                }

                return string.IsNullOrEmpty(clean) ? root : Path.GetFullPath(Path.Combine(root, clean));
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AcpHostRpcHandler CreateHandler() => new(
        new Mock<IExecutionEnvironmentProvider>().Object,
        new EditorConfigService(),
        new EditorConfigFileApplier(),
        _resolver.Object,
        _notifier.Object,
        NullLogger<AcpHostRpcHandler>.Instance);

    private AcpHostRpcContext Context(AgentProfile? profile = null, ISandboxPathResolver? sandbox = null, string stageId = "stage-2") => new(
        "sess-1",
        new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "T", Description = "D" },
            Profile = profile ?? new AgentProfile { Name = "impl" },
            StageId = stageId,
            Workspace = new Workspace { Id = WorkspaceId, RepositoryPath = _repoDir, ExecutionMode = ExecutionMode.BareHost }
        },
        _repoDir,
        _repoDir,
        sandbox);

    private static JsonNode? Args(string path, string? content = null) =>
        content == null
            ? new JsonObject { ["path"] = path }
            : new JsonObject { ["path"] = path, ["content"] = content };

    [Fact]
    public async Task FsWrite_LocalMemoryMarkdown_WritesSiblingMemoryDirectoryAndNotifiesAsCreated()
    {
        var handler = CreateHandler();

        var result = await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args("/workspace/memory/vision.md", "# Vision"), Context(), CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("# Vision", File.ReadAllText(Path.Combine(_memoryDir, "vision.md")).TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(Path.Combine(_repoDir, "memory")));
        _notifier.Verify(n => n.NotifyWriteAsync(
            WorkspaceId, 42, "stage-2", "sess-1",
            It.Is<AgentMemoryWriteEvent>(e => e.Scope == AgentMemoryScope.LocalWorkspace && e.Kind == AgentMemoryChangeKind.Created && e.HostFilePath.EndsWith("vision.md")),
            It.IsAny<CancellationToken>()), Times.Once);

        await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args("/workspace/memory/vision.md", "# Vision v2"), Context(), CancellationToken.None);
        _notifier.Verify(n => n.NotifyWriteAsync(
            WorkspaceId, 42, "stage-2", "sess-1",
            It.Is<AgentMemoryWriteEvent>(e => e.Kind == AgentMemoryChangeKind.Updated),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FsRead_LocalMemoryMarkdown_ReadsSiblingMemoryDirectory()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- pinned");
        var handler = CreateHandler();

        var result = await handler.HandleAsync(AcpHostMethods.FsReadTextFile, Args("/workspace/memory/MEMORY.md"), Context(), CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal("- pinned", result.Result!.GetType().GetProperty("content")!.GetValue(result.Result));
    }

    [Theory]
    [InlineData("/workspace/memory/script.sh")]
    [InlineData("/workspace/memory/.hidden.md")]
    [InlineData("/workspace/memory/sub/nested.md")]
    public async Task FsWrite_LocalMemoryNonFlatMarkdown_IsRejected(string path)
    {
        var result = await CreateHandler().HandleAsync(AcpHostMethods.FsWriteTextFile, Args(path, "x"), Context(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("Access denied", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(_memoryDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FsWrite_LocalMemoryThroughSymlink_IsRejected()
    {
        var outside = Path.Combine(_root, "outside.md");
        File.WriteAllText(outside, "original");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_memoryDir, "link.md"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var result = await CreateHandler().HandleAsync(AcpHostMethods.FsWriteTextFile, Args("/workspace/memory/link.md", "pwned"), Context(), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("original", File.ReadAllText(outside));
    }

    [Fact]
    public async Task FsWrite_ReadOnlySessionWithoutSelectiveMemoryMount_IsDenied()
    {
        var readOnly = new AgentProfile { Name = "ro", Mode = AgentMode.ReadOnly };

        var result = await CreateHandler().HandleAsync(AcpHostMethods.FsWriteTextFile, Args("/workspace/memory/x.md", "x"), Context(readOnly), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(_memoryDir, "x.md")));
    }

    [Fact]
    public async Task FsWrite_SharedAgentsMemoryInsideWorktree_NotifiesAsSharedScope()
    {
        var handler = CreateHandler();

        var result = await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args(".agents/memory/arch.md", "# Arch"), Context(), CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        Assert.True(File.Exists(Path.Combine(_repoDir, ".agents", "memory", "arch.md")));
        _notifier.Verify(n => n.NotifyWriteAsync(
            WorkspaceId, 42, "stage-2", "sess-1",
            It.Is<AgentMemoryWriteEvent>(e => e.Scope == AgentMemoryScope.SharedRepository),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FsWrite_NonMemoryFile_DoesNotNotify()
    {
        var result = await CreateHandler().HandleAsync(AcpHostMethods.FsWriteTextFile, Args("/workspace/src/a.cs", "class A {}"), Context(), CancellationToken.None);

        Assert.False(result.IsError, result.ErrorMessage);
        _notifier.Verify(n => n.NotifyWriteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<AgentMemoryWriteEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FsWrite_AbsoluteHostSharedMemoryPaths_NotifiesSharedRepositoryWithSha256()
    {
        var handler = CreateHandler();
        var archPath = Path.Combine(_repoDir, ".agents", "memory", "arch.md");
        var rootAgentsPath = Path.Combine(_repoDir, "AGENTS.md");

        var res1 = await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args(archPath, "# Architecture"), Context(), CancellationToken.None);
        var res2 = await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args(rootAgentsPath, "# Rules"), Context(), CancellationToken.None);

        Assert.False(res1.IsError, res1.ErrorMessage);
        Assert.False(res2.IsError, res2.ErrorMessage);
        Assert.True(File.Exists(archPath));
        Assert.True(File.Exists(rootAgentsPath));
        _notifier.Verify(n => n.NotifyWriteAsync(
            WorkspaceId, 42, "stage-2", "sess-1",
            It.Is<AgentMemoryWriteEvent>(e =>
                e.Scope == AgentMemoryScope.SharedRepository &&
                e.HostFilePath == Path.GetFullPath(archPath) &&
                e.ContentSha256 != null &&
                e.ContentSha256.Length == 64 &&
                e.ContentSha256 == e.ContentSha256.ToLowerInvariant()),
            It.IsAny<CancellationToken>()), Times.Once);
        _notifier.Verify(n => n.NotifyWriteAsync(
            WorkspaceId, 42, "stage-2", "sess-1",
            It.Is<AgentMemoryWriteEvent>(e =>
                e.Scope == AgentMemoryScope.SharedRepository &&
                e.HostFilePath == Path.GetFullPath(rootAgentsPath) &&
                e.ContentSha256 != null &&
                e.ContentSha256.Length == 64),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FsReadAndWrite_AbsoluteHostSharedMemorySymlinks_AreRejected()
    {
        var outside = Path.Combine(_root, "outside-secret.md");
        File.WriteAllText(outside, "top-secret");
        var sharedDir = Path.Combine(_repoDir, ".agents", "memory");
        Directory.CreateDirectory(sharedDir);
        var evilTopic = Path.Combine(sharedDir, "evil.md");
        var evilAgents = Path.Combine(_repoDir, "AGENTS.md");
        try
        {
            File.CreateSymbolicLink(evilTopic, outside);
            File.CreateSymbolicLink(evilAgents, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        var handler = CreateHandler();
        foreach (var target in new[] { evilTopic, evilAgents })
        {
            var readRes = await handler.HandleAsync(AcpHostMethods.FsReadTextFile, Args(target), Context(), CancellationToken.None);
            var writeRes = await handler.HandleAsync(AcpHostMethods.FsWriteTextFile, Args(target, "overwrite"), Context(), CancellationToken.None);
            Assert.True(readRes.IsError, $"Expected read of symlink '{target}' to fail.");
            Assert.True(writeRes.IsError, $"Expected write to symlink '{target}' to fail.");
        }

        Assert.Equal("top-secret", File.ReadAllText(outside));
    }

    [Fact]
    public void ResolveLocalMemoryFile_SymlinkedMemoryRoot_ThrowsUnauthorizedAccessException()
    {
        var outsideDir = Path.Combine(_root, "outside-memory");
        Directory.CreateDirectory(outsideDir);
        var symlinkedMemoryDir = Path.Combine(_root, "wks", WorkspaceId.ToString(), "memory-link");
        try
        {
            Directory.CreateSymbolicLink(symlinkedMemoryDir, outsideDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // Symlink creation not permitted on this machine.
        }

        Assert.Throws<UnauthorizedAccessException>(() =>
            AgentMemoryPathResolution.ResolveLocalMemoryFile("/workspace/memory", WorkspaceId, symlinkedMemoryDir));
    }

    private sealed class ProbeHarness : ExternalProcessAgentHarnessBase
    {
        public ProbeHarness(IWorkspacePathResolver resolver)
            : base(
                new HarnessActivityRecorder(new InMemoryAgentActivityStreamer(), new SessionTranscriptService(), new InMemoryTaskRepository(), NullLogger<HarnessActivityRecorder>.Instance),
                new AgentsMdService(),
                resolver,
                new Mock<IProcessRunner>().Object,
                new NoopAgentMemoryTurnTracker(),
                NullLogger.Instance)
        {
        }

        public override string HarnessId => "probe";
        protected override string AgentDisplayName => "probe";
        protected override string DefaultExecutablePath => "exe";
        protected override string DefaultArguments => string.Empty;
        protected override Func<Process>? CustomProcessFactory => null;

        public override Task<AgentRunResult> RunSessionStreamAsync(StreamWriter stdin, StreamReader stdout, AgentSessionContext context, string sessionId, Stopwatch sw, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> PromptAsync(AgentSessionContext ctx, string root) => BuildEnrichedPromptAsync(ctx, root, CancellationToken.None);
    }

    [Fact]
    public async Task BuildEnrichedPrompt_InjectsWorkspaceMemoryCatalogAndPipelineGuidelines()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- local quirk: use dotnet 9");
        File.WriteAllText(Path.Combine(_memoryDir, "ideation-vision.md"), "# Vision constraints");
        Directory.CreateDirectory(Path.Combine(_repoDir, ".agents", "memory"));
        File.WriteAllText(Path.Combine(_repoDir, ".agents", "memory", "arch.md"), "# Architecture");

        var prompt = await new ProbeHarness(_resolver.Object).PromptAsync(Context().Session, _repoDir);

        Assert.Contains("<workspace_memory>", prompt);
        Assert.Contains("- local quirk: use dotnet 9", prompt);
        Assert.Contains("<memory_catalog>", prompt);
        Assert.Contains("[Local] /workspace/memory/ideation-vision.md", prompt);
        Assert.Contains("[Shared/Git] .agents/memory/arch.md", prompt);
        Assert.Contains("<memory_guidelines>", prompt);
        Assert.Contains(".agents/memory/<topic>.md", prompt);
        Assert.Contains("/workspace/memory/*.md agent memory notes", prompt);
    }

    [Fact]
    public async Task BuildEnrichedPrompt_ReadOnlyProfileGetsIdeationGuidelines_TriageGetsNoMemory()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- pinned");
        var probe = new ProbeHarness(_resolver.Object);

        var readOnly = Context(new AgentProfile { Name = "ro", Mode = AgentMode.ReadOnly }).Session;
        var readOnlyPrompt = await probe.PromptAsync(readOnly, _repoDir);
        Assert.Contains(AgentMemoryGuidelines.Ideation.Split('\n')[1], readOnlyPrompt);

        var triage = Context(stageId: StandardStageIds.Triage).Session;
        var triagePrompt = await probe.PromptAsync(triage, _repoDir);
        Assert.DoesNotContain("<workspace_memory>", triagePrompt);
        Assert.DoesNotContain("<memory_guidelines>", triagePrompt);
        Assert.DoesNotContain("/workspace/memory/*.md agent memory notes", triagePrompt);
    }

    [Fact]
    public async Task BuildEnrichedPrompt_ReadOnlyWithReadOnlyMemoryMount_OmitsGuidelinesAndException()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- pinned");
        var table = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _repoDir,
            isReadOnly: true,
            hostCloneDirectory: _repoDir,
            additionalEntries:
            [
                new VirtualMountEntry(
                    AgentMemoryConstants.LocalMemoryDirectoryName,
                    AgentMemoryConstants.VirtualLocalMemoryPath,
                    _memoryDir,
                    string.Empty,
                    isReadOnly: true,
                    allowWriteInReadOnlyScope: false)
            ]);
        var roScope = new SandboxScopeDescriptor(
            SandboxScopeKind.WorkspaceClone,
            ExecutionMode.BareHost,
            SandboxSecurityTier.RestrictedReadOnly,
            table,
            workspaceId: WorkspaceId,
            defaultWorkingDirectory: "/workspace/clone");
        var session = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "T", Description = "D" },
            Profile = new AgentProfile { Name = "ro", Mode = AgentMode.ReadOnly, SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
            StageId = StandardStageIds.Ideation,
            RequiredMode = AgentMode.ReadOnly,
            Workspace = new Workspace { Id = WorkspaceId, RepositoryPath = _repoDir, ExecutionMode = ExecutionMode.BareHost },
            SandboxScope = roScope
        };

        var prompt = await new ProbeHarness(_resolver.Object).PromptAsync(session, _repoDir);

        Assert.Contains("<workspace_memory>", prompt);
        Assert.DoesNotContain("<memory_guidelines>", prompt);
        Assert.DoesNotContain("/workspace/memory/*.md agent memory notes", prompt);
    }
}
