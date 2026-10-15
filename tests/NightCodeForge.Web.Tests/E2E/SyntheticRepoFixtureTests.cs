using Microsoft.EntityFrameworkCore;
using Xunit;

namespace NightCodeForge.Web.Tests.E2E;

public class SyntheticRepoFixtureTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task SyntheticRepoFixture_Initializes_Builds_AndPassesBaselineTests()
    {
        await using var fixture = new SyntheticRepoFixture();
        await fixture.InitializeAsync();

        // Verify directory and key files exist
        Assert.True(Directory.Exists(fixture.RepositoryPath));
        Assert.True(File.Exists(Path.Combine(fixture.RepositoryPath, "src", "SyntheticLib", "Calculator.cs")));
        Assert.True(File.Exists(Path.Combine(fixture.RepositoryPath, "tests", "SyntheticLib.Tests", "CalculatorTests.cs")));
        Assert.True(Directory.GetFiles(fixture.RepositoryPath, "*.sln*").Length > 0);

        // Verify git status & branch
        var status = await fixture.RunGitAsync("status --short");
        Assert.True(string.IsNullOrWhiteSpace(status));

        var branch = await fixture.RunGitAsync("branch --show-current");
        Assert.Equal("main", branch);

        // Run baseline dotnet test
        var exitCode = await fixture.RunDotnetTestAsync();
        Assert.Equal(0, exitCode);
    }
}

