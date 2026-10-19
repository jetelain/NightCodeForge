using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

public class WorkspaceIdSeedE2ETests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    [Trait("Category", "E2E")]
    public async Task WorkspaceIdSeed_WhenConfiguredViaEnvironment_SeedsFirstWorkspaceToTargetSeed()
    {
        var prevEnv = Environment.GetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable, "1000");

            using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var context = new AppDbContext(options);
            await context.EnsureDatabaseCreatedAndWalAsync();

            var repo = new EfCoreWorkspaceRepository(context);
            var ws = new Workspace
            {
                Name = "E2E Provisioned Workspace",
                RepositoryPath = "/repos/synthetic-e2e",
                DefaultBaseBranch = "main"
            };

            await repo.CreateAsync(ws);

            Assert.Equal(1000, ws.Id);

            var ws2 = new Workspace
            {
                Name = "Second E2E Workspace",
                RepositoryPath = "/repos/synthetic-e2e-2",
                DefaultBaseBranch = "main"
            };

            await repo.CreateAsync(ws2);

            Assert.Equal(1001, ws2.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable, prevEnv);
        }
    }
}
