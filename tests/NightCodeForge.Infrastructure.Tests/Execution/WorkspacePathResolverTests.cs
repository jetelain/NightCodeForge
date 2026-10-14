using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class WorkspacePathResolverTests
{
    [Fact]
    public void GetHostRemoteUrl_TranslatesWindowsDriveToWslMount()
    {
        var resolver = new WorkspacePathResolver(
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
        var resolver = new WorkspacePathResolver(
            isWindowsOverride: false,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var remoteUrl = resolver.GetHostRemoteUrl("/home/julien/repos/project");
        Assert.Equal("/home/julien/repos/project", remoteUrl);
    }

    [Fact]
    public void ResolveCruciblePath_StripsVirtualContainerWorkspacePrefix_Windows()
    {
        var resolver = new WorkspacePathResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu-24.04",
            userOverride: "devuser");

        // 1. Absolute virtual container path
        var path1 = resolver.ResolveCruciblePath(10, "/workspace/src/Services/AuthService.cs");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\src\Services\AuthService.cs", path1);

        // 2. Relative virtual container path
        var path2 = resolver.ResolveCruciblePath(10, "workspace/docs/README.md");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\docs\README.md", path2);

        // 3. Just /workspace
        var path3 = resolver.ResolveCruciblePath(10, "/workspace");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10", path3);

        // 4. Pure relative path without /workspace
        var path4 = resolver.ResolveCruciblePath(10, "src/Program.cs");
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\devuser\.nightcodeforge\wks\10\src\Program.cs", path4);
    }

    [Fact]
    public void ResolveCruciblePath_StripsVirtualContainerWorkspacePrefix_Unix()
    {
        var resolver = new WorkspacePathResolver(
            isWindowsOverride: false,
            distributionOverride: "Ubuntu",
            userOverride: "julien");

        var path = resolver.ResolveCruciblePath(5, "/workspace/src/Core/Engine.cs");
        Assert.EndsWith(".nightcodeforge/wks/5/src/Core/Engine.cs", path);
    }

    [Fact]
    public void GetAgentWorkingClonePath_ReturnsExpectedCanonicalUnixPath()
    {
        var resolver = new WorkspacePathResolver(
            isWindowsOverride: true,
            distributionOverride: "Ubuntu",
            userOverride: "agentuser");

        var clonePath = resolver.GetAgentWorkingClonePath(42);
        Assert.Equal("/home/agentuser/.nightcodeforge/wks/42", clonePath);
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
        var resolver = new WorkspacePathResolver(wslMetadataService: meta, isWindowsOverride: true);

        Assert.Equal("AlmaLinux", resolver.GetWslDistributionName());
        Assert.Equal("almadev", resolver.GetWslUserName());
    }

    [Fact]
    public void ExplicitOverrides_TakePrecedenceOverMetadataService()
    {
        var meta = new FakeWslMetadata { Distro = "AlmaLinux", User = "almadev" };
        var resolver = new WorkspacePathResolver(
            wslMetadataService: meta,
            isWindowsOverride: true,
            distributionOverride: "Debian-Override",
            userOverride: "root-override");

        Assert.Equal("Debian-Override", resolver.GetWslDistributionName());
        Assert.Equal("root-override", resolver.GetWslUserName());
    }
}
