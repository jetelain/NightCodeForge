using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationSessionPersistenceTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockIdeationService _ideationService;
    private readonly ToastService _toastService;
    private readonly IdeationSessionState _sessionState;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly Workspace _workspace;

    public IdeationSessionPersistenceTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository();
        _markdownSync = new MockTaskMarkdownSyncService();
        _ideationService = new MockIdeationService();
        _toastService = new ToastService();
        _sessionState = new IdeationSessionState();

        _workspace = new Workspace
        {
            Id = 55,
            Name = "Persistence Test Workspace",
            RepositoryPath = "C:/repos/persistence-test",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(_workspace);

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<IIdeationService>(_ideationService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_sessionState);
    }

    [Fact]
    public async Task IdeationChatSession_SimulateAndReinitializePage_PreservesMessagesAndProposals()
    {
        var proposal = new IdeationProposal("Decouple Order Processing", "Extract order saga orchestrator", 1);

        _ideationService.StreamUpdatesToYield = new List<IdeationStreamUpdate>
        {
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Analyzing order domain boundaries..."),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Here is the architectural proposal for decoupling."),
            new(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: proposal),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Complete", new[] { proposal }))
        };

        // 1. Initial Render of Ideation Page
        var cut = Render<Ideation>();

        // Type user prompt
        var textarea = cut.Find("textarea.chat-textarea");
        textarea.Input("How should we refactor the order processing?");

        // Click Send
        var sendBtn = cut.Find("button.send-btn");
        await cut.InvokeAsync(() => sendBtn.Click());

        // Wait for async stream execution
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, _sessionState.Messages.Count);
            Assert.Single(_sessionState.Proposals);
        });

        // Verify markup in first render
        Assert.Contains("Analyzing order domain boundaries...", cut.Markup);
        Assert.Contains("Here is the architectural proposal", cut.Markup);
        Assert.Contains("Decouple Order Processing", cut.Markup);

        // Verify conversation was persisted into TaskRepository
        var activeConv = await _taskRepository.GetActiveIdeationConversationAsync(_workspace.Id);
        Assert.NotNull(activeConv);
        var activeConvWithTurns = await _taskRepository.GetConversationWithTurnsAsync(activeConv.Id);
        Assert.NotNull(activeConvWithTurns);
        Assert.Equal(2, activeConvWithTurns.Turns.Count);

        // 2. Re-initialize Ideation.razor (simulate user navigating away and returning)
        var cut2 = Render<Ideation>();

        // Verify that re-rendered page re-hydrates all messages, reasoning traces, and proposals
        cut2.WaitForAssertion(() =>
        {
            // Messages are rendered
            Assert.Contains("How should we refactor the order processing?", cut2.Markup);
            Assert.Contains("Here is the architectural proposal for decoupling.", cut2.Markup);

            // Reasoning trace is rendered
            Assert.Contains("Analyzing order domain boundaries...", cut2.Markup);

            // Discovered proposal is rendered in proposals panel
            Assert.Contains("Decouple Order Processing", cut2.Markup);
            Assert.Contains("Extract order saga orchestrator", cut2.Markup);
        });
    }

    [Fact]
    public async Task IdeationChatSession_ArchivedSession_RehydratesFromRepositoryUponSelection()
    {
        // 1. Seed a completed ideation session directly in the repository
        var conv = await _taskRepository.StartIdeationConversationAsync(_workspace.Id, "Archived Auth Discovery Session");
        await _taskRepository.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Review OAuth 2.1 PKCE requirements");

        var proposalJson = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new IdeationProposal("Upgrade OAuth to PKCE", "Enforce S256 code challenge on authorization code flow", 2)
        });

        await _taskRepository.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Assistant,
            content: "OAuth 2.1 mandates PKCE for all authorization code grants.",
            thinkingTrace: "RFC 7636 and OAuth 2.1 draft review...",
            metadataJson: proposalJson);

        await _taskRepository.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed);

        // 2. Also start an empty active conversation
        await _taskRepository.StartIdeationConversationAsync(_workspace.Id, "Active Blank Session");

        // 3. Render Ideation page
        var cut = Render<Ideation>();

        // Switch dropdown to archived session
        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("select.session-select");
            Assert.NotNull(select);
        });

        var sessionSelect = cut.Find("select.session-select");
        await cut.InvokeAsync(() => sessionSelect.Change(conv.Id.ToString()));

        // 4. Verify all archived turns, thinking trace, and proposals are rendered
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Review OAuth 2.1 PKCE requirements", cut.Markup);
            Assert.Contains("OAuth 2.1 mandates PKCE", cut.Markup);
            Assert.Contains("RFC 7636 and OAuth 2.1 draft review...", cut.Markup);
            Assert.Contains("Upgrade OAuth to PKCE", cut.Markup);
        });
    }
}
