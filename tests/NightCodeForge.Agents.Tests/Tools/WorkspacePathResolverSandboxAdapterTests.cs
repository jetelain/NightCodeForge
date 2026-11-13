using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class WorkspacePathResolverSandboxAdapterTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspacePathResolverSandboxAdapterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-adapter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Constructor_ValidatesArguments_AndConfiguresScopeAndMountTable()
    {
        var pathResolver = TestPathResolver.Create();

        Assert.Throws<ArgumentNullException>(() =>
            new WorkspacePathResolverSandboxAdapter(null!, pathResolver, NullLogger.Instance));
        Assert.Throws<ArgumentException>(() =>
            new WorkspacePathResolverSandboxAdapter(new CanonicalToolScope("   "), pathResolver, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new WorkspacePathResolverSandboxAdapter(new CanonicalToolScope(_tempDir), null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() =>
            new WorkspacePathResolverSandboxAdapter(new CanonicalToolScope(_tempDir), pathResolver, null!));

        // TaskId <= 0 is normalized to null (WorkspaceClone scope), Standard tier when IsReadOnly is false
        var cloneAdapter = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope(_tempDir, WorkspaceId: 4, TaskId: 0, IsReadOnly: false),
            pathResolver,
            NullLogger.Instance);

        Assert.Equal(SandboxScopeKind.WorkspaceClone, cloneAdapter.Scope.ScopeKind);
        Assert.Equal(SandboxSecurityTier.Standard, cloneAdapter.Scope.SecurityTier);
        Assert.Null(cloneAdapter.Scope.TaskId);
        Assert.Equal(4, cloneAdapter.Scope.WorkspaceId);
        Assert.Same(cloneAdapter.Scope.MountTable, cloneAdapter.MountTable);
        Assert.NotNull(cloneAdapter.MountTable.SandboxTmpMount);

        // Positive TaskId and IsReadOnly: true produces TaskWorktree + RestrictedReadOnly
        var taskAdapter = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope("/home/user/wks/4/task-9", WorkspaceId: 4, TaskId: 9, IsReadOnly: true),
            pathResolver,
            NullLogger.Instance);

        Assert.Equal(SandboxScopeKind.TaskWorktree, taskAdapter.Scope.ScopeKind);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, taskAdapter.Scope.SecurityTier);
        Assert.Equal(9, taskAdapter.Scope.TaskId);
        Assert.Equal("/home/user/wks/4/task-9", taskAdapter.Scope.RepositoryPath);
    }

    [Fact]
    public void ResolveHostPath_EnforcesWorkspaceBoundariesTmpSandboxAndReadOnlyGuards()
    {
        var pathResolver = TestPathResolver.Create();
        var writableScope = new CanonicalToolScope(_tempDir, WorkspaceId: 1, IsReadOnly: false);
        var writableAdapter = new WorkspacePathResolverSandboxAdapter(writableScope, pathResolver, NullLogger.Instance);

        // 1. Valid workspace-relative and /tmp paths
        var resolvedSrc = writableAdapter.ResolveHostPath("src/Program.cs", forWrite: true);
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "src", "Program.cs")), resolvedSrc);

        var resolvedTmp = writableAdapter.ResolveHostPath("/tmp/report.md", forWrite: true);
        Assert.Contains("report.md", resolvedTmp);

        // 2. Read-only virtual mounts (/workspace/skills and /nightcodeforge) reject writes even in writable scope
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("/workspace/skills/my-skill/SKILL.md", forWrite: true));
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("/workspace/skills", forWrite: true));
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("/nightcodeforge/ncf-logs/app.log", forWrite: true));
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("/nightcodeforge", forWrite: true));

        // 3. Path traversal escaping /tmp or workspace boundary is blocked
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("/tmp/../../outside.txt", forWrite: false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            writableAdapter.ResolveHostPath("../../outside.txt", forWrite: false));

        // 4. Read-only scope rejects any write operation
        var readOnlyScope = new CanonicalToolScope(_tempDir, WorkspaceId: 1, IsReadOnly: true);
        var readOnlyAdapter = new WorkspacePathResolverSandboxAdapter(readOnlyScope, pathResolver, NullLogger.Instance);

        Assert.Equal(resolvedSrc, readOnlyAdapter.ResolveHostPath("src/Program.cs", forWrite: false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            readOnlyAdapter.ResolveHostPath("src/Program.cs", forWrite: true));
    }

    [Fact]
    public void ResolveBashWorkingDirectory_And_TranslateBashCommand_HandleAllExecutionTiers()
    {
        var subDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(subDir);

        var adapter = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope(_tempDir, WorkspaceId: 1),
            TestPathResolver.Create(),
            NullLogger.Instance);

        // Default working directory when null/empty
        Assert.Equal(_tempDir, adapter.ResolveBashWorkingDirectory(null, ExecutionTier.BareHost));
        Assert.Equal(string.Empty, adapter.ResolveBashWorkingDirectory(null, ExecutionTier.WslDirect));
        Assert.Equal(string.Empty, adapter.ResolveBashWorkingDirectory(null, ExecutionTier.ContainerSandbox));

        // Explicit relative or virtual working directory
        Assert.Equal(subDir, adapter.ResolveBashWorkingDirectory("src", ExecutionTier.BareHost));
        Assert.Equal("src", adapter.ResolveBashWorkingDirectory("src", ExecutionTier.WslDirect));
        Assert.Equal("/workspace/src", adapter.ResolveBashWorkingDirectory("/workspace/src", ExecutionTier.ContainerSandbox));
        Assert.Equal("./src", adapter.ResolveBashWorkingDirectory("./src", ExecutionTier.WslDirect));

        // Rooted host path in non-BareHost tier validates within workspace
        Assert.Equal(subDir, adapter.ResolveBashWorkingDirectory(subDir, ExecutionTier.WslDirect));

        // TranslateBashCommand is a pass-through on the legacy adapter
        const string cmd = "dotnet test --no-build";
        Assert.Equal(cmd, adapter.TranslateBashCommand(cmd, ExecutionTier.BareHost));
        Assert.Equal(cmd, adapter.TranslateBashCommand(cmd, ExecutionTier.WslDirect));
    }

    [Fact]
    public void ResolveHostPath_AllowsWorkspaceClonePaths_WhenWorkspaceIdAndTaskIdWorktreeExistOnDisk()
    {
        var wksBase = Path.Combine(_tempDir, "wks", "7");
        var taskWorktreeDir = Path.Combine(wksBase, "task-12");
        var cloneDir = Path.Combine(wksBase, "clone");
        Directory.CreateDirectory(taskWorktreeDir);
        Directory.CreateDirectory(cloneDir);
        var cloneReadme = Path.Combine(cloneDir, "README.md");
        File.WriteAllText(cloneReadme, "# Clone readme");

        var mockPathResolver = new Mock<IWorkspacePathResolver>();
        mockPathResolver.Setup(r => r.GetWslDistributionName()).Returns(string.Empty);
        mockPathResolver
            .Setup(r => r.ResolveCruciblePath(7, string.Empty, 12))
            .Returns(taskWorktreeDir);
        mockPathResolver
            .Setup(r => r.ResolveCruciblePath(7, "/tmp", 12))
            .Returns(Path.Combine(wksBase, "tmp"));
        mockPathResolver
            .Setup(r => r.ResolveCruciblePath(7, "/workspace/clone", 12))
            .Returns(cloneDir);
        mockPathResolver
            .Setup(r => r.ResolveCruciblePath(7, "/workspace/clone/README.md", 12))
            .Returns(cloneReadme);

        // WorkspaceRoot is virtual ("/workspace"), so sandbox validation relies on WorkspaceId=7 allowed roots
        var adapter = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope("/workspace", WorkspaceId: 7, TaskId: 12),
            mockPathResolver.Object,
            NullLogger.Instance);

        var resolved = adapter.ResolveHostPath("/workspace/clone/README.md", forWrite: false);
        Assert.Equal(cloneReadme, resolved);
    }
}
