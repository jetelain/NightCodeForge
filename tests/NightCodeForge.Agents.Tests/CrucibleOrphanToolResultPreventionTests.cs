using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleOrphanToolResultPreventionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-orphan-" + Guid.NewGuid().ToString("N"));

    public CrucibleOrphanToolResultPreventionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task RunStageAsync_WhenPendingHumanAnswerSuppliedToFreshSession_DoesNotRecordOrphanAskToolResult()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 20, Title = "Fresh Stage Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        IList<ChatMessage>? receivedMessages = null;

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            receivedMessages = messages.ToList();
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents = [new TextContent("Executing implementation.")]
                }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        // Simulate context where PendingHumanAnswer is present but there are NO ask tool calls in history
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Fresh bootstrap prompt",
            SerializedStateJson = null,
            PendingHumanAnswer = "Q: Which DB?\nA: PostgreSQL",
            PendingHumanAnswerJson = "[{\"QuestionIndex\":0,\"AnswerText\":\"PostgreSQL\"}]",
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = new AgentProfile
            {
                Id = "p-coder",
                Name = "Coder Agent",
                ModelName = "qwen2.5-coder:7b",
                Mode = AgentMode.Full,
                PermittedTools = new[] { CanonicalToolNames.Bash, CanonicalToolNames.Read, CanonicalToolNames.Edit }
            }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(receivedMessages);

        // 1. ChatHistory must NOT be polluted with [Human Clarification] or follow-up prompt
        Assert.DoesNotContain(receivedMessages, m => m.Text != null && m.Text.Contains("[Human Clarification]"));
        Assert.DoesNotContain(receivedMessages, m => m.Text != null && m.Text.Contains("Please proceed with your plan based on the clarified answers."));

        // 2. Database conversation must NOT contain any orphan ToolResult with toolName: ask
        var conv = Assert.Single(await repo.GetConversationsAsync(task.Id));
        var turns = (await repo.GetConversationWithTurnsAsync(conv.Id))?.Turns;
        Assert.NotNull(turns);
        var orphanAskTurns = turns.Where(t => t.Role == StageTurnRole.ToolResult && string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Empty(orphanAskTurns);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumingWithCompletedAskInSerializedState_DoesNotInjectDuplicateHumanClarification()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 21, Title = "Resumed Ask Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Turn 1: asks a question
        var client1 = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-456", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["prompt"] = "Pick a DB",
                                    ["options"] = new[] { "PostgreSQL", "SQLite" }
                                }
                            }
                        })
                    ]
                }
            ];
            return updates;
        });

        var harness1 = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client1,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context1 = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-review",
            BootstrapPrompt = "Initial review prompt",
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = new AgentProfile
            {
                Id = "p-test",
                Name = "Reviewer Agent",
                ModelName = "qwen2.5-coder:7b",
                Mode = AgentMode.Full,
                PermittedTools = new[] { CanonicalToolNames.Ask, CanonicalToolNames.Read }
            }
        };

        var result1 = await harness1.RunStageAsync(context1);
        Assert.Equal(AgentRunStatus.WaitingForHuman, result1.Status);
        Assert.NotNull(result1.UpdatedState);

        // Submit answer using serializer (as TaskOrchestrator.SubmitAnswersAsync does)
        const string answer = "PostgreSQL";
        var completedState = ConversationHistorySerializer.CompletePendingAskToolCall(result1.UpdatedState, answer);

        IList<ChatMessage>? turn2Messages = null;
        var client2 = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            turn2Messages = messages.ToList();
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents = [new TextContent("Acknowledged PostgreSQL.")]
                }
            ];
            return updates;
        });

        var harness2 = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client2,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context2 = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-review",
            BootstrapPrompt = "Initial review prompt",
            SerializedStateJson = completedState,
            PendingHumanAnswer = answer,
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = context1.Profile
        };

        var result2 = await harness2.RunStageAsync(context2);
        Assert.Equal(AgentRunStatus.Completed, result2.Status);
        Assert.NotNull(turn2Messages);

        // Verify there is NO duplicate [Human Clarification] block in chat history
        var clarificationMessages = turn2Messages.Where(m => m.Text != null && m.Text.Contains("[Human Clarification]")).ToList();
        Assert.Empty(clarificationMessages);

        var proceedPrompts = turn2Messages.Where(m => m.Text != null && m.Text.Contains("Please proceed with your plan based on the clarified answers.")).ToList();
        Assert.Single(proceedPrompts);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumingWaitingConversationWithoutAskToolTurn_DoesNotRecordOrphanAskToolResult()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 22, Title = "Paused Task Without Ask", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Pre-create a conversation in repo with WaitingForHuman status, but containing NO ask tool turns
        var conv = await repo.StartConversationAsync(
            task.Id,
            "task-review",
            WorkflowStageKind.Agentic,
            1,
            agentProfileId: "p-test",
            harnessId: "crucible",
            modelName: "qwen2.5-coder:7b",
            workspaceId: task.WorkspaceId);

        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Initial prompt");
        await repo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Thinking...");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Paused");

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents = [new TextContent("Done.")]
                }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-review",
            BootstrapPrompt = "Initial prompt",
            PendingHumanAnswer = "Some answer",
            WorkspacePath = _tempDir,
            Workspace = new Workspace { Id = 1, Name = "TestWorkspace", RepositoryPath = _tempDir },
            Profile = new AgentProfile
            {
                Id = "p-test",
                Name = "Reviewer Agent",
                ModelName = "qwen2.5-coder:7b",
                Mode = AgentMode.Full,
                PermittedTools = new[] { CanonicalToolNames.Ask }
            }
        };

        var result = await harness.RunStageAsync(context);
        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        // Must NOT record an orphan ToolResult for 'ask' because no ask turn existed
        var orphanAskTurns = convWithTurns.Turns
            .Where(t => t.Role == StageTurnRole.ToolResult && string.Equals(t.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(orphanAskTurns);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> _streamHandler;

        public ScriptedChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> streamHandler)
        {
            _streamHandler = streamHandler;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var updates = await _streamHandler(chatMessages, options, cancellationToken);
            foreach (var update in updates)
            {
                yield return update;
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

