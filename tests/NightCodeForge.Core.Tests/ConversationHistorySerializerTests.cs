using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class ConversationHistorySerializerTests
{
    [Fact]
    public void Serialize_WithVariousTurnRoles_ProducesStructuredJson()
    {
        var turns = new List<ConversationTurn>
        {
            new()
            {
                Id = 1,
                TurnIndex = 0,
                Role = StageTurnRole.User,
                Content = "Please write a calculator class."
            },
            new()
            {
                Id = 2,
                TurnIndex = 1,
                Role = StageTurnRole.Thinking,
                Content = "I need to plan the methods: Add, Subtract, Multiply, Divide.",
                ThinkingTrace = "I need to plan the methods: Add, Subtract, Multiply, Divide."
            },
            new()
            {
                Id = 3,
                TurnIndex = 2,
                Role = StageTurnRole.Tool,
                ToolName = "read_file",
                ToolArguments = "{\"path\":\"Calculator.cs\"}",
                MetadataJson = "{\"CallId\":\"call-101\"}"
            },
            new()
            {
                Id = 4,
                TurnIndex = 3,
                Role = StageTurnRole.ToolResult,
                ToolName = "read_file",
                ToolResult = "public class Calculator {}",
                MetadataJson = "{\"CallId\":\"call-101\"}"
            },
            new()
            {
                Id = 5,
                TurnIndex = 4,
                Role = StageTurnRole.Assistant,
                Content = "I found the Calculator class definition."
            },
            new()
            {
                Id = 6,
                TurnIndex = 5,
                Role = StageTurnRole.Optimization,
                Content = "Truncated 1 older tool result"
            },
            new()
            {
                Id = 7,
                TurnIndex = 6,
                Role = StageTurnRole.System,
                Content = "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]"
            }
        };

        var json = ConversationHistorySerializer.Serialize(turns);
        Assert.NotNull(json);

        var deserialized = ConversationHistorySerializer.Deserialize(json);
        // Optimization turn is omitted from model chat history
        Assert.Equal(6, deserialized.Count);

        // Turn 0: User
        Assert.Equal("user", deserialized[0].Role);
        Assert.Equal("Please write a calculator class.", deserialized[0].Text);
        Assert.Single(deserialized[0].Contents!);
        Assert.Equal("text", deserialized[0].Contents![0].Type);

        // Turn 1: Thinking
        Assert.Equal("assistant", deserialized[1].Role);
        Assert.Single(deserialized[1].Contents!);
        Assert.Equal("reasoning", deserialized[1].Contents![0].Type);
        Assert.Contains("I need to plan the methods", deserialized[1].Contents![0].Text);

        // Turn 2: Tool call
        Assert.Equal("assistant", deserialized[2].Role);
        Assert.NotNull(deserialized[2].FunctionCalls);
        Assert.Single(deserialized[2].FunctionCalls!);
        Assert.Equal("call-101", deserialized[2].FunctionCalls![0].CallId);
        Assert.Equal("read_file", deserialized[2].FunctionCalls![0].Name);

        // Turn 3: Tool result
        Assert.Equal("tool", deserialized[3].Role);
        Assert.NotNull(deserialized[3].FunctionResults);
        Assert.Single(deserialized[3].FunctionResults!);
        Assert.Equal("call-101", deserialized[3].FunctionResults![0].CallId);
        Assert.Equal("public class Calculator {}", deserialized[3].FunctionResults![0].Result);

        // Turn 4: Assistant text
        Assert.Equal("assistant", deserialized[4].Role);
        Assert.Equal("I found the Calculator class definition.", deserialized[4].Text);

        // Turn 5: System notice
        Assert.Equal("user", deserialized[5].Role);
        Assert.Contains("Host restarted unexpectedly", deserialized[5].Text);
    }

    [Fact]
    public void FormatForPrompt_FormatsReadableChatTranscript()
    {
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Build feature X" },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, Content = "Analyzing scope" },
            new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = "search_code", ToolArguments = "{\"query\":\"feature\"}" },
            new() { TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolResult = "No results" },
            new() { TurnIndex = 4, Role = StageTurnRole.System, Content = "[System Notice: Resumed]" }
        };

        var json = ConversationHistorySerializer.Serialize(turns);
        var formatted = ConversationHistorySerializer.FormatForPrompt(json);

        Assert.Contains("[User]: Build feature X", formatted);
        Assert.Contains("[Thinking]: Analyzing scope", formatted);
        Assert.Contains("[Tool Call]: search_code", formatted);
        Assert.Contains("[Tool Result]: No results", formatted);
        Assert.Contains("[User]: [System Notice: Resumed]", formatted);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsEmpty()
    {
        Assert.Empty(ConversationHistorySerializer.Deserialize(string.Empty));
        Assert.Empty(ConversationHistorySerializer.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_MalformedJson_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ConversationHistorySerializer.Deserialize("not valid json"));
    }

    [Fact]
    public void Serialize_ToolCallWithoutResult_AddsInterruptedResultBeforeNextMessage()
    {
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = "a", MetadataJson = "{\"CallId\":\"c1\"}" },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "b", MetadataJson = "{\"CallId\":\"c2\"}" },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolResult = "ok", MetadataJson = "{\"CallId\":\"c1\"}" },
            new() { TurnIndex = 3, Role = StageTurnRole.System, Content = "notice" }
        };

        var items = ConversationHistorySerializer.Deserialize(ConversationHistorySerializer.Serialize(turns));

        Assert.Equal(5, items.Count); // call c1, call c2, result c1, synthetic result c2, notice
        Assert.Equal("c2", items[3].FunctionResults![0].CallId);
        Assert.Equal(ConversationHistorySerializer.InterruptedToolResult, items[3].FunctionResults![0].Result);
        Assert.Equal("user", items[4].Role);
    }

    [Fact]
    public void IsSerializedHistory_DistinguishesHistoryFromOpaqueSessionIds()
    {
        Assert.True(ConversationHistorySerializer.IsSerializedHistory("[]"));
        Assert.False(ConversationHistorySerializer.IsSerializedHistory("session-abc"));
        Assert.False(ConversationHistorySerializer.IsSerializedHistory(null));
    }
}
