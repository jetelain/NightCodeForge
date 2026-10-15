using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class GlobalOptimizationSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IServiceProvider _serviceProvider;

    public GlobalOptimizationSettingsServiceTests()
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
    public void GetFlags_ReturnsDefaultFlags_WhenUninitialized()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new GlobalOptimizationSettingsService(scopeFactory);

        var flags = service.GetFlags();

        Assert.NotNull(flags);
        Assert.True(flags.ThinkingTraceCompaction);
        Assert.True(flags.LoopCircuitBreaker);
        Assert.True(flags.GuidedGrammarDecoding);
        Assert.Equal(100, flags.ThinkingKeepVerbatimCount);
        Assert.Equal(100, flags.ToolKeepVerbatimCount);
        Assert.Equal(2000, flags.ToolMaxResultLength);
        Assert.Equal(1750, flags.ToolTruncateToLength);
    }

    [Fact]
    public async Task UpdateFlagsAsync_PersistsToDatabase_AndUpdatesCache()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new GlobalOptimizationSettingsService(scopeFactory);

        bool eventFired = false;
        LocalOptimizationFlags? updatedFromEvent = null;
        service.OnFlagsChanged += flags =>
        {
            eventFired = true;
            updatedFromEvent = flags;
        };

        var customFlags = new LocalOptimizationFlags
        {
            ThinkingTraceCompaction = false,
            ThinkingKeepVerbatimCount = 42,
            ToolKeepVerbatimCount = 15,
            ToolMaxResultLength = 5000,
            ToolTruncateToLength = 4000
        };

        await service.UpdateFlagsAsync(customFlags);

        // Verify cache updated
        var cached = service.GetFlags();
        Assert.False(cached.ThinkingTraceCompaction);
        Assert.Equal(42, cached.ThinkingKeepVerbatimCount);
        Assert.Equal(15, cached.ToolKeepVerbatimCount);
        Assert.Equal(5000, cached.ToolMaxResultLength);
        Assert.Equal(4000, cached.ToolTruncateToLength);

        // Verify event fired
        Assert.True(eventFired);
        Assert.NotNull(updatedFromEvent);
        Assert.Equal(42, updatedFromEvent.ThinkingKeepVerbatimCount);

        // Verify database persistence
        using var db = new AppDbContext(_dbOptions);
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == GlobalOptimizationSettingsService.SettingKey);
        Assert.NotNull(setting);
        Assert.Contains("\"ThinkingKeepVerbatimCount\":42", setting.Value);
    }

    [Fact]
    public async Task LoadFlagsAsync_LoadsFromDatabase_WhenRecordExists()
    {
        // Seed DB directly
        using (var db = new AppDbContext(_dbOptions))
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = GlobalOptimizationSettingsService.SettingKey,
                Value = "{\"ThinkingTraceCompaction\":false,\"ThinkingKeepVerbatimCount\":77,\"ToolKeepVerbatimCount\":50,\"ToolMaxResultLength\":1234,\"ToolTruncateToLength\":1000}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new GlobalOptimizationSettingsService(scopeFactory);

        var loaded = await service.LoadFlagsAsync();

        Assert.NotNull(loaded);
        Assert.False(loaded.ThinkingTraceCompaction);
        Assert.Equal(77, loaded.ThinkingKeepVerbatimCount);
        Assert.Equal(50, loaded.ToolKeepVerbatimCount);
        Assert.Equal(1234, loaded.ToolMaxResultLength);
        Assert.Equal(1000, loaded.ToolTruncateToLength);

        // Check that GetFlags also reflects it
        var cached = service.GetFlags();
        Assert.Equal(77, cached.ThinkingKeepVerbatimCount);
    }
}
