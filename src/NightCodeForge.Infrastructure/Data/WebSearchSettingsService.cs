using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Singleton service managing web search credentials and configuration with thread-safe in-memory caching
/// and asynchronous database persistence to the SystemSettings table.
/// </summary>
public class WebSearchSettingsService : IWebSearchSettingsService
{
    public const string SettingKey = "WebSearchSettings";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<WebSearchSettingsService> _logger;
    private readonly object _lock = new();
    private WebSearchSettings _cachedSettings = new();

    public event Action<WebSearchSettings>? OnSettingsChanged;

    public WebSearchSettingsService(
        IServiceScopeFactory scopeFactory,
        IConfiguration? configuration = null,
        ILogger<WebSearchSettingsService>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSearchSettingsService>.Instance;

        // Initialize fallback from environment variable or configuration if available
        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        var envKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
        var configKey = _configuration?["WebSearch:Ollama:ApiKey"] ?? _configuration?["OLLAMA_API_KEY"];
        var endpoint = _configuration?["WebSearch:Ollama:Endpoint"];

        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(configKey))
            {
                _cachedSettings.ApiKey = configKey;
            }
            else if (!string.IsNullOrWhiteSpace(envKey))
            {
                _cachedSettings.ApiKey = envKey;
            }

            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                _cachedSettings.Endpoint = endpoint;
            }
        }
    }

    /// <inheritdoc />
    public WebSearchSettings GetSettings()
    {
        lock (_lock)
        {
            var clone = _cachedSettings.Clone();
            // If API key is still empty in cache, check dynamic environment variable as a fallback
            if (string.IsNullOrWhiteSpace(clone.ApiKey))
            {
                var envKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                if (!string.IsNullOrWhiteSpace(envKey))
                {
                    clone.ApiKey = envKey;
                }
            }
            return clone;
        }
    }

    /// <inheritdoc />
    public async Task<WebSearchSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var setting = await dbContext.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

            if (setting != null && !string.IsNullOrWhiteSpace(setting.Value))
            {
                var loaded = JsonSerializer.Deserialize<WebSearchSettings>(setting.Value, (JsonSerializerOptions?)null);
                if (loaded != null)
                {
                    // If DB doesn't have an API key, check env or configuration
                    if (string.IsNullOrWhiteSpace(loaded.ApiKey))
                    {
                        var envKey = Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
                        var configKey = _configuration?["WebSearch:Ollama:ApiKey"] ?? _configuration?["OLLAMA_API_KEY"];
                        loaded.ApiKey = !string.IsNullOrWhiteSpace(configKey) ? configKey : (envKey ?? string.Empty);
                    }

                    if (string.IsNullOrWhiteSpace(loaded.Endpoint))
                    {
                        loaded.Endpoint = WebSearchSettings.DefaultEndpoint;
                    }

                    lock (_lock)
                    {
                        _cachedSettings = loaded.Clone();
                    }
                    _logger.LogInformation("Loaded web search settings from database.");
                    return GetSettings();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load web search settings from database; retaining current in-memory defaults.");
        }

        return GetSettings();
    }

    /// <inheritdoc />
    public async Task UpdateSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var cloned = settings.Clone();
        var json = JsonSerializer.Serialize(cloned, (JsonSerializerOptions?)null);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await dbContext.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

        if (existing != null)
        {
            existing.Value = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            dbContext.SystemSettings.Add(new SystemSetting
            {
                Key = SettingKey,
                Value = json,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        lock (_lock)
        {
            _cachedSettings = cloned;
        }

        _logger.LogInformation("Updated and persisted web search settings.");

        try
        {
            OnSettingsChanged?.Invoke(cloned);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking OnSettingsChanged callback.");
        }
    }
}
