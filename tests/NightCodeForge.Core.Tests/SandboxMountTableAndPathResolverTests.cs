using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Tests;

public class SandboxMountTableAndPathResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public SandboxMountTableAndPathResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-sandbox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SandboxMountTable_ForNightwatch_SortsEntriesLongestPrefixFirst_AndMatchesMostSpecificMount()
    {
        var logsHost = Path.Combine(_tempRoot, "logs");
        var skillsHost = Path.Combine(_tempRoot, "skills");
        var wksHost = Path.Combine(_tempRoot, "wks");
        var wks1CloneHost = Path.Combine(_tempRoot, "wks-1-clone");
        var extraMount = new VirtualMountEntry(
            virtualSubPath: "wks/1/clone/special",
            virtualMountPath: "/nightcodeforge/wks/1/clone/special",
            hostDirectoryPath: Path.Combine(_tempRoot, "special"),
            wslTargetDirectoryPath: "/home/testuser/.nightcodeforge/special",
            isReadOnly: true);

        var table = SandboxMountTable.ForNightwatch(
            hostNcfLogsPath: logsHost,
            wslNcfLogsPath: "/mnt/c/ncf/logs",
            hostSkillsPath: skillsHost,
            wslSkillsPath: "/mnt/c/ncf/skills",
            hostWorkspacesRootPath: wksHost,
            wslWorkspacesRootPath: "/home/testuser/.nightcodeforge/wks",
            additionalMounts:
            [
                new VirtualMountEntry(
                    virtualSubPath: "wks/1/clone",
                    virtualMountPath: "/nightcodeforge/wks/1/clone",
                    hostDirectoryPath: wks1CloneHost,
                    wslTargetDirectoryPath: "/home/testuser/.nightcodeforge/wks/1/clone",
                    isReadOnly: true),
                extraMount
            ]);

        Assert.Equal("/nightcodeforge", table.VirtualRootPath);
        Assert.Equal("~/.nightcodeforge/nightwatch-root", table.NonContainerFallbackWorkingDirectory);

        // Entries must be sorted longest VirtualMountPath first
        for (int i = 1; i < table.Entries.Count; i++)
        {
            Assert.True(table.Entries[i - 1].VirtualMountPath.Length >= table.Entries[i].VirtualMountPath.Length);
        }

        // Longest prefix match selects /nightcodeforge/wks/1/clone/special over /nightcodeforge/wks/1/clone and /nightcodeforge/wks
        var matched = table.TryMatchLongestPrefix("/nightcodeforge/wks/1/clone/special/file.txt", out var entry, out var remainder);
        Assert.True(matched);
        Assert.NotNull(entry);
        Assert.Equal("/nightcodeforge/wks/1/clone/special", entry!.VirtualMountPath);
        Assert.Equal("file.txt", remainder);
    }

    [Fact]
    public void SandboxPathResolver_NightwatchScope_ResolvesLongestPrefixHostPath_AndRejectsUnmappedOrTraversalPaths()
    {
        var logsHost = Path.Combine(_tempRoot, "logs");
        var skillsHost = Path.Combine(_tempRoot, "skills");
        var wks1CloneHost = Path.Combine(_tempRoot, "wks", "1", "clone");
        Directory.CreateDirectory(logsHost);
        Directory.CreateDirectory(skillsHost);
        Directory.CreateDirectory(wks1CloneHost);

        var scope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: logsHost,
            wslNcfLogsPath: "/mnt/c/ncf/logs",
            hostSkillsPath: skillsHost,
            wslSkillsPath: "/mnt/c/ncf/skills",
            hostWorkspacesRootPath: null,
            wslWorkspacesRootPath: null,
            additionalMounts:
            [
                new VirtualMountEntry(
                    virtualSubPath: "wks/1/clone",
                    virtualMountPath: "/nightcodeforge/wks/1/clone",
                    hostDirectoryPath: wks1CloneHost,
                    wslTargetDirectoryPath: "/home/testuser/.nightcodeforge/wks/1/clone",
                    isReadOnly: true)
            ]);

        var resolver = new SandboxPathResolver(scope);

        // 1. Virtual /nightcodeforge/ncf-logs/... resolves to logsHost
        var resolvedLog = resolver.ResolveHostPath("/nightcodeforge/ncf-logs/app-20261113.log");
        Assert.Equal(Path.GetFullPath(Path.Combine(logsHost, "app-20261113.log")), resolvedLog);

        // 2. Relative path resolves relative to /nightcodeforge virtual root
        var resolvedRelativeSkill = resolver.ResolveHostPath("skills/dotnet/SKILL.md");
        Assert.Equal(Path.GetFullPath(Path.Combine(skillsHost, "dotnet", "SKILL.md")), resolvedRelativeSkill);

        // 3. Longest-prefix match for /nightcodeforge/wks/1/clone/src/Foo.cs
        var resolvedCloneFile = resolver.ResolveHostPath("/nightcodeforge/wks/1/clone/src/Foo.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(wks1CloneHost, "src", "Foo.cs")), resolvedCloneFile);

        // 4. Unmapped path under /nightcodeforge (e.g. /nightcodeforge/wks/2/clone when only wks/1/clone is mounted) is rejected
        var unmappedEx = Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/nightcodeforge/wks/2/clone/Program.cs"));
        Assert.Contains("outside the allowed workspace sandbox", unmappedEx.Message);

        // 5. Path traversal escaping a mount is rejected
        var traversalEx = Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/nightcodeforge/ncf-logs/../../secret.txt"));
        Assert.Contains("outside the allowed workspace sandbox", traversalEx.Message);

        // 6. Write operations on read-only Nightwatch scope/mounts are rejected
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/nightcodeforge/ncf-logs/app.log", forWrite: true));
    }

    [Fact]
    public void SandboxPathResolver_WorkspaceScope_EnforcesReadOnlyMountOnSkills_WhileAllowingWritesToCloneOrTaskWorktree()
    {
        var wksRoot = Path.Combine(_tempRoot, "wks-10");
        var cloneDir = Path.Combine(wksRoot, "clone");
        var taskDir = Path.Combine(wksRoot, "task-7");
        var skillsDir = Path.Combine(_tempRoot, "global-skills");
        var tmpDir = Path.Combine(wksRoot, "tmp");
        Directory.CreateDirectory(cloneDir);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(skillsDir);
        Directory.CreateDirectory(tmpDir);

        var workspace = new Workspace
        {
            Id = 10,
            RepositoryPath = cloneDir,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var scope = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            securityTier: SandboxSecurityTier.Standard,
            taskId: 7,
            wslRootPath: wksRoot,
            clonePath: cloneDir,
            taskWorktreePath: taskDir,
            skillsDirectoryPath: skillsDir,
            tmpDirectoryPath: tmpDir);

        var resolver = new SandboxPathResolver(scope);

        // 1. Default working directory is /workspace/task-7, so relative path resolves inside taskDir
        var taskFile = resolver.ResolveHostPath("src/Service.cs", forWrite: true);
        Assert.Equal(Path.GetFullPath(Path.Combine(taskDir, "src", "Service.cs")), taskFile);

        // 2. Reading from /workspace/skills/my-skill/SKILL.md succeeds
        var skillRead = resolver.ResolveHostPath("/workspace/skills/my-skill/SKILL.md", forWrite: false);
        Assert.Equal(Path.GetFullPath(Path.Combine(skillsDir, "my-skill", "SKILL.md")), skillRead);

        // 3. Writing to /workspace/skills/my-skill/SKILL.md is rejected because /workspace/skills is read-only
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveHostPath("/workspace/skills/my-skill/SKILL.md", forWrite: true));

        // 4. Writing to /tmp/scratch.txt resolves to tmpDir and succeeds
        var tmpFile = resolver.ResolveHostPath("/tmp/scratch.txt", forWrite: true);
        Assert.Equal(Path.GetFullPath(Path.Combine(tmpDir, "scratch.txt")), tmpFile);

        // 5. When DefaultWorkingDirectory is /workspace, a relative path "skills/my-skill/SKILL.md" must still match the /workspace/skills sub-mount and enforce its read-only guard
        var rootWorkspaceScope = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            securityTier: SandboxSecurityTier.Standard,
            taskId: null,
            wslRootPath: wksRoot,
            clonePath: null,
            taskWorktreePath: null,
            skillsDirectoryPath: skillsDir,
            tmpDirectoryPath: tmpDir);
        var rootResolver = new SandboxPathResolver(rootWorkspaceScope);
        Assert.Equal("/workspace", rootWorkspaceScope.DefaultWorkingDirectory);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(skillsDir, "my-skill", "SKILL.md")),
            rootResolver.ResolveHostPath("skills/my-skill/SKILL.md", forWrite: false));
        Assert.Throws<UnauthorizedAccessException>(() =>
            rootResolver.ResolveHostPath("skills/my-skill/SKILL.md", forWrite: true));
    }

    [Fact]
    public void SandboxPathResolver_TranslateBashCommand_And_ResolveBashWorkingDirectory_HandleWslDirectAndContainerTiers()
    {
        var scope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: @"C:\Users\dev\.nightcodeforge\logs",
            wslNcfLogsPath: "/mnt/c/Users/dev/.nightcodeforge/logs",
            hostSkillsPath: @"C:\Users\dev\.nightcodeforge\skills",
            wslSkillsPath: "/mnt/c/Users/dev/.nightcodeforge/skills",
            hostWorkspacesRootPath: @"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks",
            wslWorkspacesRootPath: "/home/dev/.nightcodeforge/wks",
            additionalMounts:
            [
                new VirtualMountEntry(
                    virtualSubPath: "wks/1/clone",
                    virtualMountPath: "/nightcodeforge/wks/1/clone",
                    hostDirectoryPath: @"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks\1\clone",
                    wslTargetDirectoryPath: "/home/dev/.nightcodeforge/wks/1/clone",
                    isReadOnly: true)
            ]);

        var resolver = new SandboxPathResolver(scope);

        var rawCmd = "rg -n \"Exception\" /nightcodeforge/ncf-logs/app.log /nightcodeforge/wks/1/clone/src/Foo.cs";

        // In ContainerSandbox, virtual paths are real bind mounts -> command is unchanged
        var containerCmd = resolver.TranslateBashCommand(rawCmd, ExecutionTier.ContainerSandbox);
        Assert.Equal(rawCmd, containerCmd);

        // In WslDirect, virtual paths are rewritten in a single pass using longest-prefix matching
        var wslCmd = resolver.TranslateBashCommand(rawCmd, ExecutionTier.WslDirect);
        Assert.Equal(
            "rg -n \"Exception\" /mnt/c/Users/dev/.nightcodeforge/logs/app.log /home/dev/.nightcodeforge/wks/1/clone/src/Foo.cs",
            wslCmd);

        // In WslDirect, default working directory for SystemNightwatch falls back to ~/.nightcodeforge/nightwatch-root
        var wslDefaultCwd = resolver.ResolveBashWorkingDirectory(null, ExecutionTier.WslDirect);
        Assert.Equal("~/.nightcodeforge/nightwatch-root", wslDefaultCwd);

        // In WslDirect, a mounted virtual directory resolves to its WslTargetDirectoryPath
        var wslCloneCwd = resolver.ResolveBashWorkingDirectory("/nightcodeforge/wks/1/clone/src", ExecutionTier.WslDirect);
        Assert.Equal("/home/dev/.nightcodeforge/wks/1/clone/src", wslCloneCwd);

        // In ContainerSandbox, default working directory is /nightcodeforge
        var containerDefaultCwd = resolver.ResolveBashWorkingDirectory(null, ExecutionTier.ContainerSandbox);
        Assert.Equal("/nightcodeforge", containerDefaultCwd);

        // In ContainerSandbox, relative unmapped directory under /nightcodeforge is rejected consistently
        Assert.Throws<UnauthorizedAccessException>(() =>
            resolver.ResolveBashWorkingDirectory("unmapped-subpath", ExecutionTier.ContainerSandbox));
        Assert.Equal(
            "/nightcodeforge/wks/1/clone/src",
            resolver.ResolveBashWorkingDirectory("wks/1/clone/src", ExecutionTier.ContainerSandbox));
    }

    [Fact]
    public void VirtualMountEntry_And_SandboxMountTable_ConvertWindowsDriveAndWslUncPathsToCanonicalWslPaths()
    {
        var driveEntry = new VirtualMountEntry(
            virtualSubPath: "logs",
            virtualMountPath: "/nightcodeforge/ncf-logs",
            hostDirectoryPath: @"C:\Users\dev\.nightcodeforge\logs",
            wslTargetDirectoryPath: string.Empty,
            isReadOnly: true);
        Assert.Equal("/mnt/c/Users/dev/.nightcodeforge/logs", driveEntry.WslTargetDirectoryPath);

        var uncLocalhostEntry = new VirtualMountEntry(
            virtualSubPath: "wks",
            virtualMountPath: "/nightcodeforge/wks",
            hostDirectoryPath: @"\\wsl.localhost\Ubuntu\home\dev\.nightcodeforge\wks",
            wslTargetDirectoryPath: string.Empty,
            isReadOnly: true);
        Assert.Equal("/home/dev/.nightcodeforge/wks", uncLocalhostEntry.WslTargetDirectoryPath);

        var uncDollarEntry = new VirtualMountEntry(
            virtualSubPath: "repo",
            virtualMountPath: "/workspace/clone",
            hostDirectoryPath: @"\\wsl$\Debian\var\ncf\repo",
            wslTargetDirectoryPath: string.Empty,
            isReadOnly: false);
        Assert.Equal("/var/ncf/repo", uncDollarEntry.WslTargetDirectoryPath);

        var nwTable = SandboxMountTable.ForNightwatch(
            hostNcfLogsPath: @"D:\ncf-data\logs",
            wslNcfLogsPath: null,
            hostSkillsPath: @"C:\Users\dev\.nightcodeforge\skills",
            wslSkillsPath: null,
            hostWorkspacesRootPath: @"\\wsl.localhost\Ubuntu-24.04\home\dev\.nightcodeforge\wks",
            wslWorkspacesRootPath: null);

        Assert.Contains(nwTable.Entries, e => e.VirtualMountPath == "/nightcodeforge/ncf-logs" && e.WslTargetDirectoryPath == "/mnt/d/ncf-data/logs");
        Assert.Contains(nwTable.Entries, e => e.VirtualMountPath == "/nightcodeforge/skills" && e.WslTargetDirectoryPath == "/mnt/c/Users/dev/.nightcodeforge/skills");
        Assert.Contains(nwTable.Entries, e => e.VirtualMountPath == "/nightcodeforge/wks" && e.WslTargetDirectoryPath == "/home/dev/.nightcodeforge/wks");
    }

    [Fact]
    public void SandboxPathResolver_ResolveBashWorkingDirectory_NormalizesTrailingSlashOnVirtualRootAndMapsTmpBackingDirectory()
    {
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: Path.Combine(_tempRoot, "logs"),
            wslNcfLogsPath: "/mnt/c/ncf/logs",
            hostSkillsPath: Path.Combine(_tempRoot, "skills"),
            wslSkillsPath: "/mnt/c/ncf/skills");
        var nwResolver = new SandboxPathResolver(nwScope);

        // Trailing slash on /nightcodeforge/ must resolve to DefaultWorkingDirectory instead of failing prefix lookup
        Assert.Equal("/nightcodeforge", nwResolver.ResolveBashWorkingDirectory("/nightcodeforge/", ExecutionTier.ContainerSandbox));

        var wksRoot = Path.Combine(_tempRoot, "wks-trailing");
        var cloneDir = Path.Combine(wksRoot, "clone");
        var taskDir = Path.Combine(wksRoot, "task-9");
        var tmpDir = Path.Combine(wksRoot, "tmp");
        var tmpSubDir = Path.Combine(tmpDir, "scratch-work");
        Directory.CreateDirectory(cloneDir);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(tmpSubDir);

        var workspace = new Workspace
        {
            Id = 22,
            RepositoryPath = cloneDir,
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        var wsScope = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            securityTier: SandboxSecurityTier.Standard,
            taskId: 9,
            wslRootPath: wksRoot,
            clonePath: cloneDir,
            taskWorktreePath: taskDir,
            tmpDirectoryPath: tmpDir);
        var wsResolver = new SandboxPathResolver(wsScope);

        Assert.Equal("/workspace/task-9", wsResolver.ResolveBashWorkingDirectory("/workspace/", ExecutionTier.ContainerSandbox));
        Assert.Equal("/tmp/scratch-work", wsResolver.ResolveBashWorkingDirectory(tmpSubDir, ExecutionTier.ContainerSandbox));
    }

    [Fact]
    public void SandboxPathResolver_TranslateBashCommand_BareHostAndBraceBoundaries_NormalizesSlashesAndRewritesBracedPaths()
    {
        var scope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: @"C:\Users\dev\.nightcodeforge\logs",
            wslNcfLogsPath: "/mnt/c/Users/dev/.nightcodeforge/logs",
            hostSkillsPath: @"C:\Users\dev\.nightcodeforge\skills",
            wslSkillsPath: "/mnt/c/Users/dev/.nightcodeforge/skills");

        var resolver = new SandboxPathResolver(scope);

        var bareHostCmd = resolver.TranslateBashCommand(
            "cat ${LOG_FILE:-/nightcodeforge/ncf-logs/app.log} {/nightcodeforge/ncf-logs/a.log,/nightcodeforge/skills/dotnet/SKILL.md}",
            ExecutionTier.BareHost);

        Assert.Equal(
            "cat ${LOG_FILE:-C:/Users/dev/.nightcodeforge/logs/app.log} {C:/Users/dev/.nightcodeforge/logs/a.log,C:/Users/dev/.nightcodeforge/skills/dotnet/SKILL.md}",
            bareHostCmd);
    }
}
