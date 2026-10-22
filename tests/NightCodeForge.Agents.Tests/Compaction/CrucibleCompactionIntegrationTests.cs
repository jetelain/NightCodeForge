using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Compaction;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class CrucibleCompactionIntegrationTests
{
    [Fact]
    public async Task HarnessAgent_WithSmall32kBudget_CompactsHistory_PreservesTaskAnchor_AndTracksFiles()
    {
        // 1. Arrange: Artificial small context budget where 20,000 char tool output triggers compaction
        var compactionOptions = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 6000,
            ReserveTokens = 1500, // Trigger threshold: 4,500 tokens
            KeepRecentTokens = 1500,
            ToolResultMaxChars = 2000
        };

        var capturedRequests = new List<List<ChatMessage>>();

        // Tools: read and edit
        var readTool = AIFunctionFactory.Create(([System.ComponentModel.Description("path")] string path) =>
        {
            // Simulate large file read (20,000 characters = ~5,000 tokens)
            return $"/* Content of {path} */\n" + new string('X', 20000);
        }, "read", "Read a file from disk");

        var editTool = AIFunctionFactory.Create(([System.ComponentModel.Description("path")] string path, [System.ComponentModel.Description("content")] string content) =>
        {
            return $"File {path} successfully updated.";
        }, "edit", "Edit a file on disk");

        var mockModelClient = new MultiTurnCompactingMockChatClient(capturedRequests);
        var reasoningPruner = new HistoricalReasoningCompactionChatClient(mockModelClient);

        var strategy = new PiStyleCrucibleCompactionStrategy(compactionOptions);

        var harnessOptions = new HarnessAgentOptions
        {
            Id = "stage-implementation",
            Name = "Implementation Agent",
            HarnessInstructions = "System bootstrap instructions: follow code forge standards.",
            ChatOptions = new ChatOptions
            {
                Tools = [readTool, editTool]
            },
            CompactionStrategy = strategy,
            MaximumIterationsPerRequest = 10,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true
        };

        var agent = new HarnessAgent(reasoningPruner, harnessOptions);
        var session = await agent.CreateSessionAsync();

        var taskAnchor = "Task #42 requirements: implement context compaction integration test with 32k budget.";

        // 2. Act: Run multi-turn agent streaming loop
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(taskAnchor, session))
        {
            updates.Add(update);
        }

        // 3. Assert
        Assert.NotEmpty(updates);
        Assert.NotEmpty(capturedRequests);

        // Turn 1 request
        var firstRequest = capturedRequests[0];
        Assert.Contains(firstRequest, m => m.Role == ChatRole.User && m.Text!.Contains("Task #42 requirements"));

        // Final turn request (after tools were called and history grew)
        var lastRequest = capturedRequests.Last();

        // 1. Task anchor must ALWAYS be preserved
        Assert.Contains(lastRequest, m => m.Role == ChatRole.User && m.Text!.Contains("Task #42 requirements"));

        // 2. Compaction occurred: verify the checkpoint and tracked file operations were injected into context
        Assert.Contains(lastRequest, m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("[CONTEXT COMPACTION CHECKPOINT]"));
        Assert.Contains(lastRequest, m => m.Role == ChatRole.User && m.Text != null && m.Text.Contains("<read-files>\nLargeFile.cs\n</read-files>"));
        Assert.True(capturedRequests.Count >= 3, "Expected at least 3 model roundtrips for tool interactions.");

        // 3. Verify reasoning pruning: earlier turns must have reasoning pruned
        for (int i = 0; i < capturedRequests.Count - 1; i++)
        {
            var req = capturedRequests[i];
            var assistantMsgs = req.Where(m => m.Role == ChatRole.Assistant).ToList();
            if (assistantMsgs.Count > 1)
            {
                // All except the very last assistant message in this request should be pruned
                for (int j = 0; j < assistantMsgs.Count - 1; j++)
                {
                    Assert.DoesNotContain(assistantMsgs[j].Contents, c => c is TextReasoningContent);
                }
            }
        }
    }

    private sealed class MultiTurnCompactingMockChatClient : IChatClient
    {
        private readonly List<List<ChatMessage>> _capturedRequests;
        private int _turn = 0;

        public MultiTurnCompactingMockChatClient(List<List<ChatMessage>> capturedRequests)
        {
            _capturedRequests = capturedRequests;
        }

        public ChatClientMetadata Metadata => new("MockCompactingModel");

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _capturedRequests.Add(chatMessages.Select(CloneMessage).ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finished task")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _turn++;
            _capturedRequests.Add(chatMessages.Select(CloneMessage).ToList());

            if (_turn == 1)
            {
                // Turn 1: Model thinks, then calls read("LargeFile.cs")
                yield return new ChatResponseUpdate
                {
                    Contents =
                    {
                        new TextReasoningContent("Investigating file content to plan modifications..."),
                        new FunctionCallContent("call_read_1", "read", new Dictionary<string, object?> { ["path"] = "LargeFile.cs" })
                    }
                };
            }
            else if (_turn == 2)
            {
                // Turn 2: Model thinks, then calls edit("OutputFile.cs")
                yield return new ChatResponseUpdate
                {
                    Contents =
                    {
                        new TextReasoningContent("File analyzed. Now applying modifications..."),
                        new FunctionCallContent("call_edit_2", "edit", new Dictionary<string, object?> { ["path"] = "OutputFile.cs", ["content"] = "compacted code" })
                    }
                };
            }
            else
            {
                // Turn 3+: Model completes with summary
                yield return new ChatResponseUpdate
                {
                    Contents =
                    {
                        new TextContent("Completed implementation of compaction task.")
                    }
                };
            }

            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static ChatMessage CloneMessage(ChatMessage m)
        {
            var clone = new ChatMessage(m.Role, m.Contents.ToList())
            {
                AuthorName = m.AuthorName,
                RawRepresentation = m.RawRepresentation
            };
            if (m.AdditionalProperties != null)
            {
                clone.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                foreach (var kvp in m.AdditionalProperties)
                {
                    clone.AdditionalProperties[kvp.Key] = kvp.Value;
                }
            }
            return clone;
        }
    }
}
