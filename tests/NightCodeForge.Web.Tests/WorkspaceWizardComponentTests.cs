using Bunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
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
    private readonly MockWorkspaceTemplateService _templateService;
    private readonly MockWorkspaceTemplateUiService _templateUiService;

    public WorkspaceWizardComponentTests()
    {
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _provisioningService = new MockWorkspaceProvisioningService();
        _modelCatalogService = new MockModelCatalogService();
        _wslMetadataService = new MockWslMetadataService();
        _intakeUiService = new MockWorkspaceIntakeUiService();
        _templateService = new MockWorkspaceTemplateService();
        _templateUiService = new MockWorkspaceTemplateUiService(_templateService);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:ModelId"] = "Qwen3.8-27B:latest"
        }).Build();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IWorkspaceProvisioningService>(_provisioningService);
        Services.AddSingleton<IWorkspaceIntakeUiService>(_intakeUiService);
        Services.AddSingleton<IWorkspaceTemplateService>(_templateService);
        Services.AddSingleton<IWorkspaceTemplateUiService>(_templateUiService);
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

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_SkipsStep2AndProvisionsWithGitHubSettings()
    {
        _templateUiService.RemoteAvailability = new RemotePublishAvailability(
            true,
            VcsHostingProviderType.GitHub,
            "GitHub",
            "octocat");

        var publishedRepo = new VcsRepositorySummary(
            "99",
            "BillingApp",
            "octocat/BillingApp",
            "octocat",
            "Billing API",
            true,
            "main",
            "https://github.com/octocat/BillingApp.git",
            "git@github.com:octocat/BillingApp.git",
            DateTimeOffset.UtcNow);
        _templateService.PublishedRepositoryToReturn = publishedRepo;

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.NotEmpty(cut.FindAll(".template-gallery-grid"));

        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "BillingApp" });
        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        // Continue goes directly from Step 1 (Template) to Step 3 (Environment), skipping Step 2
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Empty(cut.FindAll("#base-branch-select"));
        Assert.Contains("Execution Mode &amp; Environment Configuration", cut.Markup);

        // Provision executes Phase A (template scaffold) then Phase B (workspace onboarding)
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.NotNull(_provisioningService.LastProvisionedModel);
        Assert.Equal("main", _provisioningService.LastProvisionedModel.DefaultBaseBranch);
        Assert.NotNull(_provisioningService.LastProvisionedModel.GitHubSettings);
        Assert.True(_provisioningService.LastProvisionedModel.GitHubSettings.Enabled);
        Assert.Equal("octocat", _provisioningService.LastProvisionedModel.GitHubSettings.RepositoryOwner);
        Assert.Equal("BillingApp", _provisioningService.LastProvisionedModel.GitHubSettings.RepositoryName);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_RemotePublishFailure_AllowsRetryWithoutReScaffoldingOrLocalOnly()
    {
        _templateUiService.RemoteAvailability = new RemotePublishAvailability(
            true,
            VcsHostingProviderType.GitHub,
            "GitHub",
            "octocat");
        _templateService.ThrowRemoteFailureAfterLocalScaffold = true;

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "RetryApp" });
        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Null(_provisioningService.LastProvisionedModel);
        Assert.NotEmpty(cut.FindAll("#retry-remote-publish-btn"));
        Assert.NotEmpty(cut.FindAll("#continue-local-only-btn"));

        // Retry remote publish succeeds without re-running ScaffoldWorkspaceAsync
        _templateService.ThrowRemoteFailureAfterLocalScaffold = false;
        _templateService.PublishedRepositoryToReturn = new VcsRepositorySummary(
            "100",
            "RetryApp",
            "octocat/RetryApp",
            "octocat",
            string.Empty,
            true,
            "main",
            "https://github.com/octocat/RetryApp.git",
            "git@github.com:octocat/RetryApp.git",
            DateTimeOffset.UtcNow);

        await cut.Find("#retry-remote-publish-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Equal(1, _templateService.PublishOnlyCallCount);
        Assert.NotNull(_provisioningService.LastProvisionedModel);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_PhaseBFailureAfterLocalScaffold_ShowsProvisioningFailedAndRetriesWithoutReScaffolding()
    {
        _provisioningService.ProvisionExceptionToThrow = new InvalidOperationException("Database locked");

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "LocalOnlyApp" });

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Contains("Provisioning Failed", cut.Markup);
        Assert.DoesNotContain("Remote Publishing Failed", cut.Markup);
        Assert.Empty(cut.FindAll("#retry-remote-publish-btn"));
        Assert.Empty(cut.FindAll("#continue-local-only-btn"));

        // Retry Phase B without re-running local template scaffolding
        _provisioningService.ProvisionExceptionToThrow = null;
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Equal(0, _templateService.PublishOnlyCallCount);
        Assert.NotNull(_provisioningService.LastProvisionedModel);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_PhaseBFailureAfterNonGitHubPublish_RetriesWithoutRepublishing()
    {
        _templateUiService.RemoteAvailability = new RemotePublishAvailability(
            true,
            VcsHostingProviderType.GitLab,
            "GitLab",
            "octocat");
        _templateService.PublishedRepositoryToReturn = new VcsRepositorySummary(
            "200",
            "GitLabApp",
            "octocat/GitLabApp",
            "octocat",
            string.Empty,
            true,
            "main",
            "https://gitlab.com/octocat/GitLabApp.git",
            "git@gitlab.com:octocat/GitLabApp.git",
            DateTimeOffset.UtcNow);
        _provisioningService.ProvisionExceptionToThrow = new InvalidOperationException("Ext4 mirror failed");

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "GitLabApp" });
        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Contains("Provisioning Failed", cut.Markup);

        _provisioningService.ProvisionExceptionToThrow = null;
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Equal(0, _templateService.PublishOnlyCallCount);
        Assert.NotNull(_provisioningService.LastProvisionedModel);
        Assert.Null(_provisioningService.LastProvisionedModel.GitHubSettings);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_EditRemoteRepoNameAfterPublishFailure_PreservesLocalScaffoldAndRetriesWithNewName()
    {
        _templateUiService.RemoteAvailability = new RemotePublishAvailability(
            true,
            VcsHostingProviderType.GitHub,
            "GitHub",
            "octocat");
        _templateService.ThrowRemoteFailureAfterLocalScaffold = true;

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "RetryApp" });
        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.NotEmpty(cut.FindAll("#retry-remote-publish-btn"));

        // Navigate back to Step 1 (Template) via Edit Configuration -> Back
        await cut.Find("button.forge-btn-secondary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-secondary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Edit only the remote repository name; local scaffold remains preserved
        await cut.Find("#template-remote-repo-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "RetryApp-v2" });

        _templateService.ThrowRemoteFailureAfterLocalScaffold = false;
        _templateService.PublishedRepositoryToReturn = new VcsRepositorySummary(
            "101",
            "RetryApp-v2",
            "octocat/RetryApp-v2",
            "octocat",
            string.Empty,
            true,
            "main",
            "https://github.com/octocat/RetryApp-v2.git",
            "git@github.com:octocat/RetryApp-v2.git",
            DateTimeOffset.UtcNow);

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Equal(1, _templateService.PublishOnlyCallCount);
        Assert.Equal("RetryApp-v2", _templateService.LastPublishedRepositoryName);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_TemplateMode_RemotePublishFailure_ContinueLocalOnlySkipsPublishAndProvisions()
    {
        _templateUiService.RemoteAvailability = new RemotePublishAvailability(
            true,
            VcsHostingProviderType.GitHub,
            "GitHub",
            "octocat");
        _templateService.ThrowRemoteFailureAfterLocalScaffold = true;

        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "FallbackLocalApp" });
        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("button.forge-btn-primary").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.NotEmpty(cut.FindAll("#continue-local-only-btn"));

        await cut.Find("#continue-local-only-btn").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _templateService.ScaffoldCallCount);
        Assert.Equal(0, _templateService.PublishOnlyCallCount);
        Assert.NotNull(_provisioningService.LastProvisionedModel);
        Assert.Null(_provisioningService.LastProvisionedModel.GitHubSettings);
        Assert.Contains("Workspace Ready!", cut.Markup);
    }

    [Fact]
    public async Task WorkspaceCreationWizardModal_SwitchingFromExistingToTemplateAndBack_ClearsStaleProbeResult()
    {
        var cut = Render<WorkspaceCreationWizardModal>(parameters => parameters.Add(p => p.IsOpen, true));

        await cut.Find("#repo-path-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "C:\\repos\\ExistingRepo" });
        await cut.FindAll("button").First(b => b.TextContent.Contains("Probe"))
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.False(cut.Find(".wizard-footer button.forge-btn-primary").HasAttribute("disabled"));

        await cut.Find("#wizard-mode-template").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "NewTemplateApp" });

        await cut.Find("#wizard-mode-existing").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(cut.Find(".wizard-footer button.forge-btn-primary").HasAttribute("disabled"));
    }

    #region Mocks

    private class MockWorkspaceProvisioningService : IWorkspaceProvisioningService
    {
        public WorkspaceCreationModel? LastProvisionedModel { get; private set; }
        public Exception? ProvisionExceptionToThrow { get; set; }

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
            if (ProvisionExceptionToThrow is not null)
            {
                throw ProvisionExceptionToThrow;
            }

            LastProvisionedModel = model;
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

    private class MockWorkspaceTemplateService : IWorkspaceTemplateService
    {
        private readonly IReadOnlyList<WorkspaceTemplateDefinition> _templates =
        [
            new WorkspaceTemplateDefinition(
                WorkspaceTemplateConstants.DotNetCleanWebAppId,
                "Web Application (Clean Architecture)",
                "Blazor Server Clean Architecture",
                "🌐",
                TemplateStackCategory.DotNet,
                TemplateArchitectureArchetype.WebApp,
                Array.Empty<WorkspaceTemplateCommand>(),
                ["src/{ProjectName}.Web", "src/{ProjectName}.Core"]),
            new WorkspaceTemplateDefinition(
                WorkspaceTemplateConstants.DotNetClassLibId,
                "Class Library",
                "Modular class library",
                "📦",
                TemplateStackCategory.DotNet,
                TemplateArchitectureArchetype.ClassLibrary,
                Array.Empty<WorkspaceTemplateCommand>(),
                ["src/{ProjectName}", "tests/{ProjectName}.Tests"])
        ];

        public int ScaffoldCallCount { get; private set; }
        public int PublishOnlyCallCount { get; private set; }
        public string? LastPublishedRepositoryName { get; private set; }
        public bool ThrowRemoteFailureAfterLocalScaffold { get; set; }
        public VcsRepositorySummary? PublishedRepositoryToReturn { get; set; }

        public IReadOnlyList<WorkspaceTemplateDefinition> GetAvailableTemplates() => _templates;

        public WorkspaceTemplateDefinition? GetTemplate(string templateId)
            => _templates.FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));

        public Task<TemplateScaffoldingResult> ScaffoldWorkspaceAsync(
            TemplateScaffoldingRequest request,
            IProgress<TemplateScaffoldingProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ScaffoldCallCount++;
            progress?.Report(new TemplateScaffoldingProgress("Local scaffold complete.", 85, false, null, LocalScaffoldCompleted: true));

            if (ThrowRemoteFailureAfterLocalScaffold)
            {
                throw new VcsHostingProviderException(VcsHostingProviderType.GitHub, "Repository already exists on GitHub.");
            }

            progress?.Report(new TemplateScaffoldingProgress("Done.", 100, true, null, LocalScaffoldCompleted: true));
            return Task.FromResult(new TemplateScaffoldingResult(
                request.TargetDirectoryPath,
                request.TemplateId,
                request.ProjectName,
                LocalScaffoldCompleted: true,
                GitInitialized: request.InitializeGit,
                PublishedRepository: request.PublishToRemoteForge ? PublishedRepositoryToReturn : null));
        }

        public Task<VcsRepositorySummary> PublishToRemoteForgeAsync(
            string targetDirectoryPath,
            VcsHostingProviderType providerType,
            string repositoryName,
            string? description,
            bool isPrivate,
            IProgress<TemplateScaffoldingProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            PublishOnlyCallCount++;
            LastPublishedRepositoryName = repositoryName;
            if (ThrowRemoteFailureAfterLocalScaffold)
            {
                throw new VcsHostingProviderException(providerType, "Retry remote publish failed.");
            }

            return Task.FromResult(PublishedRepositoryToReturn ?? new VcsRepositorySummary(
                "1",
                repositoryName,
                $"octocat/{repositoryName}",
                "octocat",
                description ?? string.Empty,
                isPrivate,
                "main",
                $"https://github.com/octocat/{repositoryName}.git",
                $"git@github.com:octocat/{repositoryName}.git",
                DateTimeOffset.UtcNow));
        }
    }

    private class MockWorkspaceTemplateUiService : IWorkspaceTemplateUiService
    {
        private readonly IWorkspaceTemplateService _templateService;

        public MockWorkspaceTemplateUiService(IWorkspaceTemplateService templateService)
        {
            _templateService = templateService;
        }

        public RemotePublishAvailability RemoteAvailability { get; set; } = new(false, null, null, null);

        public IReadOnlyList<WorkspaceTemplateDefinition> GetTemplates() => _templateService.GetAvailableTemplates();

        public WorkspaceTemplateDefinition? GetTemplate(string? templateId)
            => string.IsNullOrWhiteSpace(templateId) ? null : _templateService.GetTemplate(templateId);

        public ProjectNameValidationResult ValidateProjectName(string? projectName)
            => WorkspaceTemplateProjectNameValidator.Validate(projectName);

        public ProjectNameValidationResult ValidateRemoteRepositoryName(string? repositoryName)
            => WorkspaceTemplateProjectNameValidator.ValidateRepositoryName(repositoryName);

        public TargetPathValidationResult ValidateTargetDirectoryPath(string? targetPath, string? alreadyScaffoldedDirectoryPath = null)
            => new(!string.IsNullOrWhiteSpace(targetPath), string.IsNullOrWhiteSpace(targetPath) ? "Required" : null, false, false);

        public string ResolveDefaultTargetDirectoryPath(string projectName)
            => string.IsNullOrWhiteSpace(projectName) ? string.Empty : Path.Combine(Path.GetTempPath(), "repos", projectName.Trim());

        public Task<RemotePublishAvailability> GetRemotePublishAvailabilityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteAvailability);

        public bool CanProceed(WorkspaceTemplateSelectionModel selection, string? alreadyScaffoldedDirectoryPath = null)
        {
            if (GetTemplate(selection.SelectedTemplateId) is null ||
                !ValidateProjectName(selection.ProjectName).IsValid ||
                !ValidateTargetDirectoryPath(selection.TargetDirectoryPath, alreadyScaffoldedDirectoryPath).IsValid)
            {
                return false;
            }

            if (selection.PublishToRemoteForge)
            {
                if (!selection.InitializeGit || !selection.RemoteProviderType.HasValue)
                {
                    return false;
                }

                var repoName = string.IsNullOrWhiteSpace(selection.RemoteRepositoryName)
                    ? selection.ProjectName
                    : selection.RemoteRepositoryName;
                if (!ValidateRemoteRepositoryName(repoName).IsValid)
                {
                    return false;
                }
            }

            return true;
        }

        public IReadOnlyList<string> ResolvePreviewProjects(WorkspaceTemplateDefinition template, string? projectName)
        {
            var name = ValidateProjectName(projectName).IsValid
                ? projectName!.Trim()
                : WorkspaceTemplateConstants.ProjectNamePlaceholder;
            return template.GeneratedProjects
                .Select(p => p.Replace(WorkspaceTemplateConstants.ProjectNamePlaceholder, name, StringComparison.Ordinal))
                .ToList();
        }

        public TemplateScaffoldingRequest BuildScaffoldingRequest(WorkspaceTemplateSelectionModel selection)
            => new(
                selection.SelectedTemplateId ?? string.Empty,
                selection.ProjectName.Trim(),
                selection.TargetDirectoryPath.Trim(),
                selection.InitializeGit,
                selection.PublishToRemoteForge,
                selection.PublishToRemoteForge ? selection.RemoteProviderType : null,
                selection.RemoteIsPrivate,
                selection.RemoteDescription,
                selection.PublishToRemoteForge ? selection.RemoteRepositoryName : null);

        public GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(VcsHostingProviderType? providerType, VcsRepositorySummary? repository)
            => providerType == VcsHostingProviderType.GitHub && repository is not null
                ? new GitHubIntegrationSettings { Enabled = true, RepositoryOwner = repository.Owner, RepositoryName = repository.Name }
                : null;

        public string GetStackBadgeLabel(TemplateStackCategory category) => ".NET 10";

        public string GetArchetypeLabel(TemplateArchitectureArchetype archetype) => "Clean Architecture";
    }

    #endregion
}
