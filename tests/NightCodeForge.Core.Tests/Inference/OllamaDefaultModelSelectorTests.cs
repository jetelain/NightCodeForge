using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests.Inference;

public class OllamaDefaultModelSelectorTests
{
    private readonly OllamaDefaultModelSelector _selector = new();

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
            new("qwen2.5-coder:32b", "Qwen 32B", 65536),
            new("deepseek-r1:14b", "DeepSeek R1", 131072)
        };

        var result = _selector.SelectDefaultModel(candidates, "qwen2.5-coder:32b");

        Assert.NotNull(result);
        Assert.Equal("qwen2.5-coder:32b", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenExplicitModelMatchesWithDifferentCaseAndWhitespace_ReturnsExplicitModel()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("Qwen2.5-Coder:32B", "Qwen 32B", 65536),
            new("llama3.3:70b", "Llama 70B", 131072)
        };

        var result = _selector.SelectDefaultModel(candidates, "  qwen2.5-coder:32b  ");

        Assert.NotNull(result);
        Assert.Equal("Qwen2.5-Coder:32B", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenExplicitModelNotFound_FallsBackToAutomaticCapabilityRanking()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("small-model", "Small", 32768),
            new("large-model", "Large", 131072)
        };

        var result = _selector.SelectDefaultModel(candidates, "unlisted-explicit-model");

        Assert.NotNull(result);
        Assert.Equal("large-model", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_PrioritizesLargestContextWindow()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("context-32k", "32k", 32768, SizeBytes: 50_000_000_000),
            new("context-128k", "128k", 131072, SizeBytes: 10_000_000_000),
            new("context-64k", "64k", 65536, SizeBytes: 30_000_000_000)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("context-128k", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenContextWindowEqual_PrioritizesDenseOverMoE()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("qwen-moe", "MoE Model", 131072, SizeBytes: 40_000_000_000, IsMoE: true),
            new("qwen-dense", "Dense Model", 131072, SizeBytes: 32_000_000_000, IsMoE: false)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("qwen-dense", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenContextAndMoEEqual_PrioritizesLargerSizeBytes()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("model-7b", "7B", 131072, SizeBytes: 5_000_000_000, IsMoE: false),
            new("model-32b", "32B", 131072, SizeBytes: 20_000_000_000, IsMoE: false),
            new("model-14b", "14B", 131072, SizeBytes: 9_000_000_000, IsMoE: false)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("model-32b", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_WhenContextMoEAndSizeEqual_UsesAlphabeticalOrderAsTieBreaker()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("zebra-model", "Zebra", 131072, SizeBytes: 10_000_000_000, IsMoE: false),
            new("alpha-model", "Alpha", 131072, SizeBytes: 10_000_000_000, IsMoE: false),
            new("beta-model", "Beta", 131072, SizeBytes: 10_000_000_000, IsMoE: false)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("alpha-model", result.ModelId);
    }

    [Fact]
    public void SelectDefaultModel_HandlesNullContextWindowAndSizeBytesSafely()
    {
        var candidates = new List<AvailableModelInfo>
        {
            new("null-meta-model", "Null Meta", ContextWindowTokens: null, SizeBytes: null),
            new("known-meta-model", "Known Meta", ContextWindowTokens: 4096, SizeBytes: 1_000_000_000)
        };

        var result = _selector.SelectDefaultModel(candidates);

        Assert.NotNull(result);
        Assert.Equal("known-meta-model", result.ModelId);
    }
}

