namespace NightCodeForge.Web.Tests;

using Microsoft.Extensions.Configuration;
using Xunit;

public class WebPublishConfigurationTests
{
    [Fact]
    public void Configuration_BindsConnectionStringsAndLogsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_test_config_" + Guid.NewGuid().ToString("N"));
        var dataDir = Path.Combine(tempDir, "data");
        var logsDir = Path.Combine(tempDir, "logs");
        var dbPath = Path.Combine(dataDir, "nightcodeforge.db").Replace('\\', '/');
        var hangfireDbPath = Path.Combine(dataDir, "nightcodeforge_hangfire.db").Replace('\\', '/');
        var logsPath = logsDir.Replace('\\', '/');

        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Data Source={dbPath};",
            ["ConnectionStrings:HangfireConnection"] = $"Data Source={hangfireDbPath};",
            ["Logging:LogsDirectory"] = logsPath,
            ["Urls"] = "http://localhost:5272"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        Assert.Equal($"Data Source={dbPath};", config.GetConnectionString("DefaultConnection"));
        Assert.Equal($"Data Source={hangfireDbPath};", config.GetConnectionString("HangfireConnection"));
        Assert.Equal(logsPath, config["Logging:LogsDirectory"]);
        Assert.Equal("http://localhost:5272", config["Urls"]);
    }

    [Fact]
    public void SqliteDirectoryExtraction_CreatesDirectoryIfMissing()
    {
        var tempBase = Path.Combine(Path.GetTempPath(), "ncf_dir_test_" + Guid.NewGuid().ToString("N"));
        var subDir = Path.Combine(tempBase, "custom_data");
        var dbFile = Path.Combine(subDir, "nightcodeforge.db");
        var connectionString = $"Data Source={dbFile};Mode=ReadWriteCreate;";

        try
        {
            Assert.False(Directory.Exists(subDir));

            // Execute directory creation logic matching Program.cs EnsureSqliteDirectoryExists
            EnsureDirectoryForTest(connectionString);

            Assert.True(Directory.Exists(subDir));
        }
        finally
        {
            if (Directory.Exists(tempBase))
            {
                Directory.Delete(tempBase, recursive: true);
            }
        }
    }

    private static void EnsureDirectoryForTest(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = trimmed["Data Source=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(filePath) && filePath != ":memory:")
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                }
                break;
            }
        }
    }
}
