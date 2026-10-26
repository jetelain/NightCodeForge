using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleSyntheticPrimingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "crucible-prime-" + Guid.NewGuid().ToString("N"));

    public CrucibleSyntheticPrimingTests()
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

    private AgentSessionContext CreateContext(ForgeTask task, SyntheticPrimingExecution? priming = null)
    {
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Initial implementation prompt",
            SyntheticPriming = priming,
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
    }

    [Fact]
    public async Task RunStageAsync_WithForwardPriming_PreSeedsChatHistory_AndRecordsPrimedTurnsInDb()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 10, Title = "Test Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        IList<ChatMessage>? receivedMessages = null;

        var client = new ScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            receivedMessages = messages.ToList();
            // Assistant directly finishes without making redundant bash calls
            IReadOnlyList<ChatResponseUpdate> updates =
            [
                new ChatResponseUpdate
                {
                    Contents = [new TextContent("I see tests have passed. Proceeding with changes.")]
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

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet test",
            Output: "Exit Code: 0\nPassed: 42",
            IsSuccess: true,
            DurationMs: 150
        );

        var context = CreateContext(task, priming);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(receivedMessages);

        // Inspect chat history delivered to LLM:
        // Turn 0: User prompt
        // Turn 1: Assistant bash call
        // Turn 2: Tool bash result
        Assert.True(receivedMessages.Count >= 3);
        Assert.Equal(ChatRole.User, receivedMessages[0].Role);

        var assistantMsg = receivedMessages[1];
        Assert.Equal(ChatRole.Assistant, assistantMsg.Role);
        var fcc = assistantMsg.Contents.OfType<FunctionCallContent>().FirstOrDefault();
        Assert.NotNull(fcc);
        Assert.Equal(CanonicalToolNames.Bash, fcc.Name);
        Assert.Equal("dotnet test", fcc.Arguments?["command"]?.ToString());

        var toolMsg = receivedMessages[2];
        Assert.Equal(ChatRole.Tool, toolMsg.Role);
        var frc = toolMsg.Contents.OfType<FunctionResultContent>().FirstOrDefault();
        Assert.NotNull(frc);
        Assert.Equal(fcc.CallId, frc.CallId);
        Assert.Contains("Passed: 42", frc.Result?.ToString());

        // Inspect turns recorded in database
        var conv = Assert.Single(await repo.GetConversationsAsync(task.Id));
        var turns = (await repo.GetConversationWithTurnsAsync(conv.Id))?.Turns;
        Assert.NotNull(turns);

        var primedToolTurn = turns.FirstOrDefault(t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Bash);
        Assert.NotNull(primedToolTurn);
        Assert.NotNull(primedToolTurn.MetadataJson);
        Assert.Contains("\"IsPrimed\":true", primedToolTurn.MetadataJson);
        Assert.Contains("\"PrimingType\":\"ForwardPass\"", primedToolTurn.MetadataJson);
        Assert.NotNull(primedToolTurn.ToolArguments);
        Assert.Contains("\"command\":\"dotnet test\"", primedToolTurn.ToolArguments);

        var primedResultTurn = turns.FirstOrDefault(t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Bash);
        Assert.NotNull(primedResultTurn);
        Assert.NotNull(primedResultTurn.MetadataJson);
        Assert.Contains("\"IsPrimed\":true", primedResultTurn.MetadataJson);
        Assert.Contains("\"PrimingType\":\"ForwardPass\"", primedResultTurn.MetadataJson);
    }

    [Fact]
    public async Task RunStageAsync_WithLoopbackPriming_PreSeedsFailureDiagnostics_AndDirectiveUserPrompt()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 11, Title = "Test Task Failure Priming", WorkspaceId = 1 };
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
                    Contents = [new TextContent("I will fix the test assertion error.")]
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

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PostImplementationGates,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet test",
            Output: "Exit Code: 1\n[Failed Tests]: UnitTest1.TestFail",
            IsSuccess: false,
            DurationMs: 400
        );

        var context = CreateContext(task, priming);
        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(receivedMessages);

        // Expect:
        // Turn 0: User prompt
        // Turn 1: Assistant bash call
        // Turn 2: Tool bash error result
        // Turn 3: User directive ("Please fix the compilation/test errors shown above.")
        Assert.True(receivedMessages.Count >= 4);

        var directiveUserTurn = receivedMessages[3];
        Assert.Equal(ChatRole.User, directiveUserTurn.Role);
        Assert.Contains("Please fix the compilation/test errors shown above.", directiveUserTurn.Text);

        var conv = Assert.Single(await repo.GetConversationsAsync(task.Id));
        var turns = (await repo.GetConversationWithTurnsAsync(conv.Id))?.Turns;
        Assert.NotNull(turns);

        var primedToolTurn = turns.FirstOrDefault(t => t.Role == StageTurnRole.Tool && t.ToolName == CanonicalToolNames.Bash);
        Assert.NotNull(primedToolTurn);
        Assert.Contains("\"PrimingType\":\"LoopbackDiagnostic\"", primedToolTurn.MetadataJson);

        var directiveTurn = turns.FirstOrDefault(t => t.Role == StageTurnRole.User && t.Content == "Please fix the compilation/test errors shown above.");
        Assert.NotNull(directiveTurn);
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

