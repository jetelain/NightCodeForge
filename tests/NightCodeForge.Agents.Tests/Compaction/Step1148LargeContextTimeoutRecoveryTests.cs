using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class Step1148LargeContextTimeoutRecoveryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "step-11-48-" + Guid.NewGuid().ToString("N"));

    public Step1148LargeContextTimeoutRecoveryTests()
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
            BootstrapPrompt = "Implement feature 11.48 timeout recovery",
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

    private static string CreateLargeSerializedState(int tokenTarget)
    {
        // ~4 characters per token in English prose
        int charCount = tokenTarget * 4;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Initial user requirements: implement large context recovery."),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "MassiveData.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", new string('X', charCount))
            }),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call2", "read", new Dictionary<string, object?> { ["path"] = "Helper.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call2", "Helper content")
            }),
            new(ChatRole.Assistant, "Recent assistant progress message"),
            new(ChatRole.User, "Recent user follow-up prompt")
        };

        return CrucibleAgentHarness.SerializeSession(messages);
    }

    [Fact]
    public async Task CompactEmergencyAsync_DirectMessageList_CompactsBypassingThresholdAndCooldown()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 250_000,
            ReserveTokens = 10_000, // Trigger threshold: 240,000 tokens
            DebounceTurnCooldown = 10,
            KeepRecentTokens = 5_000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Task anchor: build high performance system."),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "A.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", new string('A', 80_000))
            }),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c2", "read", new Dictionary<string, object?> { ["path"] = "B.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c2", new string('B', 80_000))
            }),
            new(ChatRole.Assistant, "Recent progress update"),
            new(ChatRole.User, "Next step request")
        };

        // Even though 40k tokens < 240k threshold, CompactEmergencyAsync must execute compaction
        bool result = await strategy.CompactEmergencyAsync(messages);

        Assert.True(result);
        Assert.Equal("Task anchor: build high performance system.", messages[0].Text);
        Assert.Contains(ConversationHistorySerializer.CompactionCheckpointHeader, messages[1].Text);
        Assert.Contains("## Goal", messages[1].Text);
        Assert.Equal("Next step request", messages.Last().Text);
    }

    [Fact]
    public async Task CompactEmergencyAsync_WhenSummarizerTimesOut_FallsBackToDeterministicSummary()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 200_000,
            ReserveTokens = 8_000
        };

        var failingSummarizer = new Mock<IChatClient>();
        failingSummarizer
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Summarizer timed out"));

        var strategy = new PiStyleCrucibleCompactionStrategy(options, failingSummarizer.Object, NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Original task requirement."),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "File.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", new string('Z', 50_000))
            }),
            new(ChatRole.Assistant, "Recent assistant progress"),
            new(ChatRole.User, "Recent user guidance")
        };

        bool result = await strategy.CompactEmergencyAsync(messages);

        Assert.True(result);
        Assert.Contains(ConversationHistorySerializer.CompactionCheckpointHeader, messages[1].Text);
        Assert.Contains("## Goal", messages[1].Text);
        Assert.Contains("Resuming active stage execution from checkpoint.", messages[1].Text);
    }

    [Fact]
    public async Task CompactEmergencyAsync_WhenCallerTokenCancelled_PropagatesCancellation()
    {
        var options = new CrucibleCompactionOptions();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var cancellingSummarizer = new Mock<IChatClient>();
        cancellingSummarizer
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var strategy = new PiStyleCrucibleCompactionStrategy(options, cancellingSummarizer.Object, NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Anchor"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "A.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", "Result")
            }),
            new(ChatRole.User, "Follow-up")
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => strategy.CompactEmergencyAsync(messages, cts.Token));
    }

    [Fact]
    public async Task Harness_WhenTimeoutPolicyExhausted_AndContextBelowThreshold_FailsFastWithoutCompaction()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 101, Title = "Small Context Timeout Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var streamer = new RecordingStreamer();
        int callCount = 0;

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            callCount++;
            throw new TimeoutException("Prompt prefill timed out");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 2,
                MaxTimeoutRetries = 2,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(2)
            })
        }.Build();

        // Standard small context (< 100k tokens)
        var context = CreateContext(task, contextWindowTokens: 250_000);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.True(callCount >= 2, $"Expected at least 2 retry attempts, got {callCount}");
        // No emergency compaction notice should have been broadcast
        Assert.DoesNotContain(streamer.Events, e => e.Content.Contains("Attempting emergency context compaction", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Harness_Streaming_WhenTimeoutPolicyExhausted_AndContextAboveThreshold_CompactsAndResumesSuccessfully()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 102, Title = "Large Context Timeout Recovery Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var streamer = new RecordingStreamer();
        int attemptCount = 0;
        var capturedPrompts = new List<List<ChatMessage>>();

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            attemptCount++;
            capturedPrompts.Add(messages.ToList());

            // First 3 attempts (initial + 2 timeout retries) fail with TimeoutException
            if (attemptCount <= 3)
            {
                throw new TimeoutException("Suspected KV cache loss cold prefill timed out");
            }

            // Resumed attempt after emergency compaction succeeds
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new TextContent("Successfully resumed and completed after emergency context compaction!"),
                    new UsageContent(new UsageDetails { InputTokenCount = 25_000, OutputTokenCount = 150 })
                ]
            };
        }

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(m => m.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(250_000);

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            GatewayService = gatewayMock.Object,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 2,
                MaxTimeoutRetries = 2,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(3)
            })
        }.Build();

        // Seed with ~105,000 tokens (above the 100k threshold, but below the 131k cold resume threshold)
        string largeState = CreateLargeSerializedState(105_000);
        var context = CreateContext(task, contextWindowTokens: 250_000, serializedState: largeState);

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.True(attemptCount >= 4, $"Expected at least 4 attempts (3 timeouts + 1 resumed success), got {attemptCount}");

        // Verify session affinity was released at least twice (once on emergency compaction, once on stage completion)
        var expectedSessionId = LlmSessionIds.ForStage(task.Id, "task-implementation");
        gatewayMock.Verify(
            g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()),
            Times.AtLeast(2));

        // Verify notices broadcasted
        Assert.Contains(streamer.Events, e => e.Content.Contains("Attempting emergency context compaction", StringComparison.Ordinal));
        Assert.Contains(streamer.Events, e => e.Content.Contains("Emergency context compaction recovered active context", StringComparison.Ordinal));

        // Verify compaction checkpoint turn persisted in ConversationTurns
        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        var checkpointTurn = Assert.Single(convWithTurns!.Turns, t => t.Role == StageTurnRole.Optimization);
        Assert.StartsWith(ConversationHistorySerializer.CompactionCheckpointHeader, checkpointTurn.Content);

        // Verify resumed prompt was compacted under 96k tokens
        var lastPrompt = capturedPrompts.Last();
        Assert.Contains(lastPrompt, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
        int postCompactionTokens = ModelCalibratedTokenEstimator.EstimateMessagesTokens(lastPrompt, "Qwen3.8-27B:latest");
        Assert.True(postCompactionTokens <= 96_000, $"Expected resumed prompt <= 96k tokens, got {postCompactionTokens}");
    }

    [Fact]
    public async Task Harness_Streaming_IfEmergencyCompactionFails_FailsFast()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 103, Title = "Compaction Failure Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var streamer = new RecordingStreamer();
        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new TimeoutException("Timeout occurred");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 1,
                MaxTimeoutRetries = 1,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(2)
            })
        }.Build();

        // Seed with state that has no non-anchor groups to summarize (< 3 groups)
        var uncompactableMessages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('A', 420_000)) // huge anchor only
        };
        string uncompactableState = CrucibleAgentHarness.SerializeSession(uncompactableMessages);
        var context = CreateContext(task, contextWindowTokens: 250_000, serializedState: uncompactableState);

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Harness_Streaming_IfResumedCallTimesOutAgain_FailsFastWithoutLooping()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 104, Title = "Secondary Timeout Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var streamer = new RecordingStreamer();
        int totalAttempts = 0;

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            totalAttempts++;
            // Always time out
            throw new TimeoutException("Persistent timeout");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 1,
                MaxTimeoutRetries = 1,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(2)
            })
        }.Build();

        string largeState = CreateLargeSerializedState(105_000);
        var context = CreateContext(task, contextWindowTokens: 250_000, serializedState: largeState);

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Failed, result.Status);

        // First pass: 2 attempts (initial + 1 timeout retry) -> compaction -> second pass: 2 attempts (initial + 1 timeout retry) -> fail fast
        // Exactly 4 total attempts, no infinite loop!
        Assert.Equal(4, totalAttempts);

        // Emergency compaction was attempted exactly ONCE
        int attemptNotices = streamer.Events.Count(e => e.Content.Contains("Attempting emergency context compaction", StringComparison.Ordinal));
        Assert.Equal(1, attemptNotices);
    }

    [Fact]
    public async Task Harness_NonStreaming_WhenTimeoutPolicyExhausted_AndContextAboveThreshold_CompactsAndResumesSuccessfully()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 105, Title = "Non-Streaming Large Context Timeout Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var streamer = new RecordingStreamer();
        int nonStreamingAttempts = 0;

        // Force fallback to non-streaming by throwing NotSupportedException on streaming
        IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken ct)
        {
            throw new NotSupportedException("Streaming not supported");
        }

        Task<ChatResponse> ResponseHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            CancellationToken ct)
        {
            nonStreamingAttempts++;

            // First 3 non-streaming attempts fail with TimeoutException
            if (nonStreamingAttempts <= 3)
            {
                throw new TimeoutException("Non-streaming LLM prefill timed out");
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Completed via non-streaming after compaction!")));
        }

        var client = new DualModeScriptedChatClient(StreamHandler, ResponseHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 2,
                MaxTimeoutRetries = 2,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(3)
            })
        }.Build();

        string largeState = CreateLargeSerializedState(105_000);
        var context = CreateContext(task, contextWindowTokens: 250_000, serializedState: largeState);

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.True(nonStreamingAttempts >= 4, $"Expected at least 4 non-streaming attempts, got {nonStreamingAttempts}");

        // Verify notices broadcasted
        Assert.Contains(streamer.Events, e => e.Content.Contains("Attempting emergency context compaction", StringComparison.Ordinal));
        Assert.Contains(streamer.Events, e => e.Content.Contains("Emergency context compaction recovered active context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactEmergencyAsync_WhenSummaryDoesNotReduceTokenCount_ReturnsFalseWithoutInvokingOnCompacted()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 250_000,
            ReserveTokens = 10_000,
            KeepRecentTokens = 500
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);
        bool onCompactedCalled = false;
        strategy.OnCompacted = (_, _) =>
        {
            onCompactedCalled = true;
            return Task.CompletedTask;
        };

        // Tiny middle turn so deterministic summary + preamble is larger than the summarized turn itself
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hi"),
            new(ChatRole.Assistant, "Ok"),
            new(ChatRole.User, new string('R', 4_000))
        };

        bool result = await strategy.CompactEmergencyAsync(messages);

        Assert.False(result);
        Assert.False(onCompactedCalled);
        Assert.Equal(3, messages.Count);
        Assert.DoesNotContain(messages, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
    }

    [Fact]
    public async Task CompactEmergencyAsync_WhenPriorCheckpointExistsBeforeToolChain_SplitsToolChainAndCompactsSuccessfully()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 250_000,
            ReserveTokens = 10_000,
            KeepRecentTokens = 5_000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        // groups[0] = User Anchor, groups[1] = Summary Checkpoint, groups[2] = Consecutive ToolCall chain (groups.Count == 3)
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Task anchor: implement multi-pass autonomous workflow."),
            new(ChatRole.User, $"{PiStyleCrucibleCompactionStrategy.CheckpointPreamble}## Goal\nInitial pass summary\n\n## Progress\n### Done\n- [x] Phase 1"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "Part1.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", new string('A', 60_000))
            }),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c2", "read", new Dictionary<string, object?> { ["path"] = "Part2.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c2", new string('B', 60_000))
            }),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c3", "read", new Dictionary<string, object?> { ["path"] = "Part3.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c3", "Small recent tool result")
            })
        };

        bool result = await strategy.CompactEmergencyAsync(messages);

        Assert.True(result);
        Assert.Equal(1, messages.Count(PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage));
        Assert.Contains("Part1.cs", messages[1].Text);
    }

    [Fact]
    public async Task CompactEmergencyAsync_WhenMeasuredInputTokensHigh_ButEstimatedGroupTokensDoNotShrink_ReturnsFalse()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 250_000,
            ReserveTokens = 10_000,
            KeepRecentTokens = 500
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);
        // Simulate server having reported 150,000 input tokens
        strategy.RecordMeasuredCallTokens(inputTokens: 150_000, outputTokens: 200, sentIncludedMessageCount: 2, responseMessageCount: 1);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Hi"),
            new(ChatRole.Assistant, "Ok"),
            new(ChatRole.User, new string('R', 4_000))
        };

        bool result = await strategy.CompactEmergencyAsync(messages);

        Assert.False(result);
        Assert.DoesNotContain(messages, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
    }

    [Fact]
    public async Task Harness_Streaming_WhenDanglingToolCallAndPartialReasoningEmittedBeforeTimeoutExhaustion_SanitizesHistoryAndDbTurnsBeforeCompaction()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 106, Title = "Dangling Tool Call Sanitization Before Emergency Compaction", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        string helperFilePath = Path.Combine(_tempDir, "SmallHelper.cs");
        await File.WriteAllTextAsync(helperFilePath, "// small helper file");

        var streamer = new RecordingStreamer();
        int streamingCallCount = 0;
        List<ChatMessage>? resumedPrompt = null;

        async IAsyncEnumerable<ChatResponseUpdate> StreamHandler(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var msgList = messages.ToList();
            streamingCallCount++;

            if (streamingCallCount == 1)
            {
                // Call 1: Emits a tool call that executes, followed by Call 2 which times out mid-stream
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents =
                    [
                        new FunctionCallContent("call_dangling_timeout", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "SmallHelper.cs" })
                    ]
                };
                yield break;
            }

            // Calls 2, 3, 4 (initial + 2 timeout retries): Emit partial reasoning/text and then time out
            if (streamingCallCount <= 4)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextReasoningContent("Partial reasoning before stall...")]
                };
                throw new TimeoutException("Cold prefill timed out after dangling tool call");
            }

            // Call 5 (after emergency compaction): Verify dangling tool call was sanitized and history was compacted
            resumedPrompt = msgList;
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Completed cleanly after sanitizing dangling turn and compacting!")]
            };
        }

        var client = new DualModeScriptedChatClient(StreamHandler);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo,
            ActivityStreamer = streamer,
            RetryOptions = Options.Create(new LlmRetryPolicyOptions
            {
                MaxRetries = 2,
                MaxTimeoutRetries = 2,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxRetryDuration = TimeSpan.FromSeconds(3)
            })
        }.Build();

        string largeState = CreateLargeSerializedState(105_000);
        var context = CreateContext(task, contextWindowTokens: 250_000, serializedState: largeState);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(resumedPrompt);
        Assert.Contains(resumedPrompt!, PiStyleCrucibleCompactionStrategy.IsCompactionCheckpointMessage);
        Assert.DoesNotContain(
            resumedPrompt!.SelectMany(m => m.Contents).OfType<FunctionCallContent>(),
            fc => fc.CallId == "call_dangling_timeout");
        Assert.Contains(streamer.Events, e => e.Content.Contains("Emergency context compaction recovered active context", StringComparison.Ordinal));

        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        Assert.DoesNotContain(
            convWithTurns!.Turns,
            t => t.Role == StageTurnRole.Thinking && t.ThinkingTrace != null && t.ThinkingTrace.Contains("Partial reasoning before stall", StringComparison.Ordinal));
    }
}
