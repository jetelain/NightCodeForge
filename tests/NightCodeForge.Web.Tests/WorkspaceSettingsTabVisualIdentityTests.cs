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

public class WorkspaceSettingsTabVisualIdentityTests : BunitContext, IDisposable
{
    private readonly WorkspaceStateService _workspaceState = new();
    private readonly Mock<IWorkspaceRepository> _workspaceRepoMock = new();
    private readonly Mock<IWslMetadataService> _wslMock = new();
    private readonly Mock<IGitMirrorSyncService> _mirrorMock = new();
    private readonly ToastService _toastService;
    private readonly string _tempWorkspaceDir;

    public WorkspaceSettingsTabVisualIdentityTests()
    {
        _toastService = TestServices.CreateToastService();
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "ws-identity-tab-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);

        _wslMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        _wslMock.Setup(w => w.GetDefaultUserName()).Returns("user");

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepoMock.Object);
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
    public void WorkspaceSettingsTab_RendersWorkspaceIdentitySection_WithDefaults()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Default Workspace",
            RepositoryPath = _tempWorkspaceDir,
            Emoji = null,
            ColorCode = null
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        Assert.Contains("Workspace Identity &amp; Branding", cut.Markup);
        var nameInput = cut.Find("#workspace-name-edit");
        Assert.Equal("Default Workspace", nameInput.GetAttribute("value"));

        var emojiInput = cut.Find("#workspace-emoji-input");
        Assert.True(string.IsNullOrEmpty(emojiInput.GetAttribute("value")));

        // Preview navbar mock contains default folder emoji
        var previewNav = cut.Find("#preview-navbar-mock");
        Assert.Contains(WorkspacePresentationHelper.DefaultEmoji, previewNav.TextContent);
        Assert.Contains("Default Workspace", previewNav.TextContent);
    }

    [Fact]
    public void WorkspaceSettingsTab_SelectingEmojiAndColorPresets_UpdatesModelAndPreview()
    {
        var ws = new Workspace
        {
            Id = 2,
            Name = "OllamaRouter",
            RepositoryPath = _tempWorkspaceDir
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        // Click on 🚀 emoji preset button
        var rocketButton = cut.Find("#emoji-preset-🚀");
        rocketButton.Click();

        Assert.Equal("🚀", ws.Emoji);

        // Click on Emerald Green color preset chip
        var emeraldChip = cut.Find("#color-preset-emerald-green");
        emeraldChip.Click();

        Assert.Equal("#10b981", ws.ColorCode);

        // Verify live preview reflects the changes
        var preview = cut.Find("#preview-navbar-mock");
        Assert.Contains("🚀", preview.TextContent);
        Assert.Contains("OllamaRouter", preview.TextContent);
    }

    [Fact]
    public async Task WorkspaceSettingsTab_ClickingSave_CallsUpdateAsyncAndNotifiesWorkspaceState()
    {
        var ws = new Workspace
        {
            Id = 3,
            Name = "Initial Name",
            RepositoryPath = _tempWorkspaceDir,
            Emoji = null,
            ColorCode = null
        };
        _workspaceState.SetActiveWorkspace(ws);

        _workspaceRepoMock.Setup(r => r.UpdateAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var stateChangedFired = false;
        _workspaceState.OnWorkspaceChanged += () => stateChangedFired = true;

        var cut = Render<WorkspaceSettingsTab>();

        // Update name
        var nameInput = cut.Find("#workspace-name-edit");
        nameInput.Input("Updated Workspace Name");

        // Pick 🛡️ emoji
        var shieldBtn = cut.Find("#emoji-preset-🛡️");
        shieldBtn.Click();

        // Pick Violet Purple color
        var violetChip = cut.Find("#color-preset-violet-purple");
        violetChip.Click();

        // Click save button (second button in header)
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Workspace Settings"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _workspaceRepoMock.Verify(r => r.UpdateAsync(It.Is<Workspace>(w =>
            w.Id == 3 &&
            w.Name == "Updated Workspace Name" &&
            w.Emoji == "🛡️" &&
            w.ColorCode == "#8b5cf6"
        ), It.IsAny<CancellationToken>()), Times.Once);

        Assert.True(stateChangedFired);
        Assert.Equal("Updated Workspace Name", _workspaceState.ActiveWorkspace?.Name);
        Assert.Equal("🛡️", _workspaceState.ActiveWorkspace?.Emoji);
        Assert.Equal("#8b5cf6", _workspaceState.ActiveWorkspace?.ColorCode);
    }

    [Fact]
    public async Task WorkspaceSettingsTab_WhenNameEmpty_SaveFailsValidationWithoutUpdatingRepo()
    {
        var ws = new Workspace
        {
            Id = 4,
            Name = "Valid Name",
            RepositoryPath = _tempWorkspaceDir
        };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<WorkspaceSettingsTab>();

        var nameInput = cut.Find("#workspace-name-edit");
        nameInput.Input("");

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Workspace Settings"));
        await saveBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        _workspaceRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

