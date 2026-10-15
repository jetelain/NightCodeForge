using Microsoft.Extensions.Configuration;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Models;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class WorkspaceProvisioningServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly TestGitMirrorSyncService _gitMirrorSyncService;
    private readonly TestWorkspacePathResolver _pathResolver;
    private readonly TestWslMetadataService _wslMetadataService;
    private readonly TestGitCliRunner _gitCliRunner;
    private readonly TestMarkdownSyncService _markdownSyncService;
    private readonly IConfiguration _configuration;
    private readonly WorkspaceProvisioningService _service;

    public WorkspaceProvisioningServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "NCF_ProvTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _workspaceRepository = new InMemoryWorkspaceRepository();
        _gitMirrorSyncService = new TestGitMirrorSyncService();
        _pathResolver = new TestWorkspacePathResolver();
        _wslMetadataService = new TestWslMetadataService();
        _gitCliRunner = new TestGitCliRunner();
        _markdownSyncService = new TestMarkdownSyncService();

        var configValues = new Dictionary<string, string?>
        {
            ["LlmProvider:ModelId"] = "Qwen3.8-27B:latest"
        };
        _configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        _service = new WorkspaceProvisioningService(
            _workspaceRepository,
            _gitMirrorSyncService,
            _pathResolver,
            _wslMetadataService,
            _gitCliRunner,
            _markdownSyncService,
            _configuration);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task ProbeRepositoryAsync_NonExistentDirectory_ReturnsFalseWithWarning()
    {
        var nonExistentPath = Path.Combine(_tempDir, "does-not-exist");

        var result = await _service.ProbeRepositoryAsync(nonExistentPath);

        Assert.False(result.Exists);
        Assert.False(result.IsGitRepository);
        Assert.Null(result.SuggestedName);
        Assert.Empty(result.AvailableBranches);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("does not exist", result.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_EmptyPath_ReturnsFalseWithWarning()
    {
        var result = await _service.ProbeRepositoryAsync("   ");

        Assert.False(result.Exists);
        Assert.False(result.IsGitRepository);
        Assert.NotNull(result.WarningMessage);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_DirectoryWithoutGit_ReturnsExistsTrueAndIsGitFalse()
    {
        var repoDir = Path.Combine(_tempDir, "my-code-repo");
        Directory.CreateDirectory(repoDir);

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.True(result.Exists);
        Assert.False(result.IsGitRepository);
        Assert.Equal("my-code-repo", result.SuggestedName);
        Assert.Empty(result.AvailableBranches);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("not a Git repository", result.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_DotNetRepository_DetectsCSharpStackAndBranches()
    {
        var repoDir = Path.Combine(_tempDir, "dotnet-app");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "MyApp.sln"), "// sln content");

        _gitCliRunner.ExecuteHandler = (dir, args) =>
        {
            if (args.Contains("branch"))
            {
                return new GitCommandResult(0, "main\nfeature/api\n", "", TimeSpan.Zero);
            }
            return new GitCommandResult(0, "true", "", TimeSpan.Zero);
        };

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.True(result.Exists);
        Assert.True(result.IsGitRepository);
        Assert.Equal("dotnet-app", result.SuggestedName);
        Assert.Equal(".NET / C#", result.DetectedTechStack);
        Assert.Equal("main", result.DetectedBaseBranch);
        Assert.Equal(2, result.AvailableBranches.Count);
        Assert.Contains("main", result.AvailableBranches);
        Assert.Contains("feature/api", result.AvailableBranches);
        Assert.Null(result.WarningMessage);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_NodeRepository_DetectsNodeStack()
    {
        var repoDir = Path.Combine(_tempDir, "node-app");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "package.json"), "{}");

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.True(result.Exists);
        Assert.True(result.IsGitRepository);
        Assert.Equal("Node.js / TypeScript", result.DetectedTechStack);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_DetectsAgentGuidelines()
    {
        var repoDir = Path.Combine(_tempDir, "guideline-app");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "AGENTS.md"), "# Guidelines");

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.True(result.HasAgentsMarkdown);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_DetectsGitAttributes_EditorConfig_AndCoreAutoCrlf()
    {
        var repoDir = Path.Combine(_tempDir, "hygiene-app");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, ".gitattributes"), "* text=auto eol=lf\n");
        await File.WriteAllTextAsync(Path.Combine(repoDir, ".editorconfig"), "root = true\n[*]\nend_of_line = lf\n");

        _gitCliRunner.ExecuteHandler = (dir, args) =>
        {
            if (args.Contains("core.autocrlf"))
            {
                return new GitCommandResult(0, "false", "", TimeSpan.Zero);
            }
            if (args.Contains("branch"))
            {
                return new GitCommandResult(0, "* main\n", "", TimeSpan.Zero);
            }
            return new GitCommandResult(0, "", "", TimeSpan.Zero);
        };

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.True(result.Exists);
        Assert.True(result.IsGitRepository);
        Assert.True(result.HasGitAttributes);
        Assert.True(result.HasLfGitAttributes);
        Assert.True(result.HasEditorConfig);
        Assert.True(result.HasLfEditorConfig);
        Assert.Equal("false", result.CoreAutoCrlf);
        Assert.Null(result.LineEndingRecommendation);
    }

    [Fact]
    public async Task ProbeRepositoryAsync_FlagsMissingLfRules_OnWindows()
    {
        var repoDir = Path.Combine(_tempDir, "missing-hygiene-app");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        _gitCliRunner.ExecuteHandler = (dir, args) =>
        {
            if (args.Contains("core.autocrlf"))
            {
                return new GitCommandResult(0, "true", "", TimeSpan.Zero);
            }
            if (args.Contains("branch"))
            {
                return new GitCommandResult(0, "* main\n", "", TimeSpan.Zero);
            }
            return new GitCommandResult(0, "", "", TimeSpan.Zero);
        };

        var result = await _service.ProbeRepositoryAsync(repoDir);

        Assert.False(result.HasGitAttributes);
        Assert.False(result.HasLfGitAttributes);
        Assert.False(result.HasEditorConfig);
        Assert.False(result.HasLfEditorConfig);
        Assert.Equal("true", result.CoreAutoCrlf);

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            Assert.NotNull(result.LineEndingRecommendation);
            Assert.Contains(".gitattributes", result.LineEndingRecommendation);
            Assert.Contains(".editorconfig", result.LineEndingRecommendation);
            Assert.Contains("core.autocrlf false", result.LineEndingRecommendation);
        }
    }

    [Fact]
    public async Task InitializeGitRepositoryAsync_RunsGitInitSuccessfully()
    {
        var targetDir = Path.Combine(_tempDir, "init-target");
        var gitInitInvoked = false;

        _gitCliRunner.ExecuteHandler = (dir, args) =>
        {
            if (args.Contains("init"))
            {
                gitInitInvoked = true;
                return new GitCommandResult(0, "Initialized empty Git repository", "", TimeSpan.Zero);
            }
            return new GitCommandResult(0, "", "", TimeSpan.Zero);
        };

        var success = await _service.InitializeGitRepositoryAsync(targetDir);

        Assert.True(success);
        Assert.True(gitInitInvoked);
        Assert.True(Directory.Exists(targetDir));
    }

    [Fact]
    public async Task ProvisionWorkspaceAsync_EndToEnd_CreatesRecordMirrorScaffoldingAndWatcher()
    {
        var repoDir = Path.Combine(_tempDir, "sample-repo");
        Directory.CreateDirectory(repoDir);

        var model = new WorkspaceCreationModel
        {
            Name = "Sample Workspace",
            RepositoryPath = repoDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox,
            BuildTimeoutSeconds = 120,
            TestTimeoutSeconds = 90,
            UseDedicatedAgentProfiles = false
        };

        var progressMessages = new List<string>();
        var progress = new Progress<string>(msg => progressMessages.Add(msg));

        var workspace = await _service.ProvisionWorkspaceAsync(model, progress);

        // 1. Verify workspace entity persisted
        Assert.NotNull(workspace);
        Assert.True(workspace.Id > 0);
        Assert.Equal("Sample Workspace", workspace.Name);
        Assert.Equal(Path.GetFullPath(repoDir), workspace.RepositoryPath);
        Assert.Equal("main", workspace.DefaultBaseBranch);

        // 2. Verify Ext4 mirror initialized
        Assert.True(_gitMirrorSyncService.InitializeMirrorCalled);
        Assert.NotNull(workspace.Ext4MirrorPath);

        // 3. Verify docs/tasks/ folder scaffolded
        var tasksDir = Path.Combine(repoDir, "docs", "tasks");
        Assert.True(Directory.Exists(tasksDir));

        // 4. Verify markdown watcher registered
        Assert.Contains(workspace.Id, _markdownSyncService.WatchedWorkspaceIds);

        // 5. Verify progress messages reported
        Assert.NotEmpty(progressMessages);
        Assert.Contains(progressMessages, m => m.Contains("database", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progressMessages, m => m.Contains("mirror", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progressMessages, m => m.Contains("watcher", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProvisionWorkspaceAsync_WithDedicatedProfiles_CreatesWorkspaceScopedProfiles()
    {
        var repoDir = Path.Combine(_tempDir, "dedicated-repo");
        Directory.CreateDirectory(repoDir);

        var model = new WorkspaceCreationModel
        {
            Name = "Dedicated Workspace",
            RepositoryPath = repoDir,
            DefaultBaseBranch = "develop",
            ExecutionMode = ExecutionMode.WslDirect,
            UseDedicatedAgentProfiles = true,
            DedicatedModelName = "deepseek-coder:33b"
        };

        var workspace = await _service.ProvisionWorkspaceAsync(model);

        Assert.NotNull(workspace);
        Assert.NotNull(workspace.AgentProfiles);
        Assert.Equal(5, workspace.AgentProfiles.Count);
        Assert.All(workspace.AgentProfiles, p =>
        {
            Assert.Equal(workspace.Id, p.WorkspaceId);
            Assert.Equal("deepseek-coder:33b", p.ModelName);
            Assert.StartsWith($"ws-{workspace.Id}:", p.Id);
        });
    }

    #region Test Fakes

    private class TestGitCliRunner : IGitCliRunner
    {
        public Func<string, IReadOnlyList<string>, GitCommandResult>? ExecuteHandler { get; set; }

        public Task<GitCommandResult> ExecuteAsync(
            string workingDirectory,
            IEnumerable<string> argumentsList,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            var args = argumentsList.ToList();
            if (ExecuteHandler != null)
            {
                return Task.FromResult(ExecuteHandler(workingDirectory, args));
            }
            return Task.FromResult(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        }
    }

    private class TestGitMirrorSyncService : IGitMirrorSyncService
    {
        public bool InitializeMirrorCalled { get; private set; }

        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default)
        {
            InitializeMirrorCalled = true;
            return Task.FromResult($"~/.nightcodeforge/wks/{workspace.Id}/clone");
        }

        public Task PullFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitSyncResult(true, false, $"feature/task-{taskId}", null));

        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
            => Task.FromResult(new GitSyncResult(true, false, baseBranch, null));
    }

    private class TestWorkspacePathResolver : IWorkspacePathResolver
    {
        public string GetHostRemoteUrl(string repositoryPath) => repositoryPath;
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath) => relativeOrContainerPath;
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "julien";
    }

    private class TestWslMetadataService : IWslMetadataService
    {
        public string GetDefaultDistributionName() => "Ubuntu";
        public string GetDefaultUserName() => "julien";
        public void ClearCache() { }
    }

    private class TestMarkdownSyncService : ITaskMarkdownSyncService
    {
        public List<int> WatchedWorkspaceIds { get; } = new();

        public void StartWatching(int workspaceId, string workspacePath)
        {
            WatchedWorkspaceIds.Add(workspaceId);
        }

        public void StopWatching(int workspaceId)
        {
            WatchedWorkspaceIds.Remove(workspaceId);
        }

        public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) => string.Empty;
        public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => (new TaskFrontmatter(), string.Empty);
        public string ComputeContentHash(string content) => string.Empty;
        public Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    #endregion
}
