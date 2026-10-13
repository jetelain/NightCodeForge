using System.Text.Json;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class AgentActivityAndCrucibleConfigTests
{
    [Fact]
    public void CrucibleHarnessConfig_DeserializesCustomTools_AndDirectives()
    {
        var json = """
        {
          "AllowedTools": ["read", "custom_eval"],
          "ToolDirectives": {
            "read": "Always inspect full file before editing"
          },
          "CustomSystemRules": "Strictly adhere to TDD conventions.",
          "CustomTools": [
            {
              "Name": "custom_eval",
              "Description": "Evaluates custom expression",
              "Aliases": ["eval", "calc"]
            }
          ]
        }
        """;

        var config = JsonSerializer.Deserialize<CrucibleHarnessConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.NotNull(config.AllowedTools);
        Assert.Equal(2, config.AllowedTools.Count);
        Assert.Contains("custom_eval", config.AllowedTools);
        Assert.Equal("Strictly adhere to TDD conventions.", config.CustomSystemRules);
        Assert.Single(config.CustomTools);
        Assert.Equal("custom_eval", config.CustomTools[0].Name);
        Assert.Contains("calc", config.CustomTools[0].Aliases);
    }

    [Fact]
    public async Task InMemoryAgentActivityStreamer_BroadcastsAndFiltersByIssue()
    {
        var streamer = new InMemoryAgentActivityStreamer();
        var sessionEvents = new List<AgentActivityEvent>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriptionTask = Task.Run(async () =>
        {
            await foreach (var ev in streamer.SubscribeAsync(issueId: 42, cts.Token))
            {
                sessionEvents.Add(ev);
                if (sessionEvents.Count == 2) break;
            }
        });

        // Wait until subscriber is registered
        while (streamer.ActiveSubscriptionsCount == 0)
        {
            await Task.Delay(10);
        }

        // Publish events for issue 42 and another issue
        await streamer.BroadcastAsync(new AgentActivityEvent("session-1", 42, "planning", AgentActivityKind.ThinkingDelta, "Thinking about step 1...", DateTime.UtcNow));
        await streamer.BroadcastAsync(new AgentActivityEvent("session-2", 99, "planning", AgentActivityKind.ThinkingDelta, "Should not be received", DateTime.UtcNow));
        await streamer.BroadcastAsync(new AgentActivityEvent("session-1", 42, "execution", AgentActivityKind.ToolStarted, "read", DateTime.UtcNow));

        await subscriptionTask;

        Assert.Equal(2, sessionEvents.Count);
        Assert.Equal(AgentActivityKind.ThinkingDelta, sessionEvents[0].Kind);
        Assert.Equal("Thinking about step 1...", sessionEvents[0].Content);
        Assert.Equal(AgentActivityKind.ToolStarted, sessionEvents[1].Kind);
        Assert.Equal("read", sessionEvents[1].Content);
    }
}
