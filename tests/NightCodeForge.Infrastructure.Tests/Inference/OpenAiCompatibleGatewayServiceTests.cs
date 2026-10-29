using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OpenAiCompatibleGatewayServiceTests
{
    [Fact]
    public async Task ProviderTypeAndDetectProvider_ReturnOpenAiCompatible()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "https://api.openai.com/v1" }
        });
        var cache = new OpenAiModelAllowlistCache();
        var gateway = new OpenAiCompatibleGatewayService(new HttpClient(), options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleGatewayService>.Instance);

        Assert.Equal(LlmProviderType.OpenAiCompatible, gateway.ProviderType);
        var detected = await gateway.DetectProviderAsync();
        Assert.Equal(LlmProviderType.OpenAiCompatible, detected);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_ReturnsOnlineReportWithEndpointAuthority()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions { Endpoint = "https://openrouter.ai/api/v1" }
        });
        var cache = new OpenAiModelAllowlistCache();
        var gateway = new OpenAiCompatibleGatewayService(new HttpClient(), options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleGatewayService>.Instance);

        var status = await gateway.GetGatewayStatusAsync();

        Assert.True(status.IsConnected);
        Assert.Equal(LlmProviderType.OpenAiCompatible, status.ProviderType);
        Assert.Single(status.FleetTargets);
        Assert.Equal("openrouter.ai", status.FleetTargets[0].Name);
        Assert.Equal(FleetLiveness.Online, status.FleetTargets[0].Liveness);
    }

    [Fact]
    public async Task GetResidentModelsAndReclaimVram_ReturnEmptyAndTrue()
    {
        var options = Options.Create(new LlmProviderOptions());
        var cache = new OpenAiModelAllowlistCache();
        var gateway = new OpenAiCompatibleGatewayService(new HttpClient(), options, cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleGatewayService>.Instance);

        var resident = await gateway.GetResidentModelsAsync();
        Assert.Empty(resident);

        var reclaimed = await gateway.ReclaimVramAsync();
        Assert.True(reclaimed);

        var reclaimedNamed = await gateway.ReclaimVramAsync("target-1");
        Assert.True(reclaimedNamed);
    }

    [Fact]
    public async Task ResolveContextWindowAsync_FollowsPrioritySequence()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                DefaultDiscoveredContextTokens = 120000,
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "manual-model", ContextWindowTokens = 65536 }
                ]
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([
            new AvailableModelInfo("cached-with-tokens", "Cached With Tokens", ContextWindowTokens: 200000),
            new AvailableModelInfo("cached-without-tokens", "Cached Without Tokens", ContextWindowTokens: null)
        ]);

        var gateway = new OpenAiCompatibleGatewayService(new HttpClient(), Options.Create(options), cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleGatewayService>.Instance);

        // Priority 1: Manual AllowedModel override
        var ctx1 = await gateway.ResolveContextWindowAsync("manual-model");
        Assert.Equal(65536, ctx1);

        // Priority 2: Discovered metadata from cache
        var ctx2 = await gateway.ResolveContextWindowAsync("cached-with-tokens");
        Assert.Equal(200000, ctx2);

        // Priority 3: Fallback to DefaultDiscoveredContextTokens
        var ctx3 = await gateway.ResolveContextWindowAsync("cached-without-tokens");
        Assert.Equal(120000, ctx3);

        // Fallback for unlisted model
        var ctx4 = await gateway.ResolveContextWindowAsync("completely-unknown");
        Assert.Equal(120000, ctx4);
    }

    [Fact]
    public async Task ResolveContextWindowAsync_WhenModelNullAndDefaultModelIdNull_ResolvesFromDiscoveredModelsViaSelector()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            DefaultModelId = null,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                DefaultDiscoveredContextTokens = 128000
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([
            new AvailableModelInfo("expensive-model", "Expensive", ContextWindowTokens: 32000, PromptCostPerMillion: 10.0m),
            new AvailableModelInfo("cheap-model", "Cheap", ContextWindowTokens: 64000, PromptCostPerMillion: 0.10m)
        ]);

        var gateway = new OpenAiCompatibleGatewayService(new HttpClient(), Options.Create(options), cache, new OpenAiDefaultModelSelector(), NullLogger<OpenAiCompatibleGatewayService>.Instance);

        var ctx = await gateway.ResolveContextWindowAsync(null!);
        Assert.Equal(64000, ctx);
    }
}

