using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Configuration;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Configuration;

public class LlmConfigurationWriterTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<IHostEnvironment> _hostEnvMock;

    public LlmConfigurationWriterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ncf_writer_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        _hostEnvMock = new Mock<IHostEnvironment>();
        _hostEnvMock.Setup(e => e.ContentRootPath).Returns(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup in test temp folder
        }
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_CreatesFileAndDirectory_WhenAbsent()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OllamaRouter,
            Endpoint = "http://localhost:11434"
        });

        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
            opts.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";
            opts.OpenAiCompatible.ApiKey = "sk-test-key";
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Assert.True(File.Exists(configFilePath));

        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);
        Assert.NotNull(root["LlmProvider"]);

        var llm = root["LlmProvider"] as JsonObject;
        Assert.NotNull(llm);
        Assert.Equal("OpenAiCompatible", llm["ProviderType"]?.ToString());

        var openAi = llm["OpenAiCompatible"] as JsonObject;
        Assert.NotNull(openAi);
        Assert.Equal("https://api.openai.com/v1", openAi["Endpoint"]?.ToString());
        Assert.Equal("sk-test-key", openAi["ApiKey"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_PreservesOtherConfigurationSections()
    {
        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);

        var existingJson = """
        {
          "Logging": {
            "LogLevel": {
              "Default": "Debug"
            }
          },
          "ConnectionStrings": {
            "DefaultConnection": "Data Source=test.db;"
          },
          "LlmProvider": {
            "ProviderType": "Ollama",
            "Endpoint": "http://original:11434"
          }
        }
        """;
        await File.WriteAllTextAsync(configFilePath, existingJson);

        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.Ollama,
            Endpoint = "http://original:11434"
        });

        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
        });

        var updatedText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(updatedText) as JsonObject;
        Assert.NotNull(root);

        // Verify other sections preserved intact
        Assert.NotNull(root["Logging"]);
        Assert.Equal("Debug", root["Logging"]?["LogLevel"]?["Default"]?.ToString());
        Assert.NotNull(root["ConnectionStrings"]);
        Assert.Equal("Data Source=test.db;", root["ConnectionStrings"]?["DefaultConnection"]?.ToString());

        // Verify LlmProvider updated
        Assert.Equal("OpenAiCompatible", root["LlmProvider"]?["ProviderType"]?.ToString());
    }

    [Fact]
    public async Task SaveAllowedModelsAsync_WritesAllowedModelsCorrectly()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel>
        {
            new()
            {
                ModelId = "deepseek/deepseek-chat",
                DisplayName = "DeepSeek Chat",
                ContextWindowTokens = 65536,
                MaxOutputTokens = 4096,
                SupportsReasoning = true,
                DefaultThinkingEffort = ThinkingEffort.High,
                PromptCostPerMillion = 0.14m,
                CompletionCostPerMillion = 0.28m
            }
        };

        await writer.SaveAllowedModelsAsync(models);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Assert.True(File.Exists(configFilePath));

        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        var allowedModels = root["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowedModels);
        Assert.Single(allowedModels);

        var first = allowedModels[0] as JsonObject;
        Assert.NotNull(first);
        Assert.Equal("deepseek/deepseek-chat", first["ModelId"]?.ToString());
        Assert.Equal("DeepSeek Chat", first["DisplayName"]?.ToString());
        Assert.Equal(65536, first["ContextWindowTokens"]?.GetValue<int>());
        Assert.Equal(4096, first["MaxOutputTokens"]?.GetValue<int>());
        Assert.True(first["SupportsReasoning"]?.GetValue<bool>());
        Assert.Equal("High", first["DefaultThinkingEffort"]?.ToString());
        Assert.Equal(0.14m, first["PromptCostPerMillion"]?.GetValue<decimal>());
        Assert.Equal(0.28m, first["CompletionCostPerMillion"]?.GetValue<decimal>());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_IsThreadSafeUnderConcurrency()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(async () =>
            {
                await writer.SaveProviderConfigurationAsync(opts =>
                {
                    opts.RequestTimeoutMinutes = 10 + i;
                });
            }));

        await Task.WhenAll(tasks);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Assert.True(File.Exists(configFilePath));
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_PreservesExistingAllowedModels()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel>
        {
            new() { ModelId = "deepseek/deepseek-chat", DisplayName = "DeepSeek Chat" }
        };
        await writer.SaveAllowedModelsAsync(models);

        // Mutate provider configuration
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
            opts.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        // AllowedModels must be preserved intact
        var allowed = root["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("deepseek/deepseek-chat", allowed[0]?["ModelId"]?.ToString());

        // And provider changes applied
        Assert.Equal("OpenAiCompatible", root["LlmProvider"]?["ProviderType"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_PreservesSequentialMutations()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        // Mutation 1
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.Endpoint = "http://custom-node:11434";
        });

        // Mutation 2 (different property)
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.DefaultModelId = "custom-model:latest";
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        // Both mutations must persist
        Assert.Equal("http://custom-node:11434", root["LlmProvider"]?["Endpoint"]?.ToString());
        Assert.Equal("custom-model:latest", root["LlmProvider"]?["DefaultModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_WhenFileContainsInvalidJson_ThrowsAndPreservesFile()
    {
        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);
        var invalidJson = "{ invalid json content: [";
        await File.WriteAllTextAsync(configFilePath, invalidJson);

        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.SaveProviderConfigurationAsync(opts => opts.ProviderType = LlmProviderType.OpenAiCompatible));

        Assert.Contains("invalid JSON", ex.Message);

        // Verify file was NOT overwritten
        var content = await File.ReadAllTextAsync(configFilePath);
        Assert.Equal(invalidJson, content);
    }

    [Fact]
    public async Task SaveAllowedModelsAsync_FiltersNullEntries()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel?>
        {
            null,
            new() { ModelId = "valid-model" }
        };

        await writer.SaveAllowedModelsAsync(models!);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        var allowed = root?["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("valid-model", allowed[0]?["ModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_PreservesBaseOptionsFromMemory_WhenConfigJsonOnlyHasPartialSection()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://my-ollama:11434",
            CoderModelId = "coder-special:latest",
            RequestTimeoutMinutes = 25
        });
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        // First, save only AllowedModels (so config.json only has OpenAiCompatible.AllowedModels)
        await writer.SaveAllowedModelsAsync([new OpenAiAllowedModel { ModelId = "deepseek/deepseek-chat" }]);

        // Now mutate provider type
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        var llm = root["LlmProvider"] as JsonObject;
        Assert.NotNull(llm);
        Assert.Equal("OpenAiCompatible", llm["ProviderType"]?.ToString());
        Assert.Equal("http://my-ollama:11434", llm["Endpoint"]?.ToString());
        Assert.Equal("coder-special:latest", llm["CoderModelId"]?.ToString());

        var allowed = llm["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("deepseek/deepseek-chat", allowed[0]?["ModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_DoesNotResurrectAllowedModels_WhenExplicitlyCleared()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                AllowedModels = [new OpenAiAllowedModel { ModelId = "initial-model" }]
            }
        });
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        // User explicitly clears allowed models
        await writer.SaveAllowedModelsAsync([]);

        // Subsequent provider config mutation must not resurrect initial-model
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.RequestTimeoutMinutes = 30;
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        var allowed = root["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Empty(allowed);
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_DoesNotWriteNullProperties()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            CoderModelId = null,
            ArchitectModelId = null
        });
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        Assert.NotNull(root);

        var llm = root["LlmProvider"] as JsonObject;
        Assert.NotNull(llm);
        Assert.False(llm.ContainsKey("CoderModelId"));
        Assert.False(llm.ContainsKey("ArchitectModelId"));
    }

    [Fact]
    public async Task SaveAllowedModelsAsync_FiltersWhitespaceAndEmptyModelIds_AndTrimsValidIds()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel?>
        {
            null,
            new() { ModelId = "" },
            new() { ModelId = "   " },
            new() { ModelId = "  deepseek-chat  ", DisplayName = "DeepSeek" }
        };

        await writer.SaveAllowedModelsAsync(models!);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        var allowed = root?["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("deepseek-chat", allowed[0]?["ModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveAllowedModelsAsync_DeduplicatesByModelId_CaseInsensitive()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel>
        {
            new() { ModelId = "deepseek/deepseek-chat", DisplayName = "First" },
            new() { ModelId = "DEEPSEEK/DEEPSEEK-CHAT", DisplayName = "Duplicate" },
            new() { ModelId = "  deepseek/deepseek-chat  ", DisplayName = "Duplicate With Spaces" }
        };

        await writer.SaveAllowedModelsAsync(models);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        var allowed = root?["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("deepseek/deepseek-chat", allowed[0]?["ModelId"]?.ToString());
        Assert.Equal("First", allowed[0]?["DisplayName"]?.ToString());
    }

    [Fact]
    public async Task SaveAllowedModelsAsync_NormalizesNonPositiveContextTokensAndMaxOutputTokens()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        var models = new List<OpenAiAllowedModel>
        {
            new()
            {
                ModelId = "gpt-4o",
                ContextWindowTokens = 0,
                MaxOutputTokens = -10
            }
        };

        await writer.SaveAllowedModelsAsync(models);

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        var allowed = root?["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);

        var first = allowed[0] as JsonObject;
        Assert.NotNull(first);
        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, first["ContextWindowTokens"]?.GetValue<int>());
        Assert.Equal(LlmProviderOptions.DefaultMaxOutputTokens, first["MaxOutputTokens"]?.GetValue<int>());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_SanitizesAllowedModels_WhenMutatedViaProviderUpdate()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
            opts.OpenAiCompatible.AllowedModels =
            [
                new() { ModelId = "  model-a  " },
                new() { ModelId = "model-a" },
                new() { ModelId = "   " }
            ];
        });

        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        var jsonText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(jsonText) as JsonObject;
        var allowed = root?["LlmProvider"]?["OpenAiCompatible"]?["AllowedModels"] as JsonArray;
        Assert.NotNull(allowed);
        Assert.Single(allowed);
        Assert.Equal("model-a", allowed[0]?["ModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_NormalizesCase_WhenConfigJsonHadCamelCaseLlmProvider()
    {
        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);

        var existingJson = """
        {
          "llmProvider": {
            "endpoint": "http://camel-case:11434"
          }
        }
        """;
        await File.WriteAllTextAsync(configFilePath, existingJson);

        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
        });

        var updatedText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(updatedText) as JsonObject;
        Assert.NotNull(root);

        // Normalized PascalCase exists, camelCase removed
        Assert.NotNull(root["LlmProvider"]);
        Assert.Null(root["llmProvider"]);
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_PreservesLegacyModelId_WhenDefaultModelIdAbsent()
    {
        var configFilePath = HostConfigurationSources.GetFullPath(_tempDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(configFilePath)!);

        var existingJson = """
        {
          "LlmProvider": {
            "ModelId": "legacy-custom:latest"
          }
        }
        """;
        await File.WriteAllTextAsync(configFilePath, existingJson);

        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        string? observedDefaultModel = null;
        await writer.SaveProviderConfigurationAsync(opts =>
        {
            observedDefaultModel = opts.DefaultModelId;
        });

        Assert.Equal("legacy-custom:latest", observedDefaultModel);

        var updatedText = await File.ReadAllTextAsync(configFilePath);
        var root = JsonNode.Parse(updatedText) as JsonObject;
        Assert.NotNull(root);
        Assert.Equal("legacy-custom:latest", root["LlmProvider"]?["DefaultModelId"]?.ToString());
    }

    [Fact]
    public async Task SaveProviderConfigurationAsync_CleansUpTempFileOnCompletion()
    {
        var options = Options.Create(new LlmProviderOptions());
        var writer = new LlmConfigurationWriter(_hostEnvMock.Object, options, NullLogger<LlmConfigurationWriter>.Instance);

        await writer.SaveProviderConfigurationAsync(opts =>
        {
            opts.ProviderType = LlmProviderType.OpenAiCompatible;
        });

        var configDir = Path.GetDirectoryName(HostConfigurationSources.GetFullPath(_tempDirectory))!;
        var tempFiles = Directory.GetFiles(configDir, "*.tmp");
        Assert.Empty(tempFiles);
    }
}
