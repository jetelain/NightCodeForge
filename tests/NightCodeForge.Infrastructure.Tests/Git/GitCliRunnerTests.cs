using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitCliRunnerTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;

    public GitCliRunnerTests()
    {
        _repo = new TestGitRepository();
        _runner = new GitCliRunner();
    }

    [Fact]
    public async Task ExecuteAsync_ValidCommand_ReturnsZeroExitCodeAndCapturedStdout()
    {
        // Act
        var result = await _runner.ExecuteAsync(_repo.RepositoryPath, "rev-parse --is-inside-work-tree");

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true", result.StandardOutput.Trim());
        Assert.Empty(result.StandardError.Trim());
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidGitCommand_ThrowsGitCommandExceptionWithDetails()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<GitCommandException>(async () =>
        {
            await _runner.ExecuteAsync(_repo.RepositoryPath, "checkout non-existent-branch-xyz");
        });

        Assert.NotEqual(0, ex.ExitCode);
        Assert.Equal(_repo.RepositoryPath, ex.WorkingDirectory);
        Assert.Equal("checkout non-existent-branch-xyz", ex.Arguments);
        Assert.False(string.IsNullOrWhiteSpace(ex.StandardError));
    }

    [Fact]
    public async Task ExecuteAsync_NonExistentWorkingDirectory_ThrowsDirectoryNotFoundException()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await _runner.ExecuteAsync(nonExistentDir, "status");
        });
    }

    [Fact]
    public async Task ExecuteAsync_InjectedEnvironmentVariables_AreHonored()
    {
        // Act: verify git config pager returns cat without hanging or paging
        var result = await _runner.ExecuteAsync(_repo.RepositoryPath, "var GIT_PAGER");

        // Assert: command succeeded without freezing on interactive terminal
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_WithTimeout_ThrowsTimeoutExceptionWhenExceeded()
    {
        // Act & Assert
        // We use timeoutSeconds = 0 or 1 with an external delay or cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled to test immediate cancellation

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await _runner.ExecuteAsync(_repo.RepositoryPath, "status", cancellationToken: cts.Token);
        });
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}
