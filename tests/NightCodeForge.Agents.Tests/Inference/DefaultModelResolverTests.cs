using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class DefaultModelResolverTests
{
    [Fact]
    public void ResolveDefaultModel_WhenCandidatesEmpty_ReturnsNull()
    {
        var result = DefaultModelResolver.ResolveDefaultModel([], LlmProviderType.OpenAiCompatible);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveDefaultModel_WhenCandidatesNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DefaultModelResolver.ResolveDefaultModel(null!, LlmProviderType.OpenAiCompatible));
    }

    [Fact]
    public void ResolveDefaultModel_WithExplicitMatch_ReturnsExplicitModelCaseInsensitive()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-a", "Model A", 32768, 10_000_000),
            new("model-b", "Model B", 65536, 20_000_000),
            new("model-c", "Model C", 131072, 30_000_000)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama, "MODEL-B");

        Assert.NotNull(result);
        Assert.Equal("model-b", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_WithExplicitMatch_HandlesWhitespaceAroundExplicitModelId()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("deepseek-chat", "DeepSeek Chat", 65536)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible, "  deepseek-chat  ");

        Assert.NotNull(result);
        Assert.Equal("deepseek-chat", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_WhenCandidatesContainNull_FiltersNullAndDoesNotThrow()
    {
        var candidates = new List<AvailableModelInfo?>
        {
            null,
            new("model-valid", "Valid", ContextWindowTokens: 32768, SizeBytes: 10_000_000)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates!, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("model-valid", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_WhenCandidatesContainEmptyOrWhitespaceModelId_FiltersThemOut()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("", "Empty"),
            new("   ", "Whitespace"),
            new("valid-model", "Valid", ContextWindowTokens: 32768, SizeBytes: 10_000_000)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible);

        Assert.NotNull(result);
        Assert.Equal("valid-model", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_OpenAiCompatible_PicksLowestPromptCostFirst()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("expensive-model", "Expensive", PromptCostPerMillion: 2.50m, CompletionCostPerMillion: 5.00m),
            new("cheap-model", "Cheap", PromptCostPerMillion: 0.14m, CompletionCostPerMillion: 0.28m),
            new("unpriced-model", "Unpriced", PromptCostPerMillion: null, CompletionCostPerMillion: null),
            new("medium-model", "Medium", PromptCostPerMillion: 0.50m, CompletionCostPerMillion: 1.00m)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible);

        Assert.NotNull(result);
        Assert.Equal("cheap-model", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_OpenAiCompatible_WhenPromptCostTies_PicksLowestCompletionCost()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-a", "Model A", PromptCostPerMillion: 0.20m, CompletionCostPerMillion: 1.50m),
            new("model-b", "Model B", PromptCostPerMillion: 0.20m, CompletionCostPerMillion: 0.80m),
            new("model-c", "Model C", PromptCostPerMillion: 0.20m, CompletionCostPerMillion: 2.00m)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible);

        Assert.NotNull(result);
        Assert.Equal("model-b", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_OpenAiCompatible_WhenCostsTieOrNull_PicksAlphabetical()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("zeta-model", "Zeta", PromptCostPerMillion: null),
            new("alpha-model", "Alpha", PromptCostPerMillion: null),
            new("beta-model", "Beta", PromptCostPerMillion: null)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible);

        Assert.NotNull(result);
        Assert.Equal("alpha-model", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_OllamaRouter_PicksLargestContextWindowFirst()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-32k", "32k", ContextWindowTokens: 32768, SizeBytes: 70_000_000_000, IsMoE: false),
            new("model-128k", "128k", ContextWindowTokens: 131072, SizeBytes: 14_000_000_000, IsMoE: false),
            new("model-64k", "64k", ContextWindowTokens: 65536, SizeBytes: 32_000_000_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OllamaRouter);

        Assert.NotNull(result);
        Assert.Equal("model-128k", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_Ollama_PrioritizesDenseOverMoEWhenContextEqual()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("qwen-moe", "Qwen MoE", ContextWindowTokens: 65536, SizeBytes: 35_000_000_000, IsMoE: true),
            new("qwen-dense", "Qwen Dense", ContextWindowTokens: 65536, SizeBytes: 27_000_000_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("qwen-dense", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_Ollama_WhenContextAndMoEEqual_PicksLargestSize()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("small-model", "Small", ContextWindowTokens: 65536, SizeBytes: 7_000_000_000, IsMoE: false),
            new("large-model", "Large", ContextWindowTokens: 65536, SizeBytes: 32_000_000_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("large-model", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_Ollama_WhenAllEqual_PicksAlphabetical()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("zebra", "Zebra", ContextWindowTokens: 65536, SizeBytes: 14_000_000_000, IsMoE: false),
            new("antelope", "Antelope", ContextWindowTokens: 65536, SizeBytes: 14_000_000_000, IsMoE: false),
            new("bear", "Bear", ContextWindowTokens: 65536, SizeBytes: 14_000_000_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("antelope", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_WhenCandidatesHaveWhitespace_TrimsAndOrdersAlphabeticallyCorrect()
    {
        // Without trimming, " zebra" would precede "antelope" because ASCII 32 (' ') < 97 ('a')
        var candidates = new List<AvailableModelInfo>
        {
            new(" zebra  ", "Zebra", ContextWindowTokens: 65536, SizeBytes: 14_000_000_000, IsMoE: false),
            new("  antelope ", "Antelope", ContextWindowTokens: 65536, SizeBytes: 14_000_000_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("antelope", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_OpenAiCompatible_NormalizesReturnedModelId()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("   deepseek-chat   ", "DeepSeek", PromptCostPerMillion: 0.14m)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.OpenAiCompatible);

        Assert.NotNull(result);
        Assert.Equal("deepseek-chat", result.ModelId);
    }

    [Fact]
    public void ResolveDefaultModel_UnsupportedProviderType_ThrowsArgumentOutOfRangeException()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("some-model", "Some Model")
        };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DefaultModelResolver.ResolveDefaultModel(candidates, (LlmProviderType)999));

        Assert.Equal("providerType", ex.ParamName);
    }

    [Fact]
    public void ResolveDefaultModel_Ollama_HandlesNegativeContextWindowTokensAndSizeBytes()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-neg", "Neg", ContextWindowTokens: -100, SizeBytes: -500, IsMoE: false),
            new("model-pos", "Pos", ContextWindowTokens: 32768, SizeBytes: 1_000, IsMoE: false)
        };

        var result = DefaultModelResolver.ResolveDefaultModel(candidates, LlmProviderType.Ollama);

        Assert.NotNull(result);
        Assert.Equal("model-pos", result.ModelId);
    }
}
