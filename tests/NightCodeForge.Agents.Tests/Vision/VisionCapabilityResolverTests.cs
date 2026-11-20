using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Vision;

public class VisionCapabilityResolverTests
{
    [Fact]
    public void OllamaCapabilityResolver_RespectsProfileOverrideBeforeCacheAndFallback()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        cache.SetVisionCapability("cached-vision", true);
        cache.SetVisionCapability("overridden-model", true);

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.Ollama,
            DefaultModelId = "cached-vision",
            ModelProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["overridden-"] = new() { SupportsVision = false },
                ["force-vision-"] = new() { SupportsVision = true }
            }
        });

        var resolver = new OllamaCapabilityResolver(options, cache);

        Assert.True(resolver.SupportsVision("cached-vision"));
        Assert.True(resolver.SupportsVision(null)); // falls back to DefaultModelId
        Assert.False(resolver.SupportsVision("overridden-model")); // static false overrides cached true
        Assert.True(resolver.SupportsVision("force-vision-test")); // static true overrides uncached
        Assert.False(resolver.SupportsVision("unknown-text-model"));
    }

    [Fact]
    public void OpenAiCapabilityResolver_AppliesProfileThenAllowedModelThenCacheThenDiscovery()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        cache.SetVisionCapability("cache-vision", true);

        var allowlistMock = new Mock<IOpenAiModelAllowlistCache>();
        allowlistMock.Setup(a => a.TryGetModel("discovered-vl", out It.Ref<AvailableModelInfo?>.IsAny))
            .Returns((string _, out AvailableModelInfo? info) =>
            {
                info = new AvailableModelInfo("discovered-vl", "discovered-vl", 128000, SupportsVision: true);
                return true;
            });
        allowlistMock.Setup(a => a.TryGetModel("allowed-vl", out It.Ref<AvailableModelInfo?>.IsAny))
            .Returns((string _, out AvailableModelInfo? info) =>
            {
                info = new AvailableModelInfo("allowed-vl", "allowed-vl", 128000, SupportsVision: false);
                return true;
            });

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            DefaultModelId = "discovered-vl",
            ModelProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["profile-forced-text"] = new() { SupportsVision = false }
            },
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "profile-forced-text", ContextWindowTokens = 64000, SupportsVision = true },
                    new OpenAiAllowedModel { ModelId = "allowed-vl", ContextWindowTokens = 64000, SupportsVision = true }
                ]
            }
        });

        var resolver = new OpenAiCapabilityResolver(options, allowlistMock.Object, cache);

        Assert.False(resolver.SupportsVision("profile-forced-text")); // profile wins over AllowedModel
        Assert.True(resolver.SupportsVision("allowed-vl")); // AllowedModel wins over discovered cache
        Assert.True(resolver.SupportsVision("cache-vision")); // ILlmContextWindowCache hit
        Assert.True(resolver.SupportsVision("discovered-vl")); // IOpenAiModelAllowlistCache discovery hit
        Assert.True(resolver.SupportsVision(null)); // DefaultModelId fallback
        Assert.False(resolver.SupportsVision("plain-coder"));
    }
}
