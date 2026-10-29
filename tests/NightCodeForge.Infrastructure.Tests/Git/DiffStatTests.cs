using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class DiffStatTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly GitWorkspaceManager _manager;
    private readonly Workspace _workspace;

    public DiffStatTests()
    {
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        _repo = new TestGitRepository("main");
        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        _runner = new GitCliRunner(
            processRunner,
            pathResolver,
            wslMeta,
            NullLogger<GitCliRunner>.Instance);
        _manager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            processRunner,
            NullLogger<GitWorkspaceManager>.Instance);

        _repo.CreateFile("README.md", "# Base Project");
        _repo.CommitAll("Initial commit");

        _workspace = new Workspace
        {
            Id = 917,
            Name = "DiffStat Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
    }

    [Fact]
    public async Task GenerateDiffStatAsync_UsesWidePaths_ExcludesNoise_AndFormatsOutput()
    {
        await _manager.CheckoutTaskBranchAsync(_workspace, 91701, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, 91701);

        // Deep namespace path that would normally be truncated by default git diff --stat
        var deepPath = "src/VeryLongNamespace/AnotherSubNamespace/DeeplyNestedComponent/CustomerService.cs";
        _repo.CreateFile(deepPath, "public class CustomerService { public void Run() {} }\n", wtPath);

        // Noisy files that must be excluded
        _repo.CreateFile("package-lock.json", "{\"name\":\"pkg\"}", wtPath);
        _repo.CreateFile("Form1.Designer.cs", "// designer code", wtPath);
        _repo.CreateFile("Model.generated.cs", "// generated code", wtPath);
        _repo.CreateFile("TestResults/results.trx", "<TestRun />", wtPath);

        _repo.CommitAll("Add source and noisy files", wtPath);

        var stat = await _manager.GenerateDiffStatAsync(_workspace, 91701, "main");

        // Assert deep path is not truncated
        Assert.Contains("src/VeryLongNamespace/AnotherSubNamespace/DeeplyNestedComponent/CustomerService.cs", stat);

        // Assert exclusions
        Assert.DoesNotContain("package-lock.json", stat);
        Assert.DoesNotContain("Form1.Designer.cs", stat);
        Assert.DoesNotContain("Model.generated.cs", stat);
        Assert.DoesNotContain("TestResults", stat);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenEmpty_ReturnsExplicitNoChanges()
    {
        await _manager.CheckoutTaskBranchAsync(_workspace, 91702, "main");

        var stat = await _manager.GenerateDiffStatAsync(_workspace, 91702, "main");

        Assert.Equal(GitDiffConstants.NoChanges, stat);
    }

    [Fact]
    public async Task GenerateLiveDiffStatAsync_IncludesUncommittedChanges_AndExcludesNoise()
    {
        await _manager.CheckoutTaskBranchAsync(_workspace, 91703, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, 91703);

        // Uncommitted changes in working tree (including newly created untracked file)
        _repo.CreateFile("src/LiveModule.cs", "public class LiveModule {}\n", wtPath);
        _repo.CreateFile("package-lock.json", "{}", wtPath);

        var liveStat = await _manager.GenerateLiveDiffStatAsync(_workspace, 91703, "main");

        Assert.Contains("src/LiveModule.cs", liveStat);
        Assert.DoesNotContain("package-lock.json", liveStat);

        // Assert worktree primary index was not mutated by temp index execution
        var status = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain", "-uall"]);
        Assert.Contains("?? src/LiveModule.cs", status.StandardOutput);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenExceedsMaxFiles_CapsOutputWithIndicator()
    {
        await _manager.CheckoutTaskBranchAsync(_workspace, 91704, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, 91704);

        // Create 55 files (exceeding MaxFilesShown = 50)
        for (int i = 1; i <= 55; i++)
        {
            _repo.CreateFile($"src/GeneratedFile_{i:D3}.cs", $"// file {i}\n", wtPath);
        }
        _repo.CommitAll("Add 55 files", wtPath);

        var stat = await _manager.GenerateDiffStatAsync(_workspace, 91704, "main");

        Assert.Contains("... and 5 more files", stat);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenBaseBranchInvalid_ThrowsGitCommandException()
    {
        await _manager.CheckoutTaskBranchAsync(_workspace, 91705, "main");

        await Assert.ThrowsAsync<GitCommandException>(async () =>
        {
            await _manager.GenerateDiffStatAsync(_workspace, 91705, "non-existent-base-branch");
        });
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenWorkspaceNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _manager.GenerateDiffStatAsync(null!, 91706, "main"));

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _manager.GenerateLiveDiffStatAsync(null!, 91706, "main"));
    }

    [Fact]
    public async Task GenerateDiffStatAsync_ContainerSandbox_ExecutesInContainerWorktreePath()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        string? executedWorkingDir = null;
        mockRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns<string, IEnumerable<string>, int?, CancellationToken>((wd, args, to, ct) =>
            {
                executedWorkingDir = wd;
                return Task.FromResult(new GitCommandResult(0, " 1 file changed, 1 insertion(+)", "", TimeSpan.FromMilliseconds(50)));
            });

        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var containerWorkspace = new Workspace
        {
            Id = 918,
            Name = "Container Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            Ext4MirrorPath = "/home/runner/.nightcodeforge/wks/918/clone",
            RepositoryPath = "C:\\host\\repo",
            DefaultBaseBranch = "main"
        };

        var stat = await manager.GenerateDiffStatAsync(containerWorkspace, 42, "main");

        Assert.Equal("1 file changed, 1 insertion(+)", stat);
        Assert.NotNull(executedWorkingDir);
        Assert.Contains("918", executedWorkingDir);
        Assert.Contains("task-42", executedWorkingDir);
        Assert.DoesNotContain("C:\\host\\repo", executedWorkingDir);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_ContainerSandbox_ExecutesInContainerWorktreePath()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        string? executedWorkingDir = null;
        mockRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .Returns<string, IEnumerable<string>, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((wd, args, to, env, ct) =>
            {
                executedWorkingDir = wd;
                return Task.FromResult(new GitCommandResult(0, "diff --git a/test.cs b/test.cs", "", TimeSpan.FromMilliseconds(50)));
            });

        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var containerWorkspace = new Workspace
        {
            Id = 918,
            Name = "Container Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            Ext4MirrorPath = "/home/runner/.nightcodeforge/wks/918/clone",
            RepositoryPath = "C:\\host\\repo",
            DefaultBaseBranch = "main"
        };

        var diff = await manager.GenerateLiveDiffAsync(containerWorkspace, 42, "main");

        Assert.Equal("diff --git a/test.cs b/test.cs", diff);
        Assert.NotNull(executedWorkingDir);
        Assert.Contains("task-42", executedWorkingDir);
        Assert.DoesNotContain("C:\\host\\repo", executedWorkingDir);
    }

    [Theory]
    [InlineData("/home/user/repo", true, true)]
    [InlineData("/home/user/repo", false, false)]
    [InlineData("C:\\repo", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void IsForeignPosixPath_OnlyTrueForRootedPathsOnWindowsHosts(string? path, bool isWindowsHost, bool expected)
    {
        Assert.Equal(expected, GitWorkspaceManager.IsForeignPosixPath(path, isWindowsHost));
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WorktreeMissing_FallsBackToRepositoryPath()
    {
        // Worktree for this task was never created: the stat must be generated from the repository,
        // not from a nonexistent worktree directory (regression: POSIX detection on Linux hosts).
        _repo.RunGit("branch feature/task-91790");
        var hostWorkspace = CreateBareHostWorkspace();
        var stat = await _manager.GenerateDiffStatAsync(hostWorkspace, 91790, "main");

        Assert.Equal(GitDiffConstants.NoChanges, stat);
    }

    private Workspace CreateBareHostWorkspace() => new()
    {
        Id = 917,
        Name = "Bare Host Workspace",
        ExecutionMode = ExecutionMode.BareHost,
        RepositoryPath = _repo.RepositoryPath,
        DefaultBaseBranch = "main"
    };

    [Fact]
    public async Task GenerateLiveDiffAsync_IncludesUncommittedAndUntrackedFiles()
    {
        var taskId = 91710;
        await _manager.CheckoutTaskBranchAsync(_workspace, taskId, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, taskId);

        // 1. Commit on task branch
        _repo.CreateFile("file1.txt", "line 1 committed\n", wtPath);
        _repo.CommitAll("Initial file1 commit on task branch", wtPath);

        // 2. Stage an additional change in file1.txt
        _repo.CreateFile("file1.txt", "line 1 committed\nline 2 staged\n", wtPath);
        _repo.RunGit("add file1.txt", wtPath);

        // 3. Leave an unstaged modification in file1.txt
        _repo.CreateFile("file1.txt", "line 1 committed\nline 2 staged\nline 3 unstaged\n", wtPath);

        // 4. Create an untracked file
        _repo.CreateFile("file2.txt", "brand new untracked content\n", wtPath);

        // Act
        var diff = await _manager.GenerateLiveDiffAsync(_workspace, taskId, "main");

        // Assert
        Assert.Contains("diff --git a/file1.txt b/file1.txt", diff);
        Assert.Contains("+line 1 committed", diff);
        Assert.Contains("+line 2 staged", diff);
        Assert.Contains("+line 3 unstaged", diff);

        Assert.Contains("diff --git a/file2.txt b/file2.txt", diff);
        Assert.Contains("+brand new untracked content", diff);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_UsesIsolatedTempIndexWithoutMutatingWorktreeIndex()
    {
        var taskId = 91711;
        await _manager.CheckoutTaskBranchAsync(_workspace, taskId, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, taskId);

        _repo.CreateFile("untracked_probe.txt", "probe content\n", wtPath);

        // Act
        var diff = await _manager.GenerateLiveDiffAsync(_workspace, taskId, "main");

        Assert.Contains("diff --git a/untracked_probe.txt b/untracked_probe.txt", diff);

        // Assert: Primary worktree index untouched - untracked status remains "??"
        var statusResult = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
        Assert.Contains("?? untracked_probe.txt", statusResult.StandardOutput);
        Assert.DoesNotContain("A  untracked_probe.txt", statusResult.StandardOutput);
        Assert.DoesNotContain("M  untracked_probe.txt", statusResult.StandardOutput);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_RespectsExclusionPathspecs()
    {
        var taskId = 91712;
        await _manager.CheckoutTaskBranchAsync(_workspace, taskId, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, taskId);

        _repo.CreateFile("src/App.cs", "public class App {}\n", wtPath);
        _repo.CreateFile("bin/Debug/net10.0/App.dll", "binary dummy dll\n", wtPath);
        _repo.CreateFile("obj/project.assets.json", "{}\n", wtPath);
        _repo.CreateFile(".vs/Project/v17/.suo", "vs cache\n", wtPath);

        var diff = await _manager.GenerateLiveDiffAsync(_workspace, taskId, "main");

        Assert.Contains("diff --git a/src/App.cs b/src/App.cs", diff);
        Assert.DoesNotContain("bin/Debug/net10.0/App.dll", diff);
        Assert.DoesNotContain("obj/project.assets.json", diff);
        Assert.DoesNotContain(".vs/Project/v17/.suo", diff);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_ThrowsWorktreeNotFoundException_WhenWorktreeDoesNotExist()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var ex = await Assert.ThrowsAsync<WorktreeNotFoundException>(() =>
            manager.GenerateLiveDiffAsync(CreateBareHostWorkspace(), 91792, "main"));

        Assert.Equal(91792, ex.TaskId);
        Assert.Contains("task-91792", ex.AttemptedPath);
    }

    [Fact]
    public async Task GenerateLiveDiffStatAsync_ThrowsWorktreeNotFoundException_WhenWorktreeDoesNotExist()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        await Assert.ThrowsAsync<WorktreeNotFoundException>(() =>
            manager.GenerateLiveDiffStatAsync(CreateBareHostWorkspace(), 91791, "main"));
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_WhenDirectoryNotFoundException_ThrowsWorktreeNotFoundException()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        mockRunner.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException("Working directory '/home/runner/.nightcodeforge/wks/920/task-42' does not exist."));

        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var containerWorkspace = new Workspace
        {
            Id = 920,
            Name = "Container Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            Ext4MirrorPath = "/home/runner/.nightcodeforge/wks/920/clone",
            RepositoryPath = "C:\\host\\repo",
            DefaultBaseBranch = "main"
        };

        var ex = await Assert.ThrowsAsync<WorktreeNotFoundException>(() =>
            manager.GenerateLiveDiffAsync(containerWorkspace, 42, "main"));

        Assert.Equal(42, ex.TaskId);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WindowsHostPosixWorktreeWithoutWslDistro_ThrowsDomainException()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // POSIX->UNC resolution only applies on Windows hosts.
        }

        var mockRunner = new Mock<IGitCliRunner>();
        var wslMeta = new Mock<IWslMetadataService>(); // returns null distro
        var pathResolver = new WorkspacePathResolver(wslMeta.Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMeta.Object,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var workspace = new Workspace
        {
            Id = 919,
            Name = "Wsl Workspace",
            ExecutionMode = ExecutionMode.BareHost,
            Ext4MirrorPath = "/home/runner/.nightcodeforge/wks/919/clone",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };

        await Assert.ThrowsAsync<NightCodeForge.Core.Exceptions.WslDistributionUnavailableException>(() =>
            manager.EnsureWorktreeAsync(workspace, 43, "main"));
    }

    [Fact]
    public async Task GenerateDiffAsync_WhenWorktreeFails_FallsBackToCanonicalRepository()
    {
        // Arrange: Create branch feature/task-91785 in canonical repo and commit a change
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "-b", "feature/task-91785", "main"]);
        _repo.CreateFile("fallback_test.txt", "Fallback content on branch\n");
        _repo.CommitAll("Commit on task branch");
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "main"]);

        // Act: Generate diff for a task whose worktree directory does not exist
        var diff = await _manager.GenerateDiffAsync(_workspace, 91785, "main");

        // Assert: Fallback to canonical repository produces the branch diff
        Assert.NotEmpty(diff);
        Assert.Contains("diff --git a/fallback_test.txt b/fallback_test.txt", diff);
        Assert.Contains("+Fallback content on branch", diff);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenWorktreeFails_FallsBackToCanonicalRepository()
    {
        // Arrange: Create branch feature/task-91786 in canonical repo and commit a change
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "-b", "feature/task-91786", "main"]);
        _repo.CreateFile("fallback_stat.txt", "Fallback stat line 1\nFallback stat line 2\n");
        _repo.CommitAll("Commit on stat task branch");
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "main"]);

        // Act: Generate diff stat for a task whose worktree directory does not exist
        var stat = await _manager.GenerateDiffStatAsync(_workspace, 91786, "main");

        // Assert: Fallback to canonical repository produces formatted stat output
        Assert.NotEmpty(stat);
        Assert.Contains("fallback_stat.txt", stat);
        Assert.Contains("2 ++", stat);
    }

    [Fact]
    public async Task GenerateDiffAsync_WhenWorktreeDiffHasNoChanges_ReturnsEmptyWithoutError()
    {
        // Arrange: Worktree exists on task branch with 0 commits ahead of main
        await _manager.CheckoutTaskBranchAsync(_workspace, 91787, "main");

        // Act: Generate diff
        var diff = await _manager.GenerateDiffAsync(_workspace, 91787, "main");

        // Assert: Succeeded and empty diff returned
        Assert.Empty(diff);
    }

    [Fact]
    public async Task GenerateDiffStatAsync_WhenWorktreeDiffStatHasNoChanges_ReturnsNoChangesWithoutFailing()
    {
        // Arrange: Worktree exists on task branch with 0 commits ahead of main
        await _manager.CheckoutTaskBranchAsync(_workspace, 91788, "main");

        // Act: Generate diff stat
        var stat = await _manager.GenerateDiffStatAsync(_workspace, 91788, "main");

        // Assert: Returns "No changes." rather than throwing an exception
        Assert.Equal(GitDiffConstants.NoChanges, stat);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_WhenBaseBranchAdvanced_UsesMergeBaseAndExcludesUpstreamChanges()
    {
        // Arrange: Create task branch and worktree from main
        var worktreePath = await _manager.EnsureWorktreeAsync(_workspace, 91789, "main");

        // Author a change in the task worktree
        var taskFilePath = Path.Combine(worktreePath, "task_feature.txt");
        await File.WriteAllTextAsync(taskFilePath, "Task feature implementation\n");

        // Advance base branch (main) in canonical repo with an upstream commit
        await _runner.ExecuteAsync(_repo.RepositoryPath, ["checkout", "main"]);
        _repo.CreateFile("upstream_advancement.txt", "Upstream feature committed after task branched\n");
        _repo.CommitAll("Upstream commit advancing main");

        // Act: Generate live diff from task worktree against main
        var liveDiff = await _manager.GenerateLiveDiffAsync(_workspace, 91789, "main");

        // Assert: Live diff contains task's additions, but does NOT invert upstream commits
        Assert.Contains("task_feature.txt", liveDiff);
        Assert.Contains("+Task feature implementation", liveDiff);
        Assert.DoesNotContain("upstream_advancement.txt", liveDiff);
        Assert.DoesNotContain("-Upstream feature committed after task branched", liveDiff);
    }

    [Fact]
    public async Task GenerateLiveDiffAsync_InWslWorktree_InvokesProcessRunnerToCleanTempIndex()
    {
        var mockRunner = new Mock<IGitCliRunner>();
        mockRunner.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.Is<IEnumerable<string>>(args => args.Contains("merge-base")),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, "abcdef1234567890\n", "", TimeSpan.FromMilliseconds(5)));

        mockRunner.Setup(r => r.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, "diff --git a/file.cs b/file.cs", "", TimeSpan.FromMilliseconds(10)));

        var mockProcessRunner = new Mock<IProcessRunner>();
        ProcessStartSpec? capturedSpec = null;
        mockProcessRunner.Setup(p => p.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, ct) => capturedSpec = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, "", ""));

        var wslMetaMock = new Mock<IWslMetadataService>();
        wslMetaMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetaMock.Setup(w => w.GetDefaultUserName()).Returns("developer");

        var pathResolver = new WorkspacePathResolver(wslMetaMock.Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var manager = new GitWorkspaceManager(
            mockRunner.Object,
            pathResolver,
            wslMetaMock.Object,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            mockProcessRunner.Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var wslWorkspace = new Workspace
        {
            Id = 999,
            Name = "WSL Container Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            Ext4MirrorPath = "/home/developer/.nightcodeforge/wks/999/clone",
            RepositoryPath = "C:\\host\\repo",
            DefaultBaseBranch = "main"
        };

        var diff = await manager.GenerateLiveDiffAsync(wslWorkspace, 42, "main");

        Assert.NotNull(diff);
        if (OperatingSystem.IsWindows())
        {
            // On Windows with WSL path, process runner is called to clean up isolated index in WSL
            Assert.NotNull(capturedSpec);
            Assert.Equal("wsl.exe", capturedSpec.FileName);
            Assert.NotNull(capturedSpec.ArgumentList);
            Assert.Contains("Ubuntu", capturedSpec.ArgumentList);
            Assert.Contains("rm", capturedSpec.ArgumentList);
            Assert.Contains("-f", capturedSpec.ArgumentList);
        }
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}

