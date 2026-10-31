using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ConversationHistorySerializerTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("opaque-session-id", false)]
    [InlineData("{\"Role\":\"user\"}", false)]
    [InlineData("[]", true)]
    [InlineData("  [{\"Role\":\"user\"}]", true)]
    public void IsSerializedHistory_DetectsJsonArrayCorrectly(string? input, bool expected)
    {
        Assert.Equal(expected, ConversationHistorySerializer.IsSerializedHistory(input));
    }

    [Fact]
    public void Serialize_WhenTurnsIsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ConversationHistorySerializer.Serialize(null!));
    }

    [Fact]
    public void Serialize_WhenTurnsIsEmpty_ReturnsEmptyJsonArray()
    {
        var json = ConversationHistorySerializer.Serialize(Array.Empty<ConversationTurn>());
        Assert.Equal("[]", json);
    }

    [Fact]
    public void Serialize_OrdersByTurnIndex_AndSerializesAllTurnRoles()
    {
        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 5,
                Role = StageTurnRole.System,
                Content = "[System Notice: Host restarted]"
            },
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.User,
                Content = "Bootstrap prompt"
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.Thinking,
                ThinkingTrace = null,
                Content = "Fallback thinking content"
            },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.Tool,
                ToolName = "read",
                ToolArguments = "{\"path\":\"Program.cs\"}",
                MetadataJson = "{\"CallId\":\"call_read_1\"}"
            },
            new()
            {
                TurnIndex = 3,
                Role = StageTurnRole.Optimization,
                Content = "Compacted 1000 tokens"
            },
            new()
            {
                TurnIndex = 4,
                Role = StageTurnRole.ToolResult,
                ToolName = "read",
                ToolResult = "using System;",
                MetadataJson = "{\"CallId\":\"call_read_1\"}"
            },
            new()
            {
                TurnIndex = 6,
                Role = StageTurnRole.Assistant,
                Content = "Done reviewing."
            }
        };

        var json = ConversationHistorySerializer.Serialize(turns);
        var items = ConversationHistorySerializer.Deserialize(json);

        // Optimization turn (TurnIndex 3) is excluded; remaining 6 turns are serialized in TurnIndex order
        Assert.Equal(6, items.Count);

        Assert.Equal("user", items[0].Role);
        Assert.Equal("Bootstrap prompt", items[0].Text);

        Assert.Equal("assistant", items[1].Role);
        Assert.Single(items[1].Contents!);
        Assert.Equal(ConversationHistorySerializer.ContentTypeReasoning, items[1].Contents![0].Type);
        Assert.Equal("Fallback thinking content", items[1].Contents![0].Text);

        Assert.Equal("assistant", items[2].Role);
        Assert.Single(items[2].FunctionCalls!);
        Assert.Equal("call_read_1", items[2].FunctionCalls![0].CallId);
        Assert.Equal("read", items[2].FunctionCalls![0].Name);

        // Because Optimization turn was between Tool and ToolResult, Tool was NOT prematurely flushed as interrupted
        Assert.Equal("tool", items[3].Role);
        Assert.Single(items[3].FunctionResults!);
        Assert.Equal("call_read_1", items[3].FunctionResults![0].CallId);
        Assert.Equal("using System;", items[3].FunctionResults![0].Result);

        // System turn is replayed as user message for chat template compatibility
        Assert.Equal("user", items[4].Role);
        Assert.Equal("[System Notice: Host restarted]", items[4].Text);

        Assert.Equal("assistant", items[5].Role);
        Assert.Equal("Done reviewing.", items[5].Text);
    }

    [Fact]
    public void Serialize_WithNonJsonToolArgumentsAndMissingCallIds_UsesFallbacksAndFlushesTrailingPendingCall()
    {
        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.Tool,
                ToolName = "bash",
                ToolArguments = "raw-non-json-arguments",
                MetadataJson = null
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.ToolResult,
                ToolName = "bash",
                ToolResult = "exit 0",
                MetadataJson = null
            },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.Tool,
                ToolName = "write",
                ToolArguments = null,
                MetadataJson = "{\"CallId\":\"call_dangling\"}"
            }
        };

        var json = ConversationHistorySerializer.Serialize(turns);
        var items = ConversationHistorySerializer.Deserialize(json);

        Assert.Equal(4, items.Count);
        Assert.Equal("call-1", items[0].FunctionCalls![0].CallId);
        Assert.Equal("raw-non-json-arguments", items[0].FunctionCalls![0].Arguments?.ToString());
        Assert.Equal("call-1", items[1].FunctionResults![0].CallId);
        Assert.Equal("exit 0", items[1].FunctionResults![0].Result);

        // Trailing dangling tool call is flushed at the end with InterruptedToolResult
        Assert.Equal("call_dangling", items[2].FunctionCalls![0].CallId);
        Assert.Equal("tool", items[3].Role);
        Assert.Equal("call_dangling", items[3].FunctionResults![0].CallId);
        Assert.Equal(ConversationHistorySerializer.InterruptedToolResult, items[3].FunctionResults![0].Result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void Deserialize_WithEmptyOrNullJson_ReturnsEmptyList(string? json)
    {
        var result = ConversationHistorySerializer.Deserialize(json!);
        Assert.Empty(result);
    }

    [Fact]
    public void Deserialize_WithMalformedJson_ThrowsJsonException()
    {
        Assert.ThrowsAny<JsonException>(() => ConversationHistorySerializer.Deserialize("{malformed"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("{malformed", "{malformed")]
    [InlineData("[]", "[]")]
    public void CompletePendingAskToolCall_WithInvalidOrEmptyHistory_ReturnsInputSafely(string? input, string expected)
    {
        var result = ConversationHistorySerializer.CompletePendingAskToolCall(input, "Use SQLite");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CompletePendingAskToolCall_WhenNoPendingOrInterruptedCalls_ReturnsOriginalJsonUnchanged()
    {
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "read", MetadataJson = "{\"CallId\":\"c1\"}" },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = "read", ToolResult = "ok", MetadataJson = "{\"CallId\":\"c1\"}" }
        };
        var json = ConversationHistorySerializer.Serialize(turns);

        var result = ConversationHistorySerializer.CompletePendingAskToolCall(json, "My answer");
        Assert.Equal(json, result);
    }

    [Fact]
    public void CompletePendingAskToolCall_WhenCallWasClosedWithInterruptedResult_ReplacesResultInPlaceAndAppendsProceedInstruction()
    {
        // Serialize a conversation with an unanswered 'ask' call, which Serialize closes with InterruptedToolResult
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Review task" },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "ask", ToolArguments = "{\"questions\":[]}", MetadataJson = "{\"CallId\":\"ask_1\"}" }
        };
        var serializedWithInterrupted = ConversationHistorySerializer.Serialize(turns);

        var completedJson = ConversationHistorySerializer.CompletePendingAskToolCall(
            serializedWithInterrupted,
            "Use PostgreSQL",
            "Proceed with PostgreSQL.");

        var items = ConversationHistorySerializer.Deserialize(completedJson);
        Assert.Equal(4, items.Count);
        Assert.Equal("tool", items[2].Role);
        Assert.Equal("ask_1", items[2].FunctionResults![0].CallId);
        Assert.Equal("Use PostgreSQL", items[2].FunctionResults![0].Result);
        Assert.Equal("Use PostgreSQL", items[2].Contents![0].Result);
        Assert.Equal("user", items[3].Role);
        Assert.Equal("Proceed with PostgreSQL.", items[3].Text);
    }

    [Fact]
    public void CompletePendingAskToolCall_WhenPendingCallHasNoResultItem_AppendsToolResultAndOptionalInstruction()
    {
        var rawItems = new List<ConversationHistorySerializer.SerializedMessageItem>
        {
            new(
                Role: "assistant",
                FunctionCalls: new List<ConversationHistorySerializer.SerializedFunctionCall>
                {
                    new("ask_open_1", "ask", new { questions = Array.Empty<string>() })
                },
                Contents: new List<ConversationHistorySerializer.SerializedContent>
                {
                    new(ConversationHistorySerializer.ContentTypeCall, CallId: "ask_open_1", Name: "ask")
                })
        };
        var json = JsonSerializer.Serialize(rawItems);

        // Pass empty proceedInstruction to verify it omits the trailing user message when blank
        var completedJson = ConversationHistorySerializer.CompletePendingAskToolCall(json, "Clarified answer", proceedInstruction: "");
        var items = ConversationHistorySerializer.Deserialize(completedJson);

        Assert.Equal(2, items.Count);
        Assert.Equal("tool", items[1].Role);
        Assert.Equal("ask_open_1", items[1].FunctionResults![0].CallId);
        Assert.Equal("Clarified answer", items[1].FunctionResults![0].Result);
    }

    [Fact]
    public void FormatForPrompt_FormatsAllContentTypesAndLegacyItemsCorrectly()
    {
        Assert.Equal(string.Empty, ConversationHistorySerializer.FormatForPrompt(""));
        Assert.Equal("[]", ConversationHistorySerializer.FormatForPrompt("[]"));

        var items = new List<ConversationHistorySerializer.SerializedMessageItem>
        {
            new(
                Role: "user",
                Contents: new List<ConversationHistorySerializer.SerializedContent>
                {
                    new(ConversationHistorySerializer.ContentTypeText, Text: "Please implement X")
                }),
            new(
                Role: "assistant",
                Contents: new List<ConversationHistorySerializer.SerializedContent>
                {
                    new(ConversationHistorySerializer.ContentTypeReasoning, Text: "I should inspect Y"),
                    new(ConversationHistorySerializer.ContentTypeText, Text: "Inspecting Y now"),
                    new(ConversationHistorySerializer.ContentTypeCall, CallId: "c1", Name: "read", Arguments: new { path = "Y.cs" }),
                    new(ConversationHistorySerializer.ContentTypeCall, CallId: "c2", Name: "bash", Arguments: "dotnet --info")
                }),
            new(
                Role: "tool",
                Contents: new List<ConversationHistorySerializer.SerializedContent>
                {
                    new(ConversationHistorySerializer.ContentTypeResult, CallId: "c1", Result: "class Y {}")
                }),
            new(
                Role: "system",
                Contents: new List<ConversationHistorySerializer.SerializedContent>
                {
                    new(ConversationHistorySerializer.ContentTypeText, Text: "Host restarted")
                }),
            new(
                Role: "assistant",
                Text: "Legacy assistant text without Contents")
        };

        var json = JsonSerializer.Serialize(items);
        var formatted = ConversationHistorySerializer.FormatForPrompt(json);

        Assert.Contains("[User]: Please implement X", formatted);
        Assert.Contains("[Thinking]: I should inspect Y", formatted);
        Assert.Contains("[Assistant]: Inspecting Y now", formatted);
        Assert.Contains("[Tool Call]: read({\"path\":\"Y.cs\"})", formatted);
        Assert.Contains("[Tool Call]: bash(\"dotnet --info\")", formatted);
        Assert.Contains("[Tool Result]: class Y {}", formatted);
        Assert.Contains("[System Notice]: Host restarted", formatted);
        Assert.Contains("[assistant]: Legacy assistant text without Contents", formatted);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("not-json", null)]
    [InlineData("[1,2,3]", null)]
    [InlineData("{\"Other\":\"val\"}", null)]
    [InlineData("{\"CallId\":123}", null)]
    [InlineData("{\"CallId\":\"call_pascal\"}", "call_pascal")]
    [InlineData("{\"callId\":\"call_camel\"}", "call_camel")]
    public void ExtractCallId_HandlesAllMetadataVariations(string? metadataJson, string? expected)
    {
        Assert.Equal(expected, ConversationHistorySerializer.ExtractCallId(metadataJson));

        var turn = metadataJson != null ? new ConversationTurn { MetadataJson = metadataJson } : null;
        Assert.Equal(expected, ConversationHistorySerializer.ExtractCallId(turn));
    }

    [Fact]
    public void GetTrailingInterruptedTurns_HandlesNullEmptyAskAndAbortedSteps()
    {
        Assert.Throws<ArgumentNullException>(() => ConversationHistorySerializer.GetTrailingInterruptedTurns(null!));
        Assert.Empty(ConversationHistorySerializer.GetTrailingInterruptedTurns(Array.Empty<ConversationTurn>()));

        // 1. Trailing 'ask' tool turn must NEVER be removed even when unanswered
        var askTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, ThinkingTrace = "Need clarification" },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = "ask", MetadataJson = "{\"CallId\":\"ask_1\"}" }
        };
        Assert.Empty(ConversationHistorySerializer.GetTrailingInterruptedTurns(askTurns));

        // 2. Inline deterministic tool turn with ToolResult != null and no CallId is answered -> not removed
        var inlineAnsweredTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = "build", ToolResult = "Build succeeded" }
        };
        Assert.Empty(ConversationHistorySerializer.GetTrailingInterruptedTurns(inlineAnsweredTurns));

        // 3. Multiple parallel trailing unanswered tool turns + preceding thinking turns are all returned for removal
        var abortedStepTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "read", MetadataJson = "{\"CallId\":\"c1\"}" },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = "read", ToolResult = "ok", MetadataJson = "{\"CallId\":\"c1\"}" },
            new() { TurnIndex = 3, Role = StageTurnRole.Thinking, ThinkingTrace = "Thinking part 1" },
            new() { TurnIndex = 4, Role = StageTurnRole.Thinking, ThinkingTrace = "Thinking part 2" },
            new() { TurnIndex = 5, Role = StageTurnRole.Tool, ToolName = "bash", MetadataJson = "{\"CallId\":\"c2\"}" },
            new() { TurnIndex = 6, Role = StageTurnRole.Tool, ToolName = "write", MetadataJson = null, ToolResult = null }
        };

        var toRemove = ConversationHistorySerializer.GetTrailingInterruptedTurns(abortedStepTurns);
        Assert.Equal(4, toRemove.Count);
        Assert.Contains(toRemove, t => t.TurnIndex == 6);
        Assert.Contains(toRemove, t => t.TurnIndex == 5);
        Assert.Contains(toRemove, t => t.TurnIndex == 4);
        Assert.Contains(toRemove, t => t.TurnIndex == 3);

        // 4. Aborted tool step with accompanying Assistant text + Thinking turns removes all turns of that aborted step
        var abortedStepWithAssistantTurns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, ThinkingTrace = "Planning bash command" },
            new() { TurnIndex = 2, Role = StageTurnRole.Assistant, Content = "I will run the build now." },
            new() { TurnIndex = 3, Role = StageTurnRole.Tool, ToolName = "bash", MetadataJson = "{\"CallId\":\"c_unanswered\"}" }
        };

        var removedWithAssistant = ConversationHistorySerializer.GetTrailingInterruptedTurns(abortedStepWithAssistantTurns);
        Assert.Equal(3, removedWithAssistant.Count);
        Assert.Contains(removedWithAssistant, t => t.TurnIndex == 3);
        Assert.Contains(removedWithAssistant, t => t.TurnIndex == 2);
        Assert.Contains(removedWithAssistant, t => t.TurnIndex == 1);

        // 5. Trailing Thinking turn (paused mid-thinking before any tool/assistant turn was emitted) is removed
        var trailingThinkingOnly = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Assistant, Content = "Completed turn 1" },
            new() { TurnIndex = 2, Role = StageTurnRole.Thinking, ThinkingTrace = "Interrupted mid-thought..." }
        };

        var removedThinking = ConversationHistorySerializer.GetTrailingInterruptedTurns(trailingThinkingOnly);
        var singleThinking = Assert.Single(removedThinking);
        Assert.Equal(2, singleThinking.TurnIndex);

        // 6. Multi-tool step where first tool completed (ToolResult written at end) and second tool was interrupted by pause:
        // only the unanswered second tool turn is removed, while Thinking, Assistant, and the completed first tool turn remain intact.
        var partiallyCompletedMultiToolStep = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Start" },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, ThinkingTrace = "I will write a file and run tests" },
            new() { TurnIndex = 2, Role = StageTurnRole.Assistant, Content = "Writing file and running tests." },
            new() { TurnIndex = 3, Role = StageTurnRole.Tool, ToolName = "write", MetadataJson = "{\"CallId\":\"call_write_1\"}" },
            new() { TurnIndex = 4, Role = StageTurnRole.Tool, ToolName = "bash", MetadataJson = "{\"CallId\":\"call_bash_2\"}" },
            new() { TurnIndex = 5, Role = StageTurnRole.ToolResult, ToolName = "write", ToolResult = "Wrote file", MetadataJson = "{\"CallId\":\"call_write_1\"}" }
        };

        var removedFromPartialMultiTool = ConversationHistorySerializer.GetTrailingInterruptedTurns(partiallyCompletedMultiToolStep);
        var singleRemovedTool = Assert.Single(removedFromPartialMultiTool);
        Assert.Equal(4, singleRemovedTool.TurnIndex);
        Assert.Equal("bash", singleRemovedTool.ToolName);
    }
}
