using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class StageSummaryFidelityTests
{
    [Fact]
    public void Extractor_PreservesGenericsAndCode_WhileParsingAndStrippingRoutingTags()
    {
        var input = "Refactored IRepository<TEntity> and Task<ActionResult<List<string>>> in Service.cs.\n" +
                    "<request-changes>\nPlease add unit tests for IRepository<TEntity>.\n</request-changes>";

        var result = StageSummaryExtractor.Extract(input);

        Assert.Equal("Refactored IRepository<TEntity> and Task<ActionResult<List<string>>> in Service.cs.", result.Summary);
        Assert.Equal("Please add unit tests for IRepository<TEntity>.", result.ReviewerFeedback);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
        Assert.False(result.IsApproved);
    }

    [Fact]
    public void Extractor_ParsesAndStripsUnderscoreRequestChangesTag()
    {
        var input = "Fixed the bug.\n<request_changes>\nMissing integration test.\n</request_changes>";

        var result = StageSummaryExtractor.Extract(input);

        Assert.Equal("Fixed the bug.", result.Summary);
        Assert.Equal("Missing integration test.", result.ReviewerFeedback);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
    }

    [Fact]
    public void Extractor_ParsesAndStripsApprovedTag_EnclosingAndSelfClosing()
    {
        var input1 = "All tasks completed.\n<approved>LGTM, ready to merge.</approved>";
        var result1 = StageSummaryExtractor.Extract(input1);
        Assert.Equal("All tasks completed.", result1.Summary);
        Assert.True(result1.IsApproved);
        Assert.Null(result1.TargetRouting);

        var input2 = "Implementation verified.\n<approved />";
        var result2 = StageSummaryExtractor.Extract(input2);
        Assert.Equal("Implementation verified.", result2.Summary);
        Assert.True(result2.IsApproved);
        Assert.Null(result2.TargetRouting);
    }

    [Fact]
    public void Extractor_WhenConcludingTextIsEmptyOrOnlyTags_FallsBackToDefault()
    {
        var resultEmpty = StageSummaryExtractor.Extract(null);
        Assert.Equal(StageSummaryExtractor.DefaultCompletedSummary, resultEmpty.Summary);

        var resultOnlyTags = StageSummaryExtractor.Extract("<approved></approved>");
        Assert.Equal(StageSummaryExtractor.DefaultCompletedSummary, resultOnlyTags.Summary);
        Assert.True(resultOnlyTags.IsApproved);

        var customFallback = StageSummaryExtractor.Extract("", fallbackSummary: "Custom completed summary.");
        Assert.Equal("Custom completed summary.", customFallback.Summary);
    }

    [Fact]
    public void Extractor_GetConcludingText_SelectsLastAssistantMessageWithNonEmptyText()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Start"),
            new(ChatRole.Assistant, "First thought"),
            new(ChatRole.Tool, "Tool result"),
            new(ChatRole.Assistant, new List<AIContent> { new TextReasoningContent("Thinking only") }), // No text
            new(ChatRole.Assistant, "Final concluding synthesis"),
            new(ChatRole.User, "Follow-up without assistant reply")
        };

        var concluding = StageSummaryExtractor.GetConcludingText(messages);
        Assert.Equal("Final concluding synthesis", concluding);
    }

    [Fact]
    public async Task CrucibleAgentHarness_MultiTurn_PreservesIntermediateTurns_AndReturnsConcludingSummary()
    {
        int turnIndex = 0;
        var chatClientMock = new Mock<IChatClient>();
        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, opts, ct) =>
            {
                turnIndex++;
                if (turnIndex == 1)
                {
                    return new[]
                    {
                        new ChatResponseUpdate
                        {
                            Contents =
                            {
                                new TextReasoningContent("Turn 1: need to inspect file first."),
                                new TextContent("Checking the codebase..."),
                                new FunctionCallContent("call-read-1", "read", new Dictionary<string, object?> { ["path"] = "Program.cs" })
                            }
                        }
                    }.ToAsyncEnumerable();
                }
                else
                {
                    return new[]
                    {
                        new ChatResponseUpdate
                        {
                            Contents =
                            {
                                new TextReasoningContent("Turn 2: all criteria met."),
                                new TextContent("All requirements implemented cleanly.\n<approved>Ready for merge</approved>")
                            }
                        }
                    }.ToAsyncEnumerable();
                }
            });

        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-fidelity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "class Program {}");

        try
        {
            var harness = new TestCrucibleHarnessBuilder
            {
                BaseChatClient = chatClientMock.Object,
                PathResolver = TestPathResolver.Create()
            }.Build();

            var context = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 42, Title = "Multi-turn task", Description = "Test" },
                Workspace = new Workspace { Id = 1, RepositoryPath = tempDir },
                Profile = new AgentProfile { Id = "coder", Mode = AgentMode.Full },
                StageId = StandardStageIds.Implementation,
                RequiredMode = AgentMode.Full,
                WorkspacePath = tempDir
            };

            var result = await harness.RunStageAsync(context);

            // Concluding summary contains only the final message, with <approved> tag stripped and no chatter from turn 1
            Assert.Equal("All requirements implemented cleanly.", result.Summary);
            Assert.Null(result.TargetRouting);

            // Verify intermediate turn is preserved in the serialized history
            var deserialized = CrucibleAgentHarness.DeserializeSession(result.UpdatedState!);
            var assistantMessages = deserialized.Where(m => m.Role == ChatRole.Assistant).ToList();
            Assert.True(assistantMessages.Count >= 2, $"Expected at least 2 assistant messages, found {assistantMessages.Count}");

            // First turn contains "Checking the codebase..." and its reasoning
            var firstTurn = assistantMessages.First(m => m.Text?.Contains("Checking the codebase") == true);
            Assert.Contains("Turn 1: need to inspect", string.Concat(firstTurn.Contents.OfType<TextReasoningContent>().Select(r => r.Text)));

            // Concluding turn contains "All requirements implemented"
            var concludingTurn = assistantMessages.Last();
            Assert.Contains("All requirements implemented cleanly", concludingTurn.Text);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AcpAgentHarness_MultiTurn_IsolatesIntermediateChatterAndThought_FromConcludingSummary()
    {
        var harness = new TestAcpHarnessBuilder().Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 50, Title = "ACP Multi-Turn", Description = "Implement" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        // Turn 1: thought, message chatter, tool call, tool call update
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"text\":\"Thinking about tests...\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Let me inspect the files first.\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"tool_call\",\"toolCallId\":\"tc-1\",\"title\":\"read\",\"rawInput\":\"{}\"}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"tc-1\",\"status\":\"completed\",\"rawOutput\":\"content\"}}}");
        // Turn 2: thought, concluding message with approved tag
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_thought_chunk\",\"content\":{\"text\":\"Finalizing...\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Refactored Dictionary<string, int> cleanly.\\n<approved>All green</approved>\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        // Summary must only be the concluding message, stripped of <approved>, with generics preserved and no chatter
        Assert.Equal("Refactored Dictionary<string, int> cleanly.", result.Summary);
        Assert.DoesNotContain("Let me inspect", result.Summary);
        Assert.DoesNotContain("Thinking about tests", result.Summary);
        Assert.Null(result.TargetRouting);
    }

    [Fact]
    public async Task PiRpcAgentHarness_MultiTurn_IsolatesIntermediateChatterAndThought_FromConcludingSummary()
    {
        var harness = new TestPiRpcHarnessBuilder().Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 60, Title = "Pi Multi-Turn", Description = "Implement" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent" }
        };

        var piOutput = new StringBuilder();
        // Turn 1: thinking, text chatter, tool execution start & end
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_delta\",\"delta\":\"Investigating repository...\"}}");
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Checking dependencies first.\"}}");
        piOutput.AppendLine("{\"type\":\"tool_execution_start\",\"toolName\":\"read\",\"args\":\"{}\"}");
        piOutput.AppendLine("{\"type\":\"tool_execution_end\",\"toolName\":\"read\",\"result\":\"done\"}");
        // Turn 2: thinking, concluding message with request-changes tag
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"thinking_delta\",\"delta\":\"Found flaw.\"}}");
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Completed inspection with List<string>.\\n<request-changes>Need unit tests for EdgeCases.</request-changes>\"}}");
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        // Summary is concluding message only, request-changes stripped, generics preserved
        Assert.Equal("Completed inspection with List<string>.", result.Summary);
        Assert.DoesNotContain("Checking dependencies first", result.Summary);
        Assert.DoesNotContain("Investigating repository", result.Summary);
        Assert.Equal("Need unit tests for EdgeCases.", result.ReviewerFeedback);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
    }

    [Fact]
    public void Extractor_ParsesAndStripsTagsWithInternalWhitespace_AndSelfClosing()
    {
        var inputWhitespace = "All tests passing.\n<approved >LGTM, ready to ship.</approved >";
        var result1 = StageSummaryExtractor.Extract(inputWhitespace);
        Assert.Equal("All tests passing.", result1.Summary);
        Assert.True(result1.IsApproved);

        var inputSelfClosingReq = "Needs rework.\n<request-changes />";
        var result2 = StageSummaryExtractor.Extract(inputSelfClosingReq);
        Assert.Equal("Needs rework.", result2.Summary);
        Assert.Equal(StandardStageIds.Implementation, result2.TargetRouting);
        Assert.Null(result2.ReviewerFeedback);
    }

    [Fact]
    public async Task PiRpcAgentHarness_RootTurnEnd_FlushesIntermediateTurns_AndPreservesConcludingSummary()
    {
        var harness = new TestPiRpcHarnessBuilder().Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 61, Title = "Pi Root TurnEnd", Description = "Implement" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "PiAgent" }
        };

        var piOutput = new StringBuilder();
        // Turn 1 ending via root-level turn_end (without tool calls)
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Intermediate analysis.\"}}");
        piOutput.AppendLine("{\"type\":\"turn_end\"}");
        // Turn 2 ending via agent_settled with concluding message
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Final synthesis completed successfully.\"}}");
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-2", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Final synthesis completed successfully.", result.Summary);
        Assert.DoesNotContain("Intermediate analysis", result.Summary);
    }

    [Fact]
    public async Task AcpAgentHarness_TrailingWhitespaceTurn_PreservesPrecedingConcludingMessage()
    {
        var harness = new TestAcpHarnessBuilder().Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 52, Title = "ACP Trailing Whitespace", Description = "Implement" },
            StageId = "stage-2",
            Profile = new AgentProfile { Name = "AcpAgent" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\test" }
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        // Turn 1: valid concluding message
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Completed all items.\"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"turn_end\"}}}");
        // Turn 2: only whitespace formatting/newlines before stop
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"  \\n\\n  \"}}}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-1", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("Completed all items.", result.Summary);
    }
}
