using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Inference;
using Pmad.AI.Tokens;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class Step1145MeasuredTokenCompactionTests
{
    [Fact]
    public async Task CompactAsync_ForgeTask51Regression_WhenRawBytesDivFourBelowThreshold_ButMeasuredPlusQwenDeltaAboveThreshold_TriggersCompaction()
    {
        // ForgeTask #51 scenario:
        // ContextWindowTokens = 262,144, ReserveTokens = 8,192 => TriggerThresholdTokens = 253,952.
        // Raw UTF8ByteCount / 4 (index.IncludedTokenCount) is ~240,000 (< 253,952),
        // but Call N measured InputTokenCount = 253,000 + OutputTokenCount = 500,
        // and the new tool result adds ~1,000 Qwen tokens => ProjectedPromptTokens >= 254,500 (> 253,952).
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 262_144,
            ReserveTokens = 8_192,
            KeepRecentTokens = 16_384,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };
        Assert.Equal(253_952, options.TriggerThresholdTokens);

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchorGroup = CreateGroup(
            CompactionGroupKind.User,
            [new ChatMessage(ChatRole.User, "Generate and serve a runtime OpenAPI document for the /api/router/ management API")],
            tokenCount: 1_000);

        var oldCallMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call_old", "read", new Dictionary<string, object?> { ["path"] = "RouterApi.cs" })
        });
        var oldResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call_old", "public class RouterApi { /* historical code */ }")
        });
        var oldToolGroup = CreateGroup(CompactionGroupKind.ToolCall, [oldCallMsg, oldResultMsg], tokenCount: 238_000);

        // Call N assistant response + new tool result appended after Call N:
        var callNResponseMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("Checking endpoint schemas..."),
            new FunctionCallContent("call_133", "read", new Dictionary<string, object?> { ["path"] = "OpenApiGenerator.cs" })
        });
        // ~3,500 chars of C# code => ~1,000+ Qwen tokens
        var newToolPayload = string.Concat(Enumerable.Repeat("public record OpenApiOperation(string Id, string Path, int Status); // symbol-heavy C# line\n", 45));
        var newToolResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call_133", newToolPayload)
        });
        var recentToolGroup = CreateGroup(CompactionGroupKind.ToolCall, [callNResponseMsg, newToolResultMsg], tokenCount: 1_000);

        var index = new CompactionMessageIndex([anchorGroup, oldToolGroup, recentToolGroup]);

        // Verify raw MAF UTF8ByteCount/4 is well below TriggerThresholdTokens (240,000 < 253,952)
        Assert.Equal(240_000, index.IncludedTokenCount);
        Assert.True(index.IncludedTokenCount < options.TriggerThresholdTokens);

        // Record Call N server-measured usage: 3 messages were sent in Call N's prompt (anchor + oldCall + oldResult),
        // producing 1 assistant response message (callNResponseMsg), followed by 1 new tool result message (newToolResultMsg).
        strategy.RecordMeasuredCallTokens(
            inputTokens: 253_000,
            outputTokens: 500,
            sentIncludedMessageCount: 3,
            responseMessageCount: 1);

        int projected = strategy.CalculateProjectedPromptTokens(index);
        int expectedNewResultTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(newToolResultMsg, HeuristicTokenEstimator.Qwen);
        Assert.Equal(253_000 + 500 + expectedNewResultTokens, projected);
        Assert.True(projected > options.TriggerThresholdTokens, $"Expected projected tokens ({projected}) > threshold ({options.TriggerThresholdTokens})");

        // Act
        bool compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.True(compacted);
        Assert.True(oldToolGroup.IsExcluded);
        Assert.Contains(index.Groups, g => g.Kind == CompactionGroupKind.Summary && !g.IsExcluded);

        // Baseline must be reset immediately after compaction succeeds
        Assert.Equal(0, strategy.LastMeasuredInputTokens);
        Assert.Equal(0, strategy.LastMeasuredOutputTokens);
        Assert.Equal(0, strategy.LastMeasuredMessageCount);
    }

    [Fact]
    public async Task CompactAsync_AfterCompactionResetsBaseline_DoesNotFalselyRetriggerOnNextTurn()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 262_144,
            ReserveTokens = 8_192,
            KeepRecentTokens = 16_384,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchor = CreateGroup(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "Task anchor")], tokenCount: 500);
        var oldWork = CreateGroup(CompactionGroupKind.AssistantText, [new ChatMessage(ChatRole.Assistant, "Historical work")], tokenCount: 200_000);
        var recentCall = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "A.cs" })
        });
        var recentRes = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c1", new string('x', 4000))
        });
        var recentGroup = CreateGroup(CompactionGroupKind.ToolCall, [recentCall, recentRes], tokenCount: 1_500);

        var index = new CompactionMessageIndex([anchor, oldWork, recentGroup]);
        strategy.RecordMeasuredCallTokens(inputTokens: 254_000, outputTokens: 400, sentIncludedMessageCount: 2, responseMessageCount: 1);

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(0, strategy.LastMeasuredInputTokens);

        // Before the post-compaction LLM call completes, ProjectedPromptTokens uses the post-compaction included messages
        int postCompactionProjected = strategy.CalculateProjectedPromptTokens(index);
        Assert.True(postCompactionProjected < options.TriggerThresholdTokens);
        Assert.False(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task CompactAsync_WhenRecentCompactionOccurredOneTurnAgo_AndMeasuredSpikeCrossesThreshold_BypassesTurnCooldown()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 50_000,
            ReserveTokens = 5_000, // TriggerThreshold = 45,000
            KeepRecentTokens = 5_000,
            DebounceTurnCooldown = 3,
            DebounceTokenDelta = 2_500,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchor = CreateGroup(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "Task anchor")], tokenCount: 1_000);
        var turn1 = CreateGroup(CompactionGroupKind.AssistantText, [new ChatMessage(ChatRole.Assistant, "Turn 1")], tokenCount: 42_000);
        var turn2 = CreateGroup(CompactionGroupKind.AssistantText, [new ChatMessage(ChatRole.Assistant, "Turn 2")], tokenCount: 3_000);

        var index1 = new CompactionMessageIndex([anchor, turn1, turn2]);
        Assert.True(await strategy.CompactAsync(index1, NullLogger.Instance, CancellationToken.None));

        // 1 turn later (turnsSinceLastCompaction = 1 < DebounceTurnCooldown = 3):
        // A single tool call + huge output pushes server-measured + delta back above 45,000 with > 2,500 new tokens.
        var turn3Call = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c3", "read", new Dictionary<string, object?> { ["path"] = "Huge.cs" })
        });
        var turn3Result = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c3", new string('C', 20_000))
        });
        var turn3Group = CreateGroup(CompactionGroupKind.ToolCall, [turn3Call, turn3Result], tokenCount: 5_000);

        var groups2 = index1.Groups.ToList();
        groups2.Add(turn3Group);
        var index2 = new CompactionMessageIndex(groups2);

        // Included messages before turn3Result: anchor (1) + summary (1) + turn2 (1) = 3 sent messages, 1 assistant response (turn3Call)
        strategy.RecordMeasuredCallTokens(
            inputTokens: 41_000,
            outputTokens: 300,
            sentIncludedMessageCount: 3,
            responseMessageCount: 1);

        int projected = strategy.CalculateProjectedPromptTokens(index2);
        Assert.True(projected > options.TriggerThresholdTokens);

        // Turn cooldown must be bypassed to prevent overflowing the context window on the next call
        bool compacted2 = await strategy.CompactAsync(index2, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted2);
    }

    [Theory]
    [InlineData("Qwen3.8-27B:latest", "Qwen")]
    [InlineData("qwen2.5-coder:32b", "Qwen")]
    [InlineData("gemma3:27b", "Gemma")]
    [InlineData("Gemma-2-9b-it", "Gemma")]
    [InlineData("llama3.3:70b", "Default")]
    [InlineData(null, "Default")]
    public void ModelCalibratedTokenEstimator_ResolvesExpectedPmadEstimator(string? modelId, string expectedProfile)
    {
        var resolved = ModelCalibratedTokenEstimator.Resolve(modelId);
        var expected = expectedProfile switch
        {
            "Qwen" => HeuristicTokenEstimator.Qwen,
            "Gemma" => HeuristicTokenEstimator.Gemma,
            _ => HeuristicTokenEstimator.Default
        };

        Assert.Same(expected, resolved);
    }

    [Fact]
    public void ModelCalibratedTokenEstimator_IncludesTextReasoningToolCallsAndToolResults()
    {
        var baseMsg = new ChatMessage(ChatRole.Assistant, " Calling tool ");
        int baseTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(baseMsg, "Qwen3.8-27B:latest");

        var withReasoning = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(" Calling tool "),
            new TextReasoningContent("Extensive internal step-by-step reasoning about C# symbols and AST nodes.")
        });
        int reasoningTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(withReasoning, "Qwen3.8-27B:latest");
        Assert.True(reasoningTokens > baseTokens);

        var withToolCall = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent(" Calling tool "),
            new TextReasoningContent("Extensive internal step-by-step reasoning about C# symbols and AST nodes."),
            new FunctionCallContent("call_42", "edit", new Dictionary<string, object?>
            {
                ["path"] = "src/Services/RouterService.cs",
                ["oldText"] = "public void OldMethod() { }",
                ["newText"] = "public async Task NewMethodAsync(CancellationToken ct) { }"
            })
        });
        int toolCallTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(withToolCall, "Qwen3.8-27B:latest");
        Assert.True(toolCallTokens > reasoningTokens);

        var toolResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call_42", "Successfully updated src/Services/RouterService.cs (42 lines modified).")
        });
        int toolResultTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(toolResultMsg, "Qwen3.8-27B:latest");
        Assert.True(toolResultTokens > ModelCalibratedTokenEstimator.PerMessageFramingTokens);
    }

    [Fact]
    public async Task ThinkingAndActivityTracingChatClient_GetResponseAsync_CapturesMeasuredUsageAndForwardsToCallback()
    {
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Response text"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 123_456,
                OutputTokenCount = 789
            }
        };

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tracer = new ThinkingAndActivityTracingChatClient(innerMock.Object);
        MeasuredCallTokenUsage? captured = null;
        tracer.SetMeasuredUsageCallback(u => captured = u);

        var requestMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "First message"),
            new(ChatRole.Assistant, "Second message"),
            new(ChatRole.User, "Third message")
        };

        await tracer.GetResponseAsync(requestMessages);

        Assert.NotNull(captured);
        Assert.Equal(123_456, captured.Value.InputTokens);
        Assert.Equal(789, captured.Value.OutputTokens);
        Assert.Equal(3, captured.Value.SentMessageCount);
        Assert.Equal(1, captured.Value.ResponseMessageCount);
        Assert.Equal(captured, tracer.LastMeasuredUsage);
    }

    [Fact]
    public async Task ThinkingAndActivityTracingChatClient_GetStreamingResponseAsync_CapturesUsageContentAndOpenAiFallback()
    {
        // 1. Streaming with UsageContent (OllamaSharpChatClientAdapter path)
        var innerOllama = new Mock<IChatClient>();
        innerOllama
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamWithUsageContent(250_100, 320));

        var tracer1 = new ThinkingAndActivityTracingChatClient(innerOllama.Object);
        MeasuredCallTokenUsage? captured1 = null;
        tracer1.SetMeasuredUsageCallback(u => captured1 = u);

        await foreach (var _ in tracer1.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Prompt 1"), new ChatMessage(ChatRole.Assistant, "Reply 1")]))
        {
        }

        Assert.NotNull(captured1);
        Assert.Equal(250_100, captured1.Value.InputTokens);
        Assert.Equal(320, captured1.Value.OutputTokens);
        Assert.Equal(2, captured1.Value.SentMessageCount);

        // 2. Streaming with AdditionalProperties["usage"] (OpenAI-compatible fallback path)
        var innerOpenAi = new Mock<IChatClient>();
        innerOpenAi
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamWithRawJsonUsage(180_500, 450));

        var tracer2 = new ThinkingAndActivityTracingChatClient(innerOpenAi.Object);
        MeasuredCallTokenUsage? captured2 = null;
        tracer2.SetMeasuredUsageCallback(u => captured2 = u);

        await foreach (var _ in tracer2.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Prompt")]))
        {
        }

        Assert.NotNull(captured2);
        Assert.Equal(180_500, captured2.Value.InputTokens);
        Assert.Equal(450, captured2.Value.OutputTokens);
        Assert.Equal(1, captured2.Value.SentMessageCount);

        // 3. Stream with zero/missing usage does not invoke callback or overwrite LastMeasuredUsage
        var innerNoUsage = new Mock<IChatClient>();
        innerNoUsage
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamWithoutUsage());

        var tracer3 = new ThinkingAndActivityTracingChatClient(innerNoUsage.Object);
        MeasuredCallTokenUsage? captured3 = null;
        tracer3.SetMeasuredUsageCallback(u => captured3 = u);

        await foreach (var _ in tracer3.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Prompt")]))
        {
        }

        Assert.Null(captured3);
        Assert.Null(tracer3.LastMeasuredUsage);
    }

    [Fact]
    public void CalculateProjectedPromptTokens_WhenMeasuredOutputTokensZero_EstimatesAssistantResponseViaPmadEstimator()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 100_000,
            ReserveTokens = 5_000,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };
        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var userMsg = new ChatMessage(ChatRole.User, "Initial query");
        var assistantMsg = new ChatMessage(ChatRole.Assistant, "Assistant response whose output tokens were reported as 0 by backend");
        var toolResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c1", "Tool output payload")
        });

        var g1 = CreateGroup(CompactionGroupKind.User, [userMsg], tokenCount: 10);
        var g2 = CreateGroup(CompactionGroupKind.ToolCall, [assistantMsg, toolResultMsg], tokenCount: 20);
        var index = new CompactionMessageIndex([g1, g2]);

        // InputTokenCount = 50,000, OutputTokenCount = 0 (missing), sentIncludedMessageCount = 1, responseMessageCount = 1
        strategy.RecordMeasuredCallTokens(
            inputTokens: 50_000,
            outputTokens: 0,
            sentIncludedMessageCount: 1,
            responseMessageCount: 1);

        int expectedAssistantEstimate = ModelCalibratedTokenEstimator.EstimateMessageTokens(assistantMsg, HeuristicTokenEstimator.Qwen);
        int expectedToolEstimate = ModelCalibratedTokenEstimator.EstimateMessageTokens(toolResultMsg, HeuristicTokenEstimator.Qwen);

        int projected = strategy.CalculateProjectedPromptTokens(index);
        Assert.Equal(50_000 + expectedAssistantEstimate + expectedToolEstimate, projected);
    }

    [Fact]
    public async Task ShouldCompact_AndBaseTarget_ReflectProjectedPromptTokensThreshold()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 20_000,
            ReserveTokens = 4_000, // TriggerThresholdTokens = 16,000
            KeepRecentTokens = 2_000,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchor = CreateGroup(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "Task anchor")], tokenCount: 1_000);
        var turn1 = CreateGroup(CompactionGroupKind.AssistantText, [new ChatMessage(ChatRole.Assistant, "Turn 1")], tokenCount: 5_000);
        var turn2 = CreateGroup(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "Turn 2")], tokenCount: 1_000);
        var index = new CompactionMessageIndex([anchor, turn1, turn2]);

        // Below threshold (7,000 <= 16,000): ShouldCompact is false
        Assert.False(strategy.ShouldCompact(index));

        // Record measured call tokens pushing projected tokens above 16,000
        strategy.RecordMeasuredCallTokens(inputTokens: 16_200, outputTokens: 300, sentIncludedMessageCount: 2, responseMessageCount: 1);
        Assert.True(strategy.ShouldCompact(index));

        // After compaction, baseline is reset and projected tokens fall back below threshold
        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));
        Assert.False(strategy.ShouldCompact(index));
    }

    [Fact]
    public async Task CompactAsync_WhenEmergencyToolClampingShrinksRecentToolOutput_RecomputesGroupTokensAndPreservesPrecedingRecentTurn()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 20_000,
            ReserveTokens = 4_000, // TriggerThreshold = 16,000
            SafetyMarginTokens = 2_000,
            KeepRecentTokens = 2_500,
            EmergencyToolResultMaxChars = 8_000,
            ToolResultMaxChars = 2_000,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchor = CreateGroup(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "Task anchor")], tokenCount: 1_000);
        var oldHistory = CreateGroup(CompactionGroupKind.AssistantText, [new ChatMessage(ChatRole.Assistant, "Old history")], tokenCount: 12_000);

        // Preceding recent turn (~1,000 tokens): should fit in KeepRecentTokens (2,500) once the newest tool output is clamped!
        var recentAssistantTurn = CreateGroup(
            CompactionGroupKind.AssistantText,
            [new ChatMessage(ChatRole.Assistant, "Recent analysis to preserve verbatim")],
            tokenCount: 1_000);

        // Newest tool call + oversized result (14,000 chars > 8,000 EmergencyToolResultMaxChars).
        // Before clamping: 4,000 tokens (> KeepRecentTokens = 2,500), which would consume the entire recent budget alone.
        // After clamping to 2,000 chars: ~550 tokens, so 550 + 1,000 = 1,550 < 2,500 and recentAssistantTurn is preserved too!
        var newestToolCall = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c_recent", "read", new Dictionary<string, object?> { ["path"] = "Large.cs" })
        });
        var newestToolResult = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c_recent", new string('X', 14_000))
        });
        var newestToolGroup = CreateGroup(CompactionGroupKind.ToolCall, [newestToolCall, newestToolResult], tokenCount: 4_000);

        var index = new CompactionMessageIndex([anchor, oldHistory, recentAssistantTurn, newestToolGroup]);

        bool compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted);

        // Old history was compacted, while both recentAssistantTurn and newestToolGroup were kept in the recent window
        Assert.True(oldHistory.IsExcluded);
        Assert.False(recentAssistantTurn.IsExcluded);
        Assert.False(newestToolGroup.IsExcluded);
    }

    [Fact]
    public async Task CompactAsync_WhenIndexRebuiltFromIncludedMessagesViaCreate_PreservesDebounceAndUpdatesExistingSummaryGroup()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 6_000,
            ReserveTokens = 1_500, // TriggerThreshold = 4,500
            SafetyMarginTokens = 500,
            KeepRecentTokens = 500,
            DebounceTurnCooldown = 2,
            DebounceTokenDelta = 1_000,
            EmergencyThresholdRatio = 0.98, // EmergencyThreshold = 5,880
            EmergencyToolResultMaxChars = 50_000,
            SummarizerModelId = "Qwen3.8-27B:latest"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, null, NullLogger.Instance);

        var anchorMsg = new ChatMessage(ChatRole.User, "Implement router endpoints");
        var call1Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "src/FirstRead.cs" })
        });
        var res1Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c1", new string('a', 18_000))
        });
        var call2Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c2", "write", new Dictionary<string, object?> { ["path"] = "src/SecondMod.cs" })
        });
        var res2Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c2", "ok")
        });

        var index1 = CreateIndexFromMessages([anchorMsg, call1Msg, res1Msg, call2Msg, res2Msg]);
        Assert.True(await strategy.CompactAsync(index1, NullLogger.Instance, CancellationToken.None));

        // Simulate MAF InMemoryChatHistoryProvider / session recreation rebuilding CompactionMessageIndex from flat included ChatMessages.
        // Note: CompactionMessageIndex.Create classifies the ChatRole.User checkpoint summary message as CompactionGroupKind.User!
        var rebuiltMessages1 = index1.GetIncludedMessages().ToList();
        var rebuiltCheckIndex = CreateIndexFromMessages(rebuiltMessages1);
        var rebuiltCheckpointGroup = rebuiltCheckIndex.Groups[1];
        Assert.Equal(CompactionGroupKind.User, rebuiltCheckpointGroup.Kind);
        Assert.True(PiStyleCrucibleCompactionStrategy.IsSummaryGroup(rebuiltCheckpointGroup));

        // Append only 1 new assistant turn (turnsSinceLastCompaction = 1 < DebounceTurnCooldown = 2) with tokens above trigger (4,500) but below emergency (5,880)
        var call3Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c3", "read", new Dictionary<string, object?> { ["path"] = "src/ThirdRead.cs" })
        });
        var res3Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c3", new string('b', 18_000))
        });
        var rebuiltMessagesWithTurn3 = rebuiltMessages1.Concat([call3Msg, res3Msg]).ToList();
        var indexAfterTurn3 = CreateIndexFromMessages(rebuiltMessagesWithTurn3);

        // Must be debounced by turn cooldown (1 < 2)
        Assert.False(await strategy.CompactAsync(indexAfterTurn3, NullLogger.Instance, CancellationToken.None));

        // Append a 2nd new assistant turn (turnsSinceLastCompaction = 2 >= DebounceTurnCooldown = 2)
        var call4Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c4", "edit", new Dictionary<string, object?> { ["path"] = "src/FourthEdit.cs" })
        });
        var res4Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c4", new string('d', 2_000))
        });
        var rebuiltMessagesWithTurn4 = rebuiltMessagesWithTurn3.Concat([call4Msg, res4Msg]).ToList();
        var indexAfterTurn4 = CreateIndexFromMessages(rebuiltMessagesWithTurn4);

        Assert.True(await strategy.CompactAsync(indexAfterTurn4, NullLogger.Instance, CancellationToken.None));

        // Verify the rebuilt checkpoint group was recognized as existingSummaryGroup, excluded, and its file operations merged into the new summary
        var activeSummaries = indexAfterTurn4.Groups.Where(g => !g.IsExcluded && PiStyleCrucibleCompactionStrategy.IsSummaryGroup(g)).ToList();
        var singleActiveSummary = Assert.Single(activeSummaries);
        var summaryText = singleActiveSummary.Messages[0].Text!;
        Assert.Contains("src/FirstRead.cs", summaryText);
        Assert.Contains("src/SecondMod.cs", summaryText);
        Assert.Contains("src/ThirdRead.cs", summaryText);
    }

    [Fact]
    public async Task ThinkingAndActivityTracingChatClient_CapturesUsageFromMessageUsageContentAndReasoningAdditionalCounts()
    {
        // 1. Non-streaming response where response.Usage is null, UsageContent is in response.Messages,
        // and AdditionalCounts["reasoning_tokens"] > OutputTokenCount
        var usageDetails = new UsageDetails
        {
            InputTokenCount = 42_000,
            OutputTokenCount = 100,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["reasoning_tokens"] = 650
            }
        };
        var msgWithUsageContent = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("Final answer"),
            new UsageContent(usageDetails)
        });
        var responseWithoutTopLevelUsage = new ChatResponse(msgWithUsageContent)
        {
            Usage = null
        };

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseWithoutTopLevelUsage);

        var tracer = new ThinkingAndActivityTracingChatClient(innerMock.Object);
        MeasuredCallTokenUsage? captured = null;
        tracer.SetMeasuredUsageCallback(u => captured = u);

        await tracer.GetResponseAsync([new ChatMessage(ChatRole.User, "Prompt")]);

        Assert.NotNull(captured);
        Assert.Equal(42_000, captured.Value.InputTokens);
        Assert.Equal(650, captured.Value.OutputTokens);

        // 2. Streaming response where UsageContent has AdditionalCounts["reasoning_tokens"] > OutputTokenCount
        var innerStreamMock = new Mock<IChatClient>();
        innerStreamMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(StreamWithReasoningUsageContent(88_000, 50, 900));

        var streamTracer = new ThinkingAndActivityTracingChatClient(innerStreamMock.Object);
        MeasuredCallTokenUsage? streamCaptured = null;
        streamTracer.SetMeasuredUsageCallback(u => streamCaptured = u);

        await foreach (var _ in streamTracer.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Prompt")]))
        {
        }

        Assert.NotNull(streamCaptured);
        Assert.Equal(88_000, streamCaptured.Value.InputTokens);
        Assert.Equal(900, streamCaptured.Value.OutputTokens);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithReasoningUsageContent(
        int inputTokens,
        int outputTokens,
        int reasoningTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Thinking and answering");
        await Task.Yield();
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    AdditionalCounts = new AdditionalPropertiesDictionary<long>
                    {
                        ["reasoning_tokens"] = reasoningTokens
                    }
                })
            ]
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithoutUsage(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithUsageContent(
        int inputTokens,
        int outputTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 1");
        await Task.Yield();
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents =
            [
                new UsageContent(new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = inputTokens + outputTokens
                })
            ]
        };
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamWithRawJsonUsage(
        int promptTokens,
        int completionTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 1");
        await Task.Yield();
        using var doc = JsonDocument.Parse($"{{\"prompt_tokens\":{promptTokens},\"completion_tokens\":{completionTokens}}}");
        var usageElement = doc.RootElement.Clone();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 2")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["usage"] = usageElement
            }
        };
    }

    private static CompactionMessageIndex CreateIndexFromMessages(IList<ChatMessage> messages)
    {
        var method = typeof(CompactionMessageIndex).GetMethod(
            "Create",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var args = method.GetParameters().Length == 2
            ? new object?[] { messages, null }
            : new object?[] { messages };
        return (CompactionMessageIndex)method.Invoke(null, args)!;
    }

    private static CompactionMessageGroup CreateGroup(CompactionGroupKind kind, IReadOnlyList<ChatMessage> messages, int tokenCount)
    {
        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (CompactionMessageGroup)ctor.Invoke([kind, messages, tokenCount * 4, tokenCount, 0]);
    }
}
