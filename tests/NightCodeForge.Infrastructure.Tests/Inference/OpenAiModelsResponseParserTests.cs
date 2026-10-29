using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OpenAiModelsResponseParserTests
{
    [Fact]
    public void Parse_OpenRouterFormat_ExtractsIdContextPricingAndFlags()
    {
        var json = """
        {
          "data": [
            {
              "id": "anthropic/claude-3.5-sonnet",
              "name": "Claude 3.5 Sonnet",
              "context_length": 200000,
              "pricing": {
                "prompt": "0.000003",
                "completion": "0.000015"
              },
              "architecture": {
                "modality": "text->text",
                "instruct_type": "thinking"
              }
            },
            {
              "id": "mistralai/mixtral-8x7b-instruct",
              "name": "Mixtral 8x7B",
              "context_length": 32768,
              "pricing": {
                "prompt": 0.00000024,
                "completion": 0.00000024
              },
              "architecture": {
                "modality": "text->text",
                "instruct_type": null
              }
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);

        Assert.Equal(2, models.Count);

        var m1 = models[0];
        Assert.Equal("anthropic/claude-3.5-sonnet", m1.ModelId);
        Assert.Equal("Claude 3.5 Sonnet", m1.DisplayName);
        Assert.Equal(200000, m1.ContextWindowTokens);
        Assert.Equal(3.0m, m1.PromptCostPerMillion);
        Assert.Equal(15.0m, m1.CompletionCostPerMillion);
        Assert.True(m1.SupportsReasoning);
        Assert.False(m1.IsMoE);
        Assert.Equal(LlmProviderType.OpenAiCompatible, m1.ProviderType);

        var m2 = models[1];
        Assert.Equal("mistralai/mixtral-8x7b-instruct", m2.ModelId);
        Assert.Equal("Mixtral 8x7B", m2.DisplayName);
        Assert.Equal(32768, m2.ContextWindowTokens);
        Assert.Equal(0.24m, m2.PromptCostPerMillion);
        Assert.Equal(0.24m, m2.CompletionCostPerMillion);
        Assert.True(m2.IsMoE);
    }

    [Fact]
    public void Parse_StandardOpenAiFormat_ExtractsModelsWithDefaultContext()
    {
        var json = """
        {
          "object": "list",
          "data": [
            {
              "id": "gpt-4o",
              "object": "model",
              "created": 1715368132,
              "owned_by": "system"
            },
            {
              "id": "o3-mini",
              "object": "model",
              "created": 1735368132,
              "owned_by": "system"
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json, defaultContextTokens: 131072);

        Assert.Equal(2, models.Count);
        Assert.Equal("gpt-4o", models[0].ModelId);
        Assert.Equal("gpt-4o", models[0].DisplayName);
        Assert.Equal(131072, models[0].ContextWindowTokens);
        Assert.False(models[0].SupportsReasoning);

        Assert.Equal("o3-mini", models[1].ModelId);
        Assert.True(models[1].SupportsReasoning);
    }

    [Fact]
    public void Parse_RawArrayFormat_ExtractsModels()
    {
        var json = """
        [
          { "id": "local-model-1", "max_model_len": 65536 },
          { "name": "local-model-2", "context_window": 32768 }
        ]
        """;

        var models = OpenAiModelsResponseParser.Parse(json);

        Assert.Equal(2, models.Count);
        Assert.Equal("local-model-1", models[0].ModelId);
        Assert.Equal(65536, models[0].ContextWindowTokens);

        Assert.Equal("local-model-2", models[1].ModelId);
        Assert.Equal(32768, models[1].ContextWindowTokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("{ invalid json }")]
    [InlineData("{}")]
    [InlineData("{\"other\": 123}")]
    public void Parse_InvalidOrEmptyJson_ReturnsEmptyListWithoutThrowing(string? invalidJson)
    {
        var models = OpenAiModelsResponseParser.Parse(invalidJson!);
        Assert.Empty(models);
    }

    [Fact]
    public void Parse_SkipsItemsWithoutIdOrName()
    {
        var json = """
        {
          "data": [
            { "id": "" },
            { "id": "   " },
            { "other_prop": "val" },
            { "id": "valid-model" }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal("valid-model", models[0].ModelId);
    }

    [Theory]
    [InlineData("deepseek-ai/deepseek-r1", true)]
    [InlineData("deepseek-r1-distill-qwen-32b", true)]
    [InlineData("openai/o1-mini", true)]
    [InlineData("o1-preview", true)]
    [InlineData("o3-mini", true)]
    [InlineData("qwq-32b-preview", true)]
    [InlineData("custom-model-with-thinking-mode", true)]
    [InlineData("starcoder1", false)]
    [InlineData("solar10b", false)]
    [InlineData("mirror1", false)]
    [InlineData("auto1", false)]
    [InlineData("demo1", false)]
    [InlineData("scenario3", false)]
    [InlineData("amber1", false)]
    [InlineData("gpt-4o", false)]
    public void Parse_ReasoningHeuristic_CorrectlyDistinguishesTokenBoundaries(string modelId, bool expectedSupportsReasoning)
    {
        var json = $$"""
        {
          "data": [
            { "id": "{{modelId}}" }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal(expectedSupportsReasoning, models[0].SupportsReasoning);
    }

    [Theory]
    [InlineData("mistralai/mixtral-8x7b-instruct", true)]
    [InlineData("qwen/qwen1.5-moe-a2.7b", true)]
    [InlineData("databricks/dbrx-instruct", true)]
    [InlineData("gizmoe-v1", false)]
    [InlineData("llama-3-8b", false)]
    public void Parse_MoeHeuristic_CorrectlyDistinguishesTokenBoundaries(string modelId, bool expectedIsMoe)
    {
        var json = $$"""
        {
          "data": [
            { "id": "{{modelId}}" }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal(expectedIsMoe, models[0].IsMoE);
    }

    [Fact]
    public void Parse_DisplayName_TakesPrecedenceOverName()
    {
        var json = """
        {
          "data": [
            {
              "id": "deepseek/deepseek-chat",
              "name": "deepseek/deepseek-chat",
              "display_name": "DeepSeek V3"
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal("deepseek/deepseek-chat", models[0].ModelId);
        Assert.Equal("DeepSeek V3", models[0].DisplayName);
    }

    [Fact]
    public void Parse_FloatingPointAndScientificNotation_ParsesContextAndPricing()
    {
        var json = """
        {
          "data": [
            {
              "id": "floating-model",
              "context_length": 65536.0,
              "pricing": {
                "prompt": 1.5e-6,
                "completion": "3.0e-6"
              }
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal(65536, models[0].ContextWindowTokens);
        Assert.Equal(1.5m, models[0].PromptCostPerMillion);
        Assert.Equal(3.0m, models[0].CompletionCostPerMillion);
    }

    [Fact]
    public void Parse_SupportedParametersAndExplicitFlag_DetectsReasoning()
    {
        var json = """
        {
          "data": [
            {
              "id": "model-with-supported-params",
              "supported_parameters": ["temperature", "reasoning_effort", "max_tokens"]
            },
            {
              "id": "model-with-explicit-flag",
              "supports_reasoning": true
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Equal(2, models.Count);
        Assert.True(models[0].SupportsReasoning);
        Assert.True(models[1].SupportsReasoning);
    }

    [Theory]
    [InlineData("num_experts", 8, true)]
    [InlineData("expert_count", 16, true)]
    [InlineData("num_experts", 1, false)]
    public void Parse_ArchitectureExpertsCount_DetectsMoE(string propName, int count, bool expectedMoE)
    {
        var json = $$"""
        {
          "data": [
            {
              "id": "custom-test-model",
              "architecture": {
                "{{propName}}": {{count}}
              }
            }
          ]
        }
        """;

        var models = OpenAiModelsResponseParser.Parse(json);
        Assert.Single(models);
        Assert.Equal(expectedMoE, models[0].IsMoE);
    }
}

