using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow.Filtering;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Tests.Crucible;

public sealed class CrucibleConversationDecouplingTests : IDisposable
{
    private readonly string _tempDir;

    public CrucibleConversationDecouplingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-conv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CrucibleConversationState_WithInMemoryTurnSink_RecordsTurnsAndSanitizesWithoutTaskRepository()
    {
        var sink = new InMemoryConversationTurnSink();
        var conversation = new Conversation { Id = 1, StageId = StandardStageIds.Implementation };
        var state = new CrucibleConversationState(conversation, attemptIndex: 1, sink, NullLogger.Instance);

        state.EnqueueTurn(StageTurnRole.User, "err", content: "Solve task");
        state.EnqueueTurn(StageTurnRole.Assistant, "err", content: "Calling tool");
        state.EnqueueTurn(
            StageTurnRole.Tool,
            "err",
            toolName: CanonicalToolNames.Bash,
            toolArguments: """{"command":"dotnet test"}""",
            metadataJson: JsonSerializer.Serialize(new { CallId = "call-1" }));
        await state.DrainAsync();

        Assert.Equal(3, sink.Turns.Count);
        Assert.Equal(3, state.GetTrackedTurnsSnapshot().Count);

        await state.SanitizeTrailingInterruptedTurnsAsync(CancellationToken.None);

        // Trailing interrupted assistant + unanswered Tool turns are trimmed back to the User turn
        Assert.Single(sink.Turns);
        Assert.Single(state.GetTrackedTurnsSnapshot());
        Assert.Equal(StageTurnRole.User, sink.Turns[0].Role);
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_CapturesTurnsAndWritesTranscriptToTargetPath()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoDir);

        var targetTranscript = Path.Combine(_tempDir, "bench-out", "transcripts", "cand-1", "inst-1.md");
        var capture = new InMemoryConversationCapture(targetTranscript);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Bench task", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench Profile", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Fix the bug",
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);
        state.EnqueueTurn(StageTurnRole.Assistant, "err", content: "Done.");

        var fileName = await coordinator.SaveTranscriptAsync(
            state,
            new CrucibleTranscriptRequest(
                context,
                repoDir,
                state.ChatHistory,
                ElapsedMs: 1200,
                PromptTokens: 100,
                CompletionTokens: 50,
                AgentRunStatus.Completed,
                Summary: "Completed",
                TracingClient: null,
                HarnessOptions: null,
                ActiveSkills: ["csharp-nav"]),
            CancellationToken.None);

        Assert.Equal("inst-1.md", fileName);
        Assert.Equal(targetTranscript, capture.SavedTranscriptPath);
        Assert.True(File.Exists(targetTranscript));
        Assert.False(Directory.Exists(Path.Combine(repoDir, "docs", "transcripts")));
        Assert.Equal(2, capture.TurnSink!.Turns.Count);
        Assert.Equal(StageExecutionStatus.Completed, capture.Conversation!.Status);
        Assert.NotNull(capture.CapturedMetrics);
        Assert.Equal(1200, capture.CapturedMetrics!.DurationMs);
        Assert.Equal(100, capture.CapturedMetrics.PromptTokens);
        Assert.Equal(50, capture.CapturedMetrics.CompletionTokens);
        Assert.Equal(150, capture.CapturedMetrics.TotalTokens);
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_ReusingCaptureAcrossBeginAsync_ClearsStaleMetricsAndTranscriptPath()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-reuse");
        Directory.CreateDirectory(repoDir);

        var targetTranscript = Path.Combine(_tempDir, "bench-out", "transcripts", "cand-1", "reuse.md");
        var capture = new InMemoryConversationCapture(targetTranscript)
        {
            SavedTranscriptPath = "/stale/path.md",
            TotalModelCalls = 42,
            CapturedMetrics = new AgentRunMetrics(DurationMs: 999, PromptTokens: 111, CompletionTokens: 222, TotalTokens: 333)
        };

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Reuse task", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench Profile", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Fix the bug",
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Null(capture.SavedTranscriptPath);
        Assert.Equal(0, capture.TotalModelCalls);
        Assert.Null(capture.CapturedMetrics);
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_ReusingCapture_IsolatesTurnSinkAcrossRuns()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-sink-reset");
        Directory.CreateDirectory(repoDir);

        var capture = new InMemoryConversationCapture(Path.Combine(_tempDir, "bench-out", "sink-reset.md"));
        var context1 = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Run 1", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "First prompt",
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        var state1 = await coordinator.BeginAsync(context1, StandardHarnessIds.Crucible, CancellationToken.None);
        state1.EnqueueTurn(StageTurnRole.Assistant, "err", content: "First answer");
        await state1.DrainAsync();
        var firstSink = capture.TurnSink;
        Assert.Equal(2, firstSink.Turns.Count);

        var context2 = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Run 2", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Second prompt",
            WorkspacePath = repoDir
        };

        var state2 = await coordinator.BeginAsync(context2, StandardHarnessIds.Crucible, CancellationToken.None);
        await state2.DrainAsync();

        Assert.NotSame(firstSink, capture.TurnSink);
        var singleTurn = Assert.Single(capture.TurnSink.Turns);
        Assert.Equal(0, singleTurn.TurnIndex);
        Assert.Equal("Second prompt", singleTurn.Content);
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_BareFilenameTargetTranscriptPath_WritesUnderWorkspaceTranscriptsWithoutThrowing()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-bare-name");
        Directory.CreateDirectory(repoDir);

        var capture = new InMemoryConversationCapture("bare-inst.md");
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Bare path", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Fix bug",
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var fileName = await coordinator.SaveTranscriptAsync(
            state,
            new CrucibleTranscriptRequest(
                context,
                repoDir,
                state.ChatHistory,
                ElapsedMs: 100,
                PromptTokens: 10,
                CompletionTokens: 5,
                AgentRunStatus.Completed,
                Summary: "Done",
                TracingClient: null,
                HarnessOptions: null,
                ActiveSkills: null),
            CancellationToken.None);

        var expectedPath = Path.Combine(repoDir, "docs", "transcripts", "bare-inst.md");
        Assert.Equal("bare-inst.md", fileName);
        Assert.Equal(expectedPath, capture.SavedTranscriptPath);
        Assert.True(File.Exists(expectedPath));
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_PreFilteredPrimingAndSerializedState_AlignWithCoordinator()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-primed-resumed");
        Directory.CreateDirectory(repoDir);

        var preFilteredOutput = "Exact pre-filtered output\nLine 2\nLine 3";
        var primingContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Primed", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Fix build",
            WorkspacePath = repoDir,
            SyntheticPriming = new SyntheticPrimingExecution(
                SourceStageId: StandardStageIds.PreImplementationBaseline,
                ToolName: CanonicalToolNames.Bash,
                Command: "dotnet build",
                Output: preFilteredOutput,
                IsSuccess: false,
                DurationMs: 42,
                IsPreFiltered: true)
        };

        var primingCapture = new InMemoryConversationCapture();
        using (InMemoryCrucibleConversationCoordinator.BeginCapture(primingCapture))
        {
            var primedState = await coordinator.BeginAsync(primingContext, StandardHarnessIds.Crucible, CancellationToken.None);
            await primedState.DrainAsync();

            var resultMsg = primedState.ChatHistory
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Single();
            Assert.Equal(preFilteredOutput, resultMsg.Result?.ToString());

            var toolTurn = Assert.Single(primingCapture.TurnSink.Turns, t => t.Role == StageTurnRole.Tool);
            using var metaDoc = JsonDocument.Parse(toolTurn.MetadataJson!);
            Assert.True(metaDoc.RootElement.GetProperty("IsPrimed").GetBoolean());
            Assert.Equal(StandardStageIds.PreImplementationBaseline, metaDoc.RootElement.GetProperty("SourceStageId").GetString());
            Assert.Equal("LoopbackDiagnostic", metaDoc.RootElement.GetProperty("PrimingType").GetString());
            Assert.False(string.IsNullOrWhiteSpace(metaDoc.RootElement.GetProperty("CallId").GetString()));
        }

        var priorHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Original prompt"),
            new(ChatRole.Assistant, "Prior step completed")
        };
        var serialized = CrucibleAgentHarness.SerializeSession(priorHistory);
        var resumedContext = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Resumed", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Should not overwrite",
            SerializedStateJson = serialized,
            WorkspacePath = repoDir
        };

        var resumedState = await coordinator.BeginAsync(resumedContext, StandardHarnessIds.Crucible, CancellationToken.None);
        Assert.True(resumedState.IsResumedConversation);
        Assert.Equal(2, resumedState.ChatHistory.Count);
        Assert.Equal("Original prompt", resumedState.ChatHistory[0].Text);
        Assert.Equal("Prior step completed", resumedState.ChatHistory[1].Text);

        var waitingHistory = new List<ChatMessage>
        {
            new(ChatRole.User, "Clarify DB choice"),
            new(ChatRole.Assistant, [new FunctionCallContent("call-read-1", CanonicalToolNames.Read)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-read-1", "appsettings.json")]),
            new(ChatRole.Assistant, [new FunctionCallContent("call-ask-1", CanonicalToolNames.Ask)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-ask-1", NightCodeForge.Core.Workflow.ConversationHistorySerializer.InterruptedToolResult)])
        };
        var waitingSerialized = CrucibleAgentHarness.SerializeSession(waitingHistory);
        var resumedAskCapture = new InMemoryConversationCapture();
        using (InMemoryCrucibleConversationCoordinator.BeginCapture(resumedAskCapture))
        {
            var resumedAskContext = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 0, Title = "Resumed Ask", Description = "Desc" },
                Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
                Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
                StageId = StandardStageIds.Implementation,
                BootstrapPrompt = "Clarify DB choice",
                SerializedStateJson = waitingSerialized,
                PendingHumanAnswer = "Use PostgreSQL",
                WorkspacePath = repoDir
            };

            var resumedAskState = await coordinator.BeginAsync(resumedAskContext, StandardHarnessIds.Crucible, CancellationToken.None);
            await resumedAskState.DrainAsync();

            Assert.True(resumedAskState.IsToolCallRecorded("call-read-1"));
            Assert.True(resumedAskState.IsToolCallRecorded("call-ask-1"));

            var askResult = resumedAskState.ChatHistory
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
                .Single(r => r.CallId == "call-ask-1");
            Assert.Equal("Use PostgreSQL", askResult.Result?.ToString());
            Assert.Equal(ChatRole.User, resumedAskState.ChatHistory[^1].Role);
            Assert.Contains(resumedAskCapture.TurnSink.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask && t.ToolResult == "Use PostgreSQL");
        }
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_ConcurrentSaveTranscript_UsesIsolatedStagingDirsWithoutCollisions()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-conc");
        var sharedTranscriptDir = Path.Combine(_tempDir, "shared-transcripts", "cand-1");
        Directory.CreateDirectory(repoDir);

        var tasks = Enumerable.Range(0, 8).Select(async index =>
        {
            var targetPath = Path.Combine(sharedTranscriptDir, $"inst-{index}.md");
            var capture = new InMemoryConversationCapture(targetPath);
            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 0, Title = $"Task {index}", Description = "Desc" },
                Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
                Profile = new AgentProfile { Id = "bench-impl", Name = "Bench Profile", ModelName = "qwen3:8b" },
                StageId = StandardStageIds.Implementation,
                BootstrapPrompt = $"Prompt {index}",
                WorkspacePath = repoDir
            };

            using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
            var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);
            state.EnqueueTurn(StageTurnRole.Assistant, "err", content: $"Answer {index}");

            await coordinator.SaveTranscriptAsync(
                state,
                new CrucibleTranscriptRequest(
                    context,
                    repoDir,
                    state.ChatHistory,
                    ElapsedMs: 100 + index,
                    PromptTokens: 10,
                    CompletionTokens: 5,
                    AgentRunStatus.Completed,
                    Summary: $"Done-{index}",
                    TracingClient: null,
                    HarnessOptions: null,
                    ActiveSkills: null),
                CancellationToken.None);

            return (targetPath, index);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (targetPath, index) in results)
        {
            Assert.True(File.Exists(targetPath));
            var content = await File.ReadAllTextAsync(targetPath);
            Assert.Contains($"Prompt {index}", content);
        }

        Assert.Empty(Directory.GetDirectories(sharedTranscriptDir, ".tmp-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrucibleAgentHarness_UsesConversationStateSessionId_AndRecordsTerminalToolTelemetry(bool triggerAsk)
    {
        var repoDir = Path.Combine(_tempDir, triggerAsk ? "repo-ask" : "repo-rc");
        Directory.CreateDirectory(repoDir);

        var capturedSessionIds = new ConcurrentBag<string?>();

        var chatClient = new TerminalToolChatClient(triggerAsk);
        var chatClientFactory = new Mock<ILlmChatClientFactory>();
        chatClientFactory
            .Setup(f => f.CreateClient(It.IsAny<AgentProfile?>(), It.IsAny<string?>()))
            .Callback<AgentProfile?, string?>((_, sessionId) => capturedSessionIds.Add(sessionId))
            .Returns(chatClient);

        var gateway = new Mock<ILlmGatewayService>();
        gateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(65536);

        var llmOptions = Options.Create(new LlmProviderOptions());
        var agentFactory = new HarnessAgentFactory(
            chatClientFactory.Object,
            gateway.Object,
            new OllamaCapabilityResolver(llmOptions, EmptyLlmContextWindowCache.Instance),
            new Mock<IGlobalOptimizationSettingsService>().Object,
            llmOptions,
            NullLogger<HarnessAgentFactory>.Instance);

        var mcpManager = new Mock<IMcpClientManager>();
        mcpManager.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerDefinition>());

        var toolsetFactory = new CanonicalToolsetFactory(
            TestPathResolver.Create(),
            new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            mcpManager.Object,
            TestBacklogContext.CreateDefault(),
            CliOutputFilterRegistry.Default);

        var workspaceContext = new AgentWorkspaceContextService(
            TestPathResolver.Create(),
            new AgentsMdService(),
            new Mock<IWorkspaceSymbolService>().Object,
            new Mock<ITaskMarkdownSyncService>().Object,
            TestBacklogContext.CreateDefault(),
            new TranscriptAnalysisService(),
            new NoopAgentMemoryTurnTracker(),
            NullLogger<AgentWorkspaceContextService>.Instance);

        var coordinator = new InMemoryCrucibleConversationCoordinator(
            new SessionTranscriptService(Path.Combine(_tempDir, "transcripts")),
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var opsControl = new Mock<IOperationsControlService>();
        opsControl.Setup(o => o.IsPaused).Returns(false);

        var harness = new CrucibleAgentHarness(
            toolsetFactory,
            workspaceContext,
            agentFactory,
            coordinator,
            new InMemoryAgentActivityStreamer(),
            new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance),
            opsControl.Object,
            Options.Create(new LlmRetryPolicyOptions { MaxRetries = 1 }),
            NullLogger<CrucibleAgentHarness>.Instance,
            TimeProvider.System);

        var capture = new InMemoryConversationCapture(Path.Combine(_tempDir, "out", triggerAsk ? "ask.md" : "rc.md"));
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Stateless", Description = "Check terminal tools" },
            Workspace = new Workspace
            {
                Id = 0,
                Name = "bench",
                RepositoryPath = repoDir,
                GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.ForceDisabled
            },
            Profile = new AgentProfile
            {
                Id = "bench-prof",
                Name = "Bench",
                ModelName = "qwen3:8b",
                Mode = AgentMode.Full,
                PermittedTools = triggerAsk
                    ? [CanonicalToolNames.Read, CanonicalToolNames.Ask]
                    : [CanonicalToolNames.Read, CanonicalToolNames.RequestChanges]
            },
            StageId = triggerAsk ? StandardStageIds.TaskReview : StandardStageIds.CodeReviewAndFix,
            RequiredMode = AgentMode.Full,
            BootstrapPrompt = "Inspect",
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        var result = await harness.RunStageAsync(context, CancellationToken.None);

        var sessionId = Assert.Single(capturedSessionIds);
        Assert.NotNull(sessionId);
        Assert.StartsWith("inmem-", sessionId, StringComparison.Ordinal);

        var toolTurns = capture.TurnSink.Turns.Where(t => t.Role == StageTurnRole.Tool).ToList();
        var toolTurn = Assert.Single(toolTurns);

        if (triggerAsk)
        {
            Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
            Assert.Equal(CanonicalToolNames.Ask, toolTurn.ToolName);
        }
        else
        {
            Assert.Equal(AgentRunStatus.Completed, result.Status);
            Assert.Equal("Missing null check", result.ReviewerFeedback);
            Assert.Equal(CanonicalToolNames.RequestChanges, toolTurn.ToolName);
        }
    }

    [Fact]
    public async Task InMemoryCrucibleConversationCoordinator_BeginAsync_DrainsInitialTurnsBeforeReturning()
    {
        var transcriptService = new SessionTranscriptService(Path.Combine(_tempDir, "default-transcripts"));
        var coordinator = new InMemoryCrucibleConversationCoordinator(
            transcriptService,
            NullLogger<InMemoryCrucibleConversationCoordinator>.Instance);

        var repoDir = Path.Combine(_tempDir, "repo-drain");
        Directory.CreateDirectory(repoDir);

        var capture = new InMemoryConversationCapture(Path.Combine(_tempDir, "bench-out", "drain.md"));
        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 0, Title = "Drain test", Description = "Desc" },
            Workspace = new Workspace { Id = 0, Name = "bench", RepositoryPath = repoDir },
            Profile = new AgentProfile { Id = "bench-impl", Name = "Bench", ModelName = "qwen3:8b" },
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Initial prompt",
            SyntheticPriming = new SyntheticPrimingExecution(
                SourceStageId: StandardStageIds.PreImplementationBaseline,
                ToolName: CanonicalToolNames.Bash,
                Command: "dotnet build",
                Output: "Build failed",
                IsSuccess: false),
            WorkspacePath = repoDir
        };

        using var _ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        // Immediately upon BeginAsync returning (without an external DrainAsync call), turns must already be materialized.
        Assert.NotNull(capture.TurnSink);
        Assert.Equal(4, capture.TurnSink!.Turns.Count);
        Assert.Equal(4, state.GetTrackedTurnsSnapshot().Count);
        Assert.Contains(capture.TurnSink.Turns, t => t.Content == "Initial prompt");
        Assert.Contains(capture.TurnSink.Turns, t => t.Role == StageTurnRole.ToolResult && t.Content == "Build failed");
    }

    private sealed class TerminalToolChatClient(bool triggerAsk) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var toolCall = triggerAsk
                ? new FunctionCallContent(
                    "call-ask-1",
                    CanonicalToolNames.Ask,
                    new Dictionary<string, object?>
                    {
                        ["questions"] = new[]
                        {
                            new Dictionary<string, object?> { ["prompt"] = "Which framework target?" }
                        }
                    })
                : new FunctionCallContent(
                    "call-rc-1",
                    CanonicalToolNames.RequestChanges,
                    new Dictionary<string, object?>
                    {
                        ["feedback"] = "Missing null check",
                        ["reason"] = "Bug"
                    });

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [toolCall]
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
