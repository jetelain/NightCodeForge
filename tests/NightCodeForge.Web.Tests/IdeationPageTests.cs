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

    public IdeationPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _markdownSync = new MockTaskMarkdownSyncService();
        _ideationService = new MockIdeationService();
        _toastService = new ToastService();
        _sessionState = new IdeationSessionState();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
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
