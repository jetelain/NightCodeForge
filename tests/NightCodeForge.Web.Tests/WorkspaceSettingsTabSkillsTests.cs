using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Workspaces;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkspaceSettingsTabSkillsTests : BunitContext, IDisposable
{
    private readonly WorkspaceStateService _workspaceState = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly Mock<IWslMetadataService> _wslMock = new();
    private readonly Mock<IGitMirrorSyncService> _mirrorMock = new();
    private readonly ToastService _toastService;
    private readonly string _tempWorkspaceDir;

    public WorkspaceSettingsTabSkillsTests()
    {
        _toastService = TestServices.CreateToastService();
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "ws-skills-tab-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);

        _wslMock.Setup(w => w.GetDefaultDistributionName())
            .Returns("Ubuntu");
        _wslMock.Setup(w => w.GetDefaultUserName())
            .Returns("user");

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepo);
        Services.AddSingleton(_wslMock.Object);
        Services.AddSingleton(_mirrorMock.Object);
        Services.AddSingleton(_toastService);

        var skillsService = new SkillsService();
        var bundledProvider = new BundledSkillsProvider();
        var factory = new WorkspaceSkillsServiceFactory(skillsService, bundledProvider, NullLogger<WorkspaceSkillsService>.Instance);
        Services.AddSingleton<IWorkspaceSkillsServiceFactory>(factory);
    }

    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_tempWorkspaceDir))
        {
            Directory.Delete(_tempWorkspaceDir, true);
        }
    }

    [Fact]
    public void WorkspaceSettingsTab_RendersGlobalSkillsSection_AndCandidateSkills()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = _tempWorkspaceDir,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        Assert.Contains("Global Skills Injection &amp; Stack Tooling", cut.Markup);
        Assert.Contains("csharp-nav", cut.Markup);
        Assert.Contains("mdfetch", cut.Markup);
        Assert.Contains("codecondenser", cut.Markup);

        var select = cut.Find("select[value='Inherit']");
        Assert.NotNull(select);
    }

    [Fact]
    public void WorkspaceSettingsTab_ChangingInjectionMode_UpdatesWorkspaceEntity()
    {
        var ws = new Workspace
        {
            Id = 2,
            Name = "ModeChangeRepo",
            RepositoryPath = _tempWorkspaceDir,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        // Find the injection mode select
        var select = cut.Find("#global-skills-injection-mode");
        select.Change("ForceDisabled");

        Assert.Equal(GlobalSkillsInjectionMode.ForceDisabled, ws.GlobalSkillsInjectionMode);
        Assert.Contains("Disabled via Local Workspace Override", cut.Markup);

        var updatedSelect = cut.Find("#global-skills-injection-mode");
        updatedSelect.Change("ForceEnabled");
        Assert.Equal(GlobalSkillsInjectionMode.ForceEnabled, ws.GlobalSkillsInjectionMode);
        Assert.Contains("Force Enabled", cut.Markup);
    }

    [Fact]
    public void WorkspaceSettingsTab_TogglingCandidateSkill_UpdatesDisabledGlobalSkillsJson()
    {
        var ws = new Workspace
        {
            Id = 3,
            Name = "SkillToggleRepo",
            RepositoryPath = _tempWorkspaceDir,
            GlobalSkillsInjectionMode = GlobalSkillsInjectionMode.Inherit,
            DisabledGlobalSkillsJson = null
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        // Find checkbox for csharp-nav by id
        var checkbox = cut.Find("#skill-toggle-csharp-nav");

        // Uncheck csharp-nav
        checkbox.Change(false);

        Assert.NotNull(ws.DisabledGlobalSkillsJson);
        Assert.Contains("csharp-nav", ws.DisabledGlobalSkillsJson);

        // Re-check it
        var updatedCheckbox = cut.Find("#skill-toggle-csharp-nav");
        updatedCheckbox.Change(true);

        Assert.NotNull(ws.DisabledGlobalSkillsJson);
        Assert.DoesNotContain("csharp-nav", ws.DisabledGlobalSkillsJson);
    }
}
