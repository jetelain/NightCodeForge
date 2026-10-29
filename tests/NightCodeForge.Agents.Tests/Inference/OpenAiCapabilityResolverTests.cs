using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OpenAiCapabilityResolverTests
{
    [Fact]
    public void Constructor_WhenOptionsNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new OpenAiCapabilityResolver(null!));
    }

    [Fact]
    public void ResolveReasoningEffort_WhenXhigh_NormalizesToHigh()
    {
        var options = new LlmProviderOptions();
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var effort = resolver.ResolveReasoningEffort("o3-mini", "xhigh");

        Assert.Equal("high", effort);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    public void ResolveReasoningEffort_PreservesStandardOpenAiEfforts(string requested, string expected)
    {
        var options = new LlmProviderOptions();
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var effort = resolver.ResolveReasoningEffort("o1", requested);

        Assert.Equal(expected, effort);
    }

    [Fact]
    public void ResolveReasoningEffort_WhenEmpty_DefaultsToHighOrConfigured()
    {
        var optionsWithLow = new LlmProviderOptions { ReasoningEffort = "low" };
        var resolverLow = new OpenAiCapabilityResolver(Options.Create(optionsWithLow));
        Assert.Equal("low", resolverLow.ResolveReasoningEffort("gpt-4o", null));

        var optionsDefault = new LlmProviderOptions { ReasoningEffort = null };
        var resolverDefault = new OpenAiCapabilityResolver(Options.Create(optionsDefault));
        Assert.Equal("high", resolverDefault.ResolveReasoningEffort("gpt-4o", null));
    }

    [Fact]
    public void ResolveReasoningEffort_WhenProfileMapsToXhigh_NormalizesToHigh()
    {
        var options = new LlmProviderOptions();
        options.ModelProfiles["custom-reasoner"] = new ModelCapabilityProfile
        {
            EffortMap = new Dictionary<string, string>
            {
                ["maximum"] = "xhigh"
            }
        };
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var effort = resolver.ResolveReasoningEffort("custom-reasoner", "maximum");

        Assert.Equal("high", effort);
    }

    [Fact]
    public void BuildReasoningProperties_EmitsReasoningEffortWithoutThinkFlag()
    {
        var options = new LlmProviderOptions { ReasoningEffort = "medium" };
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var props = resolver.BuildReasoningProperties("o1-preview");

        Assert.Equal("medium", props["reasoning_effort"]);
        Assert.False(props.ContainsKey("think"), "OpenAI-compatible endpoints must not emit Ollama 'think' flag.");
    }

    [Fact]
    public void CreateChatOptions_WithAgentProfile_PopulatesChatOptionsCorrectly()
    {
        var options = new LlmProviderOptions { DefaultModelId = "gpt-4o" };
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var profile = new AgentProfile
        {
            ModelName = "o3-mini",
            Temperature = 0.7,
            ThinkingEffort = ThinkingEffort.High
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal("o3-mini", chatOptions.ModelId);
        Assert.Equal(0.7f, chatOptions.Temperature);
        Assert.Equal("high", chatOptions.AdditionalProperties?["reasoning_effort"]);
        Assert.False(chatOptions.AdditionalProperties?.ContainsKey("think"));
    }

    [Fact]
    public void CreateChatOptions_WhenThinkingEffortNone_SuppressesReasoningProperties()
    {
        var options = new LlmProviderOptions { DefaultModelId = "gpt-4o" };
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var profile = new AgentProfile
        {
            ModelName = "gpt-4o",
            Temperature = 0.5,
            ThinkingEffort = ThinkingEffort.None
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal("gpt-4o", chatOptions.ModelId);
        Assert.Equal(0.5f, chatOptions.Temperature);
        Assert.True(chatOptions.AdditionalProperties == null || !chatOptions.AdditionalProperties.ContainsKey("reasoning_effort"));
        Assert.True(chatOptions.AdditionalProperties == null || !chatOptions.AdditionalProperties.ContainsKey("think"));
    }

    [Theory]
    [InlineData(ThinkingEffort.Low, "low")]
    [InlineData(ThinkingEffort.Medium, "medium")]
    [InlineData(ThinkingEffort.High, "high")]
    [InlineData(ThinkingEffort.CustomBudget, "high")]
    public void CreateChatOptions_WithVariousThinkingEfforts_MapsCorrectly(ThinkingEffort effort, string expectedReasoningEffort)
    {
        var options = new LlmProviderOptions();
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var profile = new AgentProfile
        {
            ModelName = "o1-mini",
            ThinkingEffort = effort
        };

        var chatOptions = resolver.CreateChatOptions(profile);

        Assert.Equal(expectedReasoningEffort, chatOptions.AdditionalProperties?["reasoning_effort"]);
        Assert.False(chatOptions.AdditionalProperties?.ContainsKey("think"));
    }

    [Fact]
    public void CreateChatOptions_WhenProfileNull_FallsBackToDefaultModel()
    {
        var options = new LlmProviderOptions
        {
            DefaultModelId = "gpt-4o-mini",
            ReasoningEffort = "low"
        };
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var chatOptions = resolver.CreateChatOptions(null);

        Assert.Equal("gpt-4o-mini", chatOptions.ModelId);
        Assert.Null(chatOptions.Temperature);
        Assert.Equal("low", chatOptions.AdditionalProperties?["reasoning_effort"]);
        Assert.False(chatOptions.AdditionalProperties?.ContainsKey("think"));
    }

    [Fact]
    public void ResolveReasoningEffort_WhenMinimal_NormalizesToLow()
    {
        var options = new LlmProviderOptions();
        var resolver = new OpenAiCapabilityResolver(Options.Create(options));

        var effort = resolver.ResolveReasoningEffort("o3-mini", "minimal");

        Assert.Equal("low", effort);
    }
}

