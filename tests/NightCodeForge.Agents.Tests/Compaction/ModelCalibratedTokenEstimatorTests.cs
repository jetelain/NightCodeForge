using System.Reflection;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Compaction;
using Pmad.AI.Tokens;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class ModelCalibratedTokenEstimatorTests
{
    [Theory]
    [InlineData("Qwen3.8-27B:latest", "Qwen")]
    [InlineData("qwen2.5-coder:32b", "Qwen")]
    [InlineData("QWEN-coder", "Qwen")]
    [InlineData("gemma3:27b", "Gemma")]
    [InlineData("Gemma-2-9b-it", "Gemma")]
    [InlineData("GEMMA", "Gemma")]
    [InlineData("llama3.3:70b", "Default")]
    [InlineData("deepseek-r1:32b", "Default")]
    [InlineData("", "Default")]
    [InlineData("   ", "Default")]
    [InlineData(null, "Default")]
    public void Resolve_ReturnsExpectedEstimatorInstance(string? modelId, string expectedKind)
    {
        var actual = ModelCalibratedTokenEstimator.Resolve(modelId);
        var expected = expectedKind switch
        {
            "Qwen" => HeuristicTokenEstimator.Qwen,
            "Gemma" => HeuristicTokenEstimator.Gemma,
            _ => HeuristicTokenEstimator.Default
        };

        Assert.Same(expected, actual);
    }

    [Fact]
    public void EstimateTextTokens_ThrowsArgumentNullException_WhenEstimatorIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ModelCalibratedTokenEstimator.EstimateTextTokens("hello", (HeuristicTokenEstimator)null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EstimateTextTokens_ReturnsZero_ForNullOrEmptyText(string? text)
    {
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateTextTokens(text, "qwen3.8:35b"));
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateTextTokens(text, HeuristicTokenEstimator.Qwen));
    }

    [Fact]
    public void EstimateTextTokens_ReturnsAtLeastOne_ForWhitespaceOnlyNonEmptyText()
    {
        // HeuristicTokenEstimator.Estimate returns 0 for pure whitespace, but EstimateTextTokens clamps non-empty strings to >= 1
        Assert.Equal(1, ModelCalibratedTokenEstimator.EstimateTextTokens("   ", HeuristicTokenEstimator.Default));
    }

    [Fact]
    public void EstimateTextTokens_UsesModelCalibratedEstimator_ForSymbolHeavyCode()
    {
        var code = "public record RouterTarget(string Name, Uri Endpoint, int MaxContextTokens) { public bool IsEnabled => MaxContextTokens > 0; }";
        int qwenTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(code, "Qwen3.8-27B:latest");
        int gemmaTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(code, "gemma3:27b");
        int defaultTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(code, "llama3:8b");

        Assert.Equal(HeuristicTokenEstimator.Qwen.Estimate(code.AsSpan()), qwenTokens);
        Assert.Equal(HeuristicTokenEstimator.Gemma.Estimate(code.AsSpan()), gemmaTokens);
        Assert.Equal(HeuristicTokenEstimator.Default.Estimate(code.AsSpan()), defaultTokens);
    }

    [Fact]
    public void EstimateMessageTokens_ThrowsArgumentNullException_WhenEstimatorIsNull()
    {
        var msg = new ChatMessage(ChatRole.User, "hi");
        Assert.Throws<ArgumentNullException>(() =>
            ModelCalibratedTokenEstimator.EstimateMessageTokens(msg, (HeuristicTokenEstimator)null!));
    }

    [Fact]
    public void EstimateMessageTokens_ReturnsZero_WhenMessageIsNull()
    {
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateMessageTokens(null, "Qwen3.8-27B"));
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateMessageTokens(null, HeuristicTokenEstimator.Qwen));
    }

    [Fact]
    public void EstimateMessageTokens_ReturnsFramingTokens_WhenMessageHasNoContents()
    {
        var emptyMsg = new ChatMessage(ChatRole.Assistant, Array.Empty<AIContent>());
        Assert.Equal(ModelCalibratedTokenEstimator.PerMessageFramingTokens,
            ModelCalibratedTokenEstimator.EstimateMessageTokens(emptyMsg, HeuristicTokenEstimator.Default));
    }

    [Fact]
    public void EstimateMessageTokens_HandlesFunctionCallAndFunctionResult_WithComplexAndNonSerializablePayloads()
    {
        var nonSerializable = new SelfReferencingPayload();
        nonSerializable.Self = nonSerializable;

        var callMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("c1", "complex_tool", new Dictionary<string, object?>
            {
                ["strArg"] = "plain string",
                ["objArg"] = new { Count = 5, Tags = new[] { "a", "b" } },
                ["nullArg"] = null,
                ["badArg"] = nonSerializable
            })
        });

        int callTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(callMsg, HeuristicTokenEstimator.Qwen);
        Assert.True(callTokens > ModelCalibratedTokenEstimator.PerMessageFramingTokens);

        var resultWithObject = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("c1", new { Status = "ok", Lines = 120 }),
            new FunctionResultContent("c2", nonSerializable)
        });

        int resultTokens = ModelCalibratedTokenEstimator.EstimateMessageTokens(resultWithObject, HeuristicTokenEstimator.Qwen);
        Assert.True(resultTokens > ModelCalibratedTokenEstimator.PerMessageFramingTokens);
    }

    [Fact]
    public void EstimateMessagesTokens_HandlesNullEmptyAndNullElements()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ModelCalibratedTokenEstimator.EstimateMessagesTokens(Array.Empty<ChatMessage>(), (HeuristicTokenEstimator)null!));

        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateMessagesTokens(null, "qwen3.8"));
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateMessagesTokens(Array.Empty<ChatMessage>(), "qwen3.8"));
        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateMessagesTokens(new ChatMessage?[] { null, null }!, "qwen3.8"));

        var m1 = new ChatMessage(ChatRole.User, "First message");
        var m2 = new ChatMessage(ChatRole.Assistant, "Second message");
        int sum = ModelCalibratedTokenEstimator.EstimateMessageTokens(m1, "qwen3.8")
                + ModelCalibratedTokenEstimator.EstimateMessageTokens(m2, "qwen3.8");

        Assert.Equal(sum, ModelCalibratedTokenEstimator.EstimateMessagesTokens(new ChatMessage?[] { m1, null, m2 }!, "qwen3.8"));
    }

    [Fact]
    public void EstimateGroupTokens_HandlesNullRealAndSyntheticGroups()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ModelCalibratedTokenEstimator.EstimateGroupTokens(null, null!));

        Assert.Equal(0, ModelCalibratedTokenEstimator.EstimateGroupTokens(null, HeuristicTokenEstimator.Qwen));

        // 1. Real group where ByteCount == actual UTF-8 byte count and TokenCount == ByteCount / 4:
        // Must return the Pmad.AI estimate, NOT ByteCount / 4.
        var codeText = "public static void Main(string[] args) { Console.WriteLine(\"Hello, world!\"); }";
        var realMsg = new ChatMessage(ChatRole.User, codeText);
        int actualBytes = System.Text.Encoding.UTF8.GetByteCount(codeText);
        var realGroup = CreateGroup(CompactionGroupKind.User, [realMsg], byteCount: actualBytes, tokenCount: actualBytes / 4);

        int expectedRealEstimate = ModelCalibratedTokenEstimator.EstimateMessagesTokens([realMsg], HeuristicTokenEstimator.Qwen);
        Assert.Equal(expectedRealEstimate, ModelCalibratedTokenEstimator.EstimateGroupTokens(realGroup, HeuristicTokenEstimator.Qwen));

        // 2. Synthetic test group where short placeholder text ("anchor") is given a high explicit TokenCount (e.g. 5,000):
        // EstimateGroupTokens preserves the explicit synthetic TokenCount unless useEstimatedOnly is true.
        var syntheticMsg = new ChatMessage(ChatRole.User, "anchor");
        var syntheticGroup = CreateGroup(CompactionGroupKind.User, [syntheticMsg], byteCount: 20_000, tokenCount: 5_000);

        Assert.Equal(5_000, ModelCalibratedTokenEstimator.EstimateGroupTokens(syntheticGroup, HeuristicTokenEstimator.Qwen, useEstimatedOnly: false));
        Assert.Equal(
            ModelCalibratedTokenEstimator.EstimateMessagesTokens([syntheticMsg], HeuristicTokenEstimator.Qwen),
            ModelCalibratedTokenEstimator.EstimateGroupTokens(syntheticGroup, HeuristicTokenEstimator.Qwen, useEstimatedOnly: true));

        // 3. Synthetic group with Reasoning, FunctionCall, and FunctionResult contents
        var complexMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("thinking"),
            new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "a.cs" }),
            new FunctionResultContent("c1", "result")
        });
        var complexSyntheticGroup = CreateGroup(CompactionGroupKind.ToolCall, [complexMsg], byteCount: 12_000, tokenCount: 3_000);
        Assert.Equal(3_000, ModelCalibratedTokenEstimator.EstimateGroupTokens(complexSyntheticGroup, HeuristicTokenEstimator.Qwen));
    }

    [Fact]
    public void EstimateGroupTokens_RealToolCallGroupFromCompactionMessageIndexCreate_UsesPmadEstimateNotByteCountDivFour()
    {
        // Multi-byte UTF-8 box-drawing characters ('─' = 3 UTF-8 bytes per char) make CompactionMessageIndex's
        // UTF8ByteCount / 4 higher than Pmad.AI's char-based estimate. Together with a non-empty FunctionCallContent.CallId
        // and TextReasoningContent.ProtectedData, ComputeActualMessageBytes must match CompactionMessageIndex.ComputeContentByteCount
        // so the real group is not misclassified as a synthetic test group.
        var assistantCall = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("Inspecting table layout---") { ProtectedData = "protected_sig_12" },
            new FunctionCallContent("call_id_0001", "read_file", new Dictionary<string, object?> { ["path"] = "table.txt" })
        });
        var toolResult = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call_id_0001", new string('─', 400))
        });

        var index = CreateIndexFromMessages([assistantCall, toolResult]);
        var group = Assert.Single(index.Groups);
        Assert.Equal(CompactionGroupKind.ToolCall, group.Kind);

        // Ensure ByteCount is a multiple of 4 and group.TokenCount > Pmad.AI estimate
        int expectedPmadEstimate = ModelCalibratedTokenEstimator.EstimateMessagesTokens(group.Messages, HeuristicTokenEstimator.Qwen);
        Assert.Equal(group.TokenCount * 4, group.ByteCount);
        Assert.True(group.TokenCount > expectedPmadEstimate);

        int actualEstimate = ModelCalibratedTokenEstimator.EstimateGroupTokens(group, HeuristicTokenEstimator.Qwen);
        Assert.Equal(expectedPmadEstimate, actualEstimate);
    }

    [Fact]
    public void EstimateGroupTokens_WhenToolOutputClampedInPlace_ReturnsPostClampPmadEstimate()
    {
        var hugeOutput = new string('A', 16_000);
        var funcResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call_clamp_1", hugeOutput)
        });
        var textResultMsg = new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new TextContent(hugeOutput)
        });

        var group1 = CreateGroup(CompactionGroupKind.ToolCall, [funcResultMsg], byteCount: 32_000, tokenCount: 8_000);
        var group2 = CreateGroup(CompactionGroupKind.ToolCall, [textResultMsg], byteCount: 32_000, tokenCount: 8_000);

        // Before clamping: synthetic TokenCount (8,000) is returned
        Assert.Equal(8_000, ModelCalibratedTokenEstimator.EstimateGroupTokens(group1, HeuristicTokenEstimator.Qwen));
        Assert.Equal(8_000, ModelCalibratedTokenEstimator.EstimateGroupTokens(group2, HeuristicTokenEstimator.Qwen));

        // Simulate in-place clamping by PiStyleCrucibleCompactionStrategy.ClampOversizedToolResults
        string clampedPayload = new string('A', 2_000) + $"\n{ModelCalibratedTokenEstimator.ClampedOutputMarker}2,000 characters]";
        funcResultMsg.Contents.OfType<FunctionResultContent>().Single().Result = clampedPayload;
        textResultMsg.Contents.OfType<TextContent>().Single().Text = clampedPayload;

        int expectedPostClamp1 = ModelCalibratedTokenEstimator.EstimateMessagesTokens([funcResultMsg], HeuristicTokenEstimator.Qwen);
        int expectedPostClamp2 = ModelCalibratedTokenEstimator.EstimateMessagesTokens([textResultMsg], HeuristicTokenEstimator.Qwen);

        Assert.True(expectedPostClamp1 < 1_000);
        Assert.Equal(expectedPostClamp1, ModelCalibratedTokenEstimator.EstimateGroupTokens(group1, HeuristicTokenEstimator.Qwen));
        Assert.Equal(expectedPostClamp2, ModelCalibratedTokenEstimator.EstimateGroupTokens(group2, HeuristicTokenEstimator.Qwen));
    }

    private sealed class SelfReferencingPayload
    {
        public SelfReferencingPayload? Self { get; set; }
        public override string ToString() => "SelfReferencingPayloadFallback";
    }

    private static CompactionMessageIndex CreateIndexFromMessages(IList<ChatMessage> messages)
    {
        var method = typeof(CompactionMessageIndex).GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = method.GetParameters().Length == 2
            ? new object?[] { messages, null }
            : new object?[] { messages };
        return (CompactionMessageIndex)method.Invoke(null, args)!;
    }

    private static CompactionMessageGroup CreateGroup(
        CompactionGroupKind kind,
        IReadOnlyList<ChatMessage> messages,
        int byteCount,
        int tokenCount)
    {
        var ctor = typeof(CompactionMessageGroup).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance)[0];
        return (CompactionMessageGroup)ctor.Invoke([kind, messages, byteCount, tokenCount, 0]);
    }
}

