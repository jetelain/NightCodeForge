using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitRepositoryBrowserServiceTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly GitCliRunner _gitCliRunner;
    private readonly WorkspacePathResolver _pathResolver;
    private readonly Mock<IWslMetadataService> _wslMetadataMock;
    private readonly GitRepositoryBrowserService _service;

    public GitRepositoryBrowserServiceTests()
    {
        _repo = new TestGitRepository("main");
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _taskRepo = new InMemoryTaskRepository();
        _wslMetadataMock = new Mock<IWslMetadataService>();
        _wslMetadataMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        _wslMetadataMock.Setup(w => w.GetDefaultUserName()).Returns("tester");

        _pathResolver = new WorkspacePathResolver(
            _wslMetadataMock.Object,
            Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false);

        _gitCliRunner = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            _pathResolver,
            _wslMetadataMock.Object,
            NullLogger<GitCliRunner>.Instance);

        _service = new GitRepositoryBrowserService(
            _workspaceRepo,
            _taskRepo,
            _gitCliRunner,
            _pathResolver,
            _wslMetadataMock.Object,
            NullLogger<GitRepositoryBrowserService>.Instance);
    }

    [Fact]
    public async Task ListBranchesAsync_SortsDefaultThenNumericTaskBranchesThenRemainingAlphabetically()
    {
        _repo.CreateFile("README.md", "# Repo\n");
        _repo.CommitAll("init");
        _repo.RunGit("branch feature/task-10");
        _repo.RunGit("branch feature/task-2");
        _repo.RunGit("branch feature/task-2-hotfix");
        _repo.RunGit("branch zeta");
        _repo.RunGit("branch alpha");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Branch Sort",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var branches = await _service.ListBranchesAsync(ws.Id);

        Assert.Equal(
            ["main", "feature/task-2", "feature/task-2-hotfix", "feature/task-10", "alpha", "zeta"],
            branches);
    }

    [Fact]
    public async Task InspectPathAsync_ReadsDirectoryTextFileBinaryAndOversizedFiles()
    {
        _repo.CreateFile("src/Program.cs", "Console.WriteLine(\"Hello\");\n");
        _repo.CreateFile("docs/my notes.md", "# Title\n\nBody\n");
        File.WriteAllBytes(Path.Combine(_repo.RepositoryPath, "bin.dat"), [0x7F, (byte)'E', (byte)'L', (byte)'F', 0x00, 0x01, 0x02]);

        var oversizedText = new string('x', GitRepositoryBrowserConstants.MaxPreviewFileBytes + 128);
        _repo.CreateFile("big.txt", oversizedText);
        _repo.CommitAll("add sample files");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Inspect Repo",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        // Root directory: directories first, then files alphabetically
        var root = await _service.InspectPathAsync(ws.Id, "main", null);
        Assert.Equal(GitFileContentKind.Directory, root.ContentKind);
        Assert.NotNull(root.DirectoryEntries);
        Assert.Equal(GitTreeEntryKind.Directory, root.DirectoryEntries![0].Kind);
        Assert.Equal("docs", root.DirectoryEntries[0].Name);
        Assert.Equal("src", root.DirectoryEntries[1].Name);
        Assert.Contains(root.DirectoryEntries, e => e.Name == "big.txt" && e.Kind == GitTreeEntryKind.File);

        // Text file
        var code = await _service.InspectPathAsync(ws.Id, "main", "src/Program.cs");
        Assert.Equal(GitFileContentKind.Text, code.ContentKind);
        Assert.Equal(1, code.LineCount);
        Assert.Contains("Console.WriteLine", code.TextContent);

        // Filename with spaces
        var spaced = await _service.InspectPathAsync(ws.Id, "main", "docs/my notes.md");
        Assert.Equal(GitFileContentKind.Text, spaced.ContentKind);
        Assert.Equal("docs/my notes.md", spaced.Path);

        // Binary file
        var binary = await _service.InspectPathAsync(ws.Id, "main", "bin.dat");
        Assert.Equal(GitFileContentKind.Binary, binary.ContentKind);
        Assert.Null(binary.TextContent);
        Assert.Equal(BinaryDetectionReason.NulByte, binary.BinaryReason);

        // Oversized file
        var oversized = await _service.InspectPathAsync(ws.Id, "main", "big.txt");
        Assert.Equal(GitFileContentKind.Oversized, oversized.ContentKind);
        Assert.Null(oversized.TextContent);
        Assert.Equal(GitRepositoryBrowserConstants.MaxPreviewFileBytes + 128, oversized.SizeBytes);
        Assert.Equal(GitRepositoryBrowserConstants.MaxPreviewFileBytes, oversized.MaxPreviewBytes);
    }

    [Fact]
    public async Task InspectPathAsync_HandlesSymlinkAndSubmoduleEntriesSafelyWithoutGitShowOnSubmodule()
    {
        // Commit a submodule-mode gitlink (160000) and a symlink-mode blob (120000) directly in the Git index
        // so the test is deterministic even on Windows filesystems without symlink privileges.
        var tempTargetFile = Path.Combine(_repo.RepositoryPath, ".tmp-target");
        await File.WriteAllTextAsync(tempTargetFile, "../outside/secret.txt");
        var hashRes = await _gitCliRunner.ExecuteAsync(_repo.RepositoryPath, ["hash-object", "-w", tempTargetFile]);
        File.Delete(tempTargetFile);
        var targetBlobSha = hashRes.StandardOutput.Trim();

        _repo.CreateFile("README.md", "root\n");
        _repo.CommitAll("base");
        var headRes = await _gitCliRunner.ExecuteAsync(_repo.RepositoryPath, ["rev-parse", "HEAD"]);
        var headCommitSha = headRes.StandardOutput.Trim();

        await _gitCliRunner.ExecuteAsync(_repo.RepositoryPath, ["update-index", "--add", "--cacheinfo", $"120000,{targetBlobSha},link.txt"]);
        await _gitCliRunner.ExecuteAsync(_repo.RepositoryPath, ["update-index", "--add", "--cacheinfo", $"160000,{headCommitSha},deps/submod"]);
        _repo.RunGit("commit -m \"add symlink and submodule\" --no-verify");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Special Modes",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var symlink = await _service.InspectPathAsync(ws.Id, "main", "link.txt");
        Assert.Equal(GitFileContentKind.Symlink, symlink.ContentKind);
        Assert.Equal("../outside/secret.txt", symlink.SymlinkTarget);

        var submodule = await _service.InspectPathAsync(ws.Id, "main", "deps/submod");
        Assert.Equal(GitFileContentKind.Submodule, submodule.ContentKind);
        Assert.Equal(headCommitSha, submodule.SubmoduleCommitSha);
    }

    [Fact]
    public async Task ResolveRefAsync_DeletedTaskBranch_AppliesFallbackPrecedenceAndWorkspaceIsolation()
    {
        _repo.CreateFile("app.txt", "v1\n");
        _repo.CommitAll("initial");
        _repo.RunGit("checkout -b develop");
        _repo.CreateFile("app.txt", "v2-develop\n");
        _repo.CommitAll("develop commit");
        var developShaRes = await _gitCliRunner.ExecuteAsync(_repo.RepositoryPath, ["rev-parse", "HEAD"]);
        var squashSha = developShaRes.StandardOutput.Trim();
        _repo.RunGit("checkout main");

        var ws1 = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Workspace 1",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });
        var ws2 = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Workspace 2",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var taskWithSquash = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 42,
            WorkspaceId = ws1.Id,
            Title = "Squashed Task",
            BaseBranch = "main",
            FeatureBranch = "feature/task-42",
            SquashCommitSha = squashSha
        });

        var taskBaseFallback = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 43,
            WorkspaceId = ws1.Id,
            Title = "Base Branch Fallback Task",
            BaseBranch = "develop",
            FeatureBranch = "feature/task-43",
            SquashCommitSha = null
        });

        var otherWorkspaceTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 99,
            WorkspaceId = ws2.Id,
            Title = "Other Workspace Task",
            BaseBranch = "main",
            FeatureBranch = "feature/task-99",
            SquashCommitSha = squashSha
        });

        // 1. SquashCommitSha fallback
        var resolvedSquash = await _service.ResolveRefAsync(ws1.Id, "feature/task-42");
        Assert.Equal(squashSha, resolvedSquash.CommitSha);
        Assert.NotNull(resolvedSquash.Fallback);
        Assert.Equal(GitRefFallbackReason.SquashCommitSha, resolvedSquash.Fallback!.Reason);
        Assert.Equal(taskWithSquash.Id, resolvedSquash.Fallback.TaskId);

        // 2. TaskBaseBranch fallback
        var resolvedBase = await _service.ResolveRefAsync(ws1.Id, "feature/task-43");
        Assert.Equal("develop", resolvedBase.ResolvedRef);
        Assert.NotNull(resolvedBase.Fallback);
        Assert.Equal(GitRefFallbackReason.TaskBaseBranch, resolvedBase.Fallback!.Reason);
        Assert.Equal(taskBaseFallback.Id, resolvedBase.Fallback.TaskId);

        // 3. Cross-workspace task isolation: ws1 must NOT resolve ws2's task-99
        await Assert.ThrowsAsync<GitBrowserRefNotFoundException>(() =>
            _service.ResolveRefAsync(ws1.Id, "feature/task-99"));
        await Assert.ThrowsAsync<GitBrowserRefNotFoundException>(() =>
            _service.ResolveRefAsync(ws1.Id, "nonexistent-branch", taskIdHint: otherWorkspaceTask.Id));
    }

    [Fact]
    public async Task RepositoryPriority_PrefersExt4MirrorThenCanonicalCloneThenRepositoryPath()
    {
        var mockGit = new Mock<IGitCliRunner>();
        mockGit.Setup(g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, "main\n", string.Empty, TimeSpan.Zero));

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 7,
            Name = "Priority Test",
            Ext4MirrorPath = "/mirror/wks/7",
            RepositoryPath = "/host/repo",
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        var browser = new GitRepositoryBrowserService(
            _workspaceRepo,
            _taskRepo,
            mockGit.Object,
            _pathResolver,
            _wslMetadataMock.Object,
            NullLogger<GitRepositoryBrowserService>.Instance)
        {
            DirectoryExistsCheck = path => path is "/mirror/wks/7" or "/host/repo" || path.EndsWith("/.nightcodeforge/wks/7/clone")
        };

        await browser.ListBranchesAsync(ws.Id);
        mockGit.Verify(g => g.ExecuteAsync("/mirror/wks/7", It.IsAny<IEnumerable<string>>(), null, It.IsAny<CancellationToken>()), Times.Once);

        // Without mirror, non-BareHost uses canonical working clone
        ws.Ext4MirrorPath = null;
        await browser.ListBranchesAsync(ws.Id);
        mockGit.Verify(g => g.ExecuteAsync(It.Is<string>(p => p.EndsWith("/.nightcodeforge/wks/7/clone")), It.IsAny<IEnumerable<string>>(), null, It.IsAny<CancellationToken>()), Times.Once);

        // BareHost skips clone and falls back to RepositoryPath
        ws.ExecutionMode = ExecutionMode.BareHost;
        await browser.ListBranchesAsync(ws.Id);
        mockGit.Verify(g => g.ExecuteAsync("/host/repo", It.IsAny<IEnumerable<string>>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InspectPathAsync_ThrowsDomainExceptionsForMissingWorkspaceRepoPathAndInvalidInput()
    {
        await Assert.ThrowsAsync<GitBrowserWorkspaceNotFoundException>(() =>
            _service.InspectPathAsync(9999, "main", "README.md"));

        var missingRepoWs = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Missing Repo",
            RepositoryPath = Path.Combine(Path.GetTempPath(), "missing_" + Guid.NewGuid().ToString("N")),
            ExecutionMode = ExecutionMode.BareHost
        });
        await Assert.ThrowsAsync<GitBrowserRepositoryNotFoundException>(() =>
            _service.InspectPathAsync(missingRepoWs.Id, "main", "README.md"));

        _repo.CreateFile("README.md", "hello\n");
        _repo.CommitAll("init");
        var validWs = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Valid Repo",
            RepositoryPath = _repo.RepositoryPath,
            ExecutionMode = ExecutionMode.BareHost
        });

        await Assert.ThrowsAsync<GitBrowserPathNotFoundException>(() =>
            _service.InspectPathAsync(validWs.Id, "main", "does/not/exist.cs"));
        await Assert.ThrowsAsync<GitBrowserInvalidInputException>(() =>
            _service.InspectPathAsync(validWs.Id, "-evil", "README.md"));
        await Assert.ThrowsAsync<GitBrowserInvalidInputException>(() =>
            _service.InspectPathAsync(validWs.Id, "main", "../secret.txt"));
    }

    [Fact]
    public async Task InspectPathAsync_PathspecMagicDoesNotCrashOrMatch_AndLiteralBracketsWork()
    {
        _repo.CreateFile("README.md", "root\n");
        _repo.CreateFile("pages/[id]/index.txt", "bracket route\n");
        _repo.CommitAll("add literal bracket path");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Literal Pathspec Repo",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        await Assert.ThrowsAsync<GitBrowserPathNotFoundException>(() =>
            _service.InspectPathAsync(ws.Id, "main", ":(top)README.md"));
        await Assert.ThrowsAsync<GitBrowserPathNotFoundException>(() =>
            _service.InspectPathAsync(ws.Id, "main", ":(icase)readme.md"));
        await Assert.ThrowsAsync<GitBrowserPathNotFoundException>(() =>
            _service.InspectPathAsync(ws.Id, "main", ":(glob)*.md"));

        var bracketDir = await _service.ListDirectoryAsync(ws.Id, "main", "pages/[id]");
        Assert.Single(bracketDir.Entries);
        Assert.Equal("pages/[id]/index.txt", bracketDir.Entries[0].Path);

        var bracketFile = await _service.InspectPathAsync(ws.Id, "main", "pages/[id]/index.txt");
        Assert.Equal(GitFileContentKind.Text, bracketFile.ContentKind);
        Assert.Equal("bracket route\n", bracketFile.TextContent);
    }

    [Fact]
    public async Task ListAncestorDirectoriesAsync_MissingNestedPath_ReturnsOnlyExistingDirectories()
    {
        _repo.CreateFile("src/Program.cs", "Console.WriteLine();\n");
        _repo.CommitAll("init");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Ancestor Repo",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var ancestors = await _service.ListAncestorDirectoriesAsync(ws.Id, "main", "missing/sub/file.cs");
        Assert.Single(ancestors);
        Assert.True(ancestors.ContainsKey(string.Empty));
        Assert.False(ancestors.ContainsKey("missing"));
        Assert.False(ancestors.ContainsKey("missing/sub"));
    }

    [Fact]
    public async Task InspectPathAsync_StandaloneCrLineEndings_CountsLinesAccurately()
    {
        _repo.CreateFile("legacy.txt", "line1\rline2\rline3\r");
        _repo.CommitAll("cr file");

        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "CR Line Repo",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var result = await _service.InspectPathAsync(ws.Id, "main", "legacy.txt");
        Assert.Equal(GitFileContentKind.Text, result.ContentKind);
        Assert.Equal(3, result.LineCount);
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}
