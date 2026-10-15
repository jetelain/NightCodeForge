using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class WebSearchSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IServiceProvider _serviceProvider;

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
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new WebSearchSettingsService(scopeFactory);

        var settings = service.GetSettings();

        Assert.NotNull(settings);
        Assert.Equal("Ollama", settings.Provider);
        Assert.Equal(WebSearchSettings.DefaultEndpoint, settings.Endpoint);
        Assert.Equal(5, settings.DefaultMaxResults);
    }

    [Fact]
    public async Task UpdateSettingsAsync_PersistsToDatabase_AndUpdatesCache()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new WebSearchSettingsService(scopeFactory);

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
            ApiKey = "ollama_live_api_key_xyz",
            Endpoint = "https://custom.search.internal/api/web_search",
            DefaultMaxResults = 8
        };

        await service.UpdateSettingsAsync(custom);

        // Verify cache updated
        var cached = service.GetSettings();
        Assert.Equal("ollama_live_api_key_xyz", cached.ApiKey);
        Assert.Equal("https://custom.search.internal/api/web_search", cached.Endpoint);
        Assert.Equal(8, cached.DefaultMaxResults);

        // Verify event fired
        Assert.True(eventFired);
        Assert.NotNull(updatedFromEvent);
        Assert.Equal("ollama_live_api_key_xyz", updatedFromEvent.ApiKey);

        // Verify database persistence
        using var db = new AppDbContext(_dbOptions);
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == WebSearchSettingsService.SettingKey);
        Assert.NotNull(setting);
        Assert.Contains("\"ApiKey\":\"ollama_live_api_key_xyz\"", setting.Value);
    }

    [Fact]
    public async Task LoadSettingsAsync_LoadsFromDatabase_WhenRecordExists()
    {
        // Seed DB directly
        using (var db = new AppDbContext(_dbOptions))
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = WebSearchSettingsService.SettingKey,
                Value = "{\"Provider\":\"Ollama\",\"ApiKey\":\"db_stored_key_999\",\"Endpoint\":\"https://ollama.com/api/web_search\",\"DefaultMaxResults\":3}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new WebSearchSettingsService(scopeFactory);

        var loaded = await service.LoadSettingsAsync();

        Assert.NotNull(loaded);
        Assert.Equal("db_stored_key_999", loaded.ApiKey);
        Assert.Equal(3, loaded.DefaultMaxResults);

        // GetSettings reflects it
        var cached = service.GetSettings();
        Assert.Equal("db_stored_key_999", cached.ApiKey);
    }

    [Fact]
    public void ConfigurationFallback_UsedWhenApiKeyNotInDatabase()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["WebSearch:Ollama:ApiKey"] = "config_key_555"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new WebSearchSettingsService(scopeFactory, configuration);

        var settings = service.GetSettings();
        Assert.Equal("config_key_555", settings.ApiKey);
    }
}
