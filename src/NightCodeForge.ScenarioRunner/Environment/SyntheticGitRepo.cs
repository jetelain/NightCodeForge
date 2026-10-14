using System.Diagnostics;
using System.Text;

namespace NightCodeForge.ScenarioRunner.Environment;

/// <summary>
/// Manages a synthetic, fully isolated Git repository on disk with buildable C# code, unit tests,
/// and helper methods to simulate compiler failures, git locks, and dirty working trees.
/// </summary>
public sealed class SyntheticGitRepo
{
    public string RepositoryPath { get; }

    public SyntheticGitRepo(string repositoryPath)
    {
        RepositoryPath = repositoryPath ?? throw new ArgumentNullException(nameof(repositoryPath));
    }

    /// <summary>
    /// Initializes a real Git repository with initial C# source files, configuration, and baseline commit.
    /// </summary>
    public async Task InitializeAsync(string initialBranch = "main", CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RepositoryPath))
        {
            Directory.CreateDirectory(RepositoryPath);
        }

        // Initialize Git repo
        await RunGitAsync($"init -b {initialBranch}", cancellationToken);
        await RunGitAsync("config user.name \"NightCodeForge Synthetic Agent\"", cancellationToken);
        await RunGitAsync("config user.email \"agent@nightcodeforge.local\"", cancellationToken);
        await RunGitAsync("config commit.gpgsign false", cancellationToken);
        await RunGitAsync("config receive.denyCurrentBranch updateInstead", cancellationToken);

        // 1. SyntheticLib.csproj
        CreateFile("SyntheticLib.csproj", @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>");

        // 1b. .gitignore
        CreateFile(".gitignore", @"bin/
obj/
*.user
.vs/
");

        // 2. Calculator.cs
        CreateFile("Calculator.cs", @"namespace SyntheticLib;

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

        // 3. .nightcodeforge/config.json
        CreateFile(Path.Combine(".nightcodeforge", "config.json"), @"{
  ""CodeStack"": ""dotnet"",
  ""BuildCommand"": ""dotnet build"",
  ""TestCommand"": ""dotnet test --no-build""
}");

        // 4. .editorconfig
        CreateFile(".editorconfig", @"root = true

[*]
indent_style = space
indent_size = 4
end_of_line = lf
charset = utf-8
trim_trailing_whitespace = true
insert_final_newline = true
");

        // 5. README.md
        CreateFile("README.md", @"# SyntheticLib Test Repository
This is a synthetic repository generated for NightCodeForge end-to-end scenario verification.
Contains Calculator arithmetic service and project configuration.
");

        // 6. AGENTS.md
        CreateFile("AGENTS.md", @"# Autonomous Agent Directives
- Maintain 100% test coverage and zero compiler warnings.
- Keep all XML doc comments intact and accurate.
- If requirements are ambiguous, immediately invoke the 'ask' tool for clarification.
");

        // Commit all baseline files
        await RunGitAsync("add -A", cancellationToken);
        await RunGitAsync("commit -m \"Initial commit: Scaffold synthetic test library\" --no-verify", cancellationToken);
    }

    public string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(RepositoryPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return fullPath;
    }

    public string ReadFile(string relativePath)
    {
        var fullPath = Path.Combine(RepositoryPath, relativePath);
        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    public async Task CommitAllAsync(string message, CancellationToken cancellationToken = default)
    {
        await RunGitAsync("add -A", cancellationToken);
        await RunGitAsync($"commit -m \"{message}\" --no-verify", cancellationToken);
    }

    public async Task CheckoutBranchAsync(string branchName, bool create = false, CancellationToken cancellationToken = default)
    {
        var flag = create ? "-B" : "";
        await RunGitAsync($"checkout {flag} {branchName}".Trim(), cancellationToken);
    }

    /// <summary>
    /// Injects a deliberate C# syntax/compiler error into Calculator.cs.
    /// </summary>
    public void InjectCompilerError(string? mirrorPath = null)
    {
        var path = Path.Combine(RepositoryPath, "Calculator.cs");
        var content = File.ReadAllText(path);
        content += "\n// Deliberate compiler error injected for quality gate test:\nthis is a syntax error;\n";
        File.WriteAllText(path, content, Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(mirrorPath))
        {
            var target = mirrorPath;
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) &&
                (mirrorPath.StartsWith('/') || mirrorPath.StartsWith('~')))
            {
                target = NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslUncPath(mirrorPath, "Ubuntu");
            }
            var mirrorFile = Path.Combine(target, "Calculator.cs");
            if (File.Exists(mirrorFile))
            {
                File.WriteAllText(mirrorFile, content, Encoding.UTF8);
            }
        }
    }

    /// <summary>
    /// Restores Calculator.cs to a clean, buildable state.
    /// </summary>
    public void FixCompilerError(string? mirrorPath = null)
    {
        var path = Path.Combine(RepositoryPath, "Calculator.cs");
        var content = @"namespace SyntheticLib;

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

    /// <summary>
    /// Computes the product of two integers.
    /// </summary>
    public int Multiply(int a, int b) => a * b;
}
";
        File.WriteAllText(path, content, Encoding.UTF8);

        if (!string.IsNullOrWhiteSpace(mirrorPath))
        {
            var target = mirrorPath;
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) &&
                (mirrorPath.StartsWith('/') || mirrorPath.StartsWith('~')))
            {
                target = NightCodeForge.Infrastructure.Git.WslPathConverter.ToWslUncPath(mirrorPath, "Ubuntu");
            }
            var mirrorFile = Path.Combine(target, "Calculator.cs");
            if (File.Exists(mirrorFile))
            {
                File.WriteAllText(mirrorFile, content, Encoding.UTF8);
            }
        }
    }

    /// <summary>
    /// Injects a dangling .git/index.lock file simulating an aborted git crash.
    /// </summary>
    public string InjectDanglingLock()
    {
        var lockPath = Path.Combine(RepositoryPath, ".git", "index.lock");
        File.WriteAllText(lockPath, "dangling lock file");
        return lockPath;
    }

    /// <summary>
    /// Injects uncommitted modifications to working tree files simulating uncommitted edits before crash.
    /// </summary>
    public void InjectDirtyChange()
    {
        var path = Path.Combine(RepositoryPath, "Calculator.cs");
        File.AppendAllText(path, "\n// Uncommitted working tree edit before crash\n");
    }

    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await RunGitAsync("status --short", cancellationToken);
    }

    public async Task<string> GetCommitLogAsync(int count = 5, CancellationToken cancellationToken = default)
    {
        return await RunGitAsync($"log -n {count} --oneline", cancellationToken);
    }

    public async Task<string> GetDiffAsync(string baseBranch = "main", CancellationToken cancellationToken = default)
    {
        return await RunGitAsync($"diff {baseBranch}", cancellationToken);
    }

    public async Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync("branch --show-current", cancellationToken);
        return result.Trim();
    }

    public async Task<string> RunGitAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch git CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command 'git {arguments}' failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
