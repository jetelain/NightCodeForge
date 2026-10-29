using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OllamaCapabilityResolverTests
{
    [Fact]
    public void Constructor_WhenOptionsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OllamaCapabilityResolver(null!));
    }

    [Theory]
    [InlineData("Qwen3.8-27b")]
    [InlineData("qwen3.8-27b")]
    [InlineData("Qwen3.8-27B:latest")]
    [InlineData("qwen-3.8-27b")]
    public void ResolveReasoningEffort_ForQwen38_MapsHighToXhigh(string modelId)
    {
        var options = new LlmProviderOptions();
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

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
        var options = new LlmProviderOptions();
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", requested);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForQwen38_WhenEffortNullOrEmpty_DefaultsToXhigh()
    {
        var options = new LlmProviderOptions { ReasoningEffort = null };
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", null);

        // Qwen 3.8 default effort in profile is "xhigh"
        Assert.Equal("xhigh", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_ForQwen38_WhenUnsupportedEffortRequested_FindsClosestRank()
    {
        var options = new LlmProviderOptions();
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        // "minimal" is rank 0. Supported in Qwen3.8 are low (1), medium (2), xhigh (4). Closest is "low".
        var resolved = resolver.ResolveReasoningEffort("Qwen3.8-27b", "minimal");

        Assert.Equal("low", resolved);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenModelHasNoProfile_UsesRequestedOrOptionsEffort()
    {
        var options = new LlmProviderOptions { ReasoningEffort = "low" };
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var resolvedExplicit = resolver.ResolveReasoningEffort("llama3.3:70b", "medium");
        Assert.Equal("medium", resolvedExplicit);

        var resolvedDefault = resolver.ResolveReasoningEffort("llama3.3:70b", null);
        Assert.Equal("low", resolvedDefault);
    }

    [Fact]
    public void BuildReasoningProperties_ForQwen38_IncludesThinkTrueAndMappedReasoningEffort()
    {
        var options = new LlmProviderOptions();
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var props = resolver.BuildReasoningProperties("Qwen3.8-27b", "high");

        Assert.True(props.ContainsKey("think"));
        Assert.Equal(true, props["think"]);
        Assert.Equal("xhigh", props["reasoning_effort"]);
    }

    [Fact]
    public void CreateChatOptions_WithAgentProfileAndQwen38_SetsExpectedChatOptions()
    {
        var options = new LlmProviderOptions { DefaultModelId = "Qwen3.8-27b" };
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

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
        Assert.Equal(true, chatOptions.AdditionalProperties["think"]);
        // ThinkingEffort.High maps to "high", which Qwen 3.8 maps to "xhigh"
        Assert.Equal("xhigh", chatOptions.AdditionalProperties["reasoning_effort"]);
    }

    [Fact]
    public void CreateChatOptions_WhenThinkingEffortNone_DisablesThinkAndSuppressesReasoningEffort()
    {
        var options = new LlmProviderOptions { DefaultModelId = "Qwen3.8-27b" };
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

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
        Assert.False(chatOptions.AdditionalProperties.ContainsKey("reasoning_effort"));
    }

    [Theory]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.CustomBudget, "xhigh")] // CustomBudget -> "high" -> Qwen3.8 maps to "xhigh"
    public void CreateChatOptions_WithVariousThinkingEfforts_MapsCorrectly(ThinkingEffort effort, string expectedReasoningEffort)
    {
        var options = new LlmProviderOptions();
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var profile = new AgentProfile
        {
            ModelName = "Qwen3.8-27b",
            ThinkingEffort = effort
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal(true, chatOptions.AdditionalProperties?["think"]);
        Assert.Equal(expectedReasoningEffort, chatOptions.AdditionalProperties?["reasoning_effort"]);
    }

    [Fact]
    public void CreateChatOptions_WhenProfileNull_FallsBackToDefaultModelAndConfiguredEffort()
    {
        var options = new LlmProviderOptions
        {
            DefaultModelId = "Qwen3.8-27b",
            ReasoningEffort = "low"
        };
        var resolver = new OllamaCapabilityResolver(Options.Create(options));

        var chatOptions = resolver.CreateChatOptions(null);

        Assert.Equal("Qwen3.8-27b", chatOptions.ModelId);
        Assert.Null(chatOptions.Temperature);
        Assert.Equal(true, chatOptions.AdditionalProperties?["think"]);
        Assert.Equal("low", chatOptions.AdditionalProperties?["reasoning_effort"]);
    }
}

