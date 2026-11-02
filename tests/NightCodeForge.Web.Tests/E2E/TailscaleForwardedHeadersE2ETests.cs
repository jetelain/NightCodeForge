using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Verifies the Blazor InteractiveServer circuit connects and navigates behind simulated Tailscale
/// reverse-proxy headers (X-Forwarded-Proto / X-Forwarded-For). Zero LLM calls.
/// </summary>
[Collection("PlaywrightUiE2E")]
[Trait("Category", "E2E")]
public class TailscaleForwardedHeadersE2ETests
{
    private const string ForwardedProtoHeader = "X-Forwarded-Proto";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string TailnetClientAddress = "100.64.0.10";
    private const int PageTimeoutMs = 30000;

    private readonly PlaywrightWebHostFixture _fixture;

    public TailscaleForwardedHeadersE2ETests(PlaywrightWebHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task BlazorCircuit_ConnectsAndNavigates_BehindForwardedHeaders()
    {
        await using var context = await _fixture.CreateContextAsync(extraHttpHeaders: new Dictionary<string, string>
        {
            [ForwardedProtoHeader] = "https",
            [ForwardedForHeader] = TailnetClientAddress
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(PageTimeoutMs);

        var response = await page.GotoAsync(_fixture.BaseUrl);
        Assert.NotNull(response);
        Assert.True(response!.Ok, $"Unexpected status {response.Status}");

        // Circuit connected: interactive navigation works (the settings link is rendered by the interactive shell).
        var settingsLink = page.Locator("a.forge-nav-link[href*='settings']");
        await settingsLink.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await settingsLink.ClickAsync();
        await page.WaitForURLAsync(new Regex(@"/settings"), new PageWaitForURLOptions { Timeout = 15000 });
        Assert.Contains("/settings", page.Url);
    }
}

