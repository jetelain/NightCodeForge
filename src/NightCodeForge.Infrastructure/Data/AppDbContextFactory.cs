using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF Core migrations tooling.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=nightcodeforge.db;Cache=Shared;Mode=ReadWriteCreate;",
            sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        return new AppDbContext(optionsBuilder.Options);
    }
}
