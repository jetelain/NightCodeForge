using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OpenAiModelAllowlistValidationTests
{
    [Fact]
    public void AllowlistCache_IsModelAllowed_HandlesNullEmptyAndWhitespace()
    {
        var cache = new OpenAiModelAllowlistCache();
        Assert.False(cache.IsModelAllowed(null!));
        Assert.False(cache.IsModelAllowed(string.Empty));
        Assert.False(cache.IsModelAllowed("   "));
    }

    [Fact]
    public void AllowlistCache_SetDiscoveredModels_PopulatesAndMatchesCaseInsensitive()
    {
        var cache = new OpenAiModelAllowlistCache();
        var models = new List<AvailableModelInfo>
        {
            new("gpt-4o", "GPT-4o", 128000, ProviderType: LlmProviderType.OpenAiCompatible),
            new("deepseek/deepseek-chat", "DeepSeek Chat", 65536, ProviderType: LlmProviderType.OpenAiCompatible)
        };

        cache.SetDiscoveredModels(models);

        Assert.True(cache.IsModelAllowed("gpt-4o"));
        Assert.True(cache.IsModelAllowed("GPT-4O"));
        Assert.True(cache.IsModelAllowed("deepseek/deepseek-chat"));
        Assert.False(cache.IsModelAllowed("claude-3-opus"));
    }

    [Fact]
    public void AllowlistCache_TryGetModel_ReturnsTrueAndModelWhenPresent()
    {
        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new("qwen-32b", "Qwen 32B", 131072, ProviderType: LlmProviderType.OpenAiCompatible)]);

        var found = cache.TryGetModel("QWEN-32B", out var modelInfo);

        Assert.True(found);
        Assert.NotNull(modelInfo);
        Assert.Equal("qwen-32b", modelInfo.ModelId);
        Assert.Equal(131072, modelInfo.ContextWindowTokens);
    }

    [Fact]
    public void AllowlistCache_TryGetModel_ReturnsFalseWhenAbsent()
    {
        var cache = new OpenAiModelAllowlistCache();
        var found = cache.TryGetModel("non-existent", out var modelInfo);

        Assert.False(found);
        Assert.Null(modelInfo);
    }

    [Fact]
    public void ValidateModel_WhenManualAllowedModelsConfigured_AllowsOnlyConfiguredModels()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "local-llama" }
                ]
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        // Even if discovered in cache, manual allowlist overrides
        cache.SetDiscoveredModels([new("other-discovered-model", "Other")]);

        // Allowed
        ValidateModelAllowed(options, cache, "local-llama");

        // Discovered but not in AllowedModels -> throws
        var ex = Assert.Throws<ModelNotAllowedException>(() =>
            ValidateModelAllowed(options, cache, "other-discovered-model"));
        Assert.Equal("other-discovered-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
    }

    [Fact]
    public void AllowlistCache_SetDiscoveredModels_FiltersNullElementsAndWhitespace()
    {
        var cache = new OpenAiModelAllowlistCache();
        var models = new List<AvailableModelInfo>
        {
            null!,
            new("  deepseek-chat  ", "DeepSeek Chat"),
            new("", "Empty"),
            new("   ", "Whitespace")
        };

        cache.SetDiscoveredModels(models);

        Assert.True(cache.IsModelAllowed("deepseek-chat"));
        Assert.False(cache.IsModelAllowed(""));
        var discovered = cache.GetDiscoveredModels();
        Assert.Single(discovered);
        Assert.Equal("deepseek-chat", discovered[0].ModelId);

        Assert.True(cache.TryGetModel("deepseek-chat", out var info));
        Assert.NotNull(info);
        Assert.Equal("deepseek-chat", info.ModelId);
    }

    [Fact]
    public async Task AllowlistCache_SetDiscoveredModels_IsAtomicUnderConcurrentReaders()
    {
        var cache = new OpenAiModelAllowlistCache();
        var setA = new List<AvailableModelInfo> { new("model-a1", "A1"), new("model-a2", "A2") };
        var setB = new List<AvailableModelInfo> { new("model-b1", "B1"), new("model-b2", "B2") };

        cache.SetDiscoveredModels(setA);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Writer task alternating sets
        var writerTask = Task.Run(async () =>
        {
            var flip = false;
            while (!cts.Token.IsCancellationRequested)
            {
                cache.SetDiscoveredModels(flip ? setA : setB);
                flip = !flip;
                await Task.Yield();
            }
        });

        // Reader tasks: Cache must NEVER be observed in an empty state
        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var discovered = cache.GetDiscoveredModels();
                if (discovered.Count != 2)
                {
                    exceptions.Add(new InvalidOperationException($"Cache observed in torn/empty state with count {discovered.Count}"));
                    break;
                }
            }
        })).ToArray();

        await Task.WhenAll([writerTask, ..readerTasks]);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void ValidateModel_WhenManualAllowedModelsConfigured_HandlesWhitespaceAroundModelId()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "local-llama" }
                ]
            }
        };

        var cache = new OpenAiModelAllowlistCache();

        // Should allow even with surrounding whitespace
        ValidateModelAllowed(options, cache, "  local-llama  ");
    }

    [Fact]
    public void ValidateModel_WhenNoManualAllowedModels_AllowsDiscoveredModels()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels = [] // Empty: all discovered models permitted
            }
        };

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new("auto-discovered-model", "Auto")]);

        // Allowed via discovery
        ValidateModelAllowed(options, cache, "auto-discovered-model");

        // Not discovered -> throws hard error
        var ex = Assert.Throws<ModelNotAllowedException>(() =>
            ValidateModelAllowed(options, cache, "unlisted-model"));
        Assert.Equal("unlisted-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
        Assert.Contains("unlisted-model", ex.Message);
        Assert.Contains("OpenAiCompatible", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validator_ThrowsArgumentException_WhenModelIdIsNullOrEmptyOrWhitespace(string? invalidModelId)
    {
        var options = new LlmProviderOptions();
        var cache = new OpenAiModelAllowlistCache();

        Assert.ThrowsAny<ArgumentException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, cache, invalidModelId!));
    }

    [Fact]
    public void Validator_ThrowsArgumentNullException_WhenOptionsOrCacheNull()
    {
        var cache = new OpenAiModelAllowlistCache();
        var options = new LlmProviderOptions();

        Assert.Throws<ArgumentNullException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(null!, cache, "gpt-4o"));

        Assert.Throws<ArgumentNullException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, null!, "gpt-4o"));
    }

    [Fact]
    public void Validator_HandlesNullOpenAiCompatibleOptions_WithoutNullReferenceException()
    {
        var options = new LlmProviderOptions { OpenAiCompatible = null! };
        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new("qwen-discovered", "Qwen")]);

        // When OpenAiCompatible is null, falls back to discovered models in cache
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, cache, "qwen-discovered");

        Assert.Throws<ModelNotAllowedException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, cache, "unlisted"));
    }

    private static void ValidateModelAllowed(LlmProviderOptions options, IOpenAiModelAllowlistCache allowlistCache, string modelId) =>
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, allowlistCache, modelId);
}
