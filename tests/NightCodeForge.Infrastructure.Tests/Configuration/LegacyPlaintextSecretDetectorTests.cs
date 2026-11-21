using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Configuration;

namespace NightCodeForge.Infrastructure.Tests.Configuration;

public class LegacyPlaintextSecretDetectorTests
{
    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void DetectAndWarnConfigurationSecrets_WhenLegacyKeysPresent_LogsRedactedWarningsWithoutSecretValues()
    {
        const string openAiSecret = "sk-plaintext-openai-leak-check-123";
        const string ollamaSecret = "ollama-plaintext-leak-check-456";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath] = openAiSecret,
                [ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath] = ollamaSecret
            })
            .Build();

        var logger = new ListLogger();

        var detected = LegacyPlaintextSecretDetector.DetectAndWarnConfigurationSecrets(config, logger);

        Assert.True(detected);
        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, entry =>
        {
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.DoesNotContain(openAiSecret, entry.Message);
            Assert.DoesNotContain(ollamaSecret, entry.Message);
            Assert.Contains("OS keyring", entry.Message);
        });
        Assert.Contains(logger.Messages, m => m.Message.Contains(ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath));
        Assert.Contains(logger.Messages, m => m.Message.Contains(ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath));
    }

    [Fact]
    public void DetectAndWarnConfigurationSecrets_WhenAbsentOrWhitespace_ReturnsFalseWithoutWarnings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath] = "   ",
                [ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath] = ""
            })
            .Build();

        var logger = new ListLogger();

        var detected = LegacyPlaintextSecretDetector.DetectAndWarnConfigurationSecrets(config, logger);

        Assert.False(detected);
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("{invalid-json", false)]
    [InlineData("[]", false)]
    [InlineData("{\"Provider\":\"Ollama\",\"Endpoint\":\"https://ollama.com/api/web_search\"}", false)]
    [InlineData("{\"Provider\":\"Ollama\",\"ApiKey\":\"\"}", false)]
    [InlineData("{\"Provider\":\"Ollama\",\"ApiKey\":\"db-secret\"}", true)]
    [InlineData("{\"Provider\":\"Ollama\",\"apiKey\":\"db-secret-camel\"}", true)]
    public void ContainsLegacyApiKeyProperty_IdentifiesOnlyNonEmptyLegacyApiKeyFields(string? payload, bool expected)
    {
        Assert.Equal(expected, LegacyPlaintextSecretDetector.ContainsLegacyApiKeyProperty(payload));
    }

    [Fact]
    public void DetectAndWarnConfigurationSecrets_DetectsRootEnvKeysInJsonFiles_WithoutFalsePositivesOnInMemoryOrEnvProviders()
    {
        const string rootOpenAiSecret = "sk-root-json-openai-leak";
        const string rootOllamaSecret = "ollama-root-json-leak";
        var tempFile = Path.Combine(Path.GetTempPath(), $"ncf-legacy-json-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(tempFile, $$"""
            {
              "OPENAI_API_KEY": "{{rootOpenAiSecret}}",
              "OLLAMA_API_KEY": "{{rootOllamaSecret}}"
            }
            """);

            var jsonConfig = new ConfigurationBuilder()
                .AddJsonFile(tempFile, optional: false)
                .Build();

            var jsonLogger = new ListLogger();
            Assert.True(LegacyPlaintextSecretDetector.DetectAndWarnConfigurationSecrets(jsonConfig, jsonLogger));
            Assert.Equal(2, jsonLogger.Messages.Count);
            Assert.All(jsonLogger.Messages, entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.DoesNotContain(rootOpenAiSecret, entry.Message);
                Assert.DoesNotContain(rootOllamaSecret, entry.Message);
                Assert.Contains(Path.GetFileName(tempFile), entry.Message);
            });

            // Legitimate process environment variables (non-JSON providers) MUST NOT trigger warnings
            var nonJsonConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-valid-env",
                    [ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama-valid-env"
                })
                .Build();

            var nonJsonLogger = new ListLogger();
            Assert.False(LegacyPlaintextSecretDetector.DetectAndWarnConfigurationSecrets(nonJsonConfig, nonJsonLogger));
            Assert.Empty(nonJsonLogger.Messages);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
