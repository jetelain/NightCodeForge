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
    private readonly MockWorkspaceIntakeUiService _intakeUiService;

    public WorkspaceWizardComponentTests()
    {
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _provisioningService = new MockWorkspaceProvisioningService();
        _modelCatalogService = new MockModelCatalogService();
        _wslMetadataService = new MockWslMetadataService();
        _intakeUiService = new MockWorkspaceIntakeUiService();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:ModelId"] = "Qwen3.8-27B:latest"
        }).Build();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IWorkspaceProvisioningService>(_provisioningService);
        Services.AddSingleton<IWorkspaceIntakeUiService>(_intakeUiService);
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

    [Fact]
    public async Task WizardStep1PathProbe_LocalDropdown_PopulatesPathNameAndTriggersProbe()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "repos", "sample-app");
        _intakeUiService.LocalRepositories =
        [
            new LocalRepositoryOption(
                Name: "sample-app",
                FullPath: repoPath,
                DisplayPath: "source/repos/sample-app",
                CurrentBranch: "develop",
                LastModified: DateTimeOffset.UtcNow,
                HasRemoteOrigin: true,
                RemoteOriginUrl: "https://github.com/octocat/sample-app.git")
        ];

        var model = new WorkspaceCreationModel();
        var probeInvoked = false;

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnProbeRequested, () => { probeInvoked = true; }));

        var select = cut.Find("#local-repo-select");
        Assert.Contains("sample-app (branch: develop) — source/repos/sample-app", select.InnerHtml);

        await select.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = repoPath });

        Assert.True(probeInvoked);
        Assert.Equal(repoPath, model.RepositoryPath);
        Assert.Equal("sample-app", model.Name);

        await cut.Find("#refresh-local-repos-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.True(_intakeUiService.LastForceRefresh);
    }

    [Fact]
    public async Task WizardStep1PathProbe_RemoteTab_Unauthenticated_ShowsConnectCallout()
    {
        _intakeUiService.RemoteState = new RemoteForgeState(
            HasAvailableProvider: true,
            IsAuthenticated: false,
            ActiveProviderType: null,
            ProviderDisplayName: "GitHub",
            AuthenticatedUsername: null,
            Repositories: Array.Empty<RemoteRepositoryItem>(),
            ErrorMessage: null);

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, new WorkspaceCreationModel()));

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("#remote-connect-callout"));
        Assert.Contains("Connect GitHub in Settings", cut.Markup);
    }

    [Fact]
    public async Task WizardStep1PathProbe_RemoteTab_SelectsLocalMatch_AndValidatesCloneCollision()
    {
        var localMatchedPath = Path.Combine(Path.GetTempPath(), "repos", "matched-app");
        var badClonePath = Path.Combine(Path.GetTempPath(), "repos", "existing-non-empty");

        var localMatch = new LocalRepositoryOption(
            Name: "matched-app",
            FullPath: localMatchedPath,
            DisplayPath: "source/repos/matched-app",
            CurrentBranch: "main",
            LastModified: DateTimeOffset.UtcNow,
            HasRemoteOrigin: true,
            RemoteOriginUrl: "https://github.com/octocat/matched-app.git");

        _intakeUiService.DefaultClonePath = Path.Combine(Path.GetTempPath(), "repos", "cloud-app");
        _intakeUiService.TargetPathValidator = path =>
            string.Equals(path, badClonePath, StringComparison.Ordinal)
                ? new TargetPathValidationResult(false, "Target directory already exists and is not empty.", true, true)
                : new TargetPathValidationResult(true, null, false, false);

        _intakeUiService.RemoteState = new RemoteForgeState(
            HasAvailableProvider: true,
            IsAuthenticated: true,
            ActiveProviderType: VcsHostingProviderType.GitHub,
            ProviderDisplayName: "GitHub",
            AuthenticatedUsername: "octocat",
            Repositories:
            [
                new RemoteRepositoryItem(
                    VcsHostingProviderType.GitHub,
                    new VcsRepositorySummary("1", "matched-app", "octocat/matched-app", "octocat", "Local copy", false, "main", "https://github.com/octocat/matched-app.git", "git@github.com:octocat/matched-app.git", DateTimeOffset.UtcNow),
                    localMatch),
                new RemoteRepositoryItem(
                    VcsHostingProviderType.GitHub,
                    new VcsRepositorySummary("2", "cloud-app", "octocat/cloud-app", "octocat", "Remote only", true, "main", "https://github.com/octocat/cloud-app.git", "git@github.com:octocat/cloud-app.git", DateTimeOffset.UtcNow),
                    null)
            ],
            ErrorMessage: null);

        var model = new WorkspaceCreationModel();
        var onboarded = false;

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.OnRemoteRepositoryOnboarded, () => { onboarded = true; }));

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Local copy found", cut.Markup);
        Assert.Contains("Private", cut.Markup);

        await cut.Find("button.remote-clone-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.NotEmpty(cut.FindAll("#clone-drawer"));

        await cut.Find("#clone-target-path-input").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = badClonePath });
        Assert.NotEmpty(cut.FindAll("#clone-path-validation-error"));
        Assert.True(cut.Find("#confirm-clone-btn").HasAttribute("disabled"));

        await cut.Find("button.remote-select-local-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.True(onboarded);
        Assert.Equal(localMatchedPath, model.RepositoryPath);
        Assert.NotNull(model.GitHubSettings);
        Assert.True(model.GitHubSettings.Enabled);
        Assert.Equal("octocat", model.GitHubSettings.RepositoryOwner);
        Assert.Equal("matched-app", model.GitHubSettings.RepositoryName);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_RemoteClone_AdvancesDirectlyToStep2()
    {
        var clonedPath = Path.Combine(Path.GetTempPath(), "repos", "cloud-app");
        _intakeUiService.DefaultClonePath = clonedPath;
        _intakeUiService.RemoteState = new RemoteForgeState(
            HasAvailableProvider: true,
            IsAuthenticated: true,
            ActiveProviderType: VcsHostingProviderType.GitHub,
            ProviderDisplayName: "GitHub",
            AuthenticatedUsername: "octocat",
            Repositories:
            [
                new RemoteRepositoryItem(
                    VcsHostingProviderType.GitHub,
                    new VcsRepositorySummary("2", "cloud-app", "octocat/cloud-app", "octocat", "Remote only", true, "main", "https://github.com/octocat/cloud-app.git", "git@github.com:octocat/cloud-app.git", DateTimeOffset.UtcNow),
                    null)
            ],
            ErrorMessage: null);

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.remote-clone-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#confirm-clone-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("#base-branch-select"));
        Assert.Contains("Stack & Branch Auto-Discovery", cut.Find(".step-title").TextContent);
    }

    [Theory]
    [InlineData(0, "0 repositories discovered")]
    [InlineData(1, "1 repository discovered")]
    [InlineData(2, "2 repositories discovered")]
    public void WizardStep1PathProbe_LocalRepositoryCount_PluralizesCorrectly(int count, string expectedText)
    {
        _intakeUiService.LocalRepositories = Enumerable.Range(1, count)
            .Select(i => new LocalRepositoryOption(
                Name: $"repo-{i}",
                FullPath: Path.Combine(Path.GetTempPath(), "repos", $"repo-{i}"),
                DisplayPath: $"source/repos/repo-{i}",
                CurrentBranch: "main",
                LastModified: DateTimeOffset.UtcNow,
                HasRemoteOrigin: false,
                RemoteOriginUrl: null))
            .ToList();

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, new WorkspaceCreationModel()));

        Assert.Contains(expectedText, cut.Markup);
        Assert.DoesNotContain("repositoryies", cut.Markup);
    }

    [Fact]
    public async Task WizardStep1PathProbe_RemoteTab_WhenLoadThrows_DisplaysErrorBanner()
    {
        _intakeUiService.RemoteLoadException = new InvalidOperationException("GitHub API rate limit exceeded");

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, new WorkspaceCreationModel()));

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Failed to Load Remote Repositories", cut.Markup);
        Assert.Contains("GitHub API rate limit exceeded", cut.Markup);
    }

    [Fact]
    public async Task WizardStep1PathProbe_RefreshingLocalRepos_InvalidatesCachedRemoteState()
    {
        _intakeUiService.RemoteState = new RemoteForgeState(
            HasAvailableProvider: true,
            IsAuthenticated: true,
            ActiveProviderType: VcsHostingProviderType.GitHub,
            ProviderDisplayName: "GitHub",
            AuthenticatedUsername: "octocat",
            Repositories: Array.Empty<RemoteRepositoryItem>(),
            ErrorMessage: null);

        var cut = Render<WizardStep1PathProbe>(parameters => parameters
            .Add(p => p.Model, new WorkspaceCreationModel()));

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Equal(1, _intakeUiService.LoadRemoteCallCount);

        await cut.Find("#intake-tab-local").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#refresh-local-repos-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        await cut.Find("#intake-tab-remote").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Equal(2, _intakeUiService.LoadRemoteCallCount);
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

    private class MockWorkspaceIntakeUiService : IWorkspaceIntakeUiService
    {
        public IReadOnlyList<LocalRepositoryOption> LocalRepositories { get; set; } = Array.Empty<LocalRepositoryOption>();
        public bool LastForceRefresh { get; private set; }
        public RemoteForgeState RemoteState { get; set; } = new(false, false, null, null, null, Array.Empty<RemoteRepositoryItem>(), null);
        public Exception? RemoteLoadException { get; set; }
        public int LoadRemoteCallCount { get; private set; }
        public string DefaultClonePath { get; set; } = Path.Combine(Path.GetTempPath(), "repos", "cloned-app");
        public Func<string?, TargetPathValidationResult>? TargetPathValidator { get; set; }

        public Task<IReadOnlyList<LocalRepositoryOption>> LoadLocalRepositoriesAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            LastForceRefresh = forceRefresh;
            return Task.FromResult(LocalRepositories);
        }

        public IReadOnlyList<LocalRepositoryOption> FilterLocalRepositories(IReadOnlyList<LocalRepositoryOption> repositories, string? searchQuery)
            => string.IsNullOrWhiteSpace(searchQuery)
                ? repositories
                : repositories.Where(r => r.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        public string FormatLocalRepositoryLabel(LocalRepositoryOption repository)
            => $"{repository.Name} (branch: {repository.CurrentBranch ?? "detached"}) — {repository.DisplayPath}";

        public Task<RemoteForgeState> LoadRemoteForgeStateAsync(IReadOnlyList<LocalRepositoryOption> localRepositories, CancellationToken cancellationToken = default)
        {
            LoadRemoteCallCount++;
            if (RemoteLoadException is not null)
            {
                throw RemoteLoadException;
            }

            return Task.FromResult(RemoteState);
        }

        public IReadOnlyList<RemoteRepositoryItem> FilterRemoteRepositories(IReadOnlyList<RemoteRepositoryItem> repositories, string? searchQuery)
            => string.IsNullOrWhiteSpace(searchQuery)
                ? repositories
                : repositories.Where(r => r.Repository.FullName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        public string ResolveDefaultClonePath(string repositoryName) => DefaultClonePath;

        public TargetPathValidationResult ValidateCloneTargetPath(string? targetPath)
            => TargetPathValidator?.Invoke(targetPath)
               ?? new TargetPathValidationResult(!string.IsNullOrWhiteSpace(targetPath), null, false, false);

        public Task<RemoteCloneResult> CloneRemoteRepositoryAsync(VcsHostingProviderType providerType, VcsRepositorySummary repository, string targetPath, CancellationToken cancellationToken = default)
            => Task.FromResult(new RemoteCloneResult(true, targetPath, repository.Name, repository.DefaultBranch, BuildGitHubSettingsIfApplicable(providerType, repository), null));

        public GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(VcsHostingProviderType? providerType, VcsRepositorySummary repository)
            => providerType == VcsHostingProviderType.GitHub
                ? new GitHubIntegrationSettings { Enabled = true, RepositoryOwner = repository.Owner, RepositoryName = repository.Name }
                : null;

        public string DeriveWorkspaceNameFromPath(string? repositoryPath)
            => string.IsNullOrWhiteSpace(repositoryPath) ? string.Empty : Path.GetFileName(repositoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    #endregion
}
