using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.GitHub;
using NightCodeForge.Infrastructure.Tests.Git;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.GitHub;

public class GitHubPullRequestDeliveryPlumbingTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly GitCliRunner _runner;
    private readonly WorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadata;
    private readonly GitMirrorSyncService _mirrorSyncService;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    public GitHubPullRequestDeliveryPlumbingTests()
    {
        _wslMetadata = new Mock<IWslMetadataService>().Object;
        var envOptions = Options.Create(new ExecutionEnvironmentProviderOptions());
        _pathResolver = new WorkspacePathResolver(_wslMetadata, envOptions);
        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        _runner = new GitCliRunner(
            processRunner,
            _pathResolver,
            _wslMetadata,
            NullLogger<GitCliRunner>.Instance);
        _mirrorSyncService = new GitMirrorSyncService(
            _runner,
            new Mock<IGitAlertNotifier>().Object,
            _pathResolver,
            _wslMetadata,
            NullLogger<GitMirrorSyncService>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(
            _runner,
            _pathResolver,
            _wslMetadata,
            _mirrorSyncService,
            new Mock<IWorkspaceLockProvider>().Object,
            processRunner,
            NullLogger<GitWorkspaceManager>.Instance);
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncf_pr_test_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private async Task<string> InitBareRepoAsync(string prefix)
    {
        var bareDir = CreateTempDir(prefix);
        await _runner.ExecuteAsync(bareDir, ["init", "--bare", "-b", "main"]);
        return bareDir;
    }

    private async Task ConfigureGitIdentityAsync(string repoPath)
    {
        await _runner.ExecuteAsync(repoPath, ["config", "user.name", "NightCodeForge Test"]);
        await _runner.ExecuteAsync(repoPath, ["config", "user.email", "test@nightcodeforge.local"]);
        await _runner.ExecuteAsync(repoPath, ["config", "commit.gpgsign", "false"]);
    }

    private GitHubPullRequestDeliveryService CreateDeliveryService(
        string login = "alice",
        string? accessToken = "gho_test_token_123",
        Mock<IGitHubClientService>? clientMock = null,
        bool airGapped = false)
    {
        var authMock = new Mock<IGitHubDeviceAuthService>();
        authMock
            .Setup(a => a.GetValidAccessTokenAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(accessToken ?? string.Empty));

        var tokenStoreMock = new Mock<IGitHubTokenStore>();
        tokenStoreMock
            .Setup(t => t.HasTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        tokenStoreMock
            .Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubOAuthTokenPayload
            {
                AccessToken = accessToken ?? string.Empty,
                AuthenticatedUserLogin = login,
                AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            });

        var ghClient = clientMock ?? new Mock<IGitHubClientService>();
        ghClient
            .Setup(c => c.CreatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CreateGitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, CreateGitHubPullRequestRequest req, CancellationToken _) => new GitHubPullRequest
            {
                Number = 45,
                Title = req.Title,
                Body = req.Body,
                Draft = req.Draft,
                State = "open",
                HtmlUrl = "https://github.com/acme/demo/pull/45"
            });
        ghClient
            .Setup(c => c.UpdatePullRequestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<UpdateGitHubPullRequestRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, int prNum, UpdateGitHubPullRequestRequest req, CancellationToken _) => new GitHubPullRequest
            {
                Number = prNum,
                Title = req.Title ?? "Updated PR",
                Body = req.Body,
                State = "open",
                HtmlUrl = $"https://github.com/acme/demo/pull/{prNum}"
            });

        return new GitHubPullRequestDeliveryService(
            _runner,
            _gitWorkspaceManager,
            _mirrorSyncService,
            authMock.Object,
            tokenStoreMock.Object,
            ghClient.Object,
            Options.Create(new SecurityOptions { AirGappedMode = airGapped }),
            NullLogger<GitHubPullRequestDeliveryService>.Instance);
    }

    [Fact]
    public async Task PullFromHostAsync_AncestorProtection_RetainsMirrorAheadCommits_AndReconcilesOnNonAncestorDivergence()
    {
        // Arrange: host repo + mirror + simulated github-remote
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("README.md", "# Initial\n");
        hostRepo.CommitAll("Initial commit");

        var mirrorParent = CreateTempDir("mirror_parent");
        var workspace = new Workspace
        {
            Id = 10,
            Name = "Ancestor Protection Test",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main"
        };

        var mirrorPath = await _mirrorSyncService.InitializeMirrorAsync(workspace, mirrorParent);
        await ConfigureGitIdentityAsync(mirrorPath);

        // Simulate mirror main advancing 1 commit ahead of host-origin/main (e.g. fetched from GitHub)
        File.WriteAllText(Path.Combine(mirrorPath, "TEAMMATE.md"), "Teammate commit from GitHub\n");
        await _runner.ExecuteAsync(mirrorPath, ["add", "-A"]);
        await _runner.ExecuteAsync(mirrorPath, ["commit", "-m", "feat: teammate upstream commit", "--no-verify"]);
        var aheadSha = (await _runner.ExecuteAsync(mirrorPath, ["rev-parse", "HEAD"])).StandardOutput.Trim();

        // Act 1: PullFromHostAsync when host-origin/main is an ancestor of mirror main
        await _mirrorSyncService.PullFromHostAsync(mirrorPath, "main");

        // Assert 1: Mirror retains the teammate commit!
        var currentMirrorSha = (await _runner.ExecuteAsync(mirrorPath, ["rev-parse", "HEAD"])).StandardOutput.Trim();
        Assert.Equal(aheadSha, currentMirrorSha);
        Assert.True(File.Exists(Path.Combine(mirrorPath, "TEAMMATE.md")));

        // Act 2: Advance host-origin/main with a non-ancestor commit (divergent history)
        hostRepo.CreateFile("HOST_CHANGE.md", "Host divergent commit\n");
        hostRepo.CommitAll("feat: host commit");
        var hostSha = (await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["rev-parse", "HEAD"])).StandardOutput.Trim();

        await _mirrorSyncService.PullFromHostAsync(mirrorPath, "main");

        // Assert 2: Mirror reconciles to host-origin/main when host is no longer an ancestor
        var reconciledSha = (await _runner.ExecuteAsync(mirrorPath, ["rev-parse", "HEAD"])).StandardOutput.Trim();
        Assert.Equal(hostSha, reconciledSha);
        Assert.True(File.Exists(Path.Combine(mirrorPath, "HOST_CHANGE.md")));
    }

    [Fact]
    public async Task SyncBaseBranchFromRemoteIfConfiguredAsync_FetchesRemoteMain_AndWorktreeContainsUpstreamCommit()
    {
        // Arrange: github-remote bare repo + host repo + mirror
        var githubRemoteBare = await InitBareRepoAsync("gh_remote");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("README.md", "# Project\n");
        hostRepo.CommitAll("Initial commit");

        var mirrorParent = CreateTempDir("mirror_parent");
        var workspace = new Workspace
        {
            Id = 20,
            Name = "Pre-Branch Sync Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };
        var mirrorPath = await _mirrorSyncService.InitializeMirrorAsync(workspace, Path.Combine(mirrorParent, "clone"));
        workspace.Ext4MirrorPath = mirrorPath;
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        // Simulate upstream commit pushed directly to github-remote/main
        using var teammateClone = new TestGitRepository("main");
        await _runner.ExecuteAsync(teammateClone.RepositoryPath, ["pull", githubRemoteBare, "main"]);
        teammateClone.CreateFile("src/Upstream.cs", "public class Upstream {}\n");
        teammateClone.CommitAll("feat: upstream file on github");
        await _runner.ExecuteAsync(teammateClone.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        // Act: sync base branch from remote and create task worktree
        var deliveryService = CreateDeliveryService(accessToken: null);
        await deliveryService.FetchAndFastForwardBaseBranchFromRemoteAsync(workspace, "main");
        var worktreePath = await _gitWorkspaceManager.EnsureWorktreeAsync(workspace, 7, "main");

        // Assert: worktree branched from updated main contains src/Upstream.cs and host repo was also updated
        Assert.True(File.Exists(Path.Combine(worktreePath, "src", "Upstream.cs")));
        Assert.True(File.Exists(Path.Combine(hostRepo.RepositoryPath, "src", "Upstream.cs")));
    }

    [Fact]
    public async Task StripTaskMarkdownDeliverables_CaseA_WhenBaseHasNoDocsTasks_RemovesAllDocsTasksFromRemoteBranch()
    {
        // Arrange
        var githubRemoteBare = await InitBareRepoAsync("gh_case_a");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("README.md", "# Case A\n");
        hostRepo.CommitAll("Initial commit without docs/tasks");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        var workspace = new Workspace
        {
            Id = 31,
            Name = "Case A Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var worktreePath = await _gitWorkspaceManager.EnsureWorktreeAsync(workspace, 1, "main");
        await ConfigureGitIdentityAsync(worktreePath);

        Directory.CreateDirectory(Path.Combine(worktreePath, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "docs", "tasks", "2026-11-13-sample.md"), "---\nid: 1\n---\n# Task 1");
        Directory.CreateDirectory(Path.Combine(worktreePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "src", "Feature.cs"), "public class Feature {}\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, 1, "implementation", "Add feature and task md");

        var task = new ForgeTask
        {
            Id = 1,
            WorkspaceId = workspace.Id,
            Title = "Implement Feature",
            BaseBranch = "main",
            FeatureBranch = "feature/task-1",
            MergeMode = MergeMode.ExternalPullRequest
        };

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null);

        // Act
        var pr = await deliveryService.DeliverPullRequestAsync(task, workspace);

        // Assert
        Assert.Equal(45, pr.Number);
        Assert.Equal(45, task.PullRequestNumber);

        var treeOutput = (await _runner.ExecuteAsync(githubRemoteBare, ["ls-tree", "-r", "--name-only", "refs/heads/ncf/alice/task-1"])).StandardOutput;
        Assert.Contains("src/Feature.cs", treeOutput);
        Assert.DoesNotContain("docs/tasks", treeOutput);
    }

    [Fact]
    public async Task StripTaskMarkdownDeliverables_CaseB_WhenBaseHasDocsTasksFiles_PreservesBaseDocsTasksAndStripsNewTaskMd()
    {
        // Arrange
        var githubRemoteBare = await InitBareRepoAsync("gh_case_b");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("docs/tasks/README.md", "# Committed Tasks Index\n");
        hostRepo.CommitAll("Initial commit with docs/tasks/README.md");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        var workspace = new Workspace
        {
            Id = 32,
            Name = "Case B Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var worktreePath = await _gitWorkspaceManager.EnsureWorktreeAsync(workspace, 2, "main");
        await ConfigureGitIdentityAsync(worktreePath);

        await File.WriteAllTextAsync(Path.Combine(worktreePath, "docs", "tasks", "2026-11-13-task-2.md"), "---\nid: 2\n---\n# Task 2");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "docs", "tasks", "README.md"), "# Modified locally\n");
        Directory.CreateDirectory(Path.Combine(worktreePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "src", "App.cs"), "public class App {}\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, 2, "implementation", "Add App.cs and task-2.md");

        var task = new ForgeTask
        {
            Id = 2,
            WorkspaceId = workspace.Id,
            Title = "Implement App",
            BaseBranch = "main",
            FeatureBranch = "feature/task-2",
            MergeMode = MergeMode.ExternalPullRequest
        };

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null);

        // Act
        await deliveryService.DeliverPullRequestAsync(task, workspace);

        // Assert
        var treeOutput = (await _runner.ExecuteAsync(githubRemoteBare, ["ls-tree", "-r", "--name-only", "refs/heads/ncf/alice/task-2"])).StandardOutput;
        Assert.Contains("src/App.cs", treeOutput);
        Assert.Contains("docs/tasks/README.md", treeOutput);
        Assert.DoesNotContain("2026-11-13-task-2.md", treeOutput);

        var readmeContent = (await _runner.ExecuteAsync(githubRemoteBare, ["show", "refs/heads/ncf/alice/task-2:docs/tasks/README.md"])).StandardOutput;
        Assert.Equal("# Committed Tasks Index\n", readmeContent.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task StripTaskMarkdownDeliverables_CaseC_IdempotentAcrossRequestChangesLoopback_AndPreservesCleanLiveDiff()
    {
        // Arrange
        var githubRemoteBare = await InitBareRepoAsync("gh_case_c");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("README.md", "# Case C\n");
        hostRepo.CommitAll("Initial commit");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        var workspace = new Workspace
        {
            Id = 33,
            Name = "Case C Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var worktreePath = await _gitWorkspaceManager.EnsureWorktreeAsync(workspace, 3, "main");
        await ConfigureGitIdentityAsync(worktreePath);

        Directory.CreateDirectory(Path.Combine(worktreePath, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "docs", "tasks", "2026-11-13-sample.md"), "---\nid: 3\n---\n# Task 3 v1");
        Directory.CreateDirectory(Path.Combine(worktreePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "src", "Feature.cs"), "public class Feature { public int V => 1; }\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, 3, "implementation", "First implementation");

        var task = new ForgeTask
        {
            Id = 3,
            WorkspaceId = workspace.Id,
            Title = "Iterative Feature",
            BaseBranch = "main",
            FeatureBranch = "feature/task-3",
            MergeMode = MergeMode.ExternalPullRequest
        };

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null);

        // First delivery
        await deliveryService.DeliverPullRequestAsync(task, workspace);

        // Simulate Request Changes loop: Stage 3 re-adds docs/tasks/2026-11-13-sample.md and adds src/FeatureV2.cs
        Directory.CreateDirectory(Path.Combine(worktreePath, "docs", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "docs", "tasks", "2026-11-13-sample.md"), "---\nid: 3\n---\n# Task 3 v2");
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "src", "FeatureV2.cs"), "public class FeatureV2 {}\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, 3, "implementation", "Second implementation after review");

        // Second delivery (updates existing PR #45 and pushes with explicit lease)
        await deliveryService.DeliverPullRequestAsync(task, workspace);

        // Assert remote branch has Feature.cs + FeatureV2.cs and zero docs/tasks files
        var treeOutput = (await _runner.ExecuteAsync(githubRemoteBare, ["ls-tree", "-r", "--name-only", "refs/heads/ncf/alice/task-3"])).StandardOutput;
        Assert.Contains("src/Feature.cs", treeOutput);
        Assert.Contains("src/FeatureV2.cs", treeOutput);
        Assert.DoesNotContain("docs/tasks", treeOutput);

        // Assert GenerateLiveDiffAsync produces a clean diff of only src/ changes
        var liveDiff = await _gitWorkspaceManager.GenerateLiveDiffAsync(workspace, 3, "main");
        Assert.Contains("src/Feature.cs", liveDiff);
        Assert.Contains("src/FeatureV2.cs", liveDiff);
        Assert.DoesNotContain("docs/tasks", liveDiff);
    }

    [Fact]
    public async Task PushTaskBranchWithLeaseAsync_RawUrlExplicitLease_CreatesAndUpdatesBranch_AndRejectsOutOfBandOverwrite()
    {
        // Arrange
        var githubRemoteBare = await InitBareRepoAsync("gh_lease");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("README.md", "# Lease Test\n");
        hostRepo.CommitAll("Initial commit");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        var workspace = new Workspace
        {
            Id = 40,
            Name = "Lease Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var worktreePath = await _gitWorkspaceManager.EnsureWorktreeAsync(workspace, 1, "main");
        await ConfigureGitIdentityAsync(worktreePath);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "A.txt"), "v1\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(workspace, 1, "implementation", "Commit v1");

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null);

        // Act 1: Initial push (remote branch does not exist yet -> expectedSha == "")
        var pushedLocalSha = await deliveryService.PushTaskBranchWithLeaseAsync(
            hostRepo.RepositoryPath,
            githubRemoteBare,
            localBranch: "feature/task-1",
            remoteBranchName: "ncf/alice/task-1",
            accessToken: null);

        Assert.False(string.IsNullOrWhiteSpace(pushedLocalSha));
        var remoteSha1 = (await _runner.ExecuteAsync(githubRemoteBare, ["rev-parse", "refs/heads/ncf/alice/task-1"])).StandardOutput.Trim();
        Assert.Equal(pushedLocalSha, remoteSha1);

        // Act 2: Amend/rewrite local branch and push a second time -> explicit lease succeeds
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "A.txt"), "v2-amended\n");
        await _runner.ExecuteAsync(worktreePath, ["add", "-A"]);
        await _runner.ExecuteAsync(worktreePath, ["commit", "--amend", "-m", "Commit v2 amended", "--no-verify"]);

        await deliveryService.PushTaskBranchWithLeaseAsync(
            hostRepo.RepositoryPath,
            githubRemoteBare,
            localBranch: "feature/task-1",
            remoteBranchName: "ncf/alice/task-1",
            accessToken: null);

        var remoteSha2 = (await _runner.ExecuteAsync(githubRemoteBare, ["rev-parse", "refs/heads/ncf/alice/task-1"])).StandardOutput.Trim();
        Assert.NotEqual(remoteSha1, remoteSha2);

        // Verify .git/config contains zero credentials or extraHeader entries
        var gitConfig = await File.ReadAllTextAsync(Path.Combine(hostRepo.RepositoryPath, ".git", "config"));
        Assert.DoesNotContain("extraHeader", gitConfig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", gitConfig, StringComparison.OrdinalIgnoreCase);

        // Act 3: Advance refs/heads/ncf/alice/task-1 out-of-band on github-remote, then attempt push with stale expected SHA (remoteSha1)
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "A.txt"), "v3-local\n");
        await _runner.ExecuteAsync(worktreePath, ["add", "-A"]);
        await _runner.ExecuteAsync(worktreePath, ["commit", "--amend", "-m", "Commit v3 local", "--no-verify"]);

        await Assert.ThrowsAsync<GitCommandException>(async () =>
        {
            await _runner.ExecuteAsync(
                hostRepo.RepositoryPath,
                [
                    "push",
                    $"--force-with-lease=refs/heads/ncf/alice/task-1:{remoteSha1}",
                    githubRemoteBare,
                    "refs/heads/feature/task-1:refs/heads/ncf/alice/task-1"
                ]);
        });
    }

    [Fact]
    public async Task MultiUserBranchIsolation_AliceAndBobPushSameTaskId_CoexistOnRemoteWithDistinctTrees()
    {
        // Arrange
        var githubRemoteBare = await InitBareRepoAsync("gh_multiuser");
        using var aliceRepo = new TestGitRepository("main");
        aliceRepo.CreateFile("README.md", "# Shared Project\n");
        aliceRepo.CommitAll("Initial shared commit");
        await _runner.ExecuteAsync(aliceRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        using var bobRepo = new TestGitRepository("main");
        await _runner.ExecuteAsync(bobRepo.RepositoryPath, ["pull", githubRemoteBare, "main"]);

        var aliceWorkspace = new Workspace
        {
            Id = 51,
            Name = "Alice Workspace",
            RepositoryPath = aliceRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };
        var bobWorkspace = new Workspace
        {
            Id = 52,
            Name = "Bob Workspace",
            RepositoryPath = bobRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var aliceWt = await _gitWorkspaceManager.EnsureWorktreeAsync(aliceWorkspace, 5, "main");
        await ConfigureGitIdentityAsync(aliceWt);
        await File.WriteAllTextAsync(Path.Combine(aliceWt, "Alice.cs"), "public class Alice {}\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(aliceWorkspace, 5, "implementation", "Alice work");

        var bobWt = await _gitWorkspaceManager.EnsureWorktreeAsync(bobWorkspace, 5, "main");
        await ConfigureGitIdentityAsync(bobWt);
        await File.WriteAllTextAsync(Path.Combine(bobWt, "Bob.cs"), "public class Bob {}\n");
        await _gitWorkspaceManager.CreateWipCheckpointAsync(bobWorkspace, 5, "implementation", "Bob work");

        var aliceService = CreateDeliveryService(login: "alice", accessToken: null);
        var bobService = CreateDeliveryService(login: "bob", accessToken: null);

        // Act
        await aliceService.DeliverPullRequestAsync(new ForgeTask { Id = 5, Title = "Alice Task 5", BaseBranch = "main" }, aliceWorkspace);
        await bobService.DeliverPullRequestAsync(new ForgeTask { Id = 5, Title = "Bob Task 5", BaseBranch = "main" }, bobWorkspace);

        // Assert
        var aliceTree = (await _runner.ExecuteAsync(githubRemoteBare, ["ls-tree", "-r", "--name-only", "refs/heads/ncf/alice/task-5"])).StandardOutput;
        var bobTree = (await _runner.ExecuteAsync(githubRemoteBare, ["ls-tree", "-r", "--name-only", "refs/heads/ncf/bob/task-5"])).StandardOutput;

        Assert.Contains("Alice.cs", aliceTree);
        Assert.DoesNotContain("Bob.cs", aliceTree);

        Assert.Contains("Bob.cs", bobTree);
        Assert.DoesNotContain("Alice.cs", bobTree);
    }

    [Fact]
    public async Task GitCliRunner_RedactsCredentialsAcrossLogsAndExceptions_AndResolvesNetworkTimeoutWithMinusCFlags()
    {
        // 1. Verify timeout resolution when -c flags precede push / fetch / ls-remote
        Assert.Equal(
            GitCliRunner.NetworkOperationTimeoutSeconds,
            GitCliRunner.ResolveTimeoutSeconds(["-c", "http.extraHeader=Authorization: Bearer SECRET", "push", "https://github.com/o/r.git"], null));
        Assert.Equal(
            GitCliRunner.NetworkOperationTimeoutSeconds,
            GitCliRunner.ResolveTimeoutSeconds(["-c", "http.extraHeader=Authorization: Bearer SECRET", "fetch", "https://github.com/o/r.git"], null));
        Assert.Equal(
            GitCliRunner.NetworkOperationTimeoutSeconds,
            GitCliRunner.ResolveTimeoutSeconds(["-c", "http.extraHeader=Authorization: Bearer SECRET", "ls-remote", "https://github.com/o/r.git"], null));
        Assert.Equal(
            GitCliRunner.DefaultTimeoutSeconds,
            GitCliRunner.ResolveTimeoutSeconds(["-c", "core.quotepath=false", "status"], null));

        // 2. Verify GitCommandException and ILogger never leak SUPER_SECRET_TOKEN
        var logMessages = new List<string>();
        var capturingLogger = new CapturingLogger<GitCliRunner>(logMessages);
        using var repo = new TestGitRepository("main");

        var runnerWithLogger = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            _pathResolver,
            _wslMetadata,
            capturingLogger);

        var secretArg = "http.extraHeader=Authorization: Basic SUPER_SECRET_TOKEN";
        var cmdEx = await Assert.ThrowsAsync<GitCommandException>(async () =>
        {
            await runnerWithLogger.ExecuteAsync(
                repo.RepositoryPath,
                ["-c", secretArg, "fetch", "https://x-access-token:SUPER_SECRET_TOKEN@invalid.local/nonexistent.git"]);
        });

        Assert.DoesNotContain("SUPER_SECRET_TOKEN", cmdEx.Message);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", cmdEx.Arguments);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", string.Join(" ", cmdEx.ArgumentsList));
        Assert.All(logMessages, msg => Assert.DoesNotContain("SUPER_SECRET_TOKEN", msg));

        // 3. Verify TimeoutException never leaks SUPER_SECRET_TOKEN
        var timeoutProcessRunner = new FakeDelayProcessRunner(TimeSpan.FromSeconds(5));
        var timeoutGitRunner = new GitCliRunner(
            timeoutProcessRunner,
            _pathResolver,
            _wslMetadata,
            capturingLogger);

        var timeoutEx = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await timeoutGitRunner.ExecuteAsync(
                repo.RepositoryPath,
                ["-c", secretArg, "push", "https://github.com/acme/demo.git"],
                timeoutSeconds: 1);
        });

        Assert.DoesNotContain("SUPER_SECRET_TOKEN", timeoutEx.Message);
        Assert.All(logMessages, msg => Assert.DoesNotContain("SUPER_SECRET_TOKEN", msg));
    }

    [Fact]
    public async Task BuildPullRequestTitle_And_BuildPullRequestBodyAsync_FormatsGitHubIssueRef_ResolvesKeyword_AndMetricsAttribution()
    {
        // Arrange
        using var repo = new TestGitRepository("main");
        repo.CreateFile("README.md", "# Test\n");
        repo.CommitAll("Initial commit");

        var workspace = new Workspace
        {
            Id = 60,
            Name = "PR Formatting Workspace",
            RepositoryPath = repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };

        var bag = new NightCodeForge.Core.Workflow.PipelineDataBag();
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.TestsCurrentTotal, 42);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.TestsCurrentPassed, 42);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.TestsCurrentFailed, 0);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.CoverageBaselineLineRate, 80.5);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.CoverageCurrentLineRate, 83.25);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.WarningsBaselineTotal, 2);
        bag.Set(NightCodeForge.Core.Workflow.PipelineDataBagKeys.WarningsCurrentTotal, 2);

        var task = new ForgeTask
        {
            Id = 99,
            WorkspaceId = workspace.Id,
            Title = "Fix OAuth token refresh race",
            Description = "Addresses background refresh edge case discussed in #123.",
            ExternalSource = "GitHub",
            ExternalReferenceId = "#123",
            PipelineDataJson = bag.ToJson()
        };

        var deliveryService = CreateDeliveryService(login: "octocat", accessToken: "gho_test");

        // Act
        var title = GitHubPullRequestDeliveryService.BuildPullRequestTitle(task);
        var body = await deliveryService.BuildPullRequestBodyAsync(task, workspace, "main");

        // Assert
        Assert.Equal("Fix OAuth token refresh race (#123)", title);
        Assert.Contains("Resolves #123", body);
        Assert.Contains("42 / 42 passed (0 failed)", body);
        Assert.Contains("Baseline: 80.5% -> Current: 83.25%", body);
        Assert.Contains("+2.75%", body);
        Assert.Contains("by @octocat (Local Task #99)", body);
    }

    [Fact]
    public async Task VerifyAndSyncExternallyMergedPullRequestAsync_WhenRemoteBaseBranchFetchFails_RemainsBestEffortAndReturnsMergedPr()
    {
        // Arrange
        var ghClientMock = new Mock<IGitHubClientService>();
        ghClientMock
            .Setup(c => c.GetPullRequestAsync("acme", "demo", 45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 45,
                State = "closed",
                Merged = true,
                MergeCommitSha = "deadbeef1234567890"
            });

        using var repo = new TestGitRepository("main");
        repo.CreateFile("README.md", "# Test\n");
        repo.CommitAll("Initial commit");

        // Point RepositoryName to a nonexistent path so git fetch throws GitCommandException
        var workspace = new Workspace
        {
            Id = 61,
            Name = "Best Effort Sync Workspace",
            RepositoryPath = repo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = Path.Combine(Path.GetTempPath(), $"nonexistent_bare_{Guid.NewGuid():N}")
            }
        };

        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = workspace.Id,
            Title = "Merged Task",
            PullRequestNumber = 45,
            BaseBranch = "main"
        };

        // Note: GetPullRequestAsync uses trimmed RepositoryName; setup wildcard repo name on mock
        ghClientMock
            .Setup(c => c.GetPullRequestAsync("acme", It.IsAny<string>(), 45, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 45,
                State = "closed",
                Merged = true,
                MergeCommitSha = "deadbeef1234567890"
            });

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null, clientMock: ghClientMock);

        // Act: should NOT throw even though FetchAndFastForwardBaseBranchFromRemoteAsync fails on git fetch
        var pr = await deliveryService.VerifyAndSyncExternallyMergedPullRequestAsync(task, workspace);

        // Assert
        Assert.True(pr.Merged);
        Assert.Equal("deadbeef1234567890", pr.MergeCommitSha);
    }

    [Fact]
    public async Task DeliverPullRequestAsync_WhenWorktreeMissingAndCanonicalHeadIsMain_DoesNotMutateMainBranch()
    {
        // Arrange: single clone where worktree directory does not exist and HEAD is main
        var githubRemoteBare = await InitBareRepoAsync("gh_no_wt");
        using var hostRepo = new TestGitRepository("main");
        hostRepo.CreateFile("docs/tasks/task-0001.md", "# Task on main\n");
        hostRepo.CommitAll("Initial commit on main with docs/tasks/task-0001.md");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["push", githubRemoteBare, "HEAD:refs/heads/main"]);

        // Create feature/task-8 branch with a code change, then switch hostRepo back to main
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["checkout", "-b", "feature/task-8"]);
        hostRepo.CreateFile("src/NewService.cs", "public class NewService {}\n");
        hostRepo.CommitAll("Add NewService on feature branch");
        await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["checkout", "main"]);
        var mainShaBefore = (await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["rev-parse", "main"])).StandardOutput.Trim();

        var workspace = new Workspace
        {
            Id = 62,
            Name = "No Worktree Workspace",
            RepositoryPath = hostRepo.RepositoryPath,
            DefaultBaseBranch = "main",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = githubRemoteBare
            }
        };

        var task = new ForgeTask
        {
            Id = 8,
            WorkspaceId = workspace.Id,
            Title = "Branch Without Worktree",
            BaseBranch = "main",
            FeatureBranch = "feature/task-8",
            MergeMode = MergeMode.ExternalPullRequest
        };

        var deliveryService = CreateDeliveryService(login: "alice", accessToken: null);

        // Act
        var pr = await deliveryService.DeliverPullRequestAsync(task, workspace);

        // Assert: main was not touched by StripTaskMarkdownDeliverablesFromBranchAsync
        var mainShaAfter = (await _runner.ExecuteAsync(hostRepo.RepositoryPath, ["rev-parse", "main"])).StandardOutput.Trim();
        Assert.Equal(mainShaBefore, mainShaAfter);
        Assert.Equal(45, pr.Number);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            TestGitRepository.DeleteDirectorySafe(dir);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages;

        public CapturingLogger(List<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
            if (exception != null)
            {
                _messages.Add(exception.ToString());
            }
        }
    }

    private sealed class FakeDelayProcessRunner : IProcessRunner
    {
        private readonly TimeSpan _delay;

        public FakeDelayProcessRunner(TimeSpan delay) => _delay = delay;

        public ProcessExecutionResult Run(ProcessStartSpec spec) => throw new NotImplementedException();

        public async Task<ProcessExecutionResult> RunAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return new ProcessExecutionResult(0, string.Empty, string.Empty);
        }

        public IAsyncEnumerable<ProcessStreamChunk> RunStreamingAsync(ProcessStartSpec spec, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IProcessStreamSession StartSession(ProcessStartSpec spec)
            => throw new NotImplementedException();
    }
}
