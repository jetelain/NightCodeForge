using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.Tests.Git;
using Xunit;
using Xunit.Abstractions;

namespace NightCodeForge.Infrastructure.Tests.Execution;

[Trait("Category", "Integration")]
public class ContainerGitIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testRoot;
    private readonly bool _isWindows;
    private readonly bool _useWsl;
    private readonly List<string> _containersToCleanup = new();

    public ContainerGitIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _testRoot = Path.Combine(Path.GetTempPath(), "ncf_cntr_integ_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
        _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        _useWsl = _isWindows;
    }

    public void Dispose()
    {
        foreach (var container in _containersToCleanup)
        {
            RemoveContainerSafe(container, _useWsl);
        }

        TestGitRepository.DeleteDirectorySafe(_testRoot);
    }

    [Fact]
    public async Task ContainerExecutor_WithIsolatedTaskWorktree_ExecutesGitDiffAndDiffStatSuccessfully()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        // Arrange: Setup canonical clone and an isolated task worktree off main
        var clonePath = Path.Combine(_testRoot, "clone");
        var worktreePath = Path.Combine(_testRoot, "task-77");
        Directory.CreateDirectory(clonePath);

        using var repo = new TestGitRepository("main");
        // Create initial repository content
        repo.CreateFile("README.md", "# Project Root\nInitial content\n");
        repo.CreateFile("src/App.cs", "public class App { public static void Main() {} }\n");
        repo.CommitAll("Initial commit on main");

        // Clone to clonePath so it mirrors real workspace layout
        repo.RunGit($"clone \"{repo.RepositoryPath}\" \"{clonePath}\"");
        repo.RunGit("config user.name \"NCF Agent\"", clonePath);
        repo.RunGit("config user.email \"agent@nightcodeforge.local\"", clonePath);

        // Create worktree for task-77 on branch feature/task-77
        repo.RunGit($"worktree add -b feature/task-77 \"{worktreePath}\"", clonePath);

        // Make modifications in worktree: 1 file modified, 1 file added
        var modifiedFile = Path.Combine(worktreePath, "README.md");
        File.AppendAllText(modifiedFile, "\nFeature task-77 updates\n");

        var newFile = Path.Combine(worktreePath, "src", "Feature77Service.cs");
        File.WriteAllText(newFile, "public class Feature77Service { public void Execute() {} }\n");

        repo.RunGit("add -A", worktreePath);
        repo.RunGit("commit -m \"Implement feature 77\"", worktreePath);

        // Normalize forward pointer in task-77/.git to relative: gitdir: ../clone/.git/worktrees/task-77
        var forwardGitFile = Path.Combine(worktreePath, ".git");
        File.SetAttributes(forwardGitFile, FileAttributes.Normal);
        File.WriteAllText(forwardGitFile, "gitdir: ../clone/.git/worktrees/task-77\n");

        var containerName = $"ncf-integ-test-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerName);

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = worktreePath,
            CanonicalClonePath = clonePath,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await using var executor = new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);

        // Act 1: Execute git status inside container
        var statusResult = await executor.ExecuteAsync("git status");

        // Assert 1: Status succeeds, recognizes branch and clean working tree
        Assert.Equal(0, statusResult.ExitCode);
        Assert.Contains("feature/task-77", statusResult.StandardOutput);

        // Act 2: Execute git diff main...feature/task-77 inside container
        var diffResult = await executor.ExecuteAsync("git diff main...feature/task-77");

        // Assert 2: Branch diff succeeds and contains the modified and newly added files
        Assert.Equal(0, diffResult.ExitCode);
        Assert.Contains("diff --git a/README.md b/README.md", diffResult.StandardOutput);
        Assert.Contains("+Feature task-77 updates", diffResult.StandardOutput);
        Assert.Contains("diff --git a/src/Feature77Service.cs b/src/Feature77Service.cs", diffResult.StandardOutput);
        Assert.Contains("+public class Feature77Service", diffResult.StandardOutput);

        // Act 3: Execute git diff --stat main...feature/task-77 inside container
        var diffStatResult = await executor.ExecuteAsync("git diff --stat main...feature/task-77");

        // Assert 3: Diffstat succeeds and summarizes both files
        Assert.Equal(0, diffStatResult.ExitCode);
        Assert.Contains("README.md", diffStatResult.StandardOutput);
        Assert.Contains("Feature77Service.cs", diffStatResult.StandardOutput);
        Assert.Contains("2 files changed", diffStatResult.StandardOutput);
    }

    [Fact]
    public async Task ContainerExecutor_WorkspaceRootMount_ExecutesGitDiffInsideTaskSubdirectory()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        // Arrange: Workspace root containing both clone/ and task-88/
        var workspaceRoot = Path.Combine(_testRoot, "ws-root");
        var clonePath = Path.Combine(workspaceRoot, "clone");
        var worktreePath = Path.Combine(workspaceRoot, "task-88");
        Directory.CreateDirectory(clonePath);

        using var repo = new TestGitRepository("main");
        repo.CreateFile("config.json", "{ \"version\": 1 }\n");
        repo.CommitAll("Initial commit");

        repo.RunGit($"clone \"{repo.RepositoryPath}\" \"{clonePath}\"");
        repo.RunGit("config user.name \"NCF Agent\"", clonePath);
        repo.RunGit("config user.email \"agent@nightcodeforge.local\"", clonePath);

        repo.RunGit($"worktree add -b feature/task-88 \"{worktreePath}\"", clonePath);

        var newFile = Path.Combine(worktreePath, "patch.txt");
        File.WriteAllText(newFile, "patch applied for task 88\n");
        repo.RunGit("add -A", worktreePath);
        repo.RunGit("commit -m \"Apply task 88 patch\"", worktreePath);

        // Normalize forward pointer in task-88/.git to relative: gitdir: ../clone/.git/worktrees/task-88
        var forwardGitFile = Path.Combine(worktreePath, ".git");
        File.SetAttributes(forwardGitFile, FileAttributes.Normal);
        File.WriteAllText(forwardGitFile, "gitdir: ../clone/.git/worktrees/task-88\n");

        var containerName = $"ncf-integ-test-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerName);

        // Workspace root mounted to /workspace; working directory is /workspace/task-88
        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = workspaceRoot,
            CanonicalClonePath = clonePath,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace/task-88",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await using var executor = new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);

        // Act: Run git diff from inside /workspace/task-88
        var diffResult = await executor.ExecuteAsync("git diff main...feature/task-88");

        // Assert: Diff output is complete and accurate
        Assert.Equal(0, diffResult.ExitCode);
        Assert.Contains("diff --git a/patch.txt b/patch.txt", diffResult.StandardOutput);
        Assert.Contains("+patch applied for task 88", diffResult.StandardOutput);
    }

    [Fact]
    public async Task ContainerExecutor_AgentModifiesInsideContainer_DiffVisibleToBothContainerAndGitWorkspaceManager()
    {
        if (!IsDockerAccessible(_useWsl))
        {
            _output.WriteLine("Docker is not accessible in this environment. Skipping test.");
            return;
        }

        // Arrange: Real workspace structure
        var workspaceRoot = Path.Combine(_testRoot, "ws-manager");
        var clonePath = Path.Combine(workspaceRoot, "clone");
        var worktreePath = Path.Combine(workspaceRoot, "task-99");
        Directory.CreateDirectory(clonePath);

        using var repo = new TestGitRepository("main");
        repo.CreateFile("README.md", "# Base Project\nInitial line\n");
        repo.CommitAll("Initial commit on main");

        repo.RunGit($"clone \"{repo.RepositoryPath}\" \"{clonePath}\"");
        repo.RunGit("config user.name \"NCF Agent\"", clonePath);
        repo.RunGit("config user.email \"agent@nightcodeforge.local\"", clonePath);

        repo.RunGit($"worktree add -b feature/task-99 \"{worktreePath}\"", clonePath);

        // Normalize forward pointer in task-99/.git to relative: gitdir: ../clone/.git/worktrees/task-99
        var forwardGitFile = Path.Combine(worktreePath, ".git");
        File.SetAttributes(forwardGitFile, FileAttributes.Normal);
        File.WriteAllText(forwardGitFile, "gitdir: ../clone/.git/worktrees/task-99\n");

        var containerName = $"ncf-integ-test-{Guid.NewGuid():N}";
        _containersToCleanup.Add(containerName);

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = containerName,
            ContainerImage = ContainerExecutorOptions.DefaultAgentBaseImage,
            HostWorkspacePath = worktreePath,
            CanonicalClonePath = clonePath,
            ContainerWorkspacePath = "/workspace",
            DefaultWorkingDirectory = "/workspace",
            AutoStartContainer = true,
            MountPackageCaches = false,
            MountHostSdks = false,
            UseWsl = _useWsl,
            DefaultTimeoutSeconds = 60
        };

        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        var identityResolver = new HostUserIdentityResolver(processRunner, NullLogger<HostUserIdentityResolver>.Instance);
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        await using var executor = new NightCodeForgeContainerExecutor(
            Options.Create(options),
            processRunner,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityResolver,
            telemetryMock.Object);

        // Act 1: Agent performs git commands directly inside container
        var addFileResult = await executor.ExecuteAsync("echo 'New agent content' > agent_file.txt");
        Assert.Equal(0, addFileResult.ExitCode);

        var appendResult = await executor.ExecuteAsync("echo 'Line from agent' >> README.md");
        Assert.Equal(0, appendResult.ExitCode);

        var gitAddResult = await executor.ExecuteAsync("git add -A");
        Assert.Equal(0, gitAddResult.ExitCode);

        var gitCommitResult = await executor.ExecuteAsync("git -c user.name='ContainerAgent' -c user.email='agent@cntr.local' commit -m 'Commit made inside container'");
        Assert.Equal(0, gitCommitResult.ExitCode);

        // Act 2: Agent checks diff in container
        var containerDiffResult = await executor.ExecuteAsync("git diff main...feature/task-99");
        Assert.Equal(0, containerDiffResult.ExitCode);
        Assert.Contains("diff --git a/agent_file.txt b/agent_file.txt", containerDiffResult.StandardOutput);
        Assert.Contains("+New agent content", containerDiffResult.StandardOutput);
        Assert.Contains("+Line from agent", containerDiffResult.StandardOutput);

        // Act 3: Verify host GitWorkspaceManager generates diff from the container workspace
        var workspace = new Workspace
        {
            Id = 99,
            Name = "Container In-Flight Workspace",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            RepositoryPath = clonePath,
            Ext4MirrorPath = clonePath,
            DefaultBaseBranch = "main"
        };

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetAgentWorkingClonePath(99)).Returns(clonePath);
        pathResolverMock.Setup(p => p.GetAgentWorkspaceRootPath(99)).Returns(workspaceRoot);
        pathResolverMock.Setup(p => p.ResolveCruciblePath(99, It.IsAny<string>())).Returns(worktreePath);

        var gitCliRunner = new GitCliRunner(
            processRunner,
            pathResolverMock.Object,
            new Mock<IWslMetadataService>().Object,
            NullLogger<GitCliRunner>.Instance);

        var manager = new GitWorkspaceManager(
            gitCliRunner,
            pathResolverMock.Object,
            new Mock<IWslMetadataService>().Object,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            processRunner,
            NullLogger<GitWorkspaceManager>.Instance);

        var hostDiff = await manager.GenerateDiffAsync(workspace, 99, "main");
        Assert.NotNull(hostDiff);
        Assert.Contains("agent_file.txt", hostDiff);
        Assert.Contains("Line from agent", hostDiff);

        var hostDiffStat = await manager.GenerateDiffStatAsync(workspace, 99, "main");
        Assert.NotNull(hostDiffStat);
        Assert.Contains("agent_file.txt", hostDiffStat);
        Assert.Contains("README.md", hostDiffStat);
    }

    private static bool IsDockerAccessible(bool useWsl)
    {
        try
        {
            var psi = useWsl
                ? new ProcessStartInfo("wsl.exe", "-- docker info")
                : new ProcessStartInfo("docker", "info");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var process = Process.Start(psi);
            if (process == null) return false;

            if (!process.WaitForExit(8000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // Best-effort process kill on timeout in test capability check
                }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            // Docker unavailable or process start failed: capability probe returns false
            return false;
        }
    }

    private static void RemoveContainerSafe(string containerName, bool useWsl)
    {
        try
        {
            var psi = useWsl
                ? new ProcessStartInfo("wsl.exe", $"-- docker rm -f {containerName}")
                : new ProcessStartInfo("docker", $"rm -f {containerName}");

            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
        }
        catch (Exception)
        {
            // Best-effort test container cleanup: ignore if container does not exist or daemon stopped
        }
    }
}
