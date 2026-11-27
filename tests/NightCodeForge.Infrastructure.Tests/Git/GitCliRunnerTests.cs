using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitCliRunnerTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;

    public GitCliRunnerTests()
    {
        _repo = new TestGitRepository();
        _runner = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            new WorkspacePathResolver(new Mock<IWslMetadataService>().Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions())),
            new Mock<IWslMetadataService>().Object,
            NullLogger<GitCliRunner>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_ValidCommand_ReturnsZeroExitCodeAndCapturedStdout()
    {
        // Act
        var result = await _runner.ExecuteAsync(_repo.RepositoryPath, ["rev-parse", "--is-inside-work-tree"]);

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
            await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "non-existent-branch-xyz"]);
        });

        Assert.NotEqual(0, ex.ExitCode);
        Assert.Equal(_repo.RepositoryPath, ex.WorkingDirectory);
        Assert.Equal("checkout non-existent-branch-xyz", ex.Arguments);
        Assert.Equal(["checkout", "non-existent-branch-xyz"], ex.ArgumentsList);
        Assert.False(string.IsNullOrWhiteSpace(ex.StandardError));
    }

    [Fact]
    public async Task ExecuteAsync_NonExistentWorkingDirectory_ThrowsDirectoryNotFoundException()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await _runner.ExecuteAsync(nonExistentDir, ["status"]);
        });
    }

    [Fact]
    public async Task ExecuteAsync_InjectedEnvironmentVariables_AreHonored()
    {
        // Act: verify git config pager returns cat without hanging or paging
        var result = await _runner.ExecuteAsync(_repo.RepositoryPath, ["var", "GIT_PAGER"]);

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
            await _runner.ExecuteAsync(_repo.RepositoryPath, ["status"], cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteAsync_WithArgumentsContainingSpacesAndQuotes_PassesCleanlyWithoutEscapingTasks()
    {
        // Arrange
        _repo.CreateFile("file_with_spaces.txt", "Hello world");
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["add", "-A"]);

        var commitMessage = "feat: test \"quotes\" and 'single' and spaces!";

        // Act
        var commitResult = await _runner.ExecuteAsync(_repo.RepositoryPath, ["commit", "-m", commitMessage, "--no-verify"]);
        Assert.Equal(0, commitResult.ExitCode);

        // Assert: commit log contains the exact message with quotes preserved
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains(commitMessage, log.StandardOutput);
    }

    [Fact]
    public async Task ExecuteAsync_WithInjectedProcessRunner_InvokesProcessRunnerWithArgumentList()
    {
        // Arrange
        ProcessStartSpec? capturedSpec = null;
        var mockRunner = new MockTestProcessRunner(spec =>
        {
            capturedSpec = spec;
            return new ProcessExecutionResult(0, "mock-output\n", string.Empty);
        });

        var gitRunner = new GitCliRunner(
            mockRunner,
            new WorkspacePathResolver(new Mock<IWslMetadataService>().Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions())),
            new Mock<IWslMetadataService>().Object,
            NullLogger<GitCliRunner>.Instance);

        // Act
        var result = await gitRunner.ExecuteAsync(_repo.RepositoryPath, ["status", "--porcelain"]);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("mock-output", result.StandardOutput.Trim());
        Assert.NotNull(capturedSpec);
        Assert.Equal("git", capturedSpec.FileName);
        Assert.Equal(["status", "--porcelain"], capturedSpec.ArgumentList);
    }

    [Fact]
    public async Task GitCliRunner_ExecutesInWslDirectlyWithoutUnc()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // WSL delegation only runs on Windows hosts.
        }

        ProcessStartSpec? capturedSpec = null;
        var mockRunner = new MockTestProcessRunner(spec =>
        {
            capturedSpec = spec;
            return new ProcessExecutionResult(0, "mock-diff-output\n", string.Empty);
        });

        var mockWslMeta = new Mock<IWslMetadataService>();
        mockWslMeta.Setup(m => m.GetDefaultDistributionName()).Returns("Ubuntu-24.04");
        mockWslMeta.Setup(m => m.GetDefaultUserName()).Returns("runner");

        var gitRunner = new GitCliRunner(
            mockRunner,
            new WorkspacePathResolver(mockWslMeta.Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions())),
            mockWslMeta.Object,
            NullLogger<GitCliRunner>.Instance)
        {
            SkipDirectoryValidation = true
        };

        var linuxPath = "/home/runner/.nightcodeforge/wks/42/worktree";
        var envVars = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = "/tmp/temp.index" };

        var result = await gitRunner.ExecuteAsync(linuxPath, ["diff", "main"], null, envVars);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(capturedSpec);
        Assert.Equal("wsl.exe", capturedSpec.FileName);

        var args = capturedSpec!.ArgumentList!.ToList();
        Assert.Equal("-d", args[0]);
        Assert.Equal("Ubuntu-24.04", args[1]);
        Assert.Equal("--exec", args[2]);
        Assert.Equal("env", args[3]);
        var gitIndex = args.IndexOf("git");
        Assert.True(gitIndex > 4, "Expected env variables before 'git' executable argument.");
        var envArgs = args.Skip(4).Take(gitIndex - 4).ToList();
        Assert.Contains("PAGER=cat", envArgs);
        Assert.Contains("GIT_PAGER=cat", envArgs);
        Assert.Contains("GIT_TERMINAL_PROMPT=0", envArgs);
        Assert.Contains("GIT_INDEX_FILE=/tmp/temp.index", envArgs);
        Assert.Equal("-c", args[gitIndex + 1]);
        Assert.Equal("core.pager=cat", args[gitIndex + 2]);
        Assert.Equal("-C", args[gitIndex + 3]);
        Assert.Equal(linuxPath, args[gitIndex + 4]);
        Assert.DoesNotContain(@"\\wsl", string.Join(" ", args));
    }

    [Theory]
    [InlineData(new[] { "-C", "push", "status" }, null, GitCliRunner.DefaultTimeoutSeconds)]
    [InlineData(new[] { "--git-dir", "fetch", "diff" }, null, GitCliRunner.DefaultTimeoutSeconds)]
    [InlineData(new[] { "-c", "core.pager=cat", "--no-pager", "fetch", "origin" }, null, GitCliRunner.NetworkOperationTimeoutSeconds)]
    [InlineData(new[] { "-C", "/repo", "clone", "https://github.com/o/r.git" }, null, GitCliRunner.NetworkOperationTimeoutSeconds)]
    [InlineData(new[] { "fetch", "origin" }, 45, 45)]
    public void ResolveTimeoutSeconds_SkipsGlobalOptionValuesAndDetectsNetworkSubcommands(
        string[] args,
        int? explicitTimeout,
        int expectedTimeout)
    {
        var actual = GitCliRunner.ResolveTimeoutSeconds(args, explicitTimeout);

        Assert.Equal(expectedTimeout, actual);
    }

    [Fact]
    public async Task ExecuteRawAsync_PreservesNullBytesInLsTreeAndBinaryBlobs()
    {
        _repo.CreateFile("src/hello.txt", "world\n");
        var binaryBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02 };
        await File.WriteAllBytesAsync(Path.Combine(_repo.RepositoryPath, "logo.png"), binaryBytes);
        _repo.CommitAll("add raw files");

        var lsTreeResult = await _runner.ExecuteRawAsync(_repo.RepositoryPath, ["ls-tree", "-l", "-z", "HEAD"]);
        Assert.Equal(0, lsTreeResult.ExitCode);
        Assert.Contains((byte)0, lsTreeResult.StandardOutputBytes);

        var showResult = await _runner.ExecuteRawAsync(_repo.RepositoryPath, ["show", "HEAD:logo.png"]);
        Assert.Equal(0, showResult.ExitCode);
        Assert.Equal(binaryBytes, showResult.StandardOutputBytes);
    }

    [Fact]
    public async Task ExecuteRawAsync_NonZeroExit_ThrowsGitCommandExceptionWithRedactedStdErr()
    {
        var ex = await Assert.ThrowsAsync<GitCommandException>(() =>
            _runner.ExecuteRawAsync(_repo.RepositoryPath, ["show", "HEAD:missing.bin"]));

        Assert.NotEqual(0, ex.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(ex.StandardError));
    }

    public void Dispose()
    {
        _repo.Dispose();
    }

    private class MockTestProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessStartSpec, ProcessExecutionResult> _handler;

        public MockTestProcessRunner(Func<ProcessStartSpec, ProcessExecutionResult> handler)
        {
            _handler = handler;
        }

        public ProcessExecutionResult Run(ProcessStartSpec spec) => _handler(spec);
        public Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(_handler(spec));
        public Task<ProcessRawExecutionResult> RunRawAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            var res = _handler(spec);
            return Task.FromResult(new ProcessRawExecutionResult(res.ExitCode, System.Text.Encoding.UTF8.GetBytes(res.StandardOutput), res.StandardError));
        }
        public IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IProcessStreamSession StartSession(ProcessStartSpec spec) => throw new NotImplementedException();
    }
}
