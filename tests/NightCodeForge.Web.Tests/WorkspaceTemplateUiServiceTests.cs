using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Models;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceTemplateUiServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IWorkspaceTemplateService> _templateServiceMock;
    private readonly Mock<IWorkspaceIntakeUiService> _intakeUiServiceMock;
    private readonly Mock<IVcsHostingProviderRegistry> _providerRegistryMock;
    private readonly WorkspaceTemplateUiService _service;

    public WorkspaceTemplateUiServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NCF_TemplateUiSvcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _templateServiceMock = new Mock<IWorkspaceTemplateService>();
        _templateServiceMock.Setup(s => s.GetTemplate(WorkspaceTemplateConstants.DotNetClassLibId))
            .Returns(new WorkspaceTemplateDefinition(
                WorkspaceTemplateConstants.DotNetClassLibId,
                "Class Library",
                "Modular library",
                "📦",
                TemplateStackCategory.DotNet,
                TemplateArchitectureArchetype.ClassLibrary,
                Array.Empty<WorkspaceTemplateCommand>(),
                ["src/{ProjectName}", "tests/{ProjectName}.Tests"]));

        _intakeUiServiceMock = new Mock<IWorkspaceIntakeUiService>();
        _intakeUiServiceMock.Setup(s => s.ValidateCloneTargetPath(It.IsAny<string?>()))
            .Returns<string?>(path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new TargetPathValidationResult(false, "Target clone directory path is required.", false, false);
                }

                var full = Path.GetFullPath(path.Trim());
                if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                {
                    return new TargetPathValidationResult(false, "Directory is not empty.", true, true);
                }

                return new TargetPathValidationResult(true, null, Directory.Exists(full), false);
            });
        _intakeUiServiceMock.Setup(s => s.BuildGitHubSettingsIfApplicable(It.IsAny<VcsHostingProviderType?>(), It.IsAny<VcsRepositorySummary>()))
            .Returns<VcsHostingProviderType?, VcsRepositorySummary>((type, repo) =>
                type == VcsHostingProviderType.GitHub
                    ? new GitHubIntegrationSettings
                    {
                        Enabled = true,
                        RepositoryOwner = repo.Owner,
                        RepositoryName = repo.Name
                    }
                    : null);

        _providerRegistryMock = new Mock<IVcsHostingProviderRegistry>();

        _service = new WorkspaceTemplateUiService(
            _templateServiceMock.Object,
            _intakeUiServiceMock.Object,
            _providerRegistryMock.Object,
            NullLogger<WorkspaceTemplateUiService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup
        }
    }

    [Fact]
    public void CanProceed_WhenPublishEnabledAndGitDisabled_ReturnsFalse()
    {
        var selection = new WorkspaceTemplateSelectionModel
        {
            SelectedTemplateId = WorkspaceTemplateConstants.DotNetClassLibId,
            ProjectName = "ValidApp",
            TargetDirectoryPath = Path.Combine(_tempRoot, "ValidApp"),
            InitializeGit = false,
            PublishToRemoteForge = true,
            RemoteProviderType = VcsHostingProviderType.GitHub,
            RemoteRepositoryName = "ValidApp"
        };

        Assert.False(_service.CanProceed(selection));
    }

    [Fact]
    public void CanProceed_WhenPublishEnabledAndRemoteRepositoryNameInvalid_ReturnsFalse()
    {
        var selection = new WorkspaceTemplateSelectionModel
        {
            SelectedTemplateId = WorkspaceTemplateConstants.DotNetClassLibId,
            ProjectName = "ValidApp",
            TargetDirectoryPath = Path.Combine(_tempRoot, "ValidApp"),
            InitializeGit = true,
            PublishToRemoteForge = true,
            RemoteProviderType = VcsHostingProviderType.GitHub,
            RemoteRepositoryName = "123 invalid"
        };

        Assert.False(_service.CanProceed(selection));
    }

    [Fact]
    public async Task CanProceed_WhenDirectoryAlreadyScaffolded_AllowsMatchingNonEmptyDirectoryOnly()
    {
        var scaffoldedDir = Path.Combine(_tempRoot, "ScaffoldedApp");
        Directory.CreateDirectory(scaffoldedDir);
        await File.WriteAllTextAsync(Path.Combine(scaffoldedDir, "README.md"), "# ScaffoldedApp");

        var otherNonEmptyDir = Path.Combine(_tempRoot, "OtherNonEmptyApp");
        Directory.CreateDirectory(otherNonEmptyDir);
        await File.WriteAllTextAsync(Path.Combine(otherNonEmptyDir, "README.md"), "# Other");

        var matchingSelection = new WorkspaceTemplateSelectionModel
        {
            SelectedTemplateId = WorkspaceTemplateConstants.DotNetClassLibId,
            ProjectName = "ScaffoldedApp",
            TargetDirectoryPath = scaffoldedDir + Path.DirectorySeparatorChar
        };

        Assert.True(_service.CanProceed(matchingSelection, scaffoldedDir));
        Assert.False(_service.CanProceed(matchingSelection));

        var otherSelection = new WorkspaceTemplateSelectionModel
        {
            SelectedTemplateId = WorkspaceTemplateConstants.DotNetClassLibId,
            ProjectName = "ScaffoldedApp",
            TargetDirectoryPath = otherNonEmptyDir
        };

        Assert.False(_service.CanProceed(otherSelection, scaffoldedDir));
    }

    [Fact]
    public async Task GetRemotePublishAvailabilityAsync_WhenUsernameLookupFails_ReturnsAvailableWithNullUsername()
    {
        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.GetAuthenticatedUsernameAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Temporary network glitch"));

        _providerRegistryMock.Setup(r => r.GetPrimaryAuthenticatedProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerMock.Object);

        var availability = await _service.GetRemotePublishAvailabilityAsync();

        Assert.True(availability.IsAvailable);
        Assert.Equal(VcsHostingProviderType.GitHub, availability.ProviderType);
        Assert.Equal("GitHub", availability.ProviderDisplayName);
        Assert.Null(availability.AuthenticatedUsername);
    }

    [Fact]
    public async Task GetRemotePublishAvailabilityAsync_WhenCancelled_RethrowsOperationCanceledException()
    {
        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.GetAuthenticatedUsernameAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        _providerRegistryMock.Setup(r => r.GetPrimaryAuthenticatedProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerMock.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => _service.GetRemotePublishAvailabilityAsync());
    }

    [Fact]
    public void ResolvePreviewProjects_SubstitutesValidNameAndFallsBackOnInvalidOrEmpty()
    {
        var template = _templateServiceMock.Object.GetTemplate(WorkspaceTemplateConstants.DotNetClassLibId)!;

        var validPreview = _service.ResolvePreviewProjects(template, "  Acme.Kernel  ");
        Assert.Equal(["src/Acme.Kernel", "tests/Acme.Kernel.Tests"], validPreview);

        var invalidPreview = _service.ResolvePreviewProjects(template, "123 invalid");
        Assert.Equal(["src/{ProjectName}", "tests/{ProjectName}.Tests"], invalidPreview);

        var emptyPreview = _service.ResolvePreviewProjects(template, "   ");
        Assert.Equal(["src/{ProjectName}", "tests/{ProjectName}.Tests"], emptyPreview);
    }

    [Fact]
    public void BuildGitHubSettingsIfApplicable_ReturnsSettingsOnlyForGitHubWithNonNullRepository()
    {
        var repo = new VcsRepositorySummary(
            "1",
            "MyRepo",
            "octocat/MyRepo",
            "octocat",
            string.Empty,
            true,
            "main",
            "https://github.com/octocat/MyRepo.git",
            "git@github.com:octocat/MyRepo.git",
            DateTimeOffset.UtcNow);

        var githubSettings = _service.BuildGitHubSettingsIfApplicable(VcsHostingProviderType.GitHub, repo);
        Assert.NotNull(githubSettings);
        Assert.True(githubSettings.Enabled);
        Assert.Equal("octocat", githubSettings.RepositoryOwner);
        Assert.Equal("MyRepo", githubSettings.RepositoryName);

        Assert.Null(_service.BuildGitHubSettingsIfApplicable(VcsHostingProviderType.GitLab, repo));
        Assert.Null(_service.BuildGitHubSettingsIfApplicable(VcsHostingProviderType.GitHub, null));
    }
}
