using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests.Memory;

public sealed class MemoryPromptAndNotificationTests : IDisposable
{
    private const int WorkspaceId = 11;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ncf-memprompt-" + Guid.NewGuid().ToString("N"));
    private readonly string _memoryDir;
    private readonly string _repoDir;

    public MemoryPromptAndNotificationTests()
    {
        _memoryDir = Path.Combine(_root, "wks", WorkspaceId.ToString(), "memory");
        _repoDir = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_memoryDir);
        Directory.CreateDirectory(_repoDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- PromptPrefixStabilizer: Layer 1 / 2 / 3 placement ----

    [Fact]
    public void Stabilizer_Layer1AndLayer2Prefix_AreInvariantToLocalMemoryEdits()
    {
        var stabilizer = new PromptPrefixStabilizer();

        var a = stabilizer.BuildLayers("Implement", "<task id=\"1\" />", "project", "skills",
            workspaceMemory: "<workspace_memory>one</workspace_memory>", memoryCatalog: "<memory_catalog>a</memory_catalog>",
            memoryGuidelines: AgentMemoryGuidelines.Pipeline);
        var b = stabilizer.BuildLayers("Implement", "<task id=\"1\" />", "project", "skills",
            workspaceMemory: "<workspace_memory>two, much longer</workspace_memory>", memoryCatalog: "<memory_catalog>b</memory_catalog>",
            memoryGuidelines: AgentMemoryGuidelines.Pipeline);

        Assert.Equal(a.Layer1GlobalRules, b.Layer1GlobalRules);
        Assert.Equal(a.Layer2StagePersona, b.Layer2StagePersona);
        Assert.NotEqual(a.Layer3TaskScope, b.Layer3TaskScope);
        Assert.Contains("<workspace_memory>one</workspace_memory>", a.Layer3TaskScope);
        Assert.Contains("<memory_catalog>a</memory_catalog>", a.Layer3TaskScope);
        Assert.DoesNotContain("workspace_memory", a.Layer1GlobalRules);
        Assert.DoesNotContain("memory_guidelines", a.Layer1GlobalRules);
    }

    [Fact]
    public void Stabilizer_Layer2CarriesRoleGuidelines_AndStagesShareByteIdenticalLayer1()
    {
        var stabilizer = new PromptPrefixStabilizer();

        var review = stabilizer.BuildLayers("Review directive", "scope", "project", "skills", memoryGuidelines: AgentMemoryGuidelines.Pipeline);
        var implement = stabilizer.BuildLayers("Implement directive", "scope", "project", "skills", memoryGuidelines: AgentMemoryGuidelines.Pipeline);
        var ideation = stabilizer.BuildLayers("Ideate", "scope", "project", "skills", memoryGuidelines: AgentMemoryGuidelines.Ideation);

        Assert.Equal(review.Layer1GlobalRules, implement.Layer1GlobalRules);
        Assert.Equal(review.Layer1GlobalRules, ideation.Layer1GlobalRules);
        Assert.Contains("<memory_guidelines>", review.Layer2StagePersona);
        Assert.NotEqual(review.Layer2StagePersona, ideation.Layer2StagePersona);
    }

    [Fact]
    public void Stabilizer_NoMemory_LeavesLayersWithoutMemorySections()
    {
        var layers = new PromptPrefixStabilizer().BuildLayers("d", "scope");

        Assert.DoesNotContain("memory", layers.Layer3TaskScope.Replace("/workspace/memory/*.md", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory_guidelines", layers.Layer2StagePersona);
    }

    [Fact]
    public void Stabilizer_CarvesOutMemoryDirectoryFromSiblingDirectoryRule()
    {
        Assert.Contains("/workspace/memory/", PromptPrefixStabilizer.CanonicalRulesPrompt);
        var layer3 = new PromptPrefixStabilizer().BuildLayer3TaskScope("scope", "/workspace/task-1");
        Assert.Contains("/workspace/memory/*.md", layer3);
    }

    // ---- AgentWorkspaceContextService ----

    private AgentWorkspaceContextService CreateContext(IAgentMemoryTurnTracker? tracker = null)
    {
        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.GetWorkspaceMemoryPath(WorkspaceId)).Returns(_memoryDir);
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        return new AgentWorkspaceContextService(
            resolver.Object,
            new AgentsMdService(),
            new Mock<IWorkspaceSymbolService>().Object,
            new Mock<ITaskMarkdownSyncService>().Object,
            TestBacklogContext.CreateDefault(),
            new TranscriptAnalysisService(),
            tracker ?? new NoopAgentMemoryTurnTracker(),
            NullLogger<AgentWorkspaceContextService>.Instance);
    }

    [Fact]
    public async Task BuildPromptContext_IncludesWorkspaceMemoryAndCatalog_AndWriteCapability()
    {
        File.WriteAllText(Path.Combine(_memoryDir, "MEMORY.md"), "- decision A");
        File.WriteAllText(Path.Combine(_memoryDir, "topic.md"), "# Topic");
        var workspace = new Workspace { Id = WorkspaceId, RepositoryPath = _repoDir };

        var withWrite = await CreateContext().BuildPromptContextAsync(
            workspace, _repoDir, new[] { CanonicalToolNames.Read, CanonicalToolNames.Write }, new Mock<IWorkspaceSkillsService>().Object, CancellationToken.None);
        var readOnly = await CreateContext().BuildPromptContextAsync(
            workspace, _repoDir, new[] { CanonicalToolNames.Read }, new Mock<IWorkspaceSkillsService>().Object, CancellationToken.None);

        Assert.Contains("- decision A", withWrite.WorkspaceMemory);
        Assert.Contains("/workspace/memory/topic.md", withWrite.MemoryCatalog);
        Assert.True(withWrite.CanWriteMemory);
        Assert.False(readOnly.CanWriteMemory);
    }

    [Fact]
    public async Task ContextService_DelegatesSnapshotsAndNotificationsToTracker()
    {
        var tracker = new Mock<IAgentMemoryTurnTracker>();
        var snapshot = new AgentMemorySnapshot(WorkspaceId, new Dictionary<(AgentMemoryScope Scope, string RelativePath), string>());
        tracker.Setup(t => t.CaptureAsync(WorkspaceId, _repoDir, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var service = CreateContext(tracker.Object);

        var captured = await service.CaptureMemorySnapshotAsync(WorkspaceId, _repoDir, CancellationToken.None);
        await service.PublishMemoryChangesAsync(captured, _repoDir, 5, "stage-2", "sess", CancellationToken.None);

        Assert.Same(snapshot, captured);
        tracker.Verify(t => t.PublishChangesAsync(snapshot, _repoDir, 5, "stage-2", "sess", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Ideation: dirty refresh + prompt ----

    [Fact]
    public void IsWorkspaceMutatingActivity_ExternalLocalMemoryNotice_MarksWarmSessionsDirty()
    {
        AgentActivityEvent Notice(string stage, string scope) => new(
            "s", stage == StandardStageIds.Ideation ? 0 : 42, stage, AgentActivityKind.Notice, "memory",
            DateTime.UtcNow,
            new Dictionary<string, object?>
            {
                [AgentActivityMetadataKeys.ActionType] = AgentActivityMetadataKeys.ActionTypeMemoryUpdated,
                [AgentActivityMetadataKeys.MemoryScope] = scope,
                [AgentActivityMetadataKeys.WorkspaceId] = WorkspaceId
            });

        Assert.True(IdeationConversationCoordinator.IsWorkspaceMutatingActivity(Notice("stage-2", "Local")));
        Assert.False(IdeationConversationCoordinator.IsWorkspaceMutatingActivity(Notice(StandardStageIds.Ideation, "Local")));
        Assert.False(IdeationConversationCoordinator.IsWorkspaceMutatingActivity(Notice("stage-2", "SharedGit")));
    }

    [Fact]
    public void ContextUpdateDelta_IncludesChangedMemorySections_AndNullWhenNothingChanged()
    {
        var delta = IdeationSystemPromptBuilder.BuildWorkspaceContextUpdateDelta(
            "b", "b", "a", "a", "h", "h",
            previousWorkspaceMemory: "<workspace_memory>old</workspace_memory>",
            currentWorkspaceMemory: "<workspace_memory>new</workspace_memory>",
            previousMemoryCatalog: null,
            currentMemoryCatalog: null);
        var unchanged = IdeationSystemPromptBuilder.BuildWorkspaceContextUpdateDelta(
            "b", "b", "a", "a", "h", "h", "m", "m", "c", "c");

        Assert.NotNull(delta);
        Assert.Contains("<workspace_memory>new</workspace_memory>", delta);
        Assert.DoesNotContain("memory_catalog", delta);
        Assert.Null(unchanged);
    }

    [Fact]
    public void IdeationSystemPrompt_InjectsGuidelinesMemoryAndCatalog_OnlyWhenAvailable()
    {
        var workspace = new Workspace { Id = WorkspaceId, RepositoryPath = _repoDir };
        var profile = AgentProfile.CreateDefaultIdeationProfile();
        var locations = new AgentWorkspaceLocations(_repoDir, null, _repoDir);
        var context = new AgentPromptContext("<project_context>p</project_context>", string.Empty, Array.Empty<string>())
        {
            WorkspaceMemory = "<workspace_memory>- vision</workspace_memory>",
            MemoryCatalog = "<memory_catalog>- [Local] /workspace/memory/t.md</memory_catalog>",
            CanWriteMemory = true
        };

        var prompt = IdeationSystemPromptBuilder.BuildSystemPrompt(workspace, profile, locations, null, context, string.Empty, string.Empty, string.Empty, null);
        var bare = IdeationSystemPromptBuilder.BuildSystemPrompt(
            workspace, profile, locations, null,
            new AgentPromptContext("<project_context>p</project_context>", string.Empty, Array.Empty<string>()),
            string.Empty, string.Empty, string.Empty, null);

        Assert.Contains("<memory_guidelines>", prompt);
        Assert.Contains("<workspace_memory>- vision</workspace_memory>", prompt);
        Assert.Contains("[Local] /workspace/memory/t.md", prompt);
        Assert.DoesNotContain("<memory_guidelines>", bare);
        Assert.DoesNotContain("<workspace_memory>", bare);
    }

    // ---- Notifier ----

    private static (AgentMemoryWriteNotifier Notifier, AgentMemoryMonitorService Monitor, InMemoryAgentActivityStreamer Streamer) CreateNotifier()
    {
        var streamer = new InMemoryAgentActivityStreamer();
        var monitor = new AgentMemoryMonitorService(new Mock<IWorkspacePathResolver>().Object, TimeProvider.System, NullLogger<AgentMemoryMonitorService>.Instance);
        return (new AgentMemoryWriteNotifier(monitor, streamer, TimeProvider.System, NullLogger<AgentMemoryWriteNotifier>.Instance), monitor, streamer);
    }

    private static async Task<List<AgentActivityEvent>> DrainAsync(InMemoryAgentActivityStreamer streamer, Func<Task> act)
    {
        var events = new List<AgentActivityEvent>();
        using var cts = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in streamer.SubscribeAsync(cancellationToken: cts.Token))
                {
                    events.Add(e);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the test stops listening.
            }
        });
        await Task.Delay(50);
        await act();
        await Task.Delay(100);
        await cts.CancelAsync();
        await reader;
        return events;
    }

    [Fact]
    public async Task Notifier_WriteEmitsMemoryUpdatedNoticeOnce_AndDuplicateIsSuppressed()
    {
        var (notifier, _, streamer) = CreateNotifier();
        var file = Path.Combine(_memoryDir, "vision.md");
        File.WriteAllText(file, "v1");
        var write = new AgentMemoryWriteEvent("/workspace/memory/vision.md", file, AgentMemoryScope.LocalWorkspace, AgentMemoryChangeKind.Created);

        var events = await DrainAsync(streamer, async () =>
        {
            Assert.True(await notifier.NotifyWriteAsync(WorkspaceId, 0, StandardStageIds.Ideation, "sess", write, CancellationToken.None));
            Assert.False(await notifier.NotifyWriteAsync(WorkspaceId, 0, StandardStageIds.Ideation, "sess", write, CancellationToken.None));
        });

        var evt = Assert.Single(events);
        Assert.Equal(AgentActivityKind.Notice, evt.Kind);
        Assert.Equal("Ideation created /workspace/memory/vision.md", evt.Content);
        Assert.Equal(AgentActivityMetadataKeys.ActionTypeMemoryUpdated, evt.Metadata![AgentActivityMetadataKeys.ActionType]);
        Assert.Equal(WorkspaceId, evt.Metadata[AgentActivityMetadataKeys.WorkspaceId]);
        Assert.Equal("Local", evt.Metadata[AgentActivityMetadataKeys.MemoryScope]);
        Assert.Equal("/workspace/memory/vision.md", evt.Metadata[AgentActivityMetadataKeys.MemoryRelativePath]);
        Assert.Equal("Created", evt.Metadata[AgentActivityMetadataKeys.MemoryChangeKind]);
    }

    [Fact]
    public async Task Notifier_SharedWriteNormalizesWorktreePrefixAndNamesTask()
    {
        var (notifier, _, streamer) = CreateNotifier();
        var file = Path.Combine(_repoDir, "arch.md");
        File.WriteAllText(file, "x");
        var write = new AgentMemoryWriteEvent("/workspace/task-42/.agents/memory/arch.md", file, AgentMemoryScope.SharedRepository, AgentMemoryChangeKind.Updated);

        var events = await DrainAsync(streamer, () => notifier.NotifyWriteAsync(WorkspaceId, 42, "stage-2", "sess", write, CancellationToken.None));

        var evt = Assert.Single(events);
        Assert.Equal("Task #42 (stage-2) updated .agents/memory/arch.md", evt.Content);
        Assert.Equal("SharedGit", evt.Metadata![AgentActivityMetadataKeys.MemoryScope]);
    }

    [Fact]
    public async Task Notifier_SharedWriteNormalizesDotSlashAndRelativeSegments_AndDeduplicatesSameSha()
    {
        var (notifier, _, streamer) = CreateNotifier();
        var file = Path.Combine(_repoDir, "AGENTS.md");
        File.WriteAllText(file, "rules");
        var sha = await AgentMemoryFileSystem.ComputeSha256Async(file, CancellationToken.None);

        var first = new AgentMemoryWriteEvent("./AGENTS.md", file, AgentMemoryScope.SharedRepository, AgentMemoryChangeKind.Updated, sha);
        var duplicate = new AgentMemoryWriteEvent("/workspace/task-42/./agents.md", file, AgentMemoryScope.SharedRepository, AgentMemoryChangeKind.Updated, sha);
        var agentsRules = new AgentMemoryWriteEvent("/workspace/task-42/.agents/AGENTS.md", Path.Combine(_repoDir, ".agents", "AGENTS.md"), AgentMemoryScope.SharedRepository, AgentMemoryChangeKind.Created, sha);

        var events = await DrainAsync(streamer, async () =>
        {
            Assert.True(await notifier.NotifyWriteAsync(WorkspaceId, 42, "stage-2", "sess", first, CancellationToken.None));
            Assert.False(await notifier.NotifyWriteAsync(WorkspaceId, 42, "stage-2", "sess", duplicate, CancellationToken.None));
            Assert.True(await notifier.NotifyWriteAsync(WorkspaceId, 42, "stage-2", "sess", agentsRules, CancellationToken.None));
        });

        Assert.Equal(2, events.Count);
        Assert.Equal("AGENTS.md", events[0].Metadata![AgentActivityMetadataKeys.MemoryRelativePath]);
        Assert.Equal(".agents/AGENTS.md", events[1].Metadata![AgentActivityMetadataKeys.MemoryRelativePath]);
    }

    [Fact]
    public async Task Notifier_NoneScopeIsIgnored()
    {
        var (notifier, _, streamer) = CreateNotifier();
        var file = Path.Combine(_repoDir, "a.cs");
        File.WriteAllText(file, "x");

        var events = await DrainAsync(streamer, async () =>
            Assert.False(await notifier.NotifyWriteAsync(WorkspaceId, 1, "s", "sess",
                new AgentMemoryWriteEvent("a.cs", file, AgentMemoryScope.None, AgentMemoryChangeKind.Updated), CancellationToken.None)));

        Assert.Empty(events);
    }

    [Fact]
    public async Task Notifier_NotifyChanges_SkipsChangesAlreadyRecordedByConcurrentCaller()
    {
        var (notifier, monitor, streamer) = CreateNotifier();
        var change = new AgentMemoryChangeEvent(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", AgentMemoryChangeKind.Updated, "sha");
        monitor.RecordWrite(WorkspaceId, AgentMemoryScope.LocalWorkspace, "/workspace/memory/a.md", "sha");
        IReadOnlyList<AgentMemoryChangeEvent> notified = Array.Empty<AgentMemoryChangeEvent>();

        var events = await DrainAsync(streamer, async () =>
            notified = await notifier.NotifyChangesAsync(42, "stage-2", "sess", new[] { change }, CancellationToken.None));

        Assert.Empty(notified);
        Assert.Empty(events);
    }

    [Fact]
    public async Task TurnTracker_PublishesOnlyChangesNotYetAttributed()
    {
        var resolver = new Mock<IWorkspacePathResolver>();
        resolver.Setup(r => r.GetWorkspaceMemoryPath(WorkspaceId)).Returns(_memoryDir);
        resolver.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        var streamer = new InMemoryAgentActivityStreamer();
        var monitor = new AgentMemoryMonitorService(resolver.Object, TimeProvider.System, NullLogger<AgentMemoryMonitorService>.Instance);
        var notifier = new AgentMemoryWriteNotifier(monitor, streamer, TimeProvider.System, NullLogger<AgentMemoryWriteNotifier>.Instance);
        var tracker = new AgentMemoryTurnTracker(monitor, notifier);

        var before = await tracker.CaptureAsync(WorkspaceId, null, CancellationToken.None);
        var bashFile = Path.Combine(_memoryDir, "from-bash.md");
        var ownFile = Path.Combine(_memoryDir, "from-tool.md");
        File.WriteAllText(bashFile, "bash");
        File.WriteAllText(ownFile, "tool");
        IReadOnlyList<AgentMemoryChangeEvent> published = Array.Empty<AgentMemoryChangeEvent>();

        var events = await DrainAsync(streamer, async () =>
        {
            await tracker.NotifyWriteAsync(WorkspaceId, 42, "stage-2", "sess",
                new AgentMemoryWriteEvent("/workspace/memory/from-tool.md", ownFile, AgentMemoryScope.LocalWorkspace, AgentMemoryChangeKind.Created), CancellationToken.None);
            published = await tracker.PublishChangesAsync(before, null, 42, "stage-2", "sess", CancellationToken.None);
        });

        var change = Assert.Single(published);
        Assert.Equal("/workspace/memory/from-bash.md", change.RelativePath);
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Content.EndsWith("from-tool.md"));
        Assert.Contains(events, e => e.Content.EndsWith("from-bash.md"));
    }
}
