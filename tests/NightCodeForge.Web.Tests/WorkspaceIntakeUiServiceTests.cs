using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceIntakeUiServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceIntakeUiServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NCF_IntakeUiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort temp cleanup
        }
    }

    [Fact]
    public async Task LoadRemoteForgeStateAsync_MatchesLocalRepositoriesByCanonicalUrl()
    {
        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new DiscoveredRepository(
                    Name: "hello-world",
                    FullPath: Path.Combine(_tempRoot, "hello-world"),
                    CurrentBranch: "main",
                    LastModified: DateTimeOffset.UtcNow,
                    HasRemoteOrigin: true,
                    RemoteOriginUrl: "git@github.com:Octocat/Hello-World.git")
            ]);

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.SetupGet(p => p.DisplayName).Returns("GitHub");
        providerMock.Setup(p => p.GetAuthenticatedUsernameAsync(It.IsAny<CancellationToken>())).ReturnsAsync("octocat");
        providerMock.Setup(p => p.ListRepositoriesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new VcsRepositorySummary(
                    Id: "1",
                    Name: "Hello-World",
                    FullName: "Octocat/Hello-World",
                    Owner: "Octocat",
                    Description: "Matched repo",
                    IsPrivate: false,
                    DefaultBranch: "main",
                    HttpsCloneUrl: "https://github.com/octocat/hello-world.git",
                    SshCloneUrl: "git@github.com:octocat/hello-world.git",
                    UpdatedAt: DateTimeOffset.UtcNow),
                new VcsRepositorySummary(
                    Id: "2",
                    Name: "cloud-only",
                    FullName: "Octocat/cloud-only",
                    Owner: "Octocat",
                    Description: "Remote only repo",
                    IsPrivate: true,
                    DefaultBranch: "develop",
                    HttpsCloneUrl: "https://github.com/octocat/cloud-only.git",
                    SshCloneUrl: "git@github.com:octocat/cloud-only.git",
                    UpdatedAt: DateTimeOffset.UtcNow)
            ]);

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetAvailableProviders()).Returns([providerMock.Object]);
        registryMock.Setup(r => r.GetPrimaryAuthenticatedProviderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerMock.Object);

        var service = new WorkspaceIntakeUiService(
            discoveryMock.Object,
            registryMock.Object,
            new Mock<IGitCliRunner>().Object,
            NullLogger<WorkspaceIntakeUiService>.Instance);

        var localRepos = await service.LoadLocalRepositoriesAsync();
        var remoteState = await service.LoadRemoteForgeStateAsync(localRepos);

        Assert.True(remoteState.IsAuthenticated);
        Assert.Equal("octocat", remoteState.AuthenticatedUsername);
        Assert.Equal(2, remoteState.Repositories.Count);

        var matched = Assert.Single(remoteState.Repositories, r => r.Repository.Id == "1");
        Assert.NotNull(matched.LocalMatch);
        Assert.Equal("hello-world", matched.LocalMatch.Name);

        var uncloned = Assert.Single(remoteState.Repositories, r => r.Repository.Id == "2");
        Assert.Null(uncloned.LocalMatch);

        var filtered = service.FilterRemoteRepositories(remoteState.Repositories, "cloud");
        Assert.Single(filtered);
        Assert.Equal("2", filtered[0].Repository.Id);
    }

    [Fact]
    public async Task ValidateCloneTargetPath_DetectsCollisions_AndAllowsEmptyOrNewDirectory()
    {
        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        var service = new WorkspaceIntakeUiService(
            discoveryMock.Object,
            registryMock.Object,
            new Mock<IGitCliRunner>().Object,
            NullLogger<WorkspaceIntakeUiService>.Instance);

        Assert.False(service.ValidateCloneTargetPath("   ").IsValid);
        Assert.False(service.ValidateCloneTargetPath("relative/path").IsValid);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(service.ValidateCloneTargetPath(@"C:relative\path").IsValid);
            Assert.False(service.ValidateCloneTargetPath(@"\root-relative").IsValid);
        }

        var filePath = Path.Combine(_tempRoot, "existing-file.txt");
        await File.WriteAllTextAsync(filePath, "hello");
        Assert.False(service.ValidateCloneTargetPath(filePath).IsValid);

        var nonEmptyDir = Path.Combine(_tempRoot, "non-empty");
        Directory.CreateDirectory(nonEmptyDir);
        await File.WriteAllTextAsync(Path.Combine(nonEmptyDir, "README.md"), "content");
        var nonEmptyResult = service.ValidateCloneTargetPath(nonEmptyDir);
        Assert.False(nonEmptyResult.IsValid);
        Assert.True(nonEmptyResult.DirectoryExists);
        Assert.True(nonEmptyResult.IsNonEmpty);

        var emptyDir = Path.Combine(_tempRoot, "empty-dir");
        Directory.CreateDirectory(emptyDir);
        Assert.True(service.ValidateCloneTargetPath(emptyDir).IsValid);

        var newDir = Path.Combine(_tempRoot, "fresh-clone");
        Assert.True(service.ValidateCloneTargetPath(newDir).IsValid);
    }

    [Fact]
    public async Task CloneRemoteRepositoryAsync_UsesEphemeralAuthHeader_GitTerminalPromptZero_AndPopulatesGitHubSettings()
    {
        var targetPath = Path.Combine(_tempRoot, "repos", "private-api");

        var discoveryMock = new Mock<ILocalRepositoryDiscoveryService>();
        discoveryMock.Setup(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DiscoveredRepository>());

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic eC1hY2Nlc3MtdG9rZW46Z2h1X3Rlc3Q=");

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        string? capturedWorkingDir = null;
        IReadOnlyList<string>? capturedArgs = null;
        IReadOnlyDictionary<string, string>? capturedEnv = null;

        var gitRunnerMock = new Mock<IGitCliRunner>();
        gitRunnerMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((wd, args, _, env, _) =>
            {
                capturedWorkingDir = wd;
                capturedArgs = args.ToList();
                capturedEnv = env;
            })
            .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.FromMilliseconds(20)));

        var service = new WorkspaceIntakeUiService(
            discoveryMock.Object,
            registryMock.Object,
            gitRunnerMock.Object,
            NullLogger<WorkspaceIntakeUiService>.Instance);

        var repoSummary = new VcsRepositorySummary(
            Id: "42",
            Name: "private-api",
            FullName: "octocat/private-api",
            Owner: "octocat",
            Description: "Private API",
            IsPrivate: true,
            DefaultBranch: "main",
            HttpsCloneUrl: "https://github.com/octocat/private-api.git",
            SshCloneUrl: "git@github.com:octocat/private-api.git",
            UpdatedAt: DateTimeOffset.UtcNow);

        var result = await service.CloneRemoteRepositoryAsync(VcsHostingProviderType.GitHub, repoSummary, targetPath);

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(targetPath), result.TargetPath);
        Assert.Equal("private-api", result.SuggestedWorkspaceName);
        Assert.Equal("main", result.DefaultBaseBranch);
        Assert.NotNull(result.GitHubSettings);
        Assert.True(result.GitHubSettings.Enabled);
        Assert.Equal("octocat", result.GitHubSettings.RepositoryOwner);
        Assert.Equal("private-api", result.GitHubSettings.RepositoryName);

        Assert.Equal(Path.GetFullPath(Path.Combine(_tempRoot, "repos")), capturedWorkingDir);
        Assert.NotNull(capturedArgs);
        Assert.Equal(
            ["-c", "http.extraHeader=Authorization: Basic eC1hY2Nlc3MtdG9rZW46Z2h1X3Rlc3Q=", "clone", "--", "https://github.com/octocat/private-api.git", Path.GetFullPath(targetPath)],
            capturedArgs);
        Assert.NotNull(capturedEnv);
        Assert.Equal("0", capturedEnv["GIT_TERMINAL_PROMPT"]);

        discoveryMock.Verify(d => d.GetDiscoveredRepositoriesAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("file:///tmp/repo")]
    [InlineData("--upload-pack=evil")]
    public async Task CloneRemoteRepositoryAsync_RejectsNonHttpOrHttpsCloneUrls_WithoutInvokingGit(string invalidCloneUrl)
    {
        var targetPath = Path.Combine(_tempRoot, "repos", "bad-scheme");

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var gitRunnerMock = new Mock<IGitCliRunner>(MockBehavior.Strict);

        var service = new WorkspaceIntakeUiService(
            new Mock<ILocalRepositoryDiscoveryService>().Object,
            registryMock.Object,
            gitRunnerMock.Object,
            NullLogger<WorkspaceIntakeUiService>.Instance);

        var repoSummary = new VcsRepositorySummary(
            Id: "99",
            Name: "bad-scheme",
            FullName: "octocat/bad-scheme",
            Owner: "octocat",
            Description: "Invalid scheme",
            IsPrivate: true,
            DefaultBranch: "main",
            HttpsCloneUrl: invalidCloneUrl,
            SshCloneUrl: "git@github.com:octocat/bad-scheme.git",
            UpdatedAt: DateTimeOffset.UtcNow);

        var result = await service.CloneRemoteRepositoryAsync(VcsHostingProviderType.GitHub, repoSummary, targetPath);

        Assert.False(result.Success);
        Assert.Contains("HTTP/HTTPS", result.ErrorMessage);
        gitRunnerMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CloneRemoteRepositoryAsync_RedactsCredentialHeader_WhenGitCommandFails()
    {
        var targetPath = Path.Combine(_tempRoot, "repos", "failing-clone");

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("Authorization: Basic c2VjcmV0dG9rZW4=");

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var gitRunnerMock = new Mock<IGitCliRunner>();
        gitRunnerMock.Setup(g => g.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitCommandException(
                _tempRoot,
                ["-c", "http.extraHeader=Authorization: Basic c2VjcmV0dG9rZW4=", "clone", "--", "https://github.com/octocat/private-api.git", targetPath],
                128,
                string.Empty,
                "fatal: Authentication failed for 'https://x-access-token:ghu_supersecret123@github.com/octocat/private-api.git/'"));

        var logger = new CapturingLogger<WorkspaceIntakeUiService>();
        var service = new WorkspaceIntakeUiService(
            new Mock<ILocalRepositoryDiscoveryService>().Object,
            registryMock.Object,
            gitRunnerMock.Object,
            logger);

        var repoSummary = new VcsRepositorySummary(
            Id: "42",
            Name: "private-api",
            FullName: "octocat/private-api",
            Owner: "octocat",
            Description: "Private API",
            IsPrivate: true,
            DefaultBranch: "main",
            HttpsCloneUrl: "https://github.com/octocat/private-api.git",
            SshCloneUrl: "git@github.com:octocat/private-api.git",
            UpdatedAt: DateTimeOffset.UtcNow);

        var result = await service.CloneRemoteRepositoryAsync(VcsHostingProviderType.GitHub, repoSummary, targetPath);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.DoesNotContain("c2VjcmV0dG9rZW4=", result.ErrorMessage);
        Assert.DoesNotContain("ghu_supersecret123", result.ErrorMessage);

        var errorEntry = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Error);
        Assert.Null(errorEntry.Exception);
        Assert.DoesNotContain("c2VjcmV0dG9rZW4=", errorEntry.Message);
        Assert.DoesNotContain("ghu_supersecret123", errorEntry.Message);
    }

    [Fact]
    public async Task CloneRemoteRepositoryAsync_DoesNotLogRawCredentials_WhenUnexpectedExceptionContainsCredentials()
    {
        var targetPath = Path.Combine(_tempRoot, "repos", "unexpected-fail");

        var providerMock = new Mock<IVcsHostingProvider>();
        providerMock.SetupGet(p => p.ProviderType).Returns(VcsHostingProviderType.GitHub);
        providerMock.Setup(p => p.GetGitHttpAuthHeaderAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token refresh failed for https://x-access-token:ghu_leakedtoken999@github.com/repo.git"));

        var registryMock = new Mock<IVcsHostingProviderRegistry>();
        registryMock.Setup(r => r.GetProvider(VcsHostingProviderType.GitHub)).Returns(providerMock.Object);

        var logger = new CapturingLogger<WorkspaceIntakeUiService>();
        var service = new WorkspaceIntakeUiService(
            new Mock<ILocalRepositoryDiscoveryService>().Object,
            registryMock.Object,
            new Mock<IGitCliRunner>().Object,
            logger);

        var repoSummary = new VcsRepositorySummary(
            Id: "43",
            Name: "unexpected-fail",
            FullName: "octocat/unexpected-fail",
            Owner: "octocat",
            Description: "Unexpected failure",
            IsPrivate: true,
            DefaultBranch: "main",
            HttpsCloneUrl: "https://github.com/octocat/unexpected-fail.git",
            SshCloneUrl: "git@github.com:octocat/unexpected-fail.git",
            UpdatedAt: DateTimeOffset.UtcNow);

        var result = await service.CloneRemoteRepositoryAsync(VcsHostingProviderType.GitHub, repoSummary, targetPath);

        Assert.False(result.Success);
        Assert.DoesNotContain("ghu_leakedtoken999", result.ErrorMessage);

        var errorEntry = Assert.Single(logger.Entries, e => e.LogLevel == LogLevel.Error);
        Assert.Null(errorEntry.Exception);
        Assert.DoesNotContain("ghu_leakedtoken999", errorEntry.Message);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record LogEntry(LogLevel LogLevel, string Message, Exception? Exception);

        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}
