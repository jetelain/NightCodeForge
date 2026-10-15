using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class ToolResultCompactorTests
{
    [Fact]
    public void Defaults_AreAppropriateForDevelopmentAgents()
    {
        Assert.Equal(100, ToolResultCompactor.DefaultKeepVerbatimCount);
        Assert.Equal(2000, ToolResultCompactor.DefaultMaxResultLength);
        Assert.Equal(1750, ToolResultCompactor.DefaultTruncateToLength);
    }

    [Fact]
    public void Compact_DefaultEnabled_TruncatesOlderToolOutputsBeyondKeepCount()
    {
        var compactor = new ToolResultCompactor(keepVerbatimCount: 2);
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 5; i++)
        {
            var content = new string('A', 2500);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", content)]));
        }

        compactor.Compact(messages);

        // First 3 messages should be compacted
        for (int i = 0; i < 3; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Contains("[Output truncated; 2500 characters total]", frc.Result?.ToString());
        }

        // Last 2 messages should remain verbatim
        for (int i = 3; i < 5; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Equal(2500, frc.Result?.ToString()?.Length);
        }
    }

    [Fact]
    public void Compact_WhenOptedOut_PreservesAllToolOutputsVerbatim()
    {
        var flags = new LocalOptimizationFlags { CompactOlderToolResults = false };
        var compactor = new ToolResultCompactor(flags, keepVerbatimCount: 2);
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 5; i++)
        {
            var content = new string('B', 2500);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", content)]));
        }

        compactor.Compact(messages);

        // All 5 messages should remain uncompacted
        for (int i = 0; i < 5; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Equal(2500, frc.Result?.ToString()?.Length);
            Assert.DoesNotContain("[Output truncated;", frc.Result?.ToString());
        }
    }

    [Fact]
    public void Compact_WhenOptedOutViaToolResultCompactionAlias_PreservesAllVerbatim()
    {
        var flags = new LocalOptimizationFlags { ToolResultCompaction = false };
        Assert.False(flags.CompactOlderToolResults);

        var messages = new List<ChatMessage>();
        for (int i = 0; i < 4; i++)
        {
            var content = new string('C', 2500);
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{i}", content)]));
        }

        ToolResultCompactor.Compact(messages, flags, keepVerbatimCount: 1);

        for (int i = 0; i < 4; i++)
        {
            var frc = messages[i].Contents.OfType<FunctionResultContent>().First();
            Assert.Equal(2500, frc.Result?.ToString()?.Length);
        }
    }

    [Fact]
    public void Compact_AlsoHandlesTextContentInToolMessages()
    {
        var compactor = new ToolResultCompactor(keepVerbatimCount: 1);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, new string('X', 2500)),
            new(ChatRole.Tool, new string('Y', 2500))
        };

        compactor.Compact(messages);

        // First message was text content and should be compacted
        Assert.Contains("[Output truncated; 2500 characters total]", messages[0].Text);

        // Second message is inside the keep window and should remain intact
        Assert.Equal(2500, messages[1].Text.Length);
    }

    [Fact]
    public void Compact_UsesFlagParameters_WhenNoOverrideProvided()
    {
        var flags = new LocalOptimizationFlags
        {
            ToolKeepVerbatimCount = 1,
            ToolMaxResultLength = 500,
            ToolTruncateToLength = 300
        };

        var compactor = new ToolResultCompactor(flags);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Tool, [new FunctionResultContent("call-0", new string('Z', 600))]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('W', 600))])
        };

        compactor.Compact(messages);

        var first = messages[0].Contents.OfType<FunctionResultContent>().First().Result!.ToString()!;
        Assert.Contains("[Output truncated; 600 characters total]", first);
        Assert.StartsWith(new string('Z', 300), first);

        var second = messages[1].Contents.OfType<FunctionResultContent>().First().Result!.ToString()!;
        Assert.Equal(600, second.Length);
    }
}
