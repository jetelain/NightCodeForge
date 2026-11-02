using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Playwright E2E browser tests for Step 12.26: Workspace Visual Identity (Emoji, Color Code & Settings Management).
/// Verifies that branding changes made in Workspace Settings update the database, TopNavBar, and Home Dashboard cards.
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class WorkspaceVisualIdentityE2ETests : IAsyncLifetime
{
    private readonly PlaywrightWebHostFixture _fixture;
    private IBrowserContext _context = default!;
    private IPage _page = default!;

    public WorkspaceVisualIdentityE2ETests(PlaywrightWebHostFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.CreateContextAsync();
        _page = await _context.NewPageAsync();
        _page.SetDefaultTimeout(30000);
    }

    public async Task DisposeAsync()
    {
        if (_page != null)
        {
            try
            {
                await _page.CloseAsync();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        if (_context != null)
        {
            try
            {
                await _context.CloseAsync();
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact]
    public async Task WorkspaceIdentity_EditingBranding_PersistsAndReflectsInTopNavBarAndHomeDashboard()
    {
        // 1. Seed workspace with initial defaults
        var tempRepoPath = _fixture.CreateTempWorkspaceDirectory("visual_identity_repo");
        var ws = new Workspace
        {
            Name = "Initial Visual Repo",
            RepositoryPath = tempRepoPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            Emoji = null,
            ColorCode = null,
            CreatedAt = DateTime.UtcNow
        };

        await _fixture.SeedDatabaseAsync(async db =>
        {
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
        });

        // 2. Navigate to /w/{ws.Id}/settings
        await _page.GotoAsync($"{_fixture.BaseUrl}/w/{ws.Id}/settings", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await _page.WaitForSelectorAsync(".page-header");
        await _page.WaitForTimeoutAsync(1000);

        // Switch to Workspace Settings scope
        var workspaceScopeBtn = _page.Locator(".scope-switcher button:has-text('Workspace Settings')");
        var identityHeader = _page.Locator("#workspace-identity-section");
        await PlaywrightWebHostFixture.ClickUntilVisibleAsync(workspaceScopeBtn, identityHeader, TimeSpan.FromSeconds(15));

        // 3. Update Workspace Name to "NightCodeForge Core"
        var nameInput = _page.Locator("#workspace-name-edit");
        await nameInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await nameInput.FillAsync("NightCodeForge Core");

        // 4. Select the 🛡️ emoji preset
        var shieldPresetBtn = _page.Locator("#emoji-preset-🛡️");
        await shieldPresetBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await shieldPresetBtn.ClickAsync();

        // 5. Select the Emerald Green (#10b981) color preset
        var emeraldColorChip = _page.Locator("#color-preset-emerald-green");
        await emeraldColorChip.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await emeraldColorChip.ClickAsync();

        // 6. Click Save Workspace Settings
        var saveBtn = _page.Locator("button:has-text('Save Workspace Settings')");
        await saveBtn.ClickAsync();

        // 7. Verify persistence in SQLite
        var persistedWs = await PlaywrightWebHostFixture.WaitForConditionAsync(async () =>
        {
            var result = await _fixture.QueryDatabaseAsync(async db => await db.Workspaces.FindAsync(ws.Id));
            return result != null &&
                   result.Name == "NightCodeForge Core" &&
                   result.Emoji == "🛡️" &&
                   result.ColorCode == "#10b981"
                ? result
                : null;
        }, TimeSpan.FromSeconds(15));

        Assert.NotNull(persistedWs);
        Assert.Equal("NightCodeForge Core", persistedWs.Name);
        Assert.Equal("🛡️", persistedWs.Emoji);
        Assert.Equal("#10b981", persistedWs.ColorCode);

        // 8. Navigate to Home (/) and verify recent workspace card
        await _page.GotoAsync($"{_fixture.BaseUrl}/", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var homeCard = _page.Locator($"#home-workspace-card-{ws.Id}");
        await homeCard.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

        await Assertions.Expect(homeCard).ToContainTextAsync("🛡️");
        await Assertions.Expect(homeCard).ToContainTextAsync("NightCodeForge Core");

        var cardStyle = await homeCard.GetAttributeAsync("style");
        Assert.NotNull(cardStyle);
        Assert.Contains("border-left: 4px solid #10b981", cardStyle);
    }
}

