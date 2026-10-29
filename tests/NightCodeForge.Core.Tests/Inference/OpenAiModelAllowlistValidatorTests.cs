using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using Xunit;

namespace NightCodeForge.Core.Tests.Inference;

public class OpenAiModelAllowlistValidatorTests
{
    [Fact]
    public void ValidateModelAllowed_WhenOptionsNull_ThrowsArgumentNullException()
    {
        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        Assert.Throws<ArgumentNullException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(null!, mockCache.Object, "gpt-4o"));
    }

    [Fact]
    public void ValidateModelAllowed_WhenAllowlistCacheNull_ThrowsArgumentNullException()
    {
        var options = new LlmProviderOptions();
        Assert.Throws<ArgumentNullException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, null!, "gpt-4o"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateModelAllowed_WhenModelIdNullOrWhitespace_ThrowsArgumentException(string? invalidModelId)
    {
        var options = new LlmProviderOptions();
        var mockCache = new Mock<IOpenAiModelAllowlistCache>();

        Assert.ThrowsAny<ArgumentException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, invalidModelId!));
    }

    [Fact]
    public void ValidateModelAllowed_WhenManualAllowedModelsConfigured_AndModelMatches_DoesNotThrow()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "qwen-2.5-coder-32b" },
                    new OpenAiAllowedModel { ModelId = "DeepSeek-Chat" }
                ]
            }
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>(MockBehavior.Strict);

        // Case-exact
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "qwen-2.5-coder-32b");

        // Case-insensitive
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "deepseek-chat");

        // Whitespace-trimmed
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "  qwen-2.5-coder-32b  ");
    }

    [Fact]
    public void ValidateModelAllowed_WhenManualAllowedModelsConfigured_AndModelNotPresent_ThrowsModelNotAllowedException()
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

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        // Even if the cache would allow it, manual AllowedModels overrides and forbids it
        mockCache.Setup(c => c.IsModelAllowed("unlisted-model")).Returns(true);

        var ex = Assert.Throws<ModelNotAllowedException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "unlisted-model"));

        Assert.Equal("unlisted-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
    }

    [Fact]
    public void ValidateModelAllowed_WhenManualAllowedModelsContainsNullItems_HandlesSafely()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    null!,
                    new OpenAiAllowedModel { ModelId = null! },
                    new OpenAiAllowedModel { ModelId = "gpt-4o" }
                ]
            }
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();

        // Matches valid entry without throwing NullReferenceException
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "gpt-4o");

        // Unlisted entry throws ModelNotAllowedException
        Assert.Throws<ModelNotAllowedException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "unlisted-model"));
    }

    [Fact]
    public void ValidateModelAllowed_WhenAllowedModelsContainsOnlyWhitespaceOrNull_FallsBackToAllowlistCache()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels =
                [
                    null!,
                    new OpenAiAllowedModel { ModelId = null! },
                    new OpenAiAllowedModel { ModelId = "   " }
                ]
            }
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        mockCache.Setup(c => c.IsModelAllowed("discovered-model")).Returns(true);

        // When AllowedModels has no valid model IDs, it should check the cache rather than throwing
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "discovered-model");
    }

    [Fact]
    public void ValidateModelAllowed_WhenNoManualAllowedModels_AndModelInCache_DoesNotThrow()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels = []
            }
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        mockCache.Setup(c => c.IsModelAllowed("discovered-model")).Returns(true);

        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "discovered-model");
        mockCache.Verify(c => c.IsModelAllowed("discovered-model"), Times.Once);
    }

    [Fact]
    public void ValidateModelAllowed_WhenNoManualAllowedModels_AndModelNotInCache_ThrowsModelNotAllowedException()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels = []
            }
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        mockCache.Setup(c => c.IsModelAllowed(It.IsAny<string>())).Returns(false);

        var ex = Assert.Throws<ModelNotAllowedException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "forbidden-model"));

        Assert.Equal("forbidden-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
    }

    [Fact]
    public void ValidateModelAllowed_WhenOpenAiCompatibleNull_FallsBackToAllowlistCache()
    {
        var options = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = null!
        };

        var mockCache = new Mock<IOpenAiModelAllowlistCache>();
        mockCache.Setup(c => c.IsModelAllowed("cached-model")).Returns(true);

        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "cached-model");

        mockCache.Setup(c => c.IsModelAllowed("not-cached")).Returns(false);
        Assert.Throws<ModelNotAllowedException>(() =>
            OpenAiModelAllowlistValidator.ValidateModelAllowed(options, mockCache.Object, "not-cached"));
    }
}

