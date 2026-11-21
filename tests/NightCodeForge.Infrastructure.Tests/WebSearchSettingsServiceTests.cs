using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class WebSearchSettingsServiceTests : IDisposable
{
    private sealed class RecordingLogger : ILogger<WebSearchSettingsService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _emptyConfig = new ConfigurationBuilder().Build();
    private readonly ILogger<WebSearchSettingsService> _logger = NullLogger<WebSearchSettingsService>.Instance;

    private WebSearchSettingsService CreateService(IConfiguration? config = null, ILogger<WebSearchSettingsService>? logger = null)
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new WebSearchSettingsService(scopeFactory, config ?? _emptyConfig, logger ?? _logger);
    }

    public WebSearchSettingsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var db = new AppDbContext(_dbOptions))
        {
            db.Database.EnsureCreated();
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(_dbOptions));
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public void GetSettings_ReturnsDefaultSettings_WhenUninitialized()
    {
        var service = CreateService();

        var settings = service.GetSettings();

        Assert.NotNull(settings);
        Assert.Equal("Ollama", settings.Provider);
        Assert.Equal(WebSearchSettings.DefaultEndpoint, settings.Endpoint);
        Assert.Equal(5, settings.DefaultMaxResults);
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsNonSecretFieldsToDatabase_AndScrubsLegacyApiKey()
    {
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            seedDb.SystemSettings.Add(new SystemSetting
            {
                Key = WebSearchSettingsService.SettingKey,
                Value = "{\"Provider\":\"Ollama\",\"ApiKey\":\"legacy_db_secret_999\",\"Endpoint\":\"https://ollama.com/api/web_search\",\"DefaultMaxResults\":3}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        var service = CreateService();

        bool eventFired = false;
        WebSearchSettings? updatedFromEvent = null;
        service.OnSettingsChanged += s =>
        {
            eventFired = true;
            updatedFromEvent = s;
        };

        var custom = new WebSearchSettings
        {
            Provider = "Ollama",
            Endpoint = "https://custom.search.internal/api/web_search",
            DefaultMaxResults = 8
        };

        await service.UpdateSettingsAsync(custom);

        var cached = service.GetSettings();
        Assert.Equal("https://custom.search.internal/api/web_search", cached.Endpoint);
        Assert.Equal(8, cached.DefaultMaxResults);

        Assert.True(eventFired);
        Assert.NotNull(updatedFromEvent);
        Assert.Equal("https://custom.search.internal/api/web_search", updatedFromEvent.Endpoint);

        using var db = new AppDbContext(_dbOptions);
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == WebSearchSettingsService.SettingKey);
        Assert.NotNull(setting);
        Assert.DoesNotContain("ApiKey", setting.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy_db_secret_999", setting.Value, StringComparison.Ordinal);
        Assert.Contains("\"Endpoint\":\"https://custom.search.internal/api/web_search\"", setting.Value);
    }

    [Fact]
    public async Task LoadSettingsAsync_LoadsNonSecretFields_AndLogsRedactedWarningWhenLegacyApiKeyExistsInDatabase()
    {
        const string legacySecret = "db_stored_secret_key_999";
        using (var db = new AppDbContext(_dbOptions))
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = WebSearchSettingsService.SettingKey,
                Value = $"{{\"Provider\":\"Ollama\",\"ApiKey\":\"{legacySecret}\",\"Endpoint\":\"https://custom.endpoint/api/web_search\",\"DefaultMaxResults\":3}}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var recordingLogger = new RecordingLogger();
        var service = CreateService(logger: recordingLogger);

        var loaded = await service.LoadSettingsAsync();

        Assert.NotNull(loaded);
        Assert.Equal("https://custom.endpoint/api/web_search", loaded.Endpoint);
        Assert.Equal(3, loaded.DefaultMaxResults);

        var warnings = recordingLogger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        Assert.Contains("OS keyring", warnings[0].Message);
        Assert.DoesNotContain(legacySecret, warnings[0].Message);
    }

    [Fact]
    public void Configuration_LoadsEndpointOnly_AndIgnoresPlaintextApiKeyEntries()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["WebSearch:Ollama:ApiKey"] = "config_key_555",
            ["OLLAMA_API_KEY"] = "config_env_key_666",
            ["WebSearch:Ollama:Endpoint"] = "https://search.internal/api/web_search"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var service = CreateService(configuration);

        var settings = service.GetSettings();
        Assert.Equal("https://search.internal/api/web_search", settings.Endpoint);
    }
}
