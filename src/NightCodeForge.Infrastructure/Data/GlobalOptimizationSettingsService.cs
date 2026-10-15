using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Singleton service managing global local LLM optimization flags with thread-safe in-memory caching
/// and asynchronous database persistence to the SystemSettings table.
/// </summary>
public class GlobalOptimizationSettingsService : IGlobalOptimizationSettingsService
{
    public const string SettingKey = "GlobalOptimizationFlags";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GlobalOptimizationSettingsService> _logger;
    private readonly object _lock = new();
    private LocalOptimizationFlags _cachedFlags = new();

    public event Action<LocalOptimizationFlags>? OnFlagsChanged;

    public GlobalOptimizationSettingsService(
        IServiceScopeFactory scopeFactory,
        ILogger<GlobalOptimizationSettingsService>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalOptimizationSettingsService>.Instance;
    }

    /// <inheritdoc />
    public LocalOptimizationFlags GetFlags()
    {
        lock (_lock)
        {
            return _cachedFlags.Clone();
        }
    }

    /// <inheritdoc />
    public async Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default)
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
                var flags = JsonSerializer.Deserialize<LocalOptimizationFlags>(setting.Value, (JsonSerializerOptions?)null);
                if (flags != null)
                {
                    lock (_lock)
                    {
                        _cachedFlags = flags.Clone();
                    }
                    _logger.LogInformation("Loaded global optimization flags from database.");
                    return GetFlags();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load global optimization flags from database; using default configuration.");
        }

        return GetFlags();
    }

    /// <inheritdoc />
    public async Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flags);

        var cloned = flags.Clone();
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
            _cachedFlags = cloned;
        }

        _logger.LogInformation("Updated and persisted global optimization flags.");

        try
        {
            OnFlagsChanged?.Invoke(cloned);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error firing OnFlagsChanged callback.");
        }
    }
}
