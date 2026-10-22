using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Compaction;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class PiStyleCrucibleCompactionStrategyTests
{
    [Fact]
    public async Task CompactAsync_WhenAboveThreshold_AnchorsUserPrompt_InsertsSummary_AndPreservesRecentWindow()
    {
        // Arrange: 32k small context budget proposal
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 4000,
            ReserveTokens = 1000, // Trigger threshold: 3000 tokens
            KeepRecentTokens = 1000,
            ToolResultMaxChars = 200
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        // Turn 0: User Task Anchor
        var anchorMsg = new ChatMessage(ChatRole.User, "Task requirement: implement feature X with criteria A, B, C");

        // Turn 1: Assistant call to read file
        var call1Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "File1.cs" })
        });

        // Turn 2: Massive tool result (historical tool output to be summarized)
        var result1Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call1", new string('A', 8000))
        });

        // Turn 3: Assistant call to edit file
        var call2Msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call2", "edit", new Dictionary<string, object?> { ["path"] = "File2.cs" })
        });

        // Turn 4: Tool result from edit
        var result2Msg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call2", new string('B', 8000))
        });

        // Turn 5: Recent assistant text
        var recentAssistantMsg = new ChatMessage(ChatRole.Assistant, "Recent assistant progress update");

        // Turn 6: Recent user follow-up
        var recentUserMsg = new ChatMessage(ChatRole.User, "Recent user guidance");

        var anchorGroup = CreateGroup(CompactionGroupKind.User, new[] { anchorMsg }, tokenCount: 500);
        var call1Group = CreateGroup(CompactionGroupKind.ToolCall, new[] { call1Msg }, tokenCount: 100);
        var result1Group = CreateGroup(CompactionGroupKind.ToolCall, new[] { result1Msg }, tokenCount: 1500);
        var call2Group = CreateGroup(CompactionGroupKind.ToolCall, new[] { call2Msg }, tokenCount: 100);
        var result2Group = CreateGroup(CompactionGroupKind.ToolCall, new[] { result2Msg }, tokenCount: 1500);
        var recentAssistantGroup = CreateGroup(CompactionGroupKind.AssistantText, new[] { recentAssistantMsg }, tokenCount: 500);
        var recentUserGroup = CreateGroup(CompactionGroupKind.User, new[] { recentUserMsg }, tokenCount: 500);

        var groups = new List<CompactionMessageGroup>
        {
            anchorGroup,
            call1Group,
            result1Group,
            call2Group,
            result2Group,
            recentAssistantGroup,
            recentUserGroup
        };

        var index = CreateIndex(groups);

        // Act
        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        // Assert
        Assert.True(result);

        // 1. Task anchor must remain the first message
        var includedMessages = index.GetIncludedMessages().ToList();
        Assert.Equal(anchorMsg.Text, includedMessages[0].Text);

        // 2. Summary must be present immediately after anchor
        Assert.Contains("[CONTEXT COMPACTION CHECKPOINT]", includedMessages[1].Text);
        Assert.Contains("## Goal", includedMessages[1].Text);
        Assert.Contains("<read-files>\nFile1.cs\n</read-files>", includedMessages[1].Text);
        Assert.Contains("<modified-files>\nFile2.cs\n</modified-files>", includedMessages[1].Text);

        // 3. Recent messages must be kept intact at the tail
        Assert.Contains(recentUserMsg.Text, includedMessages.Last().Text);
    }

    [Fact]
    public async Task CompactAsync_WhenBelowThreshold_DoesNotExecuteCompaction()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 10000,
            ReserveTokens = 2000, // Threshold: 8000 tokens
            KeepRecentTokens = 2000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchorMsg = new ChatMessage(ChatRole.User, "Task anchor");
        var anchorGroup = CreateGroup(CompactionGroupKind.User, new[] { anchorMsg }, tokenCount: 500);

        var assistantMsg = new ChatMessage(ChatRole.Assistant, "Small response");
        var assistantGroup = CreateGroup(CompactionGroupKind.AssistantText, new[] { assistantMsg }, tokenCount: 500);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchorGroup, assistantGroup });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        Assert.False(result);
        Assert.False(anchorGroup.IsExcluded);
        Assert.False(assistantGroup.IsExcluded);
        Assert.DoesNotContain(index.Groups, g => g.Kind == CompactionGroupKind.Summary);
    }

    [Fact]
    public async Task CompactAsync_WithSummarizerClient_CallsLlmAndAttachesFileOps()
    {
        var mockSummarizer = new Moq.Mock<IChatClient>();
        mockSummarizer
            .Setup(c => c.GetResponseAsync(Moq.It.IsAny<IEnumerable<ChatMessage>>(), Moq.It.IsAny<ChatOptions>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nCustom LLM summarized goal\n\n## Progress\n### Done\n- Custom progress")));

        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 4000,
            ReserveTokens = 1000,
            KeepRecentTokens = 1000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, mockSummarizer.Object);

        var anchorGroup = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Goal") }, tokenCount: 500);
        var callGroup = CreateGroup(CompactionGroupKind.ToolCall, new[] {
            new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "Main.cs" }) })
        }, tokenCount: 100);
        var resultGroup = CreateGroup(CompactionGroupKind.ToolCall, new[] {
            new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", "Main content") })
        }, tokenCount: 2500);
        var recentGroup = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Next") }, tokenCount: 500);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchorGroup, callGroup, resultGroup, recentGroup });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        Assert.True(result);
        var summaryGroup = index.Groups.FirstOrDefault(g => g.Kind == CompactionGroupKind.Summary);
        Assert.NotNull(summaryGroup);
        var text = summaryGroup.Messages[0].Text!;
        Assert.Contains("Custom LLM summarized goal", text);
        Assert.Contains("<read-files>\nMain.cs\n</read-files>", text);
    }

    [Fact]
    public async Task CompactAsync_AdjustsCutPoint_ToNeverOrphanToolResults()
    {
        // Setup: keepRecentTokens lands on the ToolResult group.
        // The strategy must adjust cutIndex backward to the Assistant tool call,
        // so that the Assistant call and Tool result stay together in the live recent window.
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 5000,
            ReserveTokens = 1000, // Trigger: 4000
            KeepRecentTokens = 1600
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Task anchor") }, tokenCount: 500);
        var oldMsg = CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "Old assistant message") }, tokenCount: 2000);
        var toolCallMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "File.cs" })
        });
        var callGroup = CreateGroup(CompactionGroupKind.ToolCall, new[] { toolCallMsg }, tokenCount: 100);
        var toolResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call1", "File content here")
        });
        var resultGroup = CreateGroup(CompactionGroupKind.ToolCall, new[] { toolResultMsg }, tokenCount: 1500);
        var recentUser = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Next step") }, tokenCount: 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, oldMsg, callGroup, resultGroup, recentUser });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        Assert.True(result);

        // Verify included messages: the first message after the summary must NOT be an orphaned Tool result!
        var included = index.GetIncludedMessages().ToList();
        var toolResultIdx = included.FindIndex(m => m.Role == ChatRole.Tool);
        Assert.True(toolResultIdx > 0, "Tool result must be present in included messages.");

        // The message immediately preceding ChatRole.Tool MUST be the Assistant message with FunctionCallContent!
        var precedingMsg = included[toolResultIdx - 1];
        Assert.Equal(ChatRole.Assistant, precedingMsg.Role);
        Assert.Contains(precedingMsg.Contents, c => c is FunctionCallContent fcc && fcc.Name == "read");
    }

    [Fact]
    public async Task CompactAsync_AdjustsCutPoint_AdvancesPastToolResultWhenHeadroomTight()
    {
        // When moving back would leave no groups to summarize, it advances cutIndex past the tool result,
        // so both the call and result are summarized together without orphaning.
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 4000,
            ReserveTokens = 1000, // Trigger: 3000
            KeepRecentTokens = 2000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Task anchor") }, tokenCount: 500);
        var callGroup = CreateGroup(CompactionGroupKind.ToolCall, new[]
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "A.cs" }) })
        }, tokenCount: 100);
        var resultGroup = CreateGroup(CompactionGroupKind.ToolCall, new[]
        {
            new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("call1", new string('X', 5000)) })
        }, tokenCount: 2500);
        var recentUser = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Follow-up") }, tokenCount: 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, callGroup, resultGroup, recentUser });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        Assert.True(result);
        var included = index.GetIncludedMessages().ToList();

        // There should be no orphaned Tool role in included messages
        Assert.DoesNotContain(included, m => m.Role == ChatRole.Tool);
        // Both were summarized, so file operations are tracked in the summary
        Assert.Contains(included, m => m.Text != null && m.Text.Contains("<read-files>\nA.cs\n</read-files>"));
    }

    [Fact]
    public async Task CompactAsync_DoesNotRecompact_WhenOnlySummaryGroupExists()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 4000,
            ReserveTokens = 1000, // Trigger: 3000
            KeepRecentTokens = 1000
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Task anchor") }, tokenCount: 1500);
        var existingSummary = CreateGroup(CompactionGroupKind.Summary, new[] { new ChatMessage(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\n## Goal\nPrior goal") }, tokenCount: 500);
        var recentUser = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Recent user") }, tokenCount: 1500);

        // Total: 3500 tokens > 3000 trigger threshold, but only an existing Summary group sits between anchor and recent.
        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, existingSummary, recentUser });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        // Must return false without modifying the existing summary
        Assert.False(result);
        Assert.False(existingSummary.IsExcluded);
        Assert.Single(index.Groups, g => g.Kind == CompactionGroupKind.Summary);
    }

    [Fact]
    public async Task DeterministicSummary_ExcludesPriorCompactionCheckpointsFromUserGoals()
    {
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 4000,
            ReserveTokens = 1000,
            KeepRecentTokens = 100
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options);

        var anchor = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Fix bug in parser") }, tokenCount: 500);
        var priorCheckpoint = CreateGroup(CompactionGroupKind.Summary, new[]
        {
            new ChatMessage(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nOld goal")
        }, tokenCount: 500);
        var newWork = CreateGroup(CompactionGroupKind.AssistantText, new[] { new ChatMessage(ChatRole.Assistant, "Working on parser") }, tokenCount: 2200);
        var recent = CreateGroup(CompactionGroupKind.User, new[] { new ChatMessage(ChatRole.User, "Next action") }, tokenCount: 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, priorCheckpoint, newWork, recent });

        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);

        Assert.True(result);
        var summary = index.Groups.First(g => g.Kind == CompactionGroupKind.Summary && !g.IsExcluded).Messages[0].Text!;

        // The goal section in the new summary must not contain the checkpoint boilerplate and must preserve prior goals
        var goalSection = summary[summary.IndexOf("## Goal")..summary.IndexOf("## Constraints")];
        Assert.DoesNotContain("[CONTEXT COMPACTION CHECKPOINT]", goalSection);
        Assert.Contains("Old goal", goalSection);
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
