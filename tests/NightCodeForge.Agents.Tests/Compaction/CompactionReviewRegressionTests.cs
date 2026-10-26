using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

/// <summary>
/// Regression tests covering the findings of the staged-changes review for context compaction.
/// </summary>
public class CompactionReviewRegressionTests
{
    private static CrucibleCompactionOptions SmallOptions(int keepRecent) => new()
    {
        ContextWindowTokens = 4000,
        ReserveTokens = 1000, // trigger: 3000
        KeepRecentTokens = keepRecent
    };

    [Fact]
    public void Strategy_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new PiStyleCrucibleCompactionStrategy(null!));
    }

    [Fact]
    public async Task CutPoint_IgnoresAlreadyExcludedGroups_WhenAccumulatingRecentWindow()
    {
        var strategy = new PiStyleCrucibleCompactionStrategy(SmallOptions(keepRecent: 1000));

        var anchor = CreateGroup(CompactionGroupKind.User, "anchor", 500);
        var a = CreateGroup(CompactionGroupKind.AssistantText, "A", 1500);
        var b = CreateGroup(CompactionGroupKind.AssistantText, "B", 1500);
        var alreadyCompacted = CreateGroup(CompactionGroupKind.AssistantText, "X", 3000);
        alreadyCompacted.IsExcluded = true;
        var c = CreateGroup(CompactionGroupKind.User, "C", 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, a, b, alreadyCompacted, c });

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));

        // Excluded group tokens must not eat the recent budget: B must be kept verbatim.
        Assert.True(a.IsExcluded);
        Assert.False(b.IsExcluded);
        Assert.False(c.IsExcluded);
    }

    [Fact]
    public async Task CutPoint_WhenRecentBudgetCannotBeFilled_NeverSummarizesNewestGroup()
    {
        // Oversized anchor puts the context over the trigger while everything after it is below KeepRecentTokens.
        var strategy = new PiStyleCrucibleCompactionStrategy(SmallOptions(keepRecent: 5000));

        var anchor = CreateGroup(CompactionGroupKind.User, "huge anchor", 3000);
        var a = CreateGroup(CompactionGroupKind.AssistantText, "A", 100);
        var b = CreateGroup(CompactionGroupKind.AssistantText, "B", 100);
        var newest = CreateGroup(CompactionGroupKind.User, "newest", 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, a, b, newest });

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));

        Assert.True(a.IsExcluded);
        Assert.True(b.IsExcluded);
        Assert.False(newest.IsExcluded);
        Assert.Equal("newest", index.GetIncludedMessages().Last().Text);
    }

    [Fact]
    public async Task DeterministicSummary_TruncatesHugeUserMessages()
    {
        var strategy = new PiStyleCrucibleCompactionStrategy(SmallOptions(keepRecent: 100));

        var anchor = CreateGroup(CompactionGroupKind.User, "anchor", 500);
        var hugeUser = CreateGroup(CompactionGroupKind.User, new string('Z', 20000), 2500);
        var reply = CreateGroup(CompactionGroupKind.AssistantText, "reply", 500);
        var newest = CreateGroup(CompactionGroupKind.User, "newest", 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, hugeUser, reply, newest });

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));

        var summary = index.Groups.Single(g => g.Kind == CompactionGroupKind.Summary).Messages[0].Text!;
        Assert.True(summary.Length < 5000, $"Summary was {summary.Length} chars");
        Assert.Contains("more characters truncated", summary);
    }

    [Fact]
    public async Task LlmSummary_UsesConfiguredSummarizerModelId()
    {
        ChatOptions? captured = null;
        var summarizer = new Mock<IChatClient>();
        summarizer
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, o, _) => captured = o)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nx")));

        var options = SmallOptions(keepRecent: 100);
        options.SummarizerModelId = "override-model";
        var strategy = new PiStyleCrucibleCompactionStrategy(options, summarizer.Object);

        var index = CreateIndex(new List<CompactionMessageGroup>
        {
            CreateGroup(CompactionGroupKind.User, "anchor", 500),
            CreateGroup(CompactionGroupKind.AssistantText, "old", 3000),
            CreateGroup(CompactionGroupKind.User, "newest", 100)
        });

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("override-model", captured?.ModelId);
    }

    [Fact]
    public void Serializer_ClampsLargeToolCallArguments()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "write", new Dictionary<string, object?>
                {
                    ["path"] = "a.cs",
                    ["content"] = new string('Q', 50000)
                })
            })
        };

        var text = ConversationSerializer.SerializeConversation(messages, maxToolResultChars: 200);

        Assert.True(text.Length < 1000, $"Transcript was {text.Length} chars");
        Assert.Contains("path=\"a.cs\"", text);
        Assert.Contains("more characters truncated", text);
    }

    [Theory]
    [InlineData("Path")]
    [InlineData("FILEPATH")]
    [InlineData("TargetFile")]
    [InlineData("absolutePath")]
    public void FileTracker_PathKeyLookup_IsCaseInsensitive(string key)
    {
        var tracker = new FileOperationTracker();
        tracker.RecordToolCall("read", new Dictionary<string, object?> { [key] = "src/File.cs" });

        Assert.Contains("src/File.cs", tracker.ComputeFileLists().ReadFiles);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(300)]
    [InlineData(4096)]
    public void BuildCompactionStrategy_TinyWindows_KeepsPositiveHeadroomAndSaneBudgets(int window)
    {
        var strategy = (PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            null, "some-model", explicitContextWindowTokens: window);

        var o = strategy.Options;
        Assert.True(o.ReserveTokens >= 1 && o.ReserveTokens < window);
        Assert.True(o.TriggerThresholdTokens >= window / 2, $"trigger {o.TriggerThresholdTokens} too low for window {window}");
        Assert.True(o.KeepRecentTokens >= 1 && o.KeepRecentTokens < o.TriggerThresholdTokens);
    }

    [Fact]
    public void BuildCompactionStrategy_ForwardsModelIdToSummarizer()
    {
        var strategy = (PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(null, "profile-model");

        Assert.Equal("profile-model", strategy.Options.SummarizerModelId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void BuildCompactionStrategy_NonPositiveWindow_FallsBackToDefault(int nonPositiveWindow)
    {
        var strategy = (PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            null, "some-model", explicitContextWindowTokens: nonPositiveWindow);

        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, strategy.Options.ContextWindowTokens);
    }

    [Fact]
    public async Task CutPoint_NeverOrphansTrailingToolResults_WhenNoTurnFollows()
    {
        // When a tool result is at the end of the conversation and cannot be kept alongside its call due to tight headroom,
        // it must return false rather than orphaning the tool result.
        var strategy = new PiStyleCrucibleCompactionStrategy(SmallOptions(keepRecent: 100));

        var anchor = CreateGroup(CompactionGroupKind.User, "anchor", 500);
        var call = CreateGroup(
            CompactionGroupKind.ToolCall,
            new[] { new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "A.cs" }) }) },
            100);
        var result = CreateGroup(
            CompactionGroupKind.ToolCall,
            new[] { new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "file content") }) },
            100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, call, result });

        // Compaction cannot safely proceed without orphaning the tool result: must return false
        var compacted = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.False(compacted);
        Assert.DoesNotContain(index.Groups, g => g.Kind == CompactionGroupKind.Summary);
    }

    [Fact]
    public void SeedFromSummaryXml_PreservesHistoricalFileOperations_AndPromotesReadToModified()
    {
        var tracker = new FileOperationTracker();
        var summaryXml = @"## Goal
Work on feature

<read-files>
src/ReadMe.txt
src/Promoted.cs
</read-files>

<modified-files>
src/ExistingMod.cs
</modified-files>";

        tracker.SeedFromSummaryXml(summaryXml);

        // Modify promoted.cs in current turn
        tracker.RecordToolCall("edit", new Dictionary<string, object?> { ["path"] = "src/Promoted.cs" });

        var (read, modified) = tracker.ComputeFileLists();

        Assert.Contains("src/ReadMe.txt", read);
        Assert.DoesNotContain("src/Promoted.cs", read);
        Assert.Contains("src/Promoted.cs", modified);
        Assert.Contains("src/ExistingMod.cs", modified);
    }

    [Fact]
    public async Task RepeatedCompaction_UsesUpdatePrompt_AndPreservesPriorSummaryAndFiles()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        var summarizer = new Mock<IChatClient>();
        summarizer
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((m, _, _) => capturedMessages = m)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nUpdated Goal\n\n## Progress\n### Done\n- All done")));

        var options = SmallOptions(keepRecent: 100);
        var strategy = new PiStyleCrucibleCompactionStrategy(options, summarizer.Object);

        var anchor = CreateGroup(CompactionGroupKind.User, "anchor", 500);
        var priorCheckpoint = CreateGroup(
            CompactionGroupKind.Summary,
            new[]
            {
                new ChatMessage(
                    ChatRole.User,
                    "[CONTEXT COMPACTION CHECKPOINT]\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nOld Goal\n\n<read-files>\nOldRead.cs\n</read-files>")
            },
            500);
        var call = CreateGroup(
            CompactionGroupKind.ToolCall,
            new[] { new ChatMessage(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "write", new Dictionary<string, object?> { ["path"] = "NewWrite.cs" }) }) },
            2500);
        var result = CreateGroup(
            CompactionGroupKind.ToolCall,
            new[] { new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent("c1", "ok") }) },
            100);
        var newest = CreateGroup(CompactionGroupKind.User, "newest", 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, priorCheckpoint, call, result, newest });

        Assert.True(await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None));

        // Verify update prompt was used
        Assert.NotNull(capturedMessages);
        var userPrompt = capturedMessages!.Last().Text!;
        Assert.Contains("<previous-summary>", userPrompt);
        Assert.Contains("Old Goal", userPrompt);
        Assert.Contains("The messages above are NEW conversation messages", userPrompt);

        // Verify old summary group was excluded and replaced with new summary preserving file ops
        Assert.True(priorCheckpoint.IsExcluded);
        var activeSummary = index.Groups.Single(g => g.Kind == CompactionGroupKind.Summary && !g.IsExcluded).Messages[0].Text!;
        Assert.Contains("OldRead.cs", activeSummary);
        Assert.Contains("NewWrite.cs", activeSummary);
    }

    [Fact]
    public async Task Summarizer_CallingTool_ThrowsAndFallsBackToDeterministicSummary()
    {
        var summarizer = new Mock<IChatClient>();
        summarizer
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("spurious_call", "read", new Dictionary<string, object?> { ["path"] = "X.cs" })
            })));

        var strategy = new PiStyleCrucibleCompactionStrategy(SmallOptions(keepRecent: 100), summarizer.Object);

        var anchor = CreateGroup(CompactionGroupKind.User, "anchor", 500);
        var a = CreateGroup(CompactionGroupKind.AssistantText, "action text", 3000);
        var newest = CreateGroup(CompactionGroupKind.User, "newest", 100);

        var index = CreateIndex(new List<CompactionMessageGroup> { anchor, a, newest });

        // Compaction should not fail: must fall back to deterministic summary
        var result = await strategy.CompactAsync(index, NullLogger.Instance, CancellationToken.None);
        Assert.True(result);
        var summary = index.Groups.Single(g => g.Kind == CompactionGroupKind.Summary && !g.IsExcluded).Messages[0].Text!;
        Assert.Contains("## Goal", summary);
        Assert.Contains("Resuming active stage execution from checkpoint.", summary);
    }

    private static CompactionMessageGroup CreateGroup(CompactionGroupKind kind, string text, int tokenCount)
    {
        var role = kind == CompactionGroupKind.User ? ChatRole.User : ChatRole.Assistant;
        return CreateGroup(kind, new[] { new ChatMessage(role, text) }, tokenCount);
    }

    private static CompactionMessageGroup CreateGroup(CompactionGroupKind kind, IReadOnlyList<ChatMessage> messages, int tokenCount)
    {
        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
        return (CompactionMessageGroup)ctor.Invoke(
            new object?[] { kind, messages, tokenCount * 4, tokenCount, 0 });
    }

    private static CompactionMessageIndex CreateIndex(IList<CompactionMessageGroup> groups) => new(groups, null!);
}
