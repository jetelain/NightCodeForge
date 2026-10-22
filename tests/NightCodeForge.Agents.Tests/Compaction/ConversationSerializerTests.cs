using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Compaction;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class ConversationSerializerTests
{
    [Fact]
    public void SerializeConversation_FormatsRolesAndExcludesThinking()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Please inspect the bug"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Deep reasoning trace that should NOT appear in serialized summary"),
                new TextContent("I will check the file."),
                new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "Bug.cs" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", "File content with bug on line 42")
            })
        };

        var serialized = ConversationSerializer.SerializeConversation(messages);

        Assert.Contains("[User]: Please inspect the bug", serialized);
        Assert.Contains("[Assistant]: I will check the file.", serialized);
        Assert.Contains("[Assistant tool calls]: read(path=\"Bug.cs\")", serialized);
        Assert.Contains("[Tool result]: File content with bug on line 42", serialized);
        Assert.DoesNotContain("Deep reasoning trace", serialized);
    }

    [Fact]
    public void SerializeConversation_TruncatesLongToolResults()
    {
        var longResult = new string('x', 5000);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call1", longResult)
            })
        };

        var serialized = ConversationSerializer.SerializeConversation(messages, maxToolResultChars: 100);

        Assert.Contains("[... 4900 more characters truncated]", serialized);
        Assert.StartsWith("[Tool result]: " + new string('x', 100), serialized);
    }

    [Fact]
    public void SerializeConversation_OmitThinking_WhenAssistantMessageOnlyContainsTextReasoningContent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Plan next move"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextReasoningContent("Silent reasoning that must never appear in serialized output")
            }),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call1", "read", new Dictionary<string, object?> { ["path"] = "Main.cs" })
            })
        };

        var serialized = ConversationSerializer.SerializeConversation(messages);

        Assert.Contains("[User]: Plan next move", serialized);
        Assert.Contains("[Assistant tool calls]: read(path=\"Main.cs\")", serialized);
        Assert.DoesNotContain("Silent reasoning", serialized);
        Assert.DoesNotContain("[Assistant]: Silent reasoning", serialized);
    }

    [Fact]
    public void SerializeConversation_SerializesSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "System guidance directive"),
            new(ChatRole.User, "Do work")
        };

        var serialized = ConversationSerializer.SerializeConversation(messages);

        Assert.Contains("[System]: System guidance directive", serialized);
        Assert.Contains("[User]: Do work", serialized);
    }

    [Fact]
    public void SerializeConversation_HandlesJsonElementStringArguments_AndClampsProperly()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"path\": \"src/App.cs\", \"longText\": \"" + new string('Z', 500) + "\"}");
        var root = doc.RootElement;

        var args = new Dictionary<string, object?>
        {
            ["path"] = root.GetProperty("path"),
            ["longText"] = root.GetProperty("longText")
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call1", "write", args)
            })
        };

        var serialized = ConversationSerializer.SerializeConversation(messages, maxToolResultChars: 50);

        Assert.Contains("path=\"src/App.cs\"", serialized);
        Assert.Contains("longText=\"" + new string('Z', 50), serialized);
        Assert.Contains("[... 450 more characters truncated]", serialized);
    }

    [Fact]
    public void SerializeConversation_LabelsCompactionCheckpointSeparately()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "[CONTEXT COMPACTION CHECKPOINT]\nThe conversation history prior to this point was compacted into the following summary:\n\n## Goal\nFix bug")
        };

        var serialized = ConversationSerializer.SerializeConversation(messages);

        Assert.Contains("[Summary Checkpoint]: [CONTEXT COMPACTION CHECKPOINT]", serialized);
        Assert.DoesNotContain("[User]: [CONTEXT COMPACTION CHECKPOINT]", serialized);
    }
}
