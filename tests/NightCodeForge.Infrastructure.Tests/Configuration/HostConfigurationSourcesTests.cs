namespace NightCodeForge.Infrastructure.Tests.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using NightCodeForge.Infrastructure.Configuration;
using Xunit;

public class HostConfigurationSourcesTests : IDisposable
{
    private readonly string _tempDirectory;

    public HostConfigurationSourcesTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "ncf_config_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
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
    public void AddUserConfiguration_WhenFileDoesNotExist_ReturnsAbsentAndPreservesDefaults()
    {
        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:Endpoint"] = "http://factory-default:11434",
            ["LlmProvider:ModelId"] = "factory-model"
        });

        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.Absent, status);

        var config = builder.Build();
        Assert.Equal("http://factory-default:11434", config["LlmProvider:Endpoint"]);
        Assert.Equal("factory-model", config["LlmProvider:ModelId"]);
    }

    [Fact]
    public void AddUserConfiguration_WhenFileExistsAndValid_ReturnsLoadedAndOverridesDefaults()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");

        var userJson = """
        {
          "LlmProvider": {
            "Endpoint": "http://user-override:11434",
            "ModelId": "custom-user-model"
          }
        }
        """;
        File.WriteAllText(configFile, userJson);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:Endpoint"] = "http://factory-default:11434",
            ["LlmProvider:ModelId"] = "factory-model",
            ["LlmProvider:ContextWindowTokens"] = "131072"
        });

        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);

        var config = builder.Build();
        Assert.Equal("http://user-override:11434", config["LlmProvider:Endpoint"]);
        Assert.Equal("custom-user-model", config["LlmProvider:ModelId"]);
        // Non-overridden factory value remains intact
        Assert.Equal("131072", config["LlmProvider:ContextWindowTokens"]);
    }

    [Fact]
    public void AddUserConfiguration_WhenFileContainsInvalidJson_ReturnsInvalidJsonAndFailsOpen()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");

        // Corrupted JSON with syntax errors
        File.WriteAllText(configFile, "{ \"LlmProvider\": { broken unclosed json");

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProvider:Endpoint"] = "http://factory-default:11434",
            ["LlmProvider:ModelId"] = "factory-model"
        });

        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.InvalidJson, status);

        // Fail-open: builder.Build() must not throw and factory defaults must remain in effect
        var config = builder.Build();
        Assert.Equal("http://factory-default:11434", config["LlmProvider:Endpoint"]);
        Assert.Equal("factory-model", config["LlmProvider:ModelId"]);
    }

    [Fact]
    public void AddUserConfiguration_SupportsJsonWithCommentsAndTrailingCommas()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");

        // JSONC: single-line comments and trailing commas
        var userJsonc = """
        {
          // User local Ollama configuration override
          "LlmProvider": {
            "Endpoint": "http://192.168.1.100:11434",
            "ModelId": "qwen2.5-coder:32b",
          },
        }
        """;
        File.WriteAllText(configFile, userJsonc);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);

        var config = builder.Build();
        Assert.Equal("http://192.168.1.100:11434", config["LlmProvider:Endpoint"]);
        Assert.Equal("qwen2.5-coder:32b", config["LlmProvider:ModelId"]);
    }

    [Fact]
    public void AddUserConfiguration_ConfigurationPrecedence_InsertsAfterAppsettingsAnchor()
    {
        // 1. Factory base settings
        var appsettingsFile = Path.Combine(_tempDirectory, "appsettings.json");
        File.WriteAllText(appsettingsFile, """
        {
          "SettingA": "factory-base",
          "SettingB": "factory-base",
          "SettingC": "factory-base"
        }
        """);

        // 2. Factory environment settings
        var appsettingsDevFile = Path.Combine(_tempDirectory, "appsettings.Development.json");
        File.WriteAllText(appsettingsDevFile, """
        {
          "SettingB": "factory-dev",
          "SettingC": "factory-dev"
        }
        """);

        // 3. User configuration layer
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var userConfigFile = Path.Combine(configDir, "config.json");
        File.WriteAllText(userConfigFile, """
        {
          "SettingA": "user-override",
          "SettingB": "user-override",
          "SettingC": "user-override"
        }
        """);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddJsonFile("appsettings.json", optional: false);
        builder.AddJsonFile("appsettings.Development.json", optional: false);

        // 4. Higher-precedence source registered after factory appsettings (e.g. environment variable simulation)
        builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SettingC"] = "env-override"
        });

        // Add user configuration with anchor repositioning
        var status = HostConfigurationSources.AddUserConfiguration(
            builder,
            _tempDirectory,
            environment: "Development",
            logger: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);

        var config = builder.Build();

        // SettingA: User override replaces factory base
        Assert.Equal("user-override", config["SettingA"]);

        // SettingB: User override replaces factory dev
        Assert.Equal("user-override", config["SettingB"]);

        // SettingC: Environment variable (or CLI args) takes precedence over user config
        Assert.Equal("env-override", config["SettingC"]);
    }

    [Fact]
    public void AddUserConfiguration_Idempotency_CallingMultipleTimesDoesNotDuplicateSource()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");
        File.WriteAllText(configFile, """{ "TestKey": "TestValue" }""");

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        var status1 = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);
        var initialSourceCount = builder.Sources.Count;

        var status2 = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status1);
        Assert.Equal(UserConfigLoadStatus.Loaded, status2);
        Assert.Equal(initialSourceCount, builder.Sources.Count);
    }

    [Fact]
    public void GetFullPath_ResolvesExpectedPath()
    {
        var expected = Path.Combine(_tempDirectory, "config", "config.json");
        var actual = HostConfigurationSources.GetFullPath(_tempDirectory);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AddUserConfiguration_ReloadOnChange_UpdatesConfigurationOnDiskChange()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");
        await File.WriteAllTextAsync(configFile, """{ "DynamicKey": "InitialValue" }""");

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);
        Assert.Equal(UserConfigLoadStatus.Loaded, status);

        var config = builder.Build();
        Assert.Equal("InitialValue", config["DynamicKey"]);

        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ChangeToken.OnChange(config.GetReloadToken, () => reloaded.TrySetResult());

        // Mutate the file on disk
        await File.WriteAllTextAsync(configFile, """{ "DynamicKey": "UpdatedValue" }""");

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("UpdatedValue", config["DynamicKey"]);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{ \"Key\": 1, \"key\": 2 }")]
    public void AddUserConfiguration_WhenFileIsAcceptedByJsonButRejectedByProvider_FailsOpen(string content)
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"), content);

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Factory"] = "value" });

        var status = HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger: null);

        Assert.Equal(UserConfigLoadStatus.InvalidJson, status);
        Assert.Equal("value", builder.Build()["Factory"]);
    }

    [Fact]
    public void AddUserConfiguration_OnConfigurationManager_PlacesUserLayerBetweenAppsettingsAndOtherSources()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "appsettings.json"),
            """{ "A": "base", "B": "base", "C": "base" }""");
        File.WriteAllText(Path.Combine(_tempDirectory, "appsettings.Development.json"),
            """{ "B": "dev", "C": "dev" }""");
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            """{ "A": "user", "B": "user", "C": "user" }""");

        // Same type as WebApplicationBuilder.Configuration: providers are built eagerly on Add.
        var manager = new ConfigurationManager();
        manager.SetBasePath(_tempDirectory);
        manager.AddJsonFile("appsettings.json", optional: false);
        manager.AddJsonFile("appsettings.Development.json", optional: false);
        manager.AddInMemoryCollection(new Dictionary<string, string?> { ["C"] = "env" });

        var status = HostConfigurationSources.AddUserConfiguration(manager, _tempDirectory, "Development", logger: null);

        Assert.Equal(UserConfigLoadStatus.Loaded, status);
        Assert.Equal("user", manager["A"]);
        Assert.Equal("user", manager["B"]);
        Assert.Equal("env", manager["C"]);
    }

    [Fact]
    public async Task AddUserConfiguration_WhenFileBecomesInvalidAfterLoad_LogsErrorAndDoesNotThrow()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");
        await File.WriteAllTextAsync(configFile, """{ "DynamicKey": "InitialValue" }""");

        var logger = new CapturingLogger();
        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        HostConfigurationSources.AddUserConfiguration(builder, _tempDirectory, environment: null, logger);
        var config = builder.Build();

        await File.WriteAllTextAsync(configFile, "{ broken");

        await logger.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(logger.Messages, m => m.Contains("could not be reloaded"));
    }

    [Fact]
    public async Task AddUserConfiguration_WhenFileBecomesInvalidAfterLoad_UsesDeferredLoggerAccessor()
    {
        var configDir = Path.Combine(_tempDirectory, "config");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "config.json");
        await File.WriteAllTextAsync(configFile, """{ "DynamicKey": "InitialValue" }""");

        var logger = new CapturingLogger();
        ILogger? deferredLogger = null;

        var builder = new ConfigurationBuilder();
        builder.SetBasePath(_tempDirectory);
        HostConfigurationSources.AddUserConfiguration(
            builder,
            _tempDirectory,
            environment: null,
            logger: null,
            loggerAccessor: () => deferredLogger);

        var config = builder.Build();
        deferredLogger = logger;

        await File.WriteAllTextAsync(configFile, "{ broken");

        await logger.ErrorLogged.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(logger.Messages, m => m.Contains("could not be reloaded"));
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly TaskCompletionSource _errorLogged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Messages { get; } = [];

        public Task ErrorLogged => _errorLogged.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }

            if (logLevel == LogLevel.Error)
            {
                _errorLogged.TrySetResult();
            }
        }
    }
}
