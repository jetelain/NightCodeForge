namespace NightCodeForge.Agents.Tests.Compaction;

using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Compaction;
using Xunit;

public class Step1131DynamicCompactionBudgetTests
{
    [Fact]
    public void CalculateEffectiveKeepRecentTokens_ClampsToDynamicBudget()
    {
        // Specification requirement:
        // When Tokens_anchor = 10,000, TriggerThreshold = 24,576, SafetyMargin = 4,000, KeepRecent = 16,384:
        // DynamicBudget = 24,576 - 10,000 - 4,000 = 10,576.
        // EffectiveKeepRecent = min(16,384, 10,576) = 10,576.
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 32768,
            ReserveTokens = 8192, // TriggerThresholdTokens = 24,576
            SafetyMarginTokens = 4000,
            KeepRecentTokens = 16384
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        int effectiveKeepRecent = strategy.CalculateEffectiveKeepRecentTokens(anchorTokens: 10_000);

        Assert.Equal(10_576, effectiveKeepRecent);
    }

    [Fact]
    public void CalculateEffectiveKeepRecentTokens_WhenAnchorIsSmall_KeepsConfiguredKeepRecent()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 131072,
            ReserveTokens = 8192, // TriggerThreshold = 122,880
            SafetyMarginTokens = 4000,
            KeepRecentTokens = 16384
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        // Anchor is only 1,000 tokens. Dynamic budget = 122,880 - 1,000 - 4,000 = 117,880.
        // Min(16,384, 117,880) = 16,384
        int effectiveKeepRecent = strategy.CalculateEffectiveKeepRecentTokens(anchorTokens: 1000);

        Assert.Equal(16_384, effectiveKeepRecent);
    }

    [Fact]
    public async Task CompactAsync_TurnCooldown_SkipsCompactionOnSubsequentTurnsUnlessEmergency()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 10000,
            ReserveTokens = 2000, // Trigger threshold: 8000
            SafetyMarginTokens = 1000,
            KeepRecentTokens = 1000,
            DebounceTurnCooldown = 3,
            DebounceTokenDelta = 500,
            EmergencyThresholdRatio = 0.95 // Emergency at 9500
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor task") }, tokenCount: 1000);
        var call1 = CreateToolCallGroup("c1", "read", tokenCount: 500);
        var result1 = CreateToolResultGroup("c1", "result 1", tokenCount: 6000);
        var call2 = CreateToolCallGroup("c2", "read", tokenCount: 500);
        var result2 = CreateToolResultGroup("c2", "result 2", tokenCount: 500);

        // Turn 0 has 2 assistant tool calls => assistant turns = 2.
        // Total tokens = 1000 + 500 + 6000 + 500 + 500 = 8500 (> 8000 trigger threshold).
        var index1 = CreateIndex(new List<CompactionMessageGroup> { anchor, call1, result1, call2, result2 });

        var compacted1 = await strategy.CompactAsync(index1, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted1);
        Assert.Equal(2, strategy.LastCompactedTurnCount);

        // Immediate next turn: only 1 new assistant turn (total assistant turns = 3, delta turns = 1 < 3)
        // Tokens added = 600 (> 500 delta threshold), but turn cooldown should block compaction
        var call3 = CreateToolCallGroup("c3", "bash", tokenCount: 600);
        var groups2 = index1.Groups.ToList();
        groups2.Add(call3);
        var index2 = CreateIndex(groups2);

        // Total tokens still > 8000, but not emergency (< 9500)
        var compacted2 = await strategy.CompactAsync(index2, NullLogger.Instance, CancellationToken.None);
        Assert.False(compacted2, "Compaction should be debounced by turn cooldown");

        // Emergency bypass: Add a massive tool response exceeding 95% of ContextWindowTokens (>= 9500)
        var emergencyToolResult = CreateToolResultGroup("c3", "emergency flood", tokenCount: 10000);
        groups2.Add(emergencyToolResult);
        var emergencyIndex = CreateIndex(groups2);

        var compactedEmergency = await strategy.CompactAsync(emergencyIndex, NullLogger.Instance, CancellationToken.None);
        Assert.True(compactedEmergency, "Emergency token threshold (>= 95%) must bypass turn cooldown");
    }

    [Fact]
    public async Task CompactAsync_EmergencyThreshold_EvaluatesIncludedTokensNotTotalTokens()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 10000,
            ReserveTokens = 2000, // Trigger threshold: 8000
            SafetyMarginTokens = 1000,
            KeepRecentTokens = 1000,
            DebounceTurnCooldown = 3,
            DebounceTokenDelta = 500,
            EmergencyThresholdRatio = 0.95 // Emergency at 9500
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor task") }, tokenCount: 1000);
        var oldExcludedFlood = CreateToolResultGroup("c0", "historical old output", tokenCount: 9000);
        oldExcludedFlood.IsExcluded = true; // Historical excluded group from prior compaction

        // Active included groups: 1000 (anchor) + 500 (call1) + 6000 (res1) + 500 (call2) + 500 (res2) = 8500 (> 8000 threshold, but < 9500 emergency)
        var call1 = CreateToolCallGroup("c1", "read", tokenCount: 500);
        var result1 = CreateToolResultGroup("c1", "result 1", tokenCount: 6000);
        var call2 = CreateToolCallGroup("c2", "read", tokenCount: 500);
        var result2 = CreateToolResultGroup("c2", "result 2", tokenCount: 500);

        // TotalTokenCount = 9000 + 8500 = 17500 (> 9500)
        // IncludedTokenCount = 8500 (< 9500)
        var index1 = CreateIndex(new List<CompactionMessageGroup> { anchor, oldExcludedFlood, call1, result1, call2, result2 });
        Assert.True(index1.TotalTokenCount >= 9500);
        Assert.True(index1.IncludedTokenCount < 9500);

        var compacted1 = await strategy.CompactAsync(index1, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted1);

        // Next turn: 1 new assistant turn (total assistant turns = 3, delta = 1 < 3)
        // Tokens added = 600 (> 500 delta)
        // If isEmergency used TotalTokenCount, it would evaluate to true and bypass turn cooldown!
        // With IncludedTokenCount, isEmergency is false and turn cooldown blocks compaction.
        var call3 = CreateToolCallGroup("c3", "bash", tokenCount: 600);
        var groups2 = index1.Groups.ToList();
        groups2.Add(call3);
        var index2 = CreateIndex(groups2);

        var compacted2 = await strategy.CompactAsync(index2, NullLogger.Instance, CancellationToken.None);
        Assert.False(compacted2, "Historical excluded tokens in TotalTokenCount must NOT trigger emergency bypass of turn cooldown");
    }

    [Fact]
    public async Task CompactAsync_TokenDeltaDebounce_SkipsCompactionWhenInsufficientTokensAdded()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 10000,
            ReserveTokens = 2000, // Trigger threshold: 8000
            SafetyMarginTokens = 1000,
            KeepRecentTokens = 1000,
            DebounceTurnCooldown = 2,
            DebounceTokenDelta = 2500, // Requires 2500 new tokens
            EmergencyThresholdRatio = 0.95
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor task") }, tokenCount: 1000);
        var call1 = CreateToolCallGroup("c1", "read", tokenCount: 500);
        var result1 = CreateToolResultGroup("c1", "result 1", tokenCount: 6600);
        var call2 = CreateToolCallGroup("c2", "read", tokenCount: 500);

        // Total tokens = 8600 (> 8000). Total assistant turns = 2.
        var index1 = CreateIndex(new List<CompactionMessageGroup> { anchor, call1, result1, call2 });
        var compacted1 = await strategy.CompactAsync(index1, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted1);

        // Now advance turns by 3 (satisfies DebounceTurnCooldown = 2), but add only 300 tokens (less than 2500 delta)
        var groups2 = index1.Groups.ToList();
        for (int i = 0; i < 3; i++)
        {
            groups2.Add(CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, $"call {i + 3}") }, tokenCount: 100));
        }
        var index2 = CreateIndex(groups2);

        // Total tokens is still above 8000, but token delta from last compaction is < 2500
        var compacted2 = await strategy.CompactAsync(index2, NullLogger.Instance, CancellationToken.None);
        Assert.False(compacted2, "Compaction should be debounced by token delta");
    }

    [Fact]
    public async Task CompactAsync_SyntheticConversation_PreventsBackToBackCompactionLoops()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 20000,
            ReserveTokens = 4000, // Trigger: 16000
            SafetyMarginTokens = 2000,
            KeepRecentTokens = 2000,
            DebounceTurnCooldown = 3,
            DebounceTokenDelta = 2500
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var groups = new List<CompactionMessageGroup>
        {
            CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Task anchor") }, tokenCount: 2000),
            CreateToolCallGroup("t1", "read", tokenCount: 500),
            CreateToolResultGroup("t1", "flood", tokenCount: 14000),
            CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "turn 2") }, tokenCount: 500),
        };

        var compactionTurns = new List<int>();

        // Run 50 synthetic conversation steps
        for (int turn = 0; turn < 50; turn++)
        {
            groups.Add(CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, $"turn {turn + 3}") }, tokenCount: 300));
            groups.Add(CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, $"user {turn + 3}") }, tokenCount: 400));

            var index = CreateIndex(groups.ToList());
            var compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
            if (compacted)
            {
                compactionTurns.Add(turn);
            }
        }

        // Without debouncing, compaction would trigger almost every turn.
        // With debounce and proper IncludedTokenCount evaluation, compactions only trigger when active context genuinely fills up.
        Assert.InRange(compactionTurns.Count, 1, 5);

        // Spec §4.1: Assert zero back-to-back compactions across 50 turns
        for (int i = 1; i < compactionTurns.Count; i++)
        {
            int turnGap = compactionTurns[i] - compactionTurns[i - 1];
            Assert.True(turnGap > 1, $"Detected back-to-back compaction at turns {compactionTurns[i - 1]} and {compactionTurns[i]}");
        }
    }

    [Fact]
    public async Task CompactAsync_Tier2Clamping_ClampsOversizedFunctionAndTextContent_PriorToSummarization()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 20000,
            ReserveTokens = 4000, // Trigger: 16000
            SafetyMarginTokens = 2000,
            KeepRecentTokens = 2000,
            EmergencyToolResultMaxChars = 8000,
            ToolResultMaxChars = 2000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor") }, tokenCount: 1000);
        var call = CreateToolCallGroup("c1", "bash", tokenCount: 200);

        var hugeOutput = new string('Z', 15000);
        var funcResultMsg = new ChatMessage(ChatRole.Tool, new AIContent[]
        {
            new FunctionResultContent("c1", hugeOutput)
        });
        var textContentMsg = new ChatMessage(ChatRole.Tool, new AIContent[]
        {
            new TextContent(hugeOutput)
        });
        var toolGroup1 = CreateGroup(CompactionGroupKind.ToolCall, new[] { funcResultMsg }, tokenCount: 8000);
        var toolGroup2 = CreateGroup(CompactionGroupKind.ToolCall, new[] { textContentMsg }, tokenCount: 8000);
        var recent = CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "Recent answer") }, tokenCount: 500);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, call, toolGroup1, toolGroup2, recent });

        var compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted);

        // Verify FunctionResultContent clamped
        var frc = funcResultMsg.Contents.OfType<FunctionResultContent>().Single();
        var frcString = frc.Result?.ToString() ?? string.Empty;
        Assert.StartsWith(new string('Z', 2000), frcString);
        Assert.Contains("... [Output clamped to 2,000 characters]", frcString);
        Assert.DoesNotContain(new string('Z', 2500), frcString);

        // Verify TextContent clamped
        var tc = textContentMsg.Contents.OfType<TextContent>().Single();
        Assert.StartsWith(new string('Z', 2000), tc.Text);
        Assert.Contains("... [Output clamped to 2,000 characters]", tc.Text);
        Assert.DoesNotContain(new string('Z', 2500), tc.Text);
    }

    [Fact]
    public async Task CompactAsync_Tier2Clamping_WhenEmergencyLimitBelowClampedTarget_DoesNotThrow()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 20000,
            ReserveTokens = 4000,
            SafetyMarginTokens = 2000,
            KeepRecentTokens = 2000,
            EmergencyToolResultMaxChars = 1000,
            ToolResultMaxChars = 3000 // ToolResultMaxChars > EmergencyToolResultMaxChars
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Anchor") }, tokenCount: 1000);
        var call = CreateToolCallGroup("c1", "bash", tokenCount: 200);

        // 1500 chars is between Emergency (1000) and Target (3000)
        var output1500 = new string('Y', 1500);
        var funcResultMsg = new ChatMessage(ChatRole.Tool, new AIContent[]
        {
            new FunctionResultContent("c1", output1500)
        });
        var toolGroup = CreateGroup(CompactionGroupKind.ToolCall, new[] { funcResultMsg }, tokenCount: 16000);
        var recent = CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "Recent") }, tokenCount: 500);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, call, toolGroup, recent });

        // Must not throw ArgumentOutOfRangeException
        var compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.True(compacted);

        var frc = funcResultMsg.Contents.OfType<FunctionResultContent>().Single();
        var frcString = frc.Result?.ToString() ?? string.Empty;
        Assert.Contains("... [Output clamped to 1,500 characters]", frcString);
    }

    private static CompactionMessageGroup CreateToolCallGroup(string callId, string toolName, int tokenCount)
    {
        var msg = new ChatMessage(ChatRole.Assistant, new AIContent[]
        {
            new FunctionCallContent(callId, toolName, new Dictionary<string, object?>())
        });
        return CreateGroup(CompactionGroupKind.ToolCall, new[] { msg }, tokenCount);
    }

    private static CompactionMessageGroup CreateToolResultGroup(string callId, string result, int tokenCount)
    {
        var msg = new ChatMessage(ChatRole.Tool, new AIContent[]
        {
            new FunctionResultContent(callId, result)
        });
        return CreateGroup(CompactionGroupKind.ToolCall, new[] { msg }, tokenCount);
    }

    private static CompactionMessageGroup CreateGroup(CompactionGroupKind kind, IReadOnlyList<ChatMessage> messages, int tokenCount)
    {
        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (CompactionMessageGroup)ctor.Invoke(new object?[] { kind, messages, tokenCount * 4, tokenCount, 0 });
    }

    private static CompactionMessageIndex CreateIndex(IList<CompactionMessageGroup> groups)
    {
        return new CompactionMessageIndex(groups, null!);
    }
}
