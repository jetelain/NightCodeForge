using System.Diagnostics;

namespace NightCodeForge.Infrastructure.Tests.Git;

/// <summary>
/// Helper fixture that initializes a real temporary Git repository for integration testing.
/// </summary>
public sealed class TestGitRepository : IDisposable
{
    public string RepositoryPath { get; }

    public TestGitRepository(string? initialBranch = "main")
    {
        RepositoryPath = Path.Combine(Path.GetTempPath(), "ncf_git_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RepositoryPath);

        // Run git init
        RunGit($"init -b {initialBranch}");
        RunGit("config user.name \"NightCodeForge Test\"");
        RunGit("config user.email \"test@nightcodeforge.local\"");
        RunGit("config commit.gpgsign false");
    }

    public string CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(RepositoryPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void CommitAll(string message)
    {
        RunGit("add -A");
        RunGit($"commit -m \"{message}\" --no-verify");
    }

    public void RunGit(string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit(10000);
        if (process.ExitCode != 0)
        {
            var err = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Git command 'git {arguments}' failed with exit code {process.ExitCode}: {err}");
        }
    }

    public void Dispose()
    {
        DeleteDirectorySafe(RepositoryPath);
    }

    public static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch { }
            }

            Directory.Delete(path, true);
        }
        catch { }
    }
}
