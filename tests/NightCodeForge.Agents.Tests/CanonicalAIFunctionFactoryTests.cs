using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tests;

public class CanonicalAIFunctionFactoryTests
{
    [Fact]
    public void CreateTools_DoesNotAdvertiseWebSearch_WhenWebSearchIsNotConfigured()
    {
        var unconfiguredSearch = new MockWebSearchService(isConfigured: false);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: new WorkspacePathResolver(),
            webSearchService: unconfiguredSearch);

        Assert.False(tools.IsWebSearchConfigured);

        // Even when permittedTools specifically requests web_search or is null
        var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
        Assert.DoesNotContain(allTools, t => t.Name == CanonicalToolNames.WebSearch);

        var permittedExplicitly = CanonicalAIFunctionFactory.CreateTools(tools, new[] { "read", "bash", "web_search" });
        Assert.DoesNotContain(permittedExplicitly, t => t.Name == CanonicalToolNames.WebSearch);
        Assert.Contains(permittedExplicitly, t => t.Name == "read");
        Assert.Contains(permittedExplicitly, t => t.Name == "bash");
    }

    [Fact]
    public void CreateTools_AdvertisesWebSearch_WhenWebSearchIsConfigured()
    {
        var configuredSearch = new MockWebSearchService(isConfigured: true);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: new WorkspacePathResolver(),
            webSearchService: configuredSearch);

        Assert.True(tools.IsWebSearchConfigured);

        var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
        Assert.Contains(allTools, t => t.Name == CanonicalToolNames.WebSearch);

        var permittedExplicitly = CanonicalAIFunctionFactory.CreateTools(tools, new[] { "read", "web_search" });
        Assert.Contains(permittedExplicitly, t => t.Name == CanonicalToolNames.WebSearch);
        Assert.Equal(2, permittedExplicitly.Count);
    }

    [Fact]
    public async Task CanonicalTools_WebSearchAsync_ReturnsDisabledMessage_WhenUnconfigured()
    {
        var unconfiguredSearch = new MockWebSearchService(isConfigured: false);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: new WorkspacePathResolver(),
            webSearchService: unconfiguredSearch);

        var result = await tools.WebSearchAsync("how to run local models");

        Assert.Contains("Web search is disabled because no API key is configured", result);
    }

    [Fact]
    public async Task CanonicalTools_WebSearchAsync_ExecutesSearch_WhenConfigured()
    {
        var configuredSearch = new MockWebSearchService(
            searchHandler: (q, max) => Task.FromResult(new WebSearchResponse(new List<WebSearchResultItem>
            {
                new("Result 1", "https://example.com/1", "Sample content 1")
            })),
            isConfigured: true);

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: new WorkspacePathResolver(),
            webSearchService: configuredSearch);

        var result = await tools.WebSearchAsync("test query");

        Assert.Contains("Web search results for: 'test query'", result);
        Assert.Contains("Result 1", result);
        Assert.Contains("https://example.com/1", result);
    }
}
