using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class OrchestratorHarnessTests
{
    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId => "crucible";
        public List<AgentSessionContext> ExecutedSessions { get; } = new();
        public AgentRunResult ResultToReturn { get; set; } = AgentRunResult.Completed(
            new AgentRunMetrics(DurationMs: 50, PromptTokens: 100, CompletionTokens: 50, TotalTokens: 150),
            summary: "Completed by Crucible harness");

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            ExecutedSessions.Add(context);
            return Task.FromResult(ResultToReturn);
        }
    }

    private class FakeHarnessRegistry : IHarnessRegistry
    {
        private readonly Dictionary<string, IAgentHarness> _map = new(StringComparer.OrdinalIgnoreCase);

        public FakeHarnessRegistry(IAgentHarness harness)
        {
            _map[harness.HarnessId] = harness;
        }

        public IAgentHarness GetHarness(string harnessId) => _map[harnessId];
        public void RegisterHarness(IAgentHarness harness) => _map[harness.HarnessId] = harness;
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness) => _map.TryGetValue(harnessId, out harness);
        public IEnumerable<IAgentHarness> GetAllHarnesses() => _map.Values;
    }

    private class FakeMirrorSyncService : IGitMirrorSyncService
    {
        public int PushCount { get; private set; }

        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult(mirrorDirectory);
        public Task FetchFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int issueId, CancellationToken cancellationToken = default)
        {
            PushCount++;
            return Task.FromResult(new GitSyncResult(true, false, $"feature/issue-{issueId}", null));
        }
    }

    private class FakeMarkdownSyncService : IIssueMarkdownSyncService
    {
        public int SyncCount { get; private set; }

        public string GenerateMarkdown(Issue issue, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) => string.Empty;
        public (IssueFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => (new IssueFrontmatter(), string.Empty);
        public string ComputeContentHash(string content) => string.Empty;

        public Task<string> SyncIssueToDiskAsync(Issue issue, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default)
        {
            SyncCount++;
            return Task.FromResult(Path.Combine(workspacePath, $"issue-{issue.Id}.md"));
        }

        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveOrCleanupIssueFileAsync(Issue issue, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StartWatching(int workspaceId, string workspacePath) { }
        public void StopWatching(int workspaceId) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task ProcessIssueAsync_DelegatesToHarness_ForAgenticStage()
    {
        var issueRepo = new InMemoryIssueRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryIssueDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeIssueJobQueue();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            Ext4MirrorPath = "/mnt/wsl/mirror",
            DefaultBaseBranch = "main"
        };
        await workspaceRepo.CreateAsync(workspace);

        var issue = await issueRepo.CreateAsync(new Issue
        {
            Id = 80,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Pending,
            CurrentStageId = "issue-review",
            BaseBranch = "main"
        });
        depStore.AddIssue(80, 1, IssueOverallStatus.Pending);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);
        var mirrorSync = new FakeMirrorSyncService();
        var mdSync = new FakeMarkdownSyncService();

        // Default IssueReviewStage without custom handler
        var stage1 = new IssueReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

        var orchestrator = new IssueOrchestrator(
            issueRepo, workspaceRepo, depEngine, lockProvider,
            gitManager, new DefaultWorkflowPipelineProvider(pipeline), jobQueue, depStore,
            harnessRegistry: harnessRegistry,
            gitMirrorSyncService: mirrorSync,
            markdownSyncService: mdSync);

        await orchestrator.ProcessIssueAsync(issue.Id);

        // Harness should have been invoked
        Assert.Single(fakeHarness.ExecutedSessions);
        var session = fakeHarness.ExecutedSessions[0];
        Assert.Equal(80, session.Issue.Id);
        Assert.Equal("issue-review", session.StageId);
        Assert.Equal(stage1.StageDirective, session.StageDirective);
        Assert.Contains(stage1.StageDirective, session.BootstrapPrompt);

        // Mirror sync and MD sync should have been triggered
        Assert.True(mirrorSync.PushCount > 0);
        Assert.True(mdSync.SyncCount > 0);

        // Token tracking should be recorded
        var records = issueRepo.GetAllExecutionRecords();
        Assert.Single(records);
        Assert.Equal(100, records[0].PromptTokens);
        Assert.Equal(50, records[0].CompletionTokens);
        Assert.Equal(150, records[0].TotalTokens);
    }

    [Fact]
    public async Task ProcessIssueAsync_UsesProfileStageDirectiveOverride_WhenConfigured()
    {
        var issueRepo = new InMemoryIssueRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryIssueDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeIssueJobQueue();

        const string customDirective = "Custom profile directive: Always generate unit tests with FluentAssertions.";

        var workspace = new Workspace
        {
            Id = 2,
            Name = "Profile Directive Test Workspace",
            RepositoryPath = "C:/test/repo2",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "custom-profile",
                    HarnessId = "crucible",
                    ModelName = "qwen2.5-coder:7b",
                    StageDirectives = new Dictionary<string, string>
                    {
                        ["implementation"] = customDirective
                    }
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var issue = await issueRepo.CreateAsync(new Issue
        {
            Id = 90,
            WorkspaceId = 2,
            Status = IssueOverallStatus.Pending,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddIssue(90, 2, IssueOverallStatus.Pending);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = new ImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            issueRepo, workspaceRepo, depEngine, lockProvider,
            gitManager, new DefaultWorkflowPipelineProvider(pipeline), jobQueue, depStore,
            harnessRegistry: harnessRegistry);

        await orchestrator.ProcessIssueAsync(issue.Id);

        Assert.Single(fakeHarness.ExecutedSessions);
        var session = fakeHarness.ExecutedSessions[0];
        Assert.Equal("implementation", session.StageId);
        // Tier 2 override should take precedence over the canonical Tier 1 stage default
        Assert.Equal(customDirective, session.StageDirective);
        Assert.Contains(customDirective, session.BootstrapPrompt);
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenAgentModeMismatch_FailsStageWithValidationMessage()
    {
        var issueRepo = new InMemoryIssueRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryIssueDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeIssueJobQueue();

        var workspace = new Workspace
        {
            Id = 3,
            Name = "Mode Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "read-only-prof",
                    Name = "Audit Agent",
                    Mode = AgentMode.ReadOnly,
                    HarnessId = "crucible"
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var issue = await issueRepo.CreateAsync(new Issue
        {
            Id = 80,
            WorkspaceId = 3,
            Status = IssueOverallStatus.Pending,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddIssue(80, 3, IssueOverallStatus.Pending);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        // ImplementationStage requires AgentMode.Full by default
        var stage = new ImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            issueRepo, workspaceRepo, depEngine, lockProvider,
            gitManager, new DefaultWorkflowPipelineProvider(pipeline), jobQueue, depStore,
            harnessRegistry: harnessRegistry);

        await orchestrator.ProcessIssueAsync(issue.Id);

        // The harness must NOT have been executed
        Assert.Empty(fakeHarness.ExecutedSessions);

        // Issue must have failed
        var updated = await issueRepo.GetByIdAsync(80);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Failed, updated.Status);

        var logs = issueRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Message.Contains("requires AgentMode.Full capabilities"));
    }
}
