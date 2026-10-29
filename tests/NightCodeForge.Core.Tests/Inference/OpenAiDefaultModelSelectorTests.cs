using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests.Inference;

public class OpenAiDefaultModelSelectorTests
{
    private readonly OpenAiDefaultModelSelector _selector = new();

    [Fact]
    public void SelectDefaultModel_WhenCandidatesNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _selector.SelectDefaultModel(null!));
    }

    [Fact]
    public void SelectDefaultModel_WhenCandidatesEmpty_ReturnsNull()
    {
        var result = _selector.SelectDefaultModel([]);
        Assert.Null(result);
    }

    [Fact]
    public void SelectDefaultModel_WhenCandidatesOnlyNullOrWhitespace_ReturnsNull()
    {
        var candidates = new List<AvailableModelInfo>
        {
            null!,
            new("", "Empty"),
            new("   ", "Whitespace")
        };

        var result = _selector.SelectDefaultModel(candidates);
        Assert.Null(result);
    }

    [Fact]
    public void SelectDefaultModel_WhenExplicitModelProvidedAndMatches_ReturnsExplicitModel()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("gpt-4o", "GPT-4o", PromptCostPerMillion: 5.0m, CompletionCostPerMillion: 15.0m),
            new("gpt-4o-mini", "GPT-4o Mini", PromptCostPerMillion: 0.15m, CompletionCostPerMillion: 0.60m)
        };

        var result = _selector.SelectDefaultModel(candidates, "gpt-4o");

        Assert.NotNull(result);
        Assert.Equal("gpt-4o", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenExplicitModelMatchesWithDifferentCaseAndWhitespace_ReturnsExplicitModel()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("DeepSeek-Chat", "DeepSeek", PromptCostPerMillion: 0.14m, CompletionCostPerMillion: 0.28m),
            new("gpt-4o", "GPT-4o", PromptCostPerMillion: 5.0m, CompletionCostPerMillion: 15.0m)
        };

        var result = _selector.SelectDefaultModel(candidates, "  deepseek-chat  ");

        Assert.NotNull(result);
        Assert.Equal("DeepSeek-Chat", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenExplicitModelNotFound_FallsBackToEconomicRanking()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("expensive-model", "Expensive", PromptCostPerMillion: 10.0m, CompletionCostPerMillion: 30.0m),
            new("cheap-model", "Cheap", PromptCostPerMillion: 0.20m, CompletionCostPerMillion: 0.50m)
        };

        var result = _selector.SelectDefaultModel(candidates, "non-existent-explicit-model");

        Assert.NotNull(result);
        Assert.Equal("cheap-model", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_PrioritizesModelsWithPricingOverModelsWithoutPricing()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("unpriced-model", "Unpriced", PromptCostPerMillion: null, CompletionCostPerMillion: null),
            new("priced-model", "Priced", PromptCostPerMillion: 2.50m, CompletionCostPerMillion: 5.00m)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("priced-model", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_PrioritizesLowestPromptCost()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-expensive-prompt", "ExpPrompt", PromptCostPerMillion: 2.50m, CompletionCostPerMillion: 1.00m),
            new("model-cheapest-prompt", "CheapPrompt", PromptCostPerMillion: 0.15m, CompletionCostPerMillion: 5.00m),
            new("model-mid-prompt", "MidPrompt", PromptCostPerMillion: 1.00m, CompletionCostPerMillion: 2.00m)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("model-cheapest-prompt", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenPromptCostEqual_PrioritizesLowestCompletionCost()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-high-completion", "HighComp", PromptCostPerMillion: 0.50m, CompletionCostPerMillion: 2.00m),
            new("model-low-completion", "LowComp", PromptCostPerMillion: 0.50m, CompletionCostPerMillion: 0.80m)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("model-low-completion", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenCostsEqual_UsesAlphabeticalOrderAsTieBreaker()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("zeta-model", "Zeta", PromptCostPerMillion: 1.00m, CompletionCostPerMillion: 2.00m),
            new("alpha-model", "Alpha", PromptCostPerMillion: 1.00m, CompletionCostPerMillion: 2.00m),
            new("beta-model", "Beta", PromptCostPerMillion: 1.00m, CompletionCostPerMillion: 2.00m)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("alpha-model", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenAllModelsUnpriced_UsesAlphabeticalOrderAsTieBreaker()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("z-unpriced", "Z", PromptCostPerMillion: null, CompletionCostPerMillion: null),
            new("a-unpriced", "A", PromptCostPerMillion: null, CompletionCostPerMillion: null)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("a-unpriced", result.ModelId);
    }
}

