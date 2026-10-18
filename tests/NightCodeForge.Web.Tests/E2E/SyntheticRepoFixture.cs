using System.Diagnostics;
using System.Text;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Tests.E2E;

/// <summary>
/// Manages an ephemeral synthetic .NET application in a randomized temporary directory with Git history,
/// unit tests, and buildable source code for end-to-end acceptance testing.
/// </summary>
public sealed class SyntheticRepoFixture : IAsyncDisposable
{
    public string RepositoryPath { get; }
    public string WorkspaceName { get; }

    public SyntheticRepoFixture()
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        WorkspaceName = $"E2E-App-{runId}";
        RepositoryPath = Path.Combine(Path.GetTempPath(), $"ncf-e2e-{runId}");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RepositoryPath);
        var srcDir = Path.Combine(RepositoryPath, "src", "SyntheticLib");
        var testDir = Path.Combine(RepositoryPath, "tests", "SyntheticLib.Tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testDir);
        Directory.CreateDirectory(Path.Combine(RepositoryPath, ".nightcodeforge"));

        // 1. SyntheticLib.csproj
        await File.WriteAllTextAsync(Path.Combine(srcDir, "SyntheticLib.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>");

        // 2. Calculator.cs
        await File.WriteAllTextAsync(Path.Combine(srcDir, "Calculator.cs"), @"namespace SyntheticLib;

/// <summary>
/// Arithmetic computation engine.
/// </summary>
public class Calculator
{
    /// <summary>
    /// Computes the sum of two integers.
    /// </summary>
    public int Add(int a, int b) => a + b;

    /// <summary>
    /// Computes the difference between two integers.
    /// </summary>
    public int Subtract(int a, int b) => a - b;
}
");

        // 3. SyntheticLib.Tests.csproj
        await File.WriteAllTextAsync(Path.Combine(testDir, "SyntheticLib.Tests.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Microsoft.NET.Test.Sdk"" Version=""17.14.1"" />
    <PackageReference Include=""xunit"" Version=""2.9.3"" />
    <PackageReference Include=""xunit.runner.visualstudio"" Version=""3.1.4"" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include=""..\..\src\SyntheticLib\SyntheticLib.csproj"" />
  </ItemGroup>
</Project>");

        // 4. CalculatorTests.cs
        await File.WriteAllTextAsync(Path.Combine(testDir, "CalculatorTests.cs"), @"namespace SyntheticLib.Tests;

using SyntheticLib;
using Xunit;

public class CalculatorTests
{
    private readonly Calculator _calc = new();

    [Fact]
    public void Add_ReturnsCorrectSum()
    {
        Assert.Equal(5, _calc.Add(2, 3));
    }

    [Fact]
    public void Subtract_ReturnsCorrectDifference()
    {
        Assert.Equal(1, _calc.Subtract(4, 3));
    }
}
");

        // 5. Config, AGENTS.md, .gitignore
        await File.WriteAllTextAsync(Path.Combine(RepositoryPath, ".nightcodeforge", "config.json"),
            @"{""CodeStack"": ""dotnet""}"); // Commands are computed by CodeStackPresets.ResolveConfig, to ensure tests are done with default values

        await File.WriteAllTextAsync(Path.Combine(RepositoryPath, "AGENTS.md"), 
            "# Directives\n- Maintain 100% test coverage.\n- Public APIs must include XML doc.\n- Add unit tests for every method.");

        await File.WriteAllTextAsync(Path.Combine(RepositoryPath, ".gitignore"), "bin/\nobj/\n.vs/\ndocs/tasks/\ndocs/transcripts/\n");
        await File.WriteAllTextAsync(Path.Combine(RepositoryPath, ".gitattributes"), "* text=auto eol=lf\n");

        // Create solution file so dotnet build & dotnet test work cleanly in root directory
        await RunCliAsync("dotnet", "new sln");
        await RunCliAsync("dotnet", "sln add src/SyntheticLib/SyntheticLib.csproj");
        await RunCliAsync("dotnet", "sln add tests/SyntheticLib.Tests/SyntheticLib.Tests.csproj");

        // Initialize Git repo & initial baseline commit
        await RunGitAsync("init -b main");
        await RunGitAsync("config user.name \"NCF Acceptance Bot\"");
        await RunGitAsync("config user.email \"bot@nightcodeforge.local\"");
        await RunGitAsync("config commit.gpgsign false");
        await RunGitAsync("config core.autocrlf false");
        await RunGitAsync("config receive.denyCurrentBranch updateInstead");
        await RunGitAsync("add -A");
        await RunGitAsync("add --renormalize .");
        await RunGitAsync("commit -m \"Initial commit: Scaffold synthetic test library\" --no-verify");
    }

    public async Task<int> RunDotnetTestAsync()
    {
        var psi = new ProcessStartInfo("dotnet", "test")
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    public async Task<string> RunGitAsync(string args)
    {
        return await RunCliAsync("git", args);
    }

    private async Task<string> RunCliAsync(string fileName, string args)
    {
        var psi = new ProcessStartInfo(fileName, args)
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command '{fileName} {args}' in '{RepositoryPath}' failed with code {proc.ExitCode}: {stderr}\n{stdout}");
        }
        return stdout.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("KEEP_TEST_ARTIFACTS") == "true")
            return;

        try
        {
            if (Directory.Exists(RepositoryPath))
            {
                // Clear read-only attributes before deletion
                foreach (var file in Directory.EnumerateFiles(RepositoryPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(RepositoryPath, recursive: true);
            }
        }
        catch { /* best-effort cleanup */ }
    }
}
