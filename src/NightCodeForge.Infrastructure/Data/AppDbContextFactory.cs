using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Factory for creating <see cref="AppDbContext"/> instances both at runtime and for EF Core design-time migrations tooling.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>, IDbContextFactory<AppDbContext>
{
    private readonly DbContextOptions<AppDbContext>? _options;

    public AppDbContextFactory()
    {
    }

    public AppDbContextFactory(DbContextOptions<AppDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public AppDbContext CreateDbContext()
    {
        if (_options == null)
        {
            throw new InvalidOperationException("DbContextOptions<AppDbContext> must be provided to create a runtime AppDbContext.");
        }

        return new AppDbContext(_options);
    }

    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=nightcodeforge.db;Cache=Shared;Mode=ReadWriteCreate;",
            sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        return new AppDbContext(optionsBuilder.Options);
    }
}
