using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class WorkspacePathResolverTests
{
    private static WorkspacePathResolver CreateResolver(
        IWslMetadataService? wslMetadataService = null,
        IOptions<ExecutionEnvironmentProviderOptions>? options = null,
        bool? isWindowsOverride = null,
        string? distributionOverride = null,
        string? userOverride = null)
    {
        return new WorkspacePathResolver(
            wslMetadataService ?? new FakeWslMetadata(),
            options ?? Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride,
            distributionOverride,
            userOverride);
    }
    [Fact]
    public void GetHostRemoteUrl_TranslatesWindowsDriveToWslMount()
    {
        var resolver = CreateResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var remoteUrl = resolver.GetHostRemoteUrl(@"C:\Users\Julien\source\repos\NightCodeForge");
        Assert.Equal("/mnt/c/Users/Julien/source/repos/NightCodeForge", remoteUrl);

        var forwardSlashUrl = resolver.GetHostRemoteUrl("D:/repos/project");
        Assert.Equal("/mnt/d/repos/project", forwardSlashUrl);
    }

    [Fact]
    public void GetHostRemoteUrl_PreservesRawUnixPath_WhenOnUnix()
    {
        var resolver = CreateResolver(
            isWindowsOverride: false,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var remoteUrl = resolver.GetHostRemoteUrl("/home/julien/repos/project");
        Assert.Equal("/home/julien/repos/project", remoteUrl);
    }

    [Fact]
    public void ResolveCruciblePath_StripsVirtualContainerWorkspacePrefix_Windows()
    {
        var resolver = CreateResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu-24.04",
            userOverride: "devuser");

        // 1. Absolute virtual container path resolves to clone
        var path1 = resolver.ResolveCruciblePath(10, "/workspace/src/Services/AuthService.cs");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\clone\src\Services\AuthService.cs", path1);

        // 2. Relative virtual container path resolves to clone
        var path2 = resolver.ResolveCruciblePath(10, "workspace/docs/README.md");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\clone\docs\README.md", path2);

        // 3. Just /workspace resolves to clone root
        var path3 = resolver.ResolveCruciblePath(10, "/workspace");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\clone", path3);

        // 4. Pure relative path without /workspace resolves to clone
        var path4 = resolver.ResolveCruciblePath(10, "src/Program.cs");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\clone\src\Program.cs", path4);

        // 5. With explicit taskId parameter resolves to task worktree
        var path5 = resolver.ResolveCruciblePath(10, "/workspace/src/Program.cs", taskId: 42);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\task-42\src\Program.cs", path5);

        // 6. Path embedded task-{id} resolves to task worktree even without taskId parameter
        var path6 = resolver.ResolveCruciblePath(10, "/workspace/task-42/src/Program.cs");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\task-42\src\Program.cs", path6);
    }

    [Fact]
    public void ResolveCruciblePath_StripsVirtualContainerWorkspacePrefix_Unix()
    {
        var resolver = CreateResolver(
            isWindowsOverride: false,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var path = resolver.ResolveCruciblePath(5, "/workspace/src/Core/Engine.cs");
        Assert.EndsWith(".nightcodeforge/wks/5/clone/src/Core/Engine.cs", path);
    }

    [Fact]
    public void GetAgentWorkingClonePath_ReturnsExpectedCanonicalUnixPath()
    {
        var resolver = CreateResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu",
            userOverride: "agentuser");

        var clonePath = resolver.GetAgentWorkingClonePath(42);
        Assert.Equal("/home/agentuser/.nightcodeforge/wks/42/clone", clonePath);
    }

    [Fact]
    public void ResolveCruciblePath_WithWorkspaceRoot_RemapsWorkspacePrefix()
    {
        var resolver = CreateResolver();
        var tempRoot = Path.Combine(Path.GetTempPath(), "resolver-test-" + Guid.NewGuid().ToString("N"));
        var cloneDir = Path.Combine(tempRoot, "clone");
        var taskDir = Path.Combine(tempRoot, "task-7");
        Directory.CreateDirectory(cloneDir);
        Directory.CreateDirectory(taskDir);

        // /workspace/clone/... maps to cloneDir
        var p1 = resolver.ResolveCruciblePath(cloneDir, "/workspace/clone/src/App.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "src", "App.cs")), p1);

        // /workspace/task-7/... maps to taskDir
        var p2 = resolver.ResolveCruciblePath(cloneDir, "/workspace/task-7/src/App.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(taskDir, "src", "App.cs")), p2);

        // /workspace/... resolves to workspaceRoot (cloneDir)
        var p3 = resolver.ResolveCruciblePath(cloneDir, "/workspace/src/App.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "src", "App.cs")), p3);

        // Relative path without /workspace prefix resolves to workspaceRoot
        var p4 = resolver.ResolveCruciblePath(cloneDir, "src/App.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "src", "App.cs")), p4);

        // Relative path with "clone/" without /workspace prefix is NOT stripped
        var p5 = resolver.ResolveCruciblePath(cloneDir, "clone/App.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "clone", "App.cs")), p5);
    }

    [Fact]
    public void ResolveCruciblePath_DirectRepositoryRootWithoutCloneSubdir_ResolvesCloneAndWorkspacePrefixesDirectly()
    {
        var resolver = CreateResolver();
        var tempRepoRoot = Path.Combine(Path.GetTempPath(), "repo-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepoRoot);

        try
        {
            // /clone/src/App.cs falls back to tempRepoRoot/src/App.cs because tempRepoRoot/clone does not exist
            var p1 = resolver.ResolveCruciblePath(tempRepoRoot, "/clone/src/App.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepoRoot, "src", "App.cs")), p1);

            // /workspace/clone/src/App.cs falls back to tempRepoRoot/src/App.cs
            var p2 = resolver.ResolveCruciblePath(tempRepoRoot, "/workspace/clone/src/App.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepoRoot, "src", "App.cs")), p2);

            // /workspace/src/App.cs resolves directly to tempRepoRoot/src/App.cs
            var p3 = resolver.ResolveCruciblePath(tempRepoRoot, "/workspace/src/App.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepoRoot, "src", "App.cs")), p3);

            // Pure relative path resolves to tempRepoRoot/src/App.cs
            var p4 = resolver.ResolveCruciblePath(tempRepoRoot, "src/App.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepoRoot, "src", "App.cs")), p4);
        }
        finally
        {
            if (Directory.Exists(tempRepoRoot))
            {
                Directory.Delete(tempRepoRoot, true);
            }
        }
    }

    private class FakeWslMetadata : NightCodeForge.Core.Interfaces.IWslMetadataService
    {
        public string Distro { get; set; } = "openSUSE";
        public string User { get; set; } = "linuxdev";

        public string GetDefaultDistributionName() => Distro;
        public string GetDefaultUserName() => User;
        public void ClearCache() { }
    }

    [Fact]
    public void GetWslDistributionName_And_UserName_DelegatesToMetadataService()
    {
        var meta = new FakeWslMetadata { Distro = "AlmaLinux", User = "almadev" };
        var resolver = CreateResolver(wslMetadataService: meta, isWindowsOverride: true);

        Assert.Equal("AlmaLinux", resolver.GetWslDistributionName());
        Assert.Equal("almadev", resolver.GetWslUserName());
    }

    [Fact]
    public void ExplicitOverrides_TakePrecedenceOverMetadataService()
    {
        var meta = new FakeWslMetadata { Distro = "AlmaLinux", User = "almadev" };
        var resolver = CreateResolver(
            wslMetadataService: meta,
            isWindowsOverride: true,
            distributionOverride: "Debian-Override",
            userOverride: "root-override");

        Assert.Equal("Debian-Override", resolver.GetWslDistributionName());
        Assert.Equal("root-override", resolver.GetWslUserName());
    }

    [Fact]
    public void ResolveCruciblePath_ResolvesTmpPaths_OnWindowsAndUnixAndRejectsTraversal()
    {
        var winResolver = CreateResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu-24.04",
            userOverride: "devuser");

        var winTmpFile = winResolver.ResolveCruciblePath(10, "/tmp/hp_patch.py", taskId: 35);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\tmp\hp_patch.py", winTmpFile);

        var unixResolver = CreateResolver(
            isWindowsOverride: false,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var unixTmpFile = unixResolver.ResolveCruciblePath("/home/julien/.nightcodeforge/wks/10/task-35", "/tmp/hp_patch.py", taskId: 35);
        Assert.Equal("/home/julien/.nightcodeforge/wks/10/tmp/hp_patch.py", unixTmpFile);

        // Full WSL path targeting .nightcodeforge/wks/{id}/tmp/... resolves to wks/{id}/tmp/... even with taskId
        var winEmbeddedWksTmp = winResolver.ResolveCruciblePath(10, "/home/devuser/.nightcodeforge/wks/10/tmp/hp_patch.py", taskId: 35);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\tmp\hp_patch.py", winEmbeddedWksTmp);

        var unixEmbeddedWksTmp = unixResolver.ResolveCruciblePath(10, "/home/julien/.nightcodeforge/wks/10/tmp/hp_patch.py", taskId: 35);
        Assert.EndsWith(".nightcodeforge/wks/10/tmp/hp_patch.py", unixEmbeddedWksTmp);

        // When workspaceRoot itself is located under /tmp on Unix, paths already under parentDir are not double-wrapped
        var unixTmpRootedFile = unixResolver.ResolveCruciblePath("/tmp/ncf-test-wks/clone", "/tmp/ncf-test-wks/clone/src/App.cs");
        Assert.Equal("/tmp/ncf-test-wks/clone/src/App.cs", unixTmpRootedFile);

        var unixTmpRootedVirtualTmp = unixResolver.ResolveCruciblePath("/tmp/ncf-test-wks/clone", "/tmp/hp_patch.py");
        Assert.Equal("/tmp/ncf-test-wks/tmp/hp_patch.py", unixTmpRootedVirtualTmp);

        Assert.True(WorkspacePathResolver.TryResolveTmpSubPath("/tmp/hp_patch.py", out var sub1));
        Assert.Equal("hp_patch.py", sub1);

        Assert.True(WorkspacePathResolver.TryResolveTmpSubPath("/tmp/a/../hp_patch.py", out var sub2));
        Assert.Equal("hp_patch.py", sub2);

        Assert.False(WorkspacePathResolver.TryResolveTmpSubPath("/tmp/../etc/passwd", out _));
        Assert.False(WorkspacePathResolver.TryResolveTmpSubPath("/tmp/a/../../etc/passwd", out _));
    }
}
