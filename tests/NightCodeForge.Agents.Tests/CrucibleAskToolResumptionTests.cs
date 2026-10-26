using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleAskToolResumptionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-ask-" + Guid.NewGuid().ToString("N"));

    public CrucibleAskToolResumptionTests()
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

    private AgentSessionContext CreateContext(ForgeTask task, string? serializedState = null, string? pendingAnswer = null, string? pendingAnswerJson = null)
    {
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = "task-review",
            BootstrapPrompt = "Initial review prompt",
            SerializedStateJson = serializedState,
            PendingHumanAnswer = pendingAnswer,
            PendingHumanAnswerJson = pendingAnswerJson,
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
    }

    [Fact]
    public async Task RunStageAsync_WhenAskInvoked_RecordsExactlyOneAskTurn_AndSetsWaitingForHumanWithNullCompletedAt()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 10, Title = "Test Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Scripted client that invokes the 'ask' tool on turn 1
        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-123", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["prompt"] = "Should we use PostgreSQL or SQLite?",
                                    ["rationale"] = "Need to know DB engine",
                                    ["options"] = new[] { "PostgreSQL", "SQLite" }
                                }
                            }
                        })
                    ]
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

        var context = CreateContext(task);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);
        Assert.NotNull(result.PendingQuestions);
        Assert.Single(result.PendingQuestions);
        Assert.Equal("Should we use PostgreSQL or SQLite?", result.PendingQuestions[0].Prompt);

        // Verify conversation in DB
        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.WaitingForHuman, conv.Status);
        Assert.Null(conv.CompletedAt);

        // Verify turns
        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        // Turn 0: User bootstrap prompt
        var userTurn = Assert.Single(convWithTurns.Turns, t => t.Role == StageTurnRole.User);
        Assert.Equal("Initial review prompt", userTurn.Content);

        // Exactly one Tool turn for ask with call-ask-123
        var askTurns = convWithTurns.Turns.Where(t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Ask).ToList();
        var askTurn = Assert.Single(askTurns);
        Assert.NotNull(askTurn.MetadataJson);
        Assert.Contains("call-ask-123", askTurn.MetadataJson);
        Assert.Contains("Should we use PostgreSQL or SQLite?", askTurn.ToolArguments);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumedWithPendingHumanAnswer_ReusesConversation_AndAppendsToolResultAndNoDuplicateBootstrap()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 11, Title = "Test Task Resumption", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Phase 1: Ask tool invocation
        var askClient = new ScriptedChatClient(async (messages, options, ct) =>
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
                                new Dictionary<string, object?> { ["prompt"] = "Clarify target branch?" }
                            }
                        })
                    ]
                }
            ];
            return updates;
        });

        var harness1 = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = askClient,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context1 = CreateContext(task);
        var result1 = await harness1.RunStageAsync(context1);
        Assert.Equal(AgentRunStatus.WaitingForHuman, result1.Status);

        var convsBefore = await repo.GetConversationsAsync(task.Id);
        var initialConv = Assert.Single(convsBefore);
        Assert.Equal(StageExecutionStatus.WaitingForHuman, initialConv.Status);

        // Phase 2: Resume with PendingHumanAnswer
        var resumeClient = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            // The model completes execution after receiving the tool result
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Continuing with clarified branch: feature/main")] }
            ];
            return updates;
        });

        var harness2 = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = resumeClient,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context2 = CreateContext(task, serializedState: result1.UpdatedState, pendingAnswer: "Target branch is feature/main");
        var result2 = await harness2.RunStageAsync(context2);

        Assert.Equal(AgentRunStatus.Completed, result2.Status);

        // Verify conversation was reused (NO second conversation created!)
        var convsAfter = await repo.GetConversationsAsync(task.Id);
        var resumedConv = Assert.Single(convsAfter);
        Assert.Equal(initialConv.Id, resumedConv.Id);
        Assert.Equal(StageExecutionStatus.Completed, resumedConv.Status);

        // Verify turns in the single resumed conversation
        var convWithTurns = await repo.GetConversationWithTurnsAsync(resumedConv.Id);
        Assert.NotNull(convWithTurns);

        // Bootstrap turn appears ONLY once!
        var userBootstrapTurns = convWithTurns.Turns.Where(t => t.Role == StageTurnRole.User && t.Content == "Initial review prompt").ToList();
        Assert.Single(userBootstrapTurns);

        // ToolResult turn for ask is recorded with the human answer
        var toolResultTurn = convWithTurns.Turns.FirstOrDefault(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask);
        Assert.NotNull(toolResultTurn);
        Assert.Equal("Target branch is feature/main", toolResultTurn.ToolResult);
        Assert.Contains("call-ask-456", toolResultTurn.MetadataJson);

        // Follow-up user prompt turn is present
        var followUpTurn = convWithTurns.Turns.FirstOrDefault(t => t.Role == StageTurnRole.User && t.Content == "Please proceed with your plan based on the clarified answers.");
        Assert.NotNull(followUpTurn);

        // Assistant final response is recorded in the same conversation
        var assistantTurn = convWithTurns.Turns.FirstOrDefault(t => t.Role == StageTurnRole.Assistant && t.Content != null && t.Content.Contains("Continuing with clarified branch"));
        Assert.NotNull(assistantTurn);
    }

    [Fact]
    public async Task RunStageAsync_WhenPausedConversationExistsWithoutPendingAnswer_ClosesItAndStartsNew()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 12, Title = "Test Abandoned Resumption", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Create an existing WaitingForHuman conversation
        var waitingConv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(waitingConv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Clean start execution")] }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        // Run without pending human answer
        var context = CreateContext(task, serializedState: null, pendingAnswer: null);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        // Two conversations should now exist: first cancelled, second completed
        var convs = await repo.GetConversationsAsync(task.Id);
        Assert.Equal(2, convs.Count);

        var oldConv = convs.First(c => c.Id == waitingConv.Id);
        Assert.Equal(StageExecutionStatus.Cancelled, oldConv.Status);

        var newConv = convs.First(c => c.Id != waitingConv.Id);
        Assert.Equal(StageExecutionStatus.Completed, newConv.Status);
    }

    [Fact]
    public async Task RunStageAsync_WhenResumedWithoutPendingCallIdsInHistory_PersistsFallbackToolResultTurn()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 13, Title = "Cold Resume Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Pre-create a paused conversation that was waiting for human with an ask tool call
        var waitingConv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(waitingConv.Id, StageTurnRole.User, content: "Initial prompt");
        await repo.AddConversationTurnAsync(waitingConv.Id, StageTurnRole.Tool, toolName: CanonicalToolNames.Ask, toolArguments: "{\"questions\":[{\"prompt\":\"Confirm branch\"}]}", metadataJson: "{\"CallId\":\"call-ask-cold\"}");
        await repo.CompleteConversationAsync(waitingConv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Done after cold resume")] }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        // Resumed with PendingHumanAnswer, but serialized state has NO function calls
        var context = CreateContext(task, serializedState: null, pendingAnswer: "Confirmed branch name");
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        // Reused same conversation
        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(waitingConv.Id, conv.Id);
        Assert.Equal(StageExecutionStatus.Completed, conv.Status);

        // ToolResult turn must be recorded with human answer
        var turns = (await repo.GetConversationWithTurnsAsync(conv.Id))!.Turns;
        var toolResultTurn = Assert.Single(turns, t => t.Role == StageTurnRole.ToolResult);
        Assert.Equal(CanonicalToolNames.Ask, toolResultTurn.ToolName);
        Assert.Equal("Confirmed branch name", toolResultTurn.ToolResult);
        Assert.NotNull(toolResultTurn.MetadataJson);
        Assert.Contains("CallId", toolResultTurn.MetadataJson);
    }

    [Fact]
    public async Task ResumeConversationAsync_WhenConversationNotFound_ThrowsKeyNotFoundException()
    {
        var repo = new InMemoryTaskRepository();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => repo.ResumeConversationAsync(9999));
    }

    [Fact]
    public async Task RunStageAsync_WhenTracerAlreadyWroteAskToolTurn_DoesNotRecordDuplicateTurn()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 14, Title = "Test Duplicate Prevention", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Pre-create conversation with an existing Tool turn for call-ask-dup-1
        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Existing Question\"}]}",
            metadataJson: "{\"CallId\":\"call-ask-dup-1\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman);

        // Scripted client that again emits call-ask-dup-1 (simulating tracer re-flush or duplicate ask)
        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-dup-1", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?> { ["prompt"] = "Existing Question" }
                            }
                        })
                    ]
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

        var context = CreateContext(task, pendingAnswer: "Some answer");
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);

        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        // Exactly ONE ask Tool turn must exist for call-ask-dup-1 (no duplicate written)
        var askTurns = convWithTurns.Turns.Where(t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Ask).ToList();
        Assert.Single(askTurns);
        Assert.Contains("call-ask-dup-1", askTurns[0].MetadataJson);
    }

    [Fact]
    public async Task RunStageAsync_WhenToolTurnExists_DoesNotBlockToolResultWithSameCallId()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 16, Title = "Test Tool Result Sharing CallId", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Clarify?\"}]}",
            metadataJson: "{\"CallId\":\"call-ask-shared-1\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting");

        var client = new ScriptedChatClient(async (msgs, opts, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Done")] }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context = CreateContext(task, pendingAnswer: "Clarified answer");
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        // ToolResult turn with call-ask-shared-1 was successfully recorded (not dropped)
        var resultTurn = convWithTurns.Turns.FirstOrDefault(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask);
        Assert.NotNull(resultTurn);
        Assert.Contains("call-ask-shared-1", resultTurn.MetadataJson);
        Assert.Equal("Clarified answer", resultTurn.ToolResult);
    }

    [Fact]
    public async Task RunStageAsync_WhenPendingHumanAnswerJsonProvided_EmbedsStructuredAnswersInToolResultMetadata()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 17, Title = "Test Structured Metadata Embedding", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Target environment?\",\"options\":[\"Staging\",\"Production\"]}]}",
            metadataJson: "{\"CallId\":\"call-ask-env-meta-1\"}");
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting");

        var client = new ScriptedChatClient(async (msgs, opts, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Acknowledged")] }
            ];
            return updates;
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var structuredJson = "[{\"QuestionIndex\":0,\"SelectedOptionIndex\":0,\"SelectedOption\":\"Staging\",\"AnswerText\":\"Staging\"}]";
        var context = CreateContext(task, pendingAnswer: "Staging", pendingAnswerJson: structuredJson);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        var resultTurn = convWithTurns.Turns.FirstOrDefault(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask);
        Assert.NotNull(resultTurn);
        Assert.NotNull(resultTurn.MetadataJson);
        Assert.Contains("call-ask-env-meta-1", resultTurn.MetadataJson);
        Assert.Contains("\"Answers\"", resultTurn.MetadataJson);
        Assert.Contains("\"SelectedOptionIndex\":0", resultTurn.MetadataJson);
        Assert.Contains("\"SelectedOption\":\"Staging\"", resultTurn.MetadataJson);
    }

    [Fact]
    public async Task RunStageAsync_WhenTurnWrittenByInterruptionFallback_PreservesOptionsInToolArgumentsAndState()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 18, Title = "Test QuestionItem Options Serialization", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-opt-fallback", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["prompt"] = "Select deployment target:",
                                    ["rationale"] = "Needed for infra setup",
                                    ["options"] = new[] { "AWS", "Azure", "GCP" }
                                }
                            }
                        })
                    ]
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

        var context = CreateContext(task);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.WaitingForHuman, result.Status);

        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        var askTurn = Assert.Single(convWithTurns.Turns, t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Ask);
        Assert.NotNull(askTurn.ToolArguments);

        // Verify options array is properly serialized in toolArguments
        Assert.Contains("\"options\"", askTurn.ToolArguments);
        Assert.Contains("AWS", askTurn.ToolArguments);
        Assert.Contains("Azure", askTurn.ToolArguments);
        Assert.Contains("GCP", askTurn.ToolArguments);

        // Verify that chatHistory serialized state also carries QuestionItem options without DB entity fields
        Assert.NotNull(result.UpdatedState);
        Assert.Contains("\"AWS\"", result.UpdatedState);
        Assert.DoesNotContain("\"OptionsJson\"", result.UpdatedState);
    }

    [Fact]
    public async Task RunStageAsync_WhenMultipleAskInterruptionsOccurSequentially_RecordsFollowUpPromptForEveryResumption()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 19, Title = "Test Multi-Turn Ask Resumption", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        // Turn 1: LLM asks question 1
        var client1 = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-round-1", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?> { ["prompt"] = "Question 1?" }
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

        var context1 = CreateContext(task);
        var result1 = await harness1.RunStageAsync(context1);
        Assert.Equal(AgentRunStatus.WaitingForHuman, result1.Status);

        // Turn 2: Resumed with answer 1. Agent processes answer 1, then asks question 2!
        var client2 = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents =
                    [
                        new FunctionCallContent("call-ask-round-2", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?> { ["prompt"] = "Question 2?" }
                            }
                        })
                    ]
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

        var context2 = CreateContext(task, serializedState: result1.UpdatedState, pendingAnswer: "Answer to question 1");
        var result2 = await harness2.RunStageAsync(context2);
        Assert.Equal(AgentRunStatus.WaitingForHuman, result2.Status);

        // Turn 3: Resumed with answer 2. Agent completes work.
        var client3 = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate { Contents = [new TextContent("Completed both clarification rounds.")] }
            ];
            return updates;
        });

        var harness3 = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = client3,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var context3 = CreateContext(task, serializedState: result2.UpdatedState, pendingAnswer: "Answer to question 2");
        var result3 = await harness3.RunStageAsync(context3);
        Assert.Equal(AgentRunStatus.Completed, result3.Status);

        // All 3 rounds must share the EXACT SAME conversation ID
        var convs = await repo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Completed, conv.Status);

        var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);

        const string followUpPrompt = "Please proceed with your plan based on the clarified answers.";

        // Verify that follow-up prompt was recorded for BOTH resumptions (count == 2), not skipped on round 2!
        var followUpTurns = convWithTurns.Turns
            .Where(t => t.Role == StageTurnRole.User && t.Content == followUpPrompt)
            .ToList();
        Assert.Equal(2, followUpTurns.Count);

        // Verify both tool results are present
        var toolResultTurns = convWithTurns.Turns
            .Where(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Ask)
            .ToList();
        Assert.Equal(2, toolResultTurns.Count);
        Assert.Contains(toolResultTurns, t => t.ToolResult == "Answer to question 1");
        Assert.Contains(toolResultTurns, t => t.ToolResult == "Answer to question 2");
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
