using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class LocalRepositoryDiscoveryServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public LocalRepositoryDiscoveryServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "NCF_DiscoveryTests_" + Guid.NewGuid().ToString("N"));
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
    public void BuildStandardRoots_Windows_IncludesExpectedStandardAndAdditionalRoots()
    {
        var userProfile = Path.Combine(_tempRoot, "WinUser");
        var customPath = Path.Combine(_tempRoot, "CustomRepos");

        var roots = LocalRepositoryDiscoveryService.BuildStandardRoots(
            isWindows: true,
            isWsl: false,
            userProfile: userProfile,
            homeDirectory: null,
            wslUsersRoot: null,
            additionalPaths: [customPath, Path.Combine(userProfile, "repos")]);

        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "source", "repos")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "repos")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "projects")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "Development")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "workspace")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(userProfile, "Documents", "GitHub")), roots);
        Assert.Contains(Path.GetFullPath(customPath), roots);
        Assert.Equal(roots.Count, roots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void BuildStandardRoots_LinuxAndWsl_IncludesUnixAndNonSystemWslUsers()
    {
        var linuxHome = Path.Combine(_tempRoot, "home", "julien");
        var wslUsersRoot = Path.Combine(_tempRoot, "mnt_c_Users");
        Directory.CreateDirectory(Path.Combine(wslUsersRoot, "Julien"));
        Directory.CreateDirectory(Path.Combine(wslUsersRoot, "Public"));
        Directory.CreateDirectory(Path.Combine(wslUsersRoot, "Default"));

        var roots = LocalRepositoryDiscoveryService.BuildStandardRoots(
            isWindows: false,
            isWsl: true,
            userProfile: linuxHome,
            homeDirectory: linuxHome,
            wslUsersRoot: wslUsersRoot,
            additionalPaths: ["~/extra"]);

        Assert.Contains(Path.GetFullPath(Path.Combine(linuxHome, "source", "repos")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(linuxHome, "code")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(linuxHome, "extra")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(wslUsersRoot, "Julien", "source", "repos")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(wslUsersRoot, "Julien", "repos")), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(wslUsersRoot, "Julien", "projects")), roots);
        Assert.DoesNotContain(roots, r => r.Contains("Public", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(roots, r => r.Contains("Default", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetDiscoveredRepositoriesAsync_ParsesGitMetadataWorktreesDepthAndExclusions()
    {
        var scanRoot = Path.Combine(_tempRoot, "repos");
        Directory.CreateDirectory(scanRoot);

        // Depth 1 standard repo with branch & origin
        var repoOne = CreateFakeGitRepo(
            Path.Combine(scanRoot, "app-one"),
            "ref: refs/heads/feature/wizard-ui",
            "https://github.com/octocat/app-one.git");

        // Nested sub-repo inside app-one should NOT be scanned once .git is detected
        CreateFakeGitRepo(
            Path.Combine(repoOne, "submodules", "nested-lib"),
            "ref: refs/heads/main",
            null);

        // Depth 2 categorized repo without remote
        CreateFakeGitRepo(
            Path.Combine(scanRoot, "org", "app-two"),
            "ref: refs/heads/main",
            null);

        // Depth 3 repo (exceeds MaxDepth=2) should be excluded
        CreateFakeGitRepo(
            Path.Combine(scanRoot, "a", "b", "too-deep"),
            "ref: refs/heads/main",
            null);

        // Ignored via .ncf-ignore
        var ignoredRepo = CreateFakeGitRepo(
            Path.Combine(scanRoot, "ignored-app"),
            "ref: refs/heads/main",
            null);
        await File.WriteAllTextAsync(Path.Combine(ignoredRepo, ".ncf-ignore"), "");

        // Pruned node_modules subtree
        CreateFakeGitRepo(
            Path.Combine(scanRoot, "node_modules", "hidden-pkg"),
            "ref: refs/heads/main",
            null);

        // Worktree repo using .git pointer file, commondir, and detached HEAD SHA
        var worktreeMainGitDir = Path.Combine(_tempRoot, "bare-host.git");
        var worktreeEntryDir = Path.Combine(worktreeMainGitDir, "worktrees", "wt-app");
        Directory.CreateDirectory(worktreeEntryDir);
        await File.WriteAllTextAsync(Path.Combine(worktreeMainGitDir, "config"), "[remote \"origin\"]\n\turl = git@github.com:octocat/wt-app.git\n");
        await File.WriteAllTextAsync(Path.Combine(worktreeEntryDir, "HEAD"), "1234567890abcdef1234567890abcdef12345678\n");
        await File.WriteAllTextAsync(Path.Combine(worktreeEntryDir, "commondir"), "../..\n");

        var worktreeDir = Path.Combine(scanRoot, "wt-app");
        Directory.CreateDirectory(worktreeDir);
        await File.WriteAllTextAsync(Path.Combine(worktreeDir, ".git"), $"gitdir: {worktreeEntryDir}\n");

        var options = Options.Create(new LocalDiscoveryOptions
        {
            MaxDepth = 2,
            CacheDurationMinutes = 10,
            AdditionalScanPaths = [scanRoot]
        });

        var service = new LocalRepositoryDiscoveryService(
            options,
            new MutableTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<LocalRepositoryDiscoveryService>.Instance);

        var discovered = await service.GetDiscoveredRepositoriesAsync(forceRefresh: true);
        var discoveredInRoot = discovered
            .Where(r => r.FullPath.StartsWith(scanRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(3, discoveredInRoot.Count);

        var appOne = Assert.Single(discoveredInRoot, r => r.Name == "app-one");
        Assert.Equal("feature/wizard-ui", appOne.CurrentBranch);
        Assert.True(appOne.HasRemoteOrigin);
        Assert.Equal("https://github.com/octocat/app-one.git", appOne.RemoteOriginUrl);

        var appTwo = Assert.Single(discoveredInRoot, r => r.Name == "app-two");
        Assert.Equal("main", appTwo.CurrentBranch);
        Assert.False(appTwo.HasRemoteOrigin);
        Assert.Null(appTwo.RemoteOriginUrl);

        var wtApp = Assert.Single(discoveredInRoot, r => r.Name == "wt-app");
        Assert.Equal("1234567", wtApp.CurrentBranch);
        Assert.True(wtApp.HasRemoteOrigin);
        Assert.Equal("git@github.com:octocat/wt-app.git", wtApp.RemoteOriginUrl);
    }

    [Fact]
    public async Task GetDiscoveredRepositoriesAsync_RespectsCacheTtlAndForceRefresh()
    {
        var scanRoot = Path.Combine(_tempRoot, "cached-repos");
        Directory.CreateDirectory(scanRoot);
        CreateFakeGitRepo(Path.Combine(scanRoot, "repo-alpha"), "ref: refs/heads/main", null);

        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        var service = new LocalRepositoryDiscoveryService(
            Options.Create(new LocalDiscoveryOptions
            {
                MaxDepth = 2,
                CacheDurationMinutes = 10,
                AdditionalScanPaths = [scanRoot]
            }),
            timeProvider,
            NullLogger<LocalRepositoryDiscoveryService>.Instance);

        var first = await service.GetDiscoveredRepositoriesAsync();
        Assert.Contains(first, r => r.Name == "repo-alpha");

        // Add repo-beta while cache is still warm
        CreateFakeGitRepo(Path.Combine(scanRoot, "repo-beta"), "ref: refs/heads/main", null);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var cached = await service.GetDiscoveredRepositoriesAsync();
        Assert.DoesNotContain(cached, r => r.Name == "repo-beta");

        // Force refresh immediately observes repo-beta
        var forced = await service.GetDiscoveredRepositoriesAsync(forceRefresh: true);
        Assert.Contains(forced, r => r.Name == "repo-beta");

        // Add repo-gamma and expire TTL
        CreateFakeGitRepo(Path.Combine(scanRoot, "repo-gamma"), "ref: refs/heads/main", null);
        timeProvider.Advance(TimeSpan.FromMinutes(11));
        var expired = await service.GetDiscoveredRepositoriesAsync();
        Assert.Contains(expired, r => r.Name == "repo-gamma");
    }

    [Fact]
    public async Task ResolveDefaultNewRepositoryPath_AndGetStandardRootsAsync_WorkCorrectly()
    {
        var existingAdditionalRoot = Path.Combine(_tempRoot, "custom-existing-root");
        Directory.CreateDirectory(existingAdditionalRoot);

        var service = new LocalRepositoryDiscoveryService(
            Options.Create(new LocalDiscoveryOptions
            {
                AdditionalScanPaths = [existingAdditionalRoot, Path.Combine(_tempRoot, "missing-root")]
            }),
            TimeProvider.System,
            NullLogger<LocalRepositoryDiscoveryService>.Instance);

        var existingRoots = await service.GetStandardRootsAsync();
        Assert.Contains(Path.GetFullPath(existingAdditionalRoot), existingRoots);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(_tempRoot, "missing-root")), existingRoots);

        var resolved = service.ResolveDefaultNewRepositoryPath("my-new-service");
        Assert.EndsWith(Path.Combine(Path.DirectorySeparatorChar.ToString(), "my-new-service"), resolved);

        Assert.Throws<LocalRepositoryDiscoveryException>(() => service.ResolveDefaultNewRepositoryPath("   "));
        Assert.Throws<LocalRepositoryDiscoveryException>(() => service.ResolveDefaultNewRepositoryPath("../escape"));
        Assert.Throws<LocalRepositoryDiscoveryException>(() => service.ResolveDefaultNewRepositoryPath(@"foo\bar"));
    }

    [Fact]
    public async Task GetDiscoveredRepositoriesAsync_ParsesQuotedRemoteUrlAndTrailingSectionComments()
    {
        var scanRoot = Path.Combine(_tempRoot, "quoted-config-repos");
        var repoDir = Path.Combine(scanRoot, "quoted-app");
        var gitDir = Path.Combine(repoDir, ".git");
        Directory.CreateDirectory(gitDir);

        await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(
            Path.Combine(gitDir, "config"),
            """
            [core]
            	repositoryformatversion = 0
            [remote "origin"] # primary
            	url = "https://github.com/octocat/quoted-app.git" # trailing comment
            	fetch = +refs/heads/*:refs/remotes/origin/*
            """);

        var service = new LocalRepositoryDiscoveryService(
            Options.Create(new LocalDiscoveryOptions
            {
                MaxDepth = 2,
                CacheDurationMinutes = 10,
                AdditionalScanPaths = [scanRoot]
            }),
            TimeProvider.System,
            NullLogger<LocalRepositoryDiscoveryService>.Instance);

        var discovered = await service.GetDiscoveredRepositoriesAsync(forceRefresh: true);
        var repo = Assert.Single(discovered, r => r.FullPath.StartsWith(scanRoot, StringComparison.OrdinalIgnoreCase));

        Assert.True(repo.HasRemoteOrigin);
        Assert.Equal("https://github.com/octocat/quoted-app.git", repo.RemoteOriginUrl);
        Assert.True(NightCodeForge.Core.Services.GitRemoteUrlNormalizer.AreSameRepository(
            repo.RemoteOriginUrl,
            "git@github.com:Octocat/Quoted-App.git"));
    }

    private static string CreateFakeGitRepo(string repoPath, string headContent, string? originUrl)
    {
        var gitDir = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), headContent + "\n");

        if (!string.IsNullOrWhiteSpace(originUrl))
        {
            File.WriteAllText(
                Path.Combine(gitDir, "config"),
                $"[core]\n\trepositoryformatversion = 0\n[remote \"origin\"]\n\turl = {originUrl}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n");
        }

        return repoPath;
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
