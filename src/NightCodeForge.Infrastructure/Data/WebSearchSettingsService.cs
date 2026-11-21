using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Configuration;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Singleton service managing non-secret web search settings with thread-safe in-memory caching
/// and asynchronous database persistence to the SystemSettings table.
/// </summary>
public class WebSearchSettingsService : IWebSearchSettingsService
{
    public const string SettingKey = "WebSearchSettings";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebSearchSettingsService> _logger;
    private readonly object _lock = new();
    private WebSearchSettings _cachedSettings = new();

    public event Action<WebSearchSettings>? OnSettingsChanged;

    public WebSearchSettingsService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<WebSearchSettingsService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        var endpoint = _configuration[ApiKeySecretConstants.WebSearchOllamaEndpointConfigPath];

        lock (_lock)
        {
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
            return _cachedSettings.Clone();
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
                if (LegacyPlaintextSecretDetector.ContainsLegacyApiKeyProperty(setting.Value))
                {
                    _logger.LogWarning(
                        "SECURITY WARNING: Legacy plaintext API key detected in SQLite SystemSettings entry '{SettingKey}'. Plaintext database keys are ignored and no longer loaded. Re-save the key in Settings (stored in the OS keyring via Pmad.KeySharp) or supply '{EnvVar}' and re-save settings to scrub the database row.",
                        SettingKey,
                        ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable);
                }

                var loaded = JsonSerializer.Deserialize<WebSearchSettings>(setting.Value, (JsonSerializerOptions?)null);
                if (loaded != null)
                {
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
