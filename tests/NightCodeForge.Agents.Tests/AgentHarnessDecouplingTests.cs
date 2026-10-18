using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tests;

public class AgentHarnessDecouplingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "harness-decoupling-" + Guid.NewGuid().ToString("N"));

    public AgentHarnessDecouplingTests()
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

    private class MockChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>> _streamHandler;

        public MockChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>> streamHandler)
        {
            _streamHandler = streamHandler;
        }

        public ChatClientMetadata Metadata => new("mock", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return _streamHandler(chatMessages, options);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public void CrucibleAgentHarness_BuildTaskScope_IncorporatesReviewDiff_WhenProvided()
    {
        var task = new ForgeTask { Id = 42, Title = "Feature X", Description = "Implement Feature X" };
        var diff = "diff --git a/src/App.cs b/src/App.cs\n+Console.WriteLine(\"Hello\");";

        var taskScope = CrucibleAgentHarness.BuildTaskScope(task, "docs/tasks/task-42.md", diff);

        Assert.Contains("<review_diff>\ndiff --git a/src/App.cs b/src/App.cs\n+Console.WriteLine(\"Hello\");\n</review_diff>", taskScope);
        Assert.Contains("path=\"docs/tasks/task-42.md\"", taskScope);
        Assert.Contains("title=\"Feature X\"", taskScope);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenPipelineContextIsNull_OperatesCleanlyAndReturnsFeedback()
    {
        async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
        {
            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent("Review completed.\n<request_changes>\nMissing integration tests for database.\n</request_changes>")]
            };
            await Task.CompletedTask;
        }

        var mockClient = new MockChatClient(StreamGen);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = mockClient,
            PathResolver = TestPathResolver.Create(),
            ActivityStreamer = new InMemoryAgentActivityStreamer(),
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var task = new ForgeTask { Id = 5, Title = "Review Task", Description = "Inspect diff" };
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            Profile = new AgentProfile { Id = "reviewer", Mode = AgentMode.Review },
            StageId = StandardStageIds.CodeReviewAndFix,
            RequiredMode = AgentMode.Review,
            WorkspacePath = _tempDir,
            ReviewDiff = "diff --git a/db.cs b/db.cs",
            PipelineContext = null // Explicitly null to verify decoupling
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
        Assert.Equal("Missing integration tests for database.", result.ReviewerFeedback);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenReviewDiffPresentInContext_InjectedIntoTaskScope()
    {
        List<ChatMessage>? receivedMessages = null;
        async IAsyncEnumerable<ChatResponseUpdate> StreamGen(IEnumerable<ChatMessage> msgs, ChatOptions? opts)
        {
            receivedMessages = msgs.ToList();
            if (!string.IsNullOrEmpty(opts?.Instructions))
            {
                receivedMessages.Insert(0, new ChatMessage(ChatRole.System, opts.Instructions));
            }
            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent("Done.")]
            };
            await Task.CompletedTask;
        }

        var mockClient = new MockChatClient(StreamGen);
        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = mockClient,
            PathResolver = TestPathResolver.Create(),
            ActivityStreamer = new InMemoryAgentActivityStreamer(),
            TaskRepository = new InMemoryTaskRepository()
        }.Build();

        var task = new ForgeTask { Id = 6, Title = "Review Task 6", Description = "Testing review diff scope" };
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            Profile = new AgentProfile { Id = "reviewer", Mode = AgentMode.Review },
            StageId = StandardStageIds.CodeReviewAndFix,
            RequiredMode = AgentMode.Review,
            WorkspacePath = _tempDir,
            ReviewDiff = "diff --git a/auth.cs b/auth.cs\n+public class Auth {}",
            PipelineContext = null
        };

        await harness.RunStageAsync(context);

        Assert.NotNull(receivedMessages);
        var systemMsg = receivedMessages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        Assert.NotNull(systemMsg);
        Assert.Contains("<review_diff>", systemMsg);
        Assert.Contains("diff --git a/auth.cs b/auth.cs", systemMsg);
    }

    [Fact]
    public async Task PiRpcAgentHarness_WhenPipelineContextIsNull_ExtractsReviewerFeedbackIntoAgentRunResult()
    {
        var streamer = new InMemoryAgentActivityStreamer();
        var harness = new TestPiRpcHarnessBuilder { ActivityStreamer = streamer }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 15, Title = "Pi Review", Description = "Review stage" },
            StageId = StandardStageIds.CodeReviewAndFix,
            Profile = new AgentProfile { Name = "PiReviewer", ModelName = "qwen2.5-coder:7b" },
            PipelineContext = null // Explicitly null
        };

        var piOutput = new StringBuilder();
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Code review complete.\\n<request_changes>\\nValidation logic is missing.\\n</request_changes>\"}}");
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-pi-review", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
        Assert.Equal("Validation logic is missing.", result.ReviewerFeedback);
    }

    [Fact]
    public async Task AcpAgentHarness_WhenPipelineContextIsNull_ExtractsReviewerFeedbackIntoAgentRunResult()
    {
        var streamer = new InMemoryAgentActivityStreamer();
        var harness = new TestAcpHarnessBuilder { ActivityStreamer = streamer }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 25, Title = "ACP Review", Description = "Review stage" },
            StageId = StandardStageIds.CodeReviewAndFix,
            Profile = new AgentProfile { Name = "AcpReviewer" },
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            PipelineContext = null // Explicitly null
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"sessionId\":\"s2\",\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"text\":\"Found issues:\\n<request_changes>\\nMissing authorization checks.\\n</request_changes>\"}}}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-acp-review", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRouting);
        Assert.Equal("Missing authorization checks.", result.ReviewerFeedback);
    }
}
