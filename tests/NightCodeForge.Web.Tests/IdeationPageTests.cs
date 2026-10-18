using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationPageTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockIdeationService _ideationService;
    private readonly ToastService _toastService;
    private readonly IdeationSessionState _sessionState;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;

    public IdeationPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _workspaceRepository.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        _markdownSync = new MockTaskMarkdownSyncService();
        _ideationService = new MockIdeationService();
        _toastService = TestServices.CreateToastService();
        _sessionState = new IdeationSessionState();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<IIdeationService>(_ideationService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_sessionState);
    }

    [Fact]
    public void InitialState_RendersEmptyStateAndQuickPrompts()
    {
        var cut = Render<Ideation>();

        Assert.Contains("Codebase Discovery", cut.Find(".chat-empty-title").TextContent);
        Assert.Contains("No Proposals Yet", cut.Markup);
        Assert.Contains("Quick Prompts:", cut.Markup);

        var chips = cut.FindAll("button.prompt-chip");
        Assert.Equal(4, chips.Count);
    }

    [Fact]
    public void ClickQuickPrompt_PopulatesUserInputTextarea()
    {
        var cut = Render<Ideation>();

        var archChip = cut.FindAll("button.prompt-chip")[0];
        archChip.Click();

        var textarea = cut.Find("textarea.chat-textarea");
        Assert.Contains("Explore the codebase architecture", textarea.GetAttribute("value"));
    }

    [Fact]
    public async Task SendMessage_StreamsThinkingAndMessageDeltas()
    {
        var ws = new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Analyzing dependencies..."),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "I found 2 architectural areas for improvement."),
            new(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: new IdeationProposal("Extract caching layer", "Decouple redis cache from core", 2)),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Finished", Array.Empty<IdeationProposal>()))
        };

        var cut = Render<Ideation>();

        // Type prompt in textarea
        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("What can we improve?");

        // Click Send button
        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        // Wait for async stream execution to complete
        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        // Verify history has user turn and assistant turn
        Assert.Equal("What can we improve?", _sessionState.Messages[0].Content);
        Assert.Contains("I found 2 architectural areas", _sessionState.Messages[1].Content);
        Assert.Equal("Analyzing dependencies...", _sessionState.Messages[1].ThinkingTrace);

        // Verify proposal was added to Session.Proposals
        Assert.Single(_sessionState.Proposals);
        Assert.Equal("Extract caching layer", _sessionState.Proposals[0].Title);

        // Verify proposals pane shows the card
        Assert.Contains("Extract caching layer", cut.Markup);
    }

    [Fact]
    public async Task AddProposalToBacklog_CreatesTaskInDbAndSyncsToDisk()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var ws = new Workspace { Id = 10, Name = "TestRepo", DefaultBaseBranch = "master", RepositoryPath = tempDir };
            _workspaceState.SetActiveWorkspace(ws);
            _sessionState.BoundWorkspaceId = 10;

            var proposal = new IdeationProposalViewModel
            {
                Title = "Implement Distributed Tracing",
                Description = "Add OpenTelemetry activity exporter",
                Priority = 3
            };
            _sessionState.Proposals.Add(proposal);

            var cut = Render<Ideation>();

            var addBtn = cut.Find("button.add-backlog-btn");
            await cut.InvokeAsync(() => addBtn.Click());

            // Verify task was saved in TaskRepository with Suggested status
            var tasks = await _taskRepository.GetByWorkspaceIdAsync(10);
            Assert.Single(tasks);
            var created = tasks[0];
            Assert.Equal("Implement Distributed Tracing", created.Title);
            Assert.Equal("Add OpenTelemetry activity exporter", created.Description);
            Assert.Equal(TaskOverallStatus.Suggested, created.Status);
            Assert.Equal(3, created.Priority);
            Assert.Equal("master", created.BaseBranch);
            Assert.Equal($"feature/task-{created.Id}", created.FeatureBranch);

            // Verify Proposal VM is marked as Added
            Assert.True(proposal.IsAdded);
            Assert.Equal(created.Id, proposal.CreatedTaskId);

            // Verify markdown sync was invoked
            Assert.Single(_markdownSync.SyncedTasks);
            Assert.Equal(created.Id, _markdownSync.SyncedTasks[0].Id);

            // Verify success banner in UI
            Assert.Contains($"Added as ForgeTask #{created.Id} (Suggested)", cut.Markup);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void SwitchWorkspace_ResetsSessionAndIsolatesState()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace 1" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace 2" };

        _workspaceState.SetActiveWorkspace(ws1);
        _sessionState.BoundWorkspaceId = 1;
        _sessionState.Messages.Add(new ChatMessage("user", "Hello from ws1"));
        _sessionState.Proposals.Add(new IdeationProposalViewModel { Title = "Task in WS1" });

        var cut = Render<Ideation>();
        Assert.Single(_sessionState.Messages);

        // Switch to Workspace 2
        _workspaceState.SetActiveWorkspace(ws2);

        // Session should be reset for new workspace
        Assert.Empty(_sessionState.Messages);
        Assert.Empty(_sessionState.Proposals);
        Assert.Equal(2, _sessionState.BoundWorkspaceId);
    }

    [Fact]
    public async Task SendMessage_StreamsToolCalls_RendersToolTraceAndBadges()
    {
        var ws = new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        var toolStarted = new IdeationToolCallInfo("call_test_1", "read_file", "{\"path\": \"src/Program.cs\"}");
        var toolCompleted = toolStarted with
        {
            Result = "using System;\nConsole.WriteLine(\"Hello\");",
            IsError = false,
            DurationMs = 85
        };

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: toolStarted),
            new(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: toolCompleted),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "I examined Program.cs."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("I examined Program.cs.", Array.Empty<IdeationProposal>(), new[] { toolCompleted }))
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Read Program.cs");

        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        var assistantMsg = _sessionState.Messages[1];
        Assert.NotNull(assistantMsg.ToolCalls);
        Assert.Single(assistantMsg.ToolCalls);
        Assert.Equal("read_file", assistantMsg.ToolCalls[0].ToolName);
        Assert.Equal(85, assistantMsg.ToolCalls[0].DurationMs);
        Assert.False(assistantMsg.ToolCalls[0].IsError);

        Assert.Contains("read_file", cut.Markup);
        Assert.Contains("src/Program.cs", cut.Markup);
        Assert.Contains("85ms", cut.Markup);
        Assert.Contains("tool-trace completed", cut.Markup);

        var summary = cut.Find(".tool-trace summary");
        Assert.NotNull(summary);
        Assert.Contains("Console.WriteLine", cut.Markup);
    }

    [Fact]
    public async Task SendMessage_StreamsToolFailure_RendersErrorBadge()
    {
        var ws = new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        var failedTool = new IdeationToolCallInfo("call_fail", "run_command", "{\"command\": \"cat missing.txt\"}", "File not found: missing.txt", IsError: true, DurationMs: 120);

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: failedTool with { Result = null, DurationMs = 0 }),
            new(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: failedTool),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Command failed as expected."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Command failed as expected.", Array.Empty<IdeationProposal>(), new[] { failedTool }))
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Run missing");

        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        Assert.Contains("tool-trace error", cut.Markup);
        Assert.Contains("Failed", cut.Markup);
        Assert.Contains("run_command", cut.Markup);
    }

    [Fact]
    public async Task ReasoningTraceAccordion_RendersThinkingContentAndStatus()
    {
        var ws = new Workspace { Id = 1, Name = "Repo", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Deep chain-of-thought analysis in progress..."),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Here is the architectural answer."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Finished", Array.Empty<IdeationProposal>()))
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("How does thinking work?");

        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        Assert.Contains("think-trace", cut.Markup);
        Assert.Contains("🧠 Reasoning Trace", cut.Markup);
        Assert.Contains("Deep chain-of-thought analysis in progress...", cut.Markup);
    }

    [Fact]
    public async Task IdeationPage_RendersRoutingTargetBadge_AndUpdatesTargetInConfig()
    {
        var ws = new Workspace { Id = 1, Name = "TestRepo" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;

        var cut = Render<Ideation>();

        // Default badge shows Auto Target
        Assert.Contains("🔀 Auto Target", cut.Markup);

        // Open config accordion
        var configBtn = cut.Find("button.prompt-config-toggle-btn");
        await cut.InvokeAsync(() => configBtn.Click());

        // Verify OllamaRouter target selector is rendered
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("OllamaRouter Target Backend", cut.Markup);
            Assert.Contains("Host GPU", cut.Markup);
        });

        // Change target to Local (Host GPU)
        var select = cut.Find("select.forge-input");
        select.Change(OllamaRoutingTarget.Local.ToString());

        Assert.Equal(OllamaRoutingTarget.Local, _sessionState.SelectedRoutingTarget);
        cut.WaitForAssertion(() => Assert.Contains("🖥️ Host GPU", cut.Markup));
    }

    [Fact]
    public async Task SendMessage_PersistsConversationAndTurnsWithProposalsAndThinkingTrace()
    {
        var ws = new Workspace { Id = 42, Name = "PersistenceWorkspace", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 42;

        var proposal = new IdeationProposal("Add telemetry", "Enable opentelemetry exporter", 1);
        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Thinking about metrics..."),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Telemetry proposal formulated."),
            new(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: proposal),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Finished", new[] { proposal }))
        };

        var cut = Render<Ideation>();

        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("How should we measure performance?");

        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        // Verify conversation record in repository
        var conversations = await _taskRepository.GetIdeationConversationsAsync(42);
        Assert.Single(conversations);
        var conv = conversations[0];
        Assert.Null(conv.TaskId);
        Assert.Equal(42, conv.WorkspaceId);
        Assert.Equal(ConversationType.Ideation, conv.ConversationType);
        Assert.Equal(StageExecutionStatus.Running, conv.Status);

        // Verify conversation with turns
        var convWithTurns = await _taskRepository.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        Assert.Equal(2, convWithTurns.Turns.Count);

        var userTurn = convWithTurns.Turns.First(t => t.Role == StageTurnRole.User);
        Assert.Equal("How should we measure performance?", userTurn.Content);
        Assert.Equal(0, userTurn.TurnIndex);

        var assistantTurn = convWithTurns.Turns.First(t => t.Role == StageTurnRole.Assistant);
        Assert.Equal("Telemetry proposal formulated.", assistantTurn.Content);
        Assert.Equal("Thinking about metrics...", assistantTurn.ThinkingTrace);
        Assert.NotNull(assistantTurn.MetadataJson);
        Assert.Contains("Add telemetry", assistantTurn.MetadataJson);
    }

    [Fact]
    public async Task ClearChat_CompletesCurrentConversation_AndStartsFreshConversation()
    {
        var ws = new Workspace { Id = 43, Name = "ClearWorkspace", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 43;

        var cut = Render<Ideation>();

        // Send a first message
        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Initial reply."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Initial reply.", Array.Empty<IdeationProposal>()))
        };
        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("Hello");
        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        cut.WaitForAssertion(() => Assert.Equal(2, _sessionState.Messages.Count));

        var initialConvId = _sessionState.ActiveConversationId;
        Assert.NotNull(initialConvId);

        // Click Clear Chat button
        var clearBtn = cut.Find("button.reset-session-btn");
        await cut.InvokeAsync(() => clearBtn.Click());

        // Messages should be cleared in current session
        Assert.Empty(_sessionState.Messages);

        // New active conversation ID should be different
        Assert.NotNull(_sessionState.ActiveConversationId);
        Assert.NotEqual(initialConvId, _sessionState.ActiveConversationId);

        // Previous conversation should be marked Completed in DB
        var prevConv = await _taskRepository.GetConversationWithTurnsAsync(initialConvId.Value);
        Assert.NotNull(prevConv);
        Assert.Equal(StageExecutionStatus.Completed, prevConv.Status);

        // New conversation should be Running
        var newConv = await _taskRepository.GetConversationWithTurnsAsync(_sessionState.ActiveConversationId.Value);
        Assert.NotNull(newConv);
        Assert.Equal(StageExecutionStatus.Running, newConv.Status);
    }

    [Fact]
    public async Task SessionDropdown_AllowsSwitchingBetweenSessions_RestoresTurnsAndProposals()
    {
        var ws = new Workspace { Id = 44, Name = "SwitchSessionWorkspace", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);

        // Create an archived previous conversation with turns and proposal in metadata
        var prevConv = await _taskRepository.StartIdeationConversationAsync(44, "Architecture Discovery - Auth");
        await _taskRepository.AddConversationTurnAsync(prevConv.Id, StageTurnRole.User, content: "How do we handle JWT?");
        var proposalJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new IdeationProposal("Implement JWT Auth", "Use symmetric key for tokens", 2)
        });
        await _taskRepository.AddConversationTurnAsync(
            prevConv.Id,
            StageTurnRole.Assistant,
            content: "JWT is recommended.",
            thinkingTrace: "Evaluating OAuth vs JWT...",
            metadataJson: proposalJson);
        await _taskRepository.CompleteConversationAsync(prevConv.Id, StageExecutionStatus.Completed);

        // Active conversation
        var activeConv = await _taskRepository.StartIdeationConversationAsync(44, "Current Discovery Session");

        var cut = Render<Ideation>();

        // Verify session select dropdown is rendered
        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("select.session-select");
            Assert.NotNull(select);
        });

        // Switch to the previous conversation
        var sessionSelect = cut.Find("select.session-select");
        await cut.InvokeAsync(() => sessionSelect.Change(prevConv.Id.ToString()));

        // Assert turns were restored
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _sessionState.Messages.Count);
            Assert.Equal("How do we handle JWT?", _sessionState.Messages[0].Content);
            Assert.Equal("JWT is recommended.", _sessionState.Messages[1].Content);
            Assert.Equal("Evaluating OAuth vs JWT...", _sessionState.Messages[1].ThinkingTrace);
            Assert.Single(_sessionState.Proposals);
            Assert.Equal("Implement JWT Auth", _sessionState.Proposals[0].Title);
        });
    }

    [Fact]
    public async Task AddProposalToBacklog_LinksStandardWorkflowDefinition()
    {
        var ws = new Workspace { Id = 45, Name = "WorkflowLinkingWorkspace", DefaultBaseBranch = "main" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 45;

        // Create a default workflow in workflow repository
        var defaultWf = await _workflowRepository.CreateWorkflowAsync(new WorkflowDefinition
        {
            Name = "Standard Agentic Pipeline",
            Code = "std-agentic",
            IsDefault = true
        });

        var proposal = new IdeationProposalViewModel
        {
            Title = "Implement Healthchecks",
            Description = "Add ASP.NET Core healthchecks endpoint",
            Priority = 1
        };
        _sessionState.Proposals.Add(proposal);

        var cut = Render<Ideation>();

        var addBtn = cut.Find("button.add-backlog-btn");
        await cut.InvokeAsync(() => addBtn.Click());

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(45);
        Assert.Single(tasks);
        Assert.Equal(defaultWf.Id, tasks[0].WorkflowId);
    }
}

public class MockIdeationService : IIdeationService
{
    public List<IdeationStreamUpdate> StreamUpdatesToYield { get; set; } = new();

    public Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new IdeationResult("Response", Array.Empty<IdeationProposal>()));
    }

    public Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in StreamUpdatesToYield)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            yield return update;
            await Task.Yield();
        }
    }

    public IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseStreamingAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }
}

public class MockTaskMarkdownSyncService : ITaskMarkdownSyncService
{
    public List<ForgeTask> SyncedTasks { get; } = new();

    public Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default)
    {
        SyncedTasks.Add(task);
        return Task.FromResult(Path.Combine(workspacePath, $"task-{task.Id}.md"));
    }

    public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) => string.Empty;
    public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => (new TaskFrontmatter(), string.Empty);
    public string ComputeContentHash(string content) => string.Empty;
    public void SuppressContentHash(string content) { }
    public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void StartWatching(int workspaceId, string workspacePath) { }
    public void StopWatching(int workspaceId) { }
    public void Dispose() { }
}
