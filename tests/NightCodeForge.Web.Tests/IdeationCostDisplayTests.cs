using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationCostDisplayTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockIdeationService _ideationService;
    private readonly ToastService _toastService;
    private readonly IdeationSessionState _sessionState;

    public IdeationCostDisplayTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
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
        Services.AddSingleton(new IdeationRefinementService(_taskRepository));
    }

    [Fact]
    public void IdeationPage_WhenActiveSessionCostHasData_RendersCostBadgeWithBreakdownTooltip()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Repo", RepositoryPath = "C:\\Alpha" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;
        _sessionState.ActiveSessionCost = new TaskCostSummary(
            TotalCost: 0.042,
            ActualCloudCost: 0.035,
            ElectricityCost: 0.007,
            CloudSavings: 0.120,
            Currency: "$",
            HasCostData: true);

        var cut = Render<Ideation>();

        Assert.Contains("💰 $0.042", cut.Markup);
        Assert.Contains("Estimated Spend: $0.042 (Cloud: $0.035 | Electricity: $0.007 | Cloud Savings: $0.120)", cut.Markup);
    }

    [Fact]
    public void IdeationPage_WhenActiveSessionCostIsEmpty_OmitsCostBadge()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Repo", RepositoryPath = "C:\\Alpha" };
        _workspaceState.SetActiveWorkspace(ws);
        _sessionState.BoundWorkspaceId = 1;
        _sessionState.ActiveSessionCost = TaskCostSummary.Empty;

        var cut = Render<Ideation>();

        Assert.DoesNotContain("💰", cut.Markup);
    }

    [Fact]
    public async Task IdeationPage_WhenConversationWithCostLoaded_PopulatesActiveSessionCostAndRendersBadge()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Repo", RepositoryPath = "C:\\Alpha" };
        await _workspaceRepository.CreateAsync(ws);
        _workspaceState.SetActiveWorkspace(ws);

        var conv = await _taskRepository.StartIdeationConversationAsync(ws.Id, "Architecture Plan");
        conv.Cost = 0.028;
        conv.ActualCloudCost = 0.020;
        conv.ElectricityCost = 0.008;
        conv.CloudSavings = 0.050;
        conv.Currency = "$";
        await _taskRepository.AddConversationTurnAsync(conv.Id, StageTurnRole.User, "Plan architecture");

        var cut = Render<Ideation>();

        // Select the conversation item to trigger LoadConversationIntoSession
        var select = cut.Find("select.session-select");
        await select.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = conv.Id.ToString() });

        Assert.Contains("💰 $0.028", cut.Markup);
        Assert.Contains("Estimated Spend: $0.028 (Cloud: $0.020 | Electricity: $0.008 | Cloud Savings: $0.050)", cut.Markup);
    }
}

