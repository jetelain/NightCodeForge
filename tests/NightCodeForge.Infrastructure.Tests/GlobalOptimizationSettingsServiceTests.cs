using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        var service = new GlobalOptimizationSettingsService(scopeFactory, NullLogger<GlobalOptimizationSettingsService>.Instance);

        var flags = service.GetFlags();

        Assert.NotNull(flags);
        Assert.True(flags.GuidedGrammarDecoding);
        Assert.True(flags.DeterministicSymbolMap);
        Assert.True(flags.StructuredDiagnosticExtraction);
        Assert.True(flags.JitModelPreload);
    }

    [Fact]
    public async Task UpdateFlagsAsync_PersistsToDatabase_AndUpdatesCache()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new GlobalOptimizationSettingsService(scopeFactory, NullLogger<GlobalOptimizationSettingsService>.Instance);

        bool eventFired = false;
        LocalOptimizationFlags? updatedFromEvent = null;
        service.OnFlagsChanged += flags =>
        {
            eventFired = true;
            updatedFromEvent = flags;
        };

        var customFlags = new LocalOptimizationFlags
        {
            GuidedGrammarDecoding = false,
            DeterministicSymbolMap = false,
            StructuredDiagnosticExtraction = true,
            JitModelPreload = true
        };

        await service.UpdateFlagsAsync(customFlags);

        // Verify cache updated
        var cached = service.GetFlags();
        Assert.False(cached.GuidedGrammarDecoding);
        Assert.False(cached.DeterministicSymbolMap);
        Assert.True(cached.StructuredDiagnosticExtraction);
        Assert.True(cached.JitModelPreload);

        // Verify event fired
        Assert.True(eventFired);
        Assert.NotNull(updatedFromEvent);
        Assert.False(updatedFromEvent.GuidedGrammarDecoding);

        // Verify database persistence
        using var db = new AppDbContext(_dbOptions);
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == GlobalOptimizationSettingsService.SettingKey);
        Assert.NotNull(setting);
        Assert.Contains("\"GuidedGrammarDecoding\":false", setting.Value);
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
                Value = "{\"GuidedGrammarDecoding\":false,\"DeterministicSymbolMap\":false,\"StructuredDiagnosticExtraction\":true,\"JitModelPreload\":true}",
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new GlobalOptimizationSettingsService(scopeFactory, NullLogger<GlobalOptimizationSettingsService>.Instance);

        var loaded = await service.LoadFlagsAsync();

        Assert.NotNull(loaded);
        Assert.False(loaded.GuidedGrammarDecoding);
        Assert.False(loaded.DeterministicSymbolMap);
        Assert.True(loaded.StructuredDiagnosticExtraction);
        Assert.True(loaded.JitModelPreload);

        // Check that GetFlags also reflects it
        var cached = service.GetFlags();
        Assert.False(cached.GuidedGrammarDecoding);
    }
}
