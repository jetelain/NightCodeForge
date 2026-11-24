using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Workspaces.Steps;
using NightCodeForge.Web.Models;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WizardTemplateSelectorTests : BunitContext
{
    private readonly Mock<IWorkspaceTemplateService> _templateServiceMock;
    private readonly Mock<IWorkspaceIntakeUiService> _intakeUiServiceMock;
    private readonly Mock<IVcsHostingProviderRegistry> _providerRegistryMock;
    private readonly WorkspaceTemplateUiService _templateUiService;

    public WizardTemplateSelectorTests()
    {
        _templateServiceMock = new Mock<IWorkspaceTemplateService>();
        _intakeUiServiceMock = new Mock<IWorkspaceIntakeUiService>();
        _providerRegistryMock = new Mock<IVcsHostingProviderRegistry>();

        _templateServiceMock.Setup(s => s.GetAvailableTemplates())
            .Returns([
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
                    "Modular .NET library",
                    "📦",
                    TemplateStackCategory.DotNet,
                    TemplateArchitectureArchetype.ClassLibrary,
                    Array.Empty<WorkspaceTemplateCommand>(),
                    ["src/{ProjectName}", "tests/{ProjectName}.Tests"])
            ]);
        _templateServiceMock.Setup(s => s.GetTemplate(It.IsAny<string>()))
            .Returns<string>(id => _templateServiceMock.Object.GetAvailableTemplates().FirstOrDefault(t => t.Id == id));

        _intakeUiServiceMock.Setup(s => s.ResolveDefaultClonePath(It.IsAny<string>()))
            .Returns<string>(name => Path.Combine(Path.GetTempPath(), "repos", name));
        _intakeUiServiceMock.Setup(s => s.ValidateCloneTargetPath(It.IsAny<string?>()))
            .Returns<string?>(p => string.IsNullOrWhiteSpace(p)
                ? new TargetPathValidationResult(false, "Target clone directory path is required.", false, false)
                : new TargetPathValidationResult(true, null, false, false));

        _templateUiService = new WorkspaceTemplateUiService(
            _templateServiceMock.Object,
            _intakeUiServiceMock.Object,
            _providerRegistryMock.Object,
            NullLogger<WorkspaceTemplateUiService>.Instance);

        Services.AddSingleton<IWorkspaceTemplateUiService>(_templateUiService);
    }

    [Fact]
    public async Task WizardTemplateSelector_SelectsTemplateAndValidatesProjectName()
    {
        var model = new WorkspaceCreationModel();
        var templateModel = new WorkspaceTemplateSelectionModel();

        var cut = Render<WizardTemplateSelector>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.TemplateModel, templateModel));

        Assert.Equal(WorkspaceTemplateConstants.DotNetCleanWebAppId, templateModel.SelectedTemplateId);

        await cut.Find($"button.template-card[data-template-id='{WorkspaceTemplateConstants.DotNetClassLibId}']")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(WorkspaceTemplateConstants.DotNetClassLibId, templateModel.SelectedTemplateId);
        Assert.Equal("📦", model.Emoji);

        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "123 invalid" });

        Assert.NotEmpty(cut.FindAll("#template-project-name-error"));
        Assert.False(_templateUiService.CanProceed(templateModel));

        await cut.Find("#template-project-name-input")
            .InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Acme.Billing" });

        Assert.Empty(cut.FindAll("#template-project-name-error"));
        Assert.Equal("Acme.Billing", model.Name);
        Assert.EndsWith("Acme.Billing", templateModel.TargetDirectoryPath);
        Assert.True(_templateUiService.CanProceed(templateModel));
    }

    [Fact]
    public async Task WizardTemplateSelector_WhenRemoteProviderConnected_ShowsPublishOptions()
    {
        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.GetAuthenticatedUsernameAsync(It.IsAny<CancellationToken>())).ReturnsAsync("octocat");
        _providerRegistryMock.Setup(r => r.GetPrimaryAuthenticatedProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerMock.Object);

        var model = new WorkspaceCreationModel();
        var templateModel = new WorkspaceTemplateSelectionModel();

        var cut = Render<WizardTemplateSelector>(parameters => parameters
            .Add(p => p.Model, model)
            .Add(p => p.TemplateModel, templateModel));

        Assert.NotEmpty(cut.FindAll("#template-remote-publish-card"));
        Assert.Contains("@octocat", cut.Markup);

        await cut.Find("#template-publish-toggle")
            .ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        Assert.True(templateModel.PublishToRemoteForge);
        Assert.Equal(VcsHostingProviderType.GitHub, templateModel.RemoteProviderType);
        Assert.NotEmpty(cut.FindAll("#template-remote-repo-name-input"));

        await cut.Find("#template-visibility-public")
            .ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.False(templateModel.RemoteIsPrivate);
    }

    [Fact]
    public void WizardTemplateSelector_WhenNoRemoteProviderConnected_HidesPublishCard()
    {
        _providerRegistryMock.Setup(r => r.GetPrimaryAuthenticatedProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IVcsHostingProvider?)null);

        var cut = Render<WizardTemplateSelector>(parameters => parameters
            .Add(p => p.Model, new WorkspaceCreationModel())
            .Add(p => p.TemplateModel, new WorkspaceTemplateSelectionModel()));

        Assert.Empty(cut.FindAll("#template-remote-publish-card"));
    }

    [Fact]
    public void WizardTemplateSelector_WhenScaffoldedDirectoryProvided_AllowsExistingDirectoryAndShowsHint()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NCF_SelectorScaffolded_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "README.md"), "# Existing");

        try
        {
            _intakeUiServiceMock.Setup(s => s.ValidateCloneTargetPath(It.IsAny<string?>()))
                .Returns(new TargetPathValidationResult(false, "Directory is not empty.", true, true));

            var model = new WorkspaceCreationModel();
            var templateModel = new WorkspaceTemplateSelectionModel
            {
                SelectedTemplateId = WorkspaceTemplateConstants.DotNetClassLibId,
                ProjectName = "ExistingApp",
                TargetDirectoryPath = tempDir,
                IsTargetDirectoryManuallyEdited = true
            };

            var cut = Render<WizardTemplateSelector>(parameters => parameters
                .Add(p => p.Model, model)
                .Add(p => p.TemplateModel, templateModel)
                .Add(p => p.ScaffoldedTargetDirectoryPath, tempDir));

            Assert.Empty(cut.FindAll("#template-target-path-error"));
            Assert.NotEmpty(cut.FindAll("#template-already-scaffolded-hint"));
            Assert.True(_templateUiService.CanProceed(templateModel, tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
