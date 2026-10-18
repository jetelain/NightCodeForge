using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
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
        _runner = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            pathResolver,
            wslMeta,
            NullLogger<GitCliRunner>.Instance);
        _manager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMeta,
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

        // Uncommitted changes in working tree
        _repo.CreateFile("src/LiveModule.cs", "public class LiveModule {}\n", wtPath);
        _repo.CreateFile("package-lock.json", "{}", wtPath);
        _repo.RunGit("add -N .", wtPath);

        var liveStat = await _manager.GenerateLiveDiffStatAsync(_workspace, 91703, "main");

        Assert.Contains("src/LiveModule.cs", liveStat);
        Assert.DoesNotContain("package-lock.json", liveStat);
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

    public void Dispose()
    {
        _repo.Dispose();
    }
}
