using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Workspaces;
using NightCodeForge.Web.Components.Workspaces.Steps;
using NightCodeForge.Web.Models;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceWizardComponentTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly MockWorkspaceProvisioningService _provisioningService;
    private readonly MockModelCatalogService _modelCatalogService;
    private readonly MockWslMetadataService _wslMetadataService;

    public WorkspaceWizardComponentTests()
    {
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _provisioningService = new MockWorkspaceProvisioningService();
        _modelCatalogService = new MockModelCatalogService();
        _wslMetadataService = new MockWslMetadataService();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:ModelId"] = "Qwen3.8-27B:latest"
        }).Build();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IWorkspaceProvisioningService>(_provisioningService);
        Services.AddSingleton<ILlmModelCatalogService>(_modelCatalogService);
        Services.AddSingleton<IWslMetadataService>(_wslMetadataService);
        Services.AddSingleton<IConfiguration>(config);
    }

    [Fact]
    public void WorkspaceEmptyStateHero_ClickingLaunchWizard_CallsRequestOpenWizard()
    {
        var openRequested = false;
        _workspaceState.OnOpenWizardRequested += () => openRequested = true;

        var cut = Render<WorkspaceEmptyStateHero>();

        var launchBtn = cut.Find("button.hero-launch-btn");
        launchBtn.Click();

        Assert.True(openRequested);
    }

    [Fact]
    public void WorkspaceCreationWizardModal_WhenClosed_RendersNothing()
    {
        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, false));

        Assert.Empty(cut.FindAll(".forge-modal-backdrop"));
    }

    [Fact]
    public void WorkspaceCreationWizardModal_WhenOpen_RendersHeaderAndStep1()
    {
        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        Assert.NotEmpty(cut.FindAll(".forge-modal-backdrop"));
        Assert.Contains("Workspace Provisioning Wizard", cut.Find(".wizard-main-title").TextContent);
        Assert.NotEmpty(cut.FindAll(".step-node.active"));
        Assert.Contains("Repository", cut.Find(".step-node.active").TextContent);
    }

    [Fact]
    public void WorkspaceCreationWizardModal_ReactsToOnOpenWizardRequested()
    {
        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, false));

        Assert.Empty(cut.FindAll(".forge-modal-backdrop"));

        _workspaceState.RequestOpenWizard();
        cut.Render();

        Assert.NotEmpty(cut.FindAll(".forge-modal-backdrop"));
    }

    [Fact]
    public void WorkspaceCreationWizardModal_Step4_PassesEvaluatedParameters()
    {
        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        var stepField = typeof(WorkspaceCreationWizardModal).GetField("_currentStep", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var msgField = typeof(WorkspaceCreationWizardModal).GetField("_provisioningMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        stepField.SetValue(cut.Instance, 4);
        msgField.SetValue(cut.Instance, "Setting up Git workspace mirror...");
        cut.Render();

        var step4 = cut.FindComponent<WizardStep4Provisioning>();
        Assert.NotNull(step4);
        Assert.Equal("Setting up Git workspace mirror...", step4.Instance.ProgressMessage);
        Assert.Null(step4.Instance.ErrorMessage);
        Assert.DoesNotContain("_provisioningError", cut.Markup);
        Assert.DoesNotContain("_provisioningMessage", cut.Markup);
    }

    [Fact]
    public void WizardStep4Provisioning_WhenCompletedWithWorkspace_RendersSuccessCard()
    {
        var testWorkspace = new Workspace
        {
            Id = 42,
            Name = "SuccessRepo",
            RepositoryPath = "C:\\repos\\SuccessRepo",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var cut = Render<WizardStep4Provisioning>(parameters => parameters
            .Add(p => p.IsComplete, true)
            .Add(p => p.CreatedWorkspace, testWorkspace)
            .Add(p => p.ErrorMessage, null));

        Assert.NotEmpty(cut.FindAll(".provision-success-card"));
        Assert.Empty(cut.FindAll(".provision-error-card"));
        Assert.Contains("Workspace Ready!", cut.Find(".status-title").TextContent);
        Assert.Contains("SuccessRepo", cut.Markup);
        Assert.Contains("#42", cut.Markup);
    }

    [Fact]
    public void WizardStep4Provisioning_WhenFailedWithoutCompletion_RendersErrorCard()
    {
        var cut = Render<WizardStep4Provisioning>(parameters => parameters
            .Add(p => p.IsComplete, false)
            .Add(p => p.CreatedWorkspace, null)
            .Add(p => p.ErrorMessage, "Git clone timeout occurred"));

        Assert.NotEmpty(cut.FindAll(".provision-error-card"));
        Assert.Empty(cut.FindAll(".provision-success-card"));
        Assert.Contains("Provisioning Failed", cut.Find(".status-title").TextContent);
        Assert.Contains("Git clone timeout occurred", cut.Find(".error-detail-box").TextContent);
    }

    [Fact]
    public void WizardStep4Provisioning_WhenInProgress_RendersProgressCard()
    {
        var cut = Render<WizardStep4Provisioning>(parameters => parameters
            .Add(p => p.IsComplete, false)
            .Add(p => p.CreatedWorkspace, null)
            .Add(p => p.ErrorMessage, null)
            .Add(p => p.ProgressMessage, "Initializing Ext4 mirror clone..."));

        Assert.NotEmpty(cut.FindAll(".provision-progress-card"));
        Assert.Contains("Provisioning Workspace...", cut.Find(".status-title").TextContent);
        Assert.Contains("Initializing Ext4 mirror clone...", cut.Find(".current-task-pill").TextContent);
    }

    #region Mocks

    private class MockWorkspaceProvisioningService : IWorkspaceProvisioningService
    {
        public Task<RepositoryProbeResult> ProbeRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryProbeResult(
                Exists: true,
                IsGitRepository: true,
                SuggestedName: "TestRepo",
                AvailableBranches: new[] { "main" },
                DetectedBaseBranch: "main",
                DetectedTechStack: ".NET / C#",
                HasAgentsMarkdown: true,
                WarningMessage: null));
        }

        public Task<bool> InitializeGitRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Workspace> ProvisionWorkspaceAsync(WorkspaceCreationModel model, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new Workspace
            {
                Id = 1,
                Name = model.Name,
                RepositoryPath = model.RepositoryPath,
                DefaultBaseBranch = model.DefaultBaseBranch,
                ExecutionMode = model.ExecutionMode
            });
        }
    }

    private class MockModelCatalogService : ILlmModelCatalogService
    {
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AvailableModelInfo?> GetDefaultModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AvailableModelInfo?>(new("Qwen3.8-27B:latest", "Qwen3.8-27B:latest", SizeBytes: 1024));

        public Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EndpointVerificationResult(true, true, 1));

        public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AvailableModelInfo> list = new List<AvailableModelInfo>
            {
                new("Qwen3.8-27B:latest", "Qwen3.8-27B:latest", SizeBytes: 1024)
            };
            return Task.FromResult(list);
        }
    }

    private class MockWslMetadataService : IWslMetadataService
    {
        public string GetDefaultDistributionName() => "Ubuntu";
        public string GetDefaultUserName() => "julien";
        public void ClearCache() { }
    }

    #endregion
}
