using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Crucible.ChatClients;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class Step1147CompactionResumeAndTelemetryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "step-11-47-" + Guid.NewGuid().ToString("N"));

    public Step1147CompactionResumeAndTelemetryTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private sealed class DualModeScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> _streamHandler;
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _responseHandler;

        public DualModeScriptedChatClient(
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> streamHandler,
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? responseHandler = null)
        {
            _streamHandler = streamHandler;
            _responseHandler = responseHandler ?? ((_, _, _) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nCompacted summary goal\n\n## Progress\n### Done\n- [x] Summarized prior turns"))));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => _responseHandler(chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => _streamHandler(chatMessages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class RecordingStreamer : IAgentActivityStreamer
    {
        private readonly List<AgentActivityEvent> _events = new();
        public IReadOnlyList<AgentActivityEvent> Events
        {
            get { lock (_events) return _events.ToList(); }
        }

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            lock (_events) _events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(int? taskId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private AgentSessionContext CreateContext(
        ForgeTask task,
        int? contextWindowTokens = null,
        string? serializedState = null)
    {
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-implementation",
            BootstrapPrompt = "Implement feature 11.47",
            SerializedStateJson = serializedState,
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = new AgentProfile
            {
                Id = "dev",
                Name = "Developer Agent",
                ModelName = "Qwen3.8-27B:latest",
                Mode = AgentMode.Full,
                ContextWindowTokens = contextWindowTokens,
                PermittedTools = new[] { CanonicalToolNames.Read, CanonicalToolNames.Write }
            }
        };
    }

    [Fact]
    public async Task Case1_PauseAfterCompaction_PersistsOptimizationTurn_AndResumesUnder64kWithCheckpointIntact()
    {
        // Configure a 20k context window so compaction triggers after Turn 1's large tool output
        var repo = new InMemoryTaskRepository();
        var task = await repo.CreateAsync(new ForgeTask { Id = 11471, Title = "Case 1 Pause After Compaction", WorkspaceId = 1 });
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var streamer = new RecordingStreamer();
        var factoryLogger = new RecordingLogger<HarnessAgentFactory>();

        var sampleFile = Path.Combine(_tempDir, "Sample.cs");
        await File.WriteAllTextAsync(sampleFile, new string('A', 40_000));

        int streamingCallIndex = 0;
        var capturedPrompts = new List<List<ChatMessage>>();

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var currentCall = Interlocked.Increment(ref streamingCallIndex);
            var msgList = messages.ToList();
            lock (capturedPrompts)
            {
                capturedPrompts.Add(msgList);
            }
            await Task.Yield();

            if (currentCall == 1)
            {
                // Turn 1: Read the 40k-char file
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new FunctionCallContent("call_read_huge", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "Sample.cs" }),
                        new UsageContent(new UsageDetails { InputTokenCount = 1_500, OutputTokenCount = 120 })
                    ]
                };
                yield break;
            }

            if (currentCall == 2)
            {
                // Turn 2: Read a second time so groups.Count > 2 and total tokens exceed the 12k budget
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new FunctionCallContent("call_read_huge_2", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "Sample.cs" }),
                        new UsageContent(new UsageDetails { InputTokenCount = 14_500, OutputTokenCount = 120 })
                    ]
                };
                yield break;
            }

            if (currentCall == 3)
            {
                // Before call 3 reaches the inner client, CompactionStrategyChatReducer has compacted Turn 1!
                // Now pause during call 3.
                cancellationManager.RequestPause(task.Id);
                throw new OperationCanceledException();
            }

            // Call 4 (after resume): complete the stage
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("Completed after compacted resume."),
                    new UsageContent(new UsageDetails { InputTokenCount = 2_400, OutputTokenCount = 350 })
                ]
            };
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            CancellationManager = cancellationManager,
            ActivityStreamer = streamer,
            HarnessAgentLogger = factoryLogger
        }.Build();

        // Run 1: Compacts before Call 2, then pauses during Call 2
        var context1 = CreateContext(task, contextWindowTokens: 12_000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunStageAsync(context1));

        // Verify compaction logged to real ILogger and broadcasted Notice activity events
        Assert.Contains(factoryLogger.Messages, m => m.Contains("Pi-style context compaction succeeded", StringComparison.Ordinal));
        Assert.Contains(streamer.Events, e => e.Kind == AgentActivityKind.Notice && e.Content.Contains("Context compaction complete", StringComparison.Ordinal));

        // Verify StageTurnRole.Optimization checkpoint turn was persisted in ConversationTurns
        var convsAfterPause = await repo.GetConversationsAsync(task.Id);
        var pausedConv = Assert.Single(convsAfterPause);
        var pausedConvWithTurns = await repo.GetConversationWithTurnsAsync(pausedConv.Id);
        Assert.NotNull(pausedConvWithTurns);
        var checkpointTurn = Assert.Single(pausedConvWithTurns!.Turns, t => t.Role == StageTurnRole.Optimization);
        Assert.StartsWith("[CONTEXT COMPACTION CHECKPOINT]", checkpointTurn.Content);
        Assert.NotNull(checkpointTurn.MetadataJson);

        // Resume with SerializedStateJson = null (simulating TaskOrchestrator rebuilding from ConversationTurns)
        cancellationManager.ClearPause(task.Id);
        var context2 = CreateContext(task, contextWindowTokens: 12_000, serializedState: null);
        var resumeResult = await harness.RunStageAsync(context2);

        Assert.Equal(AgentRunStatus.Completed, resumeResult.Status);
        Assert.True(capturedPrompts.Count >= 3);

        var resumedPrompt = capturedPrompts[^1];
        Assert.Contains(resumedPrompt, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
        int resumedPromptTokens = ModelCalibratedTokenEstimator.EstimateMessagesTokens(resumedPrompt, "Qwen3.8-27B:latest");
        Assert.True(resumedPromptTokens < 64_000, $"Expected resumed prompt < 64,000 tokens, got {resumedPromptTokens}");

        // Verify Stage Token Accounting is non-zero
        Assert.True(resumeResult.Metrics.PromptTokens > 0);
        Assert.True(resumeResult.Metrics.CompletionTokens > 0);
    }

    [Fact]
    public async Task Case2_PauseBeforeCompactionAt80k_ArmsColdResumeGate_AndCompactsUnder64kBeforeFirstResumedCall()
    {
        // ContextWindowTokens = 225,280 (warm TriggerThreshold = 217,088).
        // Conversation is paused with ~80k tokens in ConversationTurns (below warm threshold, but >= 64k cold-resume ceiling).
        var repo = new InMemoryTaskRepository();
        var task = await repo.CreateAsync(new ForgeTask { Id = 11472, Title = "Case 2 Cold Resume Gate At 80k", WorkspaceId = 1 });
        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);

        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature 11.47");
        for (int i = 1; i <= 6; i++)
        {
            var callId = $"call_pre_{i}";
            await repo.AddConversationTurnAsync(
                conv.Id,
                StageTurnRole.Tool,
                toolName: CanonicalToolNames.Read,
                toolArguments: $"{{\"path\":\"File{i}.cs\"}}",
                metadataJson: $"{{\"CallId\":\"{callId}\"}}");
            await repo.AddConversationTurnAsync(
                conv.Id,
                StageTurnRole.ToolResult,
                toolName: CanonicalToolNames.Read,
                toolResult: new string((char)('A' + i), 48_000), // ~13.3k Qwen tokens per turn -> ~80k total
                metadataJson: $"{{\"CallId\":\"{callId}\"}}");
            await repo.AddConversationTurnAsync(
                conv.Id,
                StageTurnRole.Assistant,
                content: $"Analyzed File{i}.cs and recorded findings for step {i}.");
        }
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused at ~80k tokens");

        List<ChatMessage>? firstResumedCallMessages = null;
        List<ChatMessage>? summarizerCallMessages = null;

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            firstResumedCallMessages ??= messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Resumed cleanly after cold-resume compaction.");
        }

        Task<ChatResponse> SummarizerHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken ct)
        {
            summarizerCallMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nCold-resumed summary\n\n## Progress\n### Done\n- [x] Read File1..File5")));
        }

        var client = new DualModeScriptedChatClient(StreamHandler, SummarizerHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        // Warm ContextWindowTokens is 225,280 (TriggerThreshold = 217,088 > 80k), yet Cold-Resume Gate must compact because restored history >= 64k!
        var context = CreateContext(task, contextWindowTokens: 225_280, serializedState: null);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(summarizerCallMessages);
        int summarizerTokens = ModelCalibratedTokenEstimator.EstimateMessagesTokens(summarizerCallMessages, "Qwen3.8-27B:latest");
        Assert.True(summarizerTokens <= 60_000, $"Summarizer input tokens ({summarizerTokens}) exceeded 60,000 ceiling");

        Assert.NotNull(firstResumedCallMessages);
        Assert.Contains(firstResumedCallMessages!, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
        int firstResumedCallTokens = ModelCalibratedTokenEstimator.EstimateMessagesTokens(firstResumedCallMessages, "Qwen3.8-27B:latest");
        Assert.True(firstResumedCallTokens < 64_000, $"First resumed LLM call tokens ({firstResumedCallTokens}) must be < 64,000");

        var updatedConv = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.Contains(updatedConv!.Turns, t => t.Role == StageTurnRole.Optimization && ConversationHistorySerializer.IsCompactionCheckpointTurn(t));
    }

    [Fact]
    public async Task Case3_PauseBeforeCompactionAt25k_ResumesDirectlyWithoutTriggeringRedundantCompaction()
    {
        var repo = new InMemoryTaskRepository();
        var task = await repo.CreateAsync(new ForgeTask { Id = 11473, Title = "Case 3 Resume Under 64k", WorkspaceId = 1 });
        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);

        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature 11.47");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Read,
            toolArguments: "{\"path\":\"Small.cs\"}",
            metadataJson: "{\"CallId\":\"call_small_1\"}");
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: CanonicalToolNames.Read,
            toolResult: new string('S', 20_000), // ~7k tokens (< 64k)
            metadataJson: "{\"CallId\":\"call_small_1\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused under 64k");

        bool summarizerCalled = false;
        List<ChatMessage>? resumedMessages = null;

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            resumedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Resumed directly without compaction.");
        }

        Task<ChatResponse> SummarizerHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken ct)
        {
            summarizerCalled = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nShould not be called")));
        }

        var client = new DualModeScriptedChatClient(StreamHandler, SummarizerHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = CreateContext(task, contextWindowTokens: 225_280, serializedState: null);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.False(summarizerCalled);
        Assert.NotNull(resumedMessages);
        Assert.DoesNotContain(resumedMessages!, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
    }

    [Fact]
    public void ConversationSerializer_WhenHundredsOfToolTurnsExist_BoundsTranscriptWithinMaxTokens()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Initial task goal: refactor all 150 services")
        };

        for (int i = 1; i <= 150; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent($"Inspecting service #{i} and planning modifications..."),
                new FunctionCallContent($"c_{i}", "edit", new Dictionary<string, object?>
                {
                    ["path"] = $"src/Services/Service{i}.cs",
                    ["oldText"] = new string('O', 3_000),
                    ["newText"] = new string('N', 3_000)
                })
            }));
            messages.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent($"c_{i}", new string('R', 4_000))
            }));
        }

        var transcript = ConversationSerializer.SerializeConversation(
            messages,
            maxToolResultChars: 2_000,
            maxTokens: 50_000,
            modelId: "Qwen3.8-27B:latest");

        int estimatedTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(transcript, "Qwen3.8-27B:latest");
        Assert.True(estimatedTokens <= 50_000, $"Expected transcript <= 50,000 tokens, got {estimatedTokens}");
        Assert.Contains("Initial task goal", transcript);
        Assert.Contains("src/Services/Service150.cs", transcript);
    }

    [Fact]
    public void TelemetryChatClient_ExtractPromptPreview_IsTailAware_SkipsCompactionCheckpoint_AndPrefixesSummarizerCalls()
    {
        // 1. Multi-turn tool loop after compaction: must NOT return the [CONTEXT COMPACTION CHECKPOINT] message at index 1!
        var compactedToolLoopMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Initial task anchor"),
            new(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nFix bug"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call_99", "bash", new Dictionary<string, object?> { ["command"] = "dotnet test" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call_99", "Passed! - Failed: 0, Passed: 42")
            })
        };

        var toolResultPreview = TelemetryChatClient.ExtractPromptPreview(compactedToolLoopMessages);
        Assert.StartsWith("[Tool Result] Passed! - Failed: 0, Passed: 42", toolResultPreview);
        Assert.DoesNotContain("[CONTEXT COMPACTION CHECKPOINT]", toolResultPreview);

        // 2. Assistant tool call at tail
        var toolCallAtTailMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Initial task anchor"),
            new(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\nSummary"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("Running build"),
                new FunctionCallContent("call_100", "bash", new Dictionary<string, object?> { ["command"] = "dotnet build" })
            })
        };

        var toolCallPreview = TelemetryChatClient.ExtractPromptPreview(toolCallAtTailMessages);
        Assert.Contains("[Tool Call: bash]", toolCallPreview);
        Assert.Contains("command=dotnet build", toolCallPreview);

        // 3. Compaction Summarizer call: prefixed with "[Context Compaction Summarizer] "
        var summarizerMessages = new List<ChatMessage>
        {
            new(ChatRole.System, PiStyleCrucibleCompactionStrategy.SummarizationSystemPrompt),
            new(ChatRole.User, "<conversation>\n[User]: Implement X\n</conversation>\n\n" + PiStyleCrucibleCompactionStrategy.SummarizationPromptTemplate)
        };

        var summarizerPreview = TelemetryChatClient.ExtractPromptPreview(summarizerMessages);
        Assert.StartsWith(TelemetryChatClient.CompactionSummarizerPreviewPrefix, summarizerPreview);
        Assert.True(summarizerPreview.Length <= 500);
    }

    [Fact]
    public async Task LlmProviderAndRetryDefaults_Use10MinuteSingleCallTimeout_And3TimeoutRetriesWithin30Minutes_AndRecordGlobalRetryTelemetry()
    {
        var providerOptions = new LlmProviderOptions();
        Assert.Equal(10, providerOptions.RequestTimeoutMinutes);

        var retryOptions = new LlmRetryPolicyOptions();
        Assert.Equal(3, retryOptions.MaxTimeoutRetries);
        Assert.Equal(TimeSpan.FromMinutes(30), retryOptions.MaxRetryDuration);

        // Verify BroadcastRetryNoticeAsync records global retry telemetry on IOperationsTelemetryService correlated via HTTP headers
        var mockTelemetry = new Mock<IOperationsTelemetryService>();
        var mockOps = new Mock<IOperationsControlService>();
        var mockStreamer = new Mock<IAgentActivityStreamer>();

        RetryNotificationContext? capturedRetryCtx = null;
        int? capturedTaskId = null;
        string? capturedStageId = null;
        string? capturedSessionId = null;

        mockTelemetry
            .Setup(t => t.RecordLlmCallRetry(It.IsAny<RetryNotificationContext>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<RetryNotificationContext, int?, string?, string?>((ctx, tId, sId, sessId) =>
            {
                capturedRetryCtx = ctx;
                capturedTaskId = tId;
                capturedStageId = sId;
                capturedSessionId = sessId;
            });

        using var factory = new StandaloneOllamaChatClientFactory(
            Options.Create(providerOptions),
            NullLoggerFactory.Instance,
            mockTelemetry.Object,
            mockOps.Object,
            TimeProvider.System,
            Options.Create(retryOptions),
            mockStreamer.Object);

        using var httpClient = new HttpClient();
        var sessionId = LlmSessionIds.ForStage(105, "task-implementation");
        LlmCorrelationHeaders.Apply(httpClient, sessionId);

        var method = typeof(LlmChatClientFactoryBase).GetMethod(
            "BroadcastRetryNoticeAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var retryCtx = new RetryNotificationContext(
            new Uri("http://localhost:11434/api/chat"),
            HttpMethod.Post,
            Headers: httpClient.DefaultRequestHeaders,
            Attempt: 2,
            MaxRetries: 20,
            Delay: TimeSpan.FromSeconds(3),
            ElapsedDuration: TimeSpan.FromMinutes(10),
            MaxDuration: TimeSpan.FromMinutes(30),
            ErrorMessage: "The request timed out after 600 seconds.",
            StatusCode: null);

        var task = (Task)method.Invoke(factory, [retryCtx, null, CancellationToken.None])!;
        await task;

        Assert.NotNull(capturedRetryCtx);
        Assert.Equal(2, capturedRetryCtx!.Attempt);
        Assert.Equal(105, capturedTaskId);
        Assert.Equal("task-implementation", capturedStageId);
        Assert.Equal(sessionId, capturedSessionId);
    }

    [Fact]
    public void ConversationSerializer_ElideMiddlePartsToFitTokenBudget_ShrinksTightBudgetWithoutStackingTruncationSuffixes()
    {
        // Reviewer Issue #6: Verify that when Head + Tail exceed a tight maxTokens budget and the final shrinking loop runs,
        // TruncateForSummary does not stack duplicate "[... more characters truncated]" suffixes and meets the budget.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('H', 4_000)),
            new(ChatRole.Assistant, new string('M', 4_000)),
            new(ChatRole.User, new string('T', 4_000))
        };

        var transcript = ConversationSerializer.SerializeConversation(
            messages,
            maxToolResultChars: 200,
            maxTokens: 250,
            modelId: "Qwen3.8-27B:latest");

        int tokens = ModelCalibratedTokenEstimator.EstimateTextTokens(transcript, "Qwen3.8-27B:latest");
        Assert.True(tokens <= 250, $"Expected transcript <= 250 tokens, got {tokens}");

        // Must contain at most one final truncation notice from the shrinking loop (never stacked)
        int suffixOccurrences = transcript.Split("more characters truncated]").Length - 1;
        Assert.True(suffixOccurrences <= 2, $"Expected no stacked final truncation suffixes, found {suffixOccurrences} in:\n{transcript}");
    }

    [Fact]
    public async Task PerServiceCallChatHistoryPersistingChatClient_WhenOnPersistAsyncThrows_PropagatesExceptionImmediately()
    {
        // Reviewer Issue #2: Persistence failure when persisting a newly created compaction checkpoint must fail fast, not be swallowed.
        var compactedMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Task anchor"),
            new(ChatRole.User, $"{ConversationHistorySerializer.CompactionCheckpointHeader}\n## Goal\nSummary")
        };

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));

        var persistingClient = new PerServiceCallChatHistoryPersistingChatClient(
            innerMock.Object,
            _ => throw new InvalidOperationException("Simulated DB checkpoint persistence failure"),
            NullLogger.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistingClient.GetResponseAsync(compactedMessages, cancellationToken: CancellationToken.None));
        Assert.Contains("Simulated DB checkpoint persistence failure", ex.Message);
    }

    [Fact]
    public async Task ResolveSerializedState_WhenDbTurnsHaveNewerCheckpointThanStaleLargerSerializedStateJson_PrefersDbTurns()
    {
        // Reviewer Issue #9: If context.SerializedStateJson has an older checkpoint with more messages (e.g., 6 messages),
        // while ConversationTurns in the DB has a newer compaction checkpoint that compacted the history down to fewer messages (e.g., 3 messages),
        // the coordinator must prefer the newer DB checkpoint rather than the stale SerializedStateJson.
        var repo = new InMemoryTaskRepository();
        var task = await repo.CreateAsync(new ForgeTask { Id = 11479, Title = "Stale SerializedState vs Newer DB Checkpoint", WorkspaceId = 1 });
        var conv = await repo.StartConversationAsync(task.Id, "task-implementation", WorkflowStageKind.Agentic, 1);

        // Persist turns in DB with a NEWER checkpoint ("## Goal\nNewer DB Checkpoint v2") that cuts at TurnIndex = 4
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Initial task anchor"); // TurnIndex 1
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Old turn 2");      // TurnIndex 2
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Old turn 3");      // TurnIndex 3
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Optimization,
            content: $"{ConversationHistorySerializer.CompactionCheckpointHeader}\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nNewer DB Checkpoint v2",
            metadataJson: "{\"IsCompactionCheckpoint\":true,\"CutTurnIndex\":4,\"SummarizedGroupCount\":2}"); // TurnIndex 4
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Recent turn after v2 compaction"); // TurnIndex 5
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused after v2 compaction");

        // Construct a STALE SerializedStateJson with an OLDER checkpoint ("## Goal\nStale Checkpoint v1") and MORE messages (6 > 3)
        var staleTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 1, Role = StageTurnRole.User, Content = "Initial task anchor" },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.Optimization,
                Content = $"{ConversationHistorySerializer.CompactionCheckpointHeader}\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nStale Checkpoint v1",
                MetadataJson = "{\"IsCompactionCheckpoint\":true,\"CutTurnIndex\":2}"
            },
            new() { TurnIndex = 3, Role = StageTurnRole.Assistant, Content = "Turn A after v1" },
            new() { TurnIndex = 4, Role = StageTurnRole.User, Content = "Turn B after v1" },
            new() { TurnIndex = 5, Role = StageTurnRole.Assistant, Content = "Turn C after v1" },
            new() { TurnIndex = 6, Role = StageTurnRole.User, Content = "Turn D after v1" }
        };
        var staleSerializedStateJson = ConversationHistorySerializer.Serialize(staleTurns);

        List<ChatMessage>? capturedResumedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            capturedResumedMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Completed with newer DB checkpoint.");
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = CreateContext(task, contextWindowTokens: 225_280, serializedState: staleSerializedStateJson);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(capturedResumedMessages);
        Assert.Contains(capturedResumedMessages!, m => m.Text != null && m.Text.Contains("Newer DB Checkpoint v2", StringComparison.Ordinal));
        Assert.DoesNotContain(capturedResumedMessages!, m => m.Text != null && m.Text.Contains("Stale Checkpoint v1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PiStyleCrucibleCompactionStrategy_SmallWindowModel_CapsCeilingBelowTriggerThreshold_DisarmsColdResume_AndTracksSummarizerTokens()
    {
        // Reviewer Issues #7, #10, and Minor:
        // 1. DefaultMaxConversationTokens is derived from DefaultMaxSummarizerInputTokens - SummarizerPromptOverheadReserveTokens.
        Assert.Equal(
            CrucibleCompactionOptions.DefaultMaxSummarizerInputTokens - CrucibleCompactionOptions.SummarizerPromptOverheadReserveTokens,
            ConversationSerializer.DefaultMaxConversationTokens);

        // 2. For a model with ContextWindowTokens = 16,000 (< 64,000) and ReserveTokens = 4,000 (TriggerThreshold = 12,000),
        //    cold-resume threshold and compacted ceiling must be capped at 12,000 (never 64,000).
        var summarizerMock = new Mock<IChatClient>();
        summarizerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nSummarized goal"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 3_210,
                    OutputTokenCount = 432
                }
            });

        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 16_000,
            ReserveTokens = 4_000, // TriggerThresholdTokens = 12,000
            KeepRecentTokens = 2_000,
            MaxCompactedContextTokens = 65_536
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, summarizerMock.Object, NullLogger.Instance);
        strategy.ArmColdResumeCompaction();
        Assert.True(strategy.IsColdResumeCompactionArmed);

        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        var anchor = (CompactionMessageGroup)ctor.Invoke([CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor") }, 4_000, 1_000, 0]);
        var middle = (CompactionMessageGroup)ctor.Invoke([CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "Work") }, 48_000, 12_000, 0]);
        var recent = (CompactionMessageGroup)ctor.Invoke([CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Recent") }, 4_000, 1_000, 0]);
        var index = new CompactionMessageIndex([anchor, middle, recent], null!);

        bool compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted);

        // Cold-resume flag must be disarmed after evaluation
        Assert.False(strategy.IsColdResumeCompactionArmed);

        // Summarizer token usage must be tracked on the strategy
        Assert.Equal(3_210, strategy.TotalSummarizerInputTokens);
        Assert.Equal(432, strategy.TotalSummarizerOutputTokens);
    }
}
