using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Persists global operations state to the SQLite SystemSettings table via IDbContextFactory.
/// </summary>
public class OperationsStateStore : IOperationsStateStore
{
    private const string SettingKey = "Operations.IsPaused";
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<OperationsStateStore> _logger;

    public OperationsStateStore(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<OperationsStateStore> logger)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> GetIsPausedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var setting = await dbContext.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

            if (setting != null && bool.TryParse(setting.Value, out var isPaused))
            {
                return isPaused;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load operations paused state from database.");
            throw;
        }
    }

    public async Task SetIsPausedAsync(bool isPaused, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await dbContext.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == SettingKey, cancellationToken);

            var valueStr = isPaused.ToString();

            if (existing != null)
            {
                existing.Value = valueStr;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                dbContext.SystemSettings.Add(new SystemSetting
                {
                    Key = SettingKey,
                    Value = valueStr,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Persisted Operations.IsPaused = {IsPaused} to database.", isPaused);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist operations paused state ({IsPaused}) to database.", isPaused);
            throw;
        }
    }
}
