using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OllamaCapabilityResolverTests
{
    private static OllamaCapabilityResolver CreateResolver(LlmProviderOptions? options = null, ILlmContextWindowCache? cache = null) =>
        new(Options.Create(options ?? new LlmProviderOptions()), cache ?? EmptyLlmContextWindowCache.Instance);

    [Fact]
    public void Constructor_WhenOptionsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OllamaCapabilityResolver(null!, EmptyLlmContextWindowCache.Instance));
    }

    [Fact]
    public void Constructor_WhenCacheNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OllamaCapabilityResolver(Options.Create(new LlmProviderOptions()), null!));
    }

    [Theory]
    [InlineData("Qwen3.8-27b")]
    [InlineData("qwen3.8-27b")]
    [InlineData("Qwen3.8-27B:latest")]
    [InlineData("qwen-3.8-27b")]
    public void ResolveReasoningEffort_ForQwen38_MapsHighToXhigh(string modelId)
    {
        var resolver = CreateResolver(new LlmProviderOptions());

        // When "high" is requested, Qwen 3.8 profile EffortMap explicitly maps it to "xhigh"
        var resolved = resolver.ResolveReasoningEffort(modelId, "high");

        Assert.Equal("xhigh", resolved);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("xhigh", "xhigh")]
    public void ResolveReasoningEffort_ForQwen38_PreservesSupportedEfforts(string requested, string expected)
    {
        var resolver = CreateResolver(new LlmProviderOptions());

        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", requested);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForQwen38_WhenEffortNullOrEmpty_DefaultsToXhigh()
    {
        var options = new LlmProviderOptions { ReasoningEffort = null };
        var resolver = CreateResolver(options);

        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", null);

        // Qwen 3.8 default effort in profile is "xhigh"
        Assert.Equal("xhigh", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForQwen38_WhenUnsupportedEffortRequested_FindsClosestRank()
    {
        var resolver = CreateResolver(new LlmProviderOptions());

        // "minimal" is rank 0. Supported in Qwen3.8 are low (1), medium (2), xhigh (4). Closest is "low".
        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", "minimal");

        Assert.Equal("low", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenModelHasNoProfile_UsesRequestedOrOptionsEffort()
    {
        var options = new LlmProviderOptions { ReasoningEffort = "low" };
        var resolver = CreateResolver(options);

        var resolvedExplicit = resolver.ResolveReasoningEffort("llama3.3:70b", "medium");
        Assert.Equal("medium", resolvedExplicit);

        var resolvedDefault = resolver.ResolveReasoningEffort("llama3.3:70b", null);
        Assert.Equal("low", resolvedDefault);
    }

    [Fact]
    public void BuildReasoningProperties_ForQwen38_IncludesThinkTrueAndMappedReasoningEffort()
    {
        var resolver = CreateResolver(new LlmProviderOptions());

        var props = resolver.BuildReasoningProperties("Qwen3.8-27b", "high");

        Assert.True(props.ContainsKey("think"));
        Assert.Equal("xhigh", props["think"]);
    }

    [Fact]
    public void CreateChatOptions_WithAgentProfileAndQwen38_SetsExpectedChatOptions()
    {
        var options = new LlmProviderOptions { DefaultModelId = "Qwen3.8-27b" };
        var resolver = CreateResolver(options);

        var profile = new AgentProfile
        {
            ModelName = "Qwen3.8-27b",
            Temperature = 0.6,
            ThinkingEffort = ThinkingEffort.High
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal("Qwen3.8-27b", chatOptions.ModelId);
        Assert.Equal(0.6f, chatOptions.Temperature);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal("xhigh", chatOptions.AdditionalProperties["think"]);
    }

    [Fact]
    public void CreateChatOptions_WhenThinkingEffortNone_DisablesThinkAndSuppressesReasoningEffort()
    {
        var options = new LlmProviderOptions { DefaultModelId = "Qwen3.8-27b" };
        var resolver = CreateResolver(options);

        var profile = new AgentProfile
        {
            ModelName = "Qwen3.8-27b",
            Temperature = 0.2,
            ThinkingEffort = ThinkingEffort.None
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal("Qwen3.8-27b", chatOptions.ModelId);
        Assert.Equal(0.2f, chatOptions.Temperature);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal(false, chatOptions.AdditionalProperties["think"]);
    }

    [Theory]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.CustomBudget, "xhigh")] // CustomBudget -> "high" -> Qwen3.8 maps to "xhigh"
    public void CreateChatOptions_WithVariousThinkingEfforts_MapsCorrectly(ThinkingEffort effort, string expectedReasoningEffort)
    {
        var resolver = CreateResolver(new LlmProviderOptions());

        var profile = new AgentProfile
        {
            ModelName = "Qwen3.8-27b",
            ThinkingEffort = effort
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal(expectedReasoningEffort, chatOptions.AdditionalProperties?["think"]);
    }

    [Fact]
    public void CreateChatOptions_WhenProfileNull_FallsBackToDefaultModelAndConfiguredEffort()
    {
        var options = new LlmProviderOptions
        {
            DefaultModelId = "Qwen3.8-27b",
            ReasoningEffort = "low"
        };
        var resolver = CreateResolver(options);

        var chatOptions = resolver.CreateChatOptions(null);

        Assert.Equal("Qwen3.8-27b", chatOptions.ModelId);
        Assert.Null(chatOptions.Temperature);
        Assert.Equal("low", chatOptions.AdditionalProperties?["think"]);
    }

    [Fact]
    public void CreateChatOptions_WithDynamicallyDiscoveredModel_MapsHighToXhigh()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var dynamicModel = "custom-reasoner:latest";
        cache.SetThinkingCapability(dynamicModel, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["low", "medium", "xhigh"],
            SupportsBooleanOnly: false,
            DefaultLevel: "medium"));

        var resolver = CreateResolver(new LlmProviderOptions(), cache);

        var profile = new AgentProfile
        {
            ModelName = dynamicModel,
            ThinkingEffort = ThinkingEffort.High
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal("xhigh", chatOptions.AdditionalProperties["think"]);
    }

    [Fact]
    public void CreateChatOptions_WithCachedBooleanOnlyModel_SetsThinkTrueBoolean()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var booleanModel = "deepseek-boolean:latest";
        cache.SetThinkingCapability(booleanModel, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: [],
            SupportsBooleanOnly: true,
            DefaultLevel: null));

        var resolver = CreateResolver(new LlmProviderOptions(), cache);

        var profileMedium = new AgentProfile
        {
            ModelName = booleanModel,
            ThinkingEffort = ThinkingEffort.Medium
        };
        var optionsMedium = resolver.CreateChatOptions(profileMedium);
        Assert.NotNull(optionsMedium.AdditionalProperties);
        Assert.Equal(true, optionsMedium.AdditionalProperties["think"]);

        var profileHigh = new AgentProfile
        {
            ModelName = booleanModel,
            ThinkingEffort = ThinkingEffort.High
        };
        var optionsHigh = resolver.CreateChatOptions(profileHigh);
        Assert.NotNull(optionsHigh.AdditionalProperties);
        Assert.Equal(true, optionsHigh.AdditionalProperties["think"]);
    }

    [Fact]
    public void CreateChatOptions_WithCachedNonThinkingModel_OmitsThinkProperty()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var nonThinkingModel = "mistral-standard:latest";
        cache.SetThinkingCapability(nonThinkingModel, ModelThinkingCapability.None);

        var resolver = CreateResolver(new LlmProviderOptions(), cache);

        var profile = new AgentProfile
        {
            ModelName = nonThinkingModel,
            ThinkingEffort = ThinkingEffort.High
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        if (chatOptions.AdditionalProperties != null)
        {
            Assert.False(chatOptions.AdditionalProperties.ContainsKey("think"));
        }
    }

    [Fact]
    public void CreateChatOptions_WhenProfileNullAndModelCached_RespectsConfiguredReasoningEffort()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var dynamicModel = "custom-reasoner:latest";
        cache.SetThinkingCapability(dynamicModel, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["low", "medium", "xhigh"],
            SupportsBooleanOnly: false,
            DefaultLevel: "medium"));

        var options = new LlmProviderOptions
        {
            DefaultModelId = dynamicModel,
            ReasoningEffort = "high"
        };
        var resolver = CreateResolver(options, cache);

        var chatOptions = resolver.CreateChatOptions(null);

        Assert.Equal(dynamicModel, chatOptions.ModelId);
        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal("xhigh", chatOptions.AdditionalProperties["think"]);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenRequestedEffortWhitespaceAndModelCached_UsesOptionsReasoningEffort()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var dynamicModel = "custom-reasoner:latest";
        cache.SetThinkingCapability(dynamicModel, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["low", "medium", "xhigh"],
            SupportsBooleanOnly: false,
            DefaultLevel: "medium"));

        var options = new LlmProviderOptions { ReasoningEffort = "low" };
        var resolver = CreateResolver(options, cache);

        var resolved = resolver.ResolveReasoningEffort(dynamicModel, "   ");

        Assert.Equal("low", resolved);
    }

    [Fact]
    public void LegacyBooleanOnlyQwen38_UsesStaticProfileToMapHighToXhigh()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var legacyQwen38 = "Qwen3.8-27B:latest";
        // Simulate older Ollama server returning only capabilities: ["thinking"] -> SupportsBooleanOnly = true, StringLevels = []
        cache.SetThinkingCapability(legacyQwen38, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: [],
            SupportsBooleanOnly: true,
            DefaultLevel: null));

        var resolver = CreateResolver(new LlmProviderOptions(), cache);
        var profile = new AgentProfile
        {
            ModelName = legacyQwen38,
            ThinkingEffort = ThinkingEffort.High
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.NotNull(chatOptions.AdditionalProperties);
        Assert.Equal("xhigh", chatOptions.AdditionalProperties["think"]);
    }

    [Fact]
    public void BuildReasoningProperties_WhenRequestedEffortIsFalseOrNone_SetsThinkFalseBoolean()
    {
        var cache = new LlmContextWindowCache(new MemoryCache(new MemoryCacheOptions()));
        var dynamicModel = "custom-reasoner:latest";
        cache.SetThinkingCapability(dynamicModel, new ModelThinkingCapability(
            SupportsThinking: true,
            StringLevels: ["low", "medium", "xhigh"],
            SupportsBooleanOnly: false,
            DefaultLevel: "medium"));

        var resolver = CreateResolver(new LlmProviderOptions(), cache);

        var propsFalse = resolver.BuildReasoningProperties(dynamicModel, "false");
        Assert.Equal(false, propsFalse["think"]);

        var propsNone = resolver.BuildReasoningProperties(dynamicModel, "none");
        Assert.Equal(false, propsNone["think"]);
    }

    [Fact]
    public void CreateChatOptions_ResolvesMaxOutputTokens_PrecedenceChain()
    {
        var options = new LlmProviderOptions
        {
            MaxOutputTokens = 16384,
            ModelProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["profile-model"] = new() { MaxOutputTokens = 4096 }
            }
        };

        var resolver = CreateResolver(options);

        // 1. Fallback to LlmProviderOptions
        var chatOptsFallback = resolver.CreateChatOptions(new AgentProfile { ModelName = "unknown-model" });
        Assert.Equal(16384, chatOptsFallback.MaxOutputTokens);

        // 2. Fallback to ModelCapabilityProfile
        var chatOptsProfile = resolver.CreateChatOptions(new AgentProfile { ModelName = "profile-model" });
        Assert.Equal(4096, chatOptsProfile.MaxOutputTokens);

        // 3. Override by AgentProfile.MaxOutputTokens
        var chatOptsOverride = resolver.CreateChatOptions(new AgentProfile { ModelName = "profile-model", MaxOutputTokens = 2048 });
        Assert.Equal(2048, chatOptsOverride.MaxOutputTokens);
    }
}
