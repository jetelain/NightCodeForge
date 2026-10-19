using NightCodeForge.Agents.Triage;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Agents.Tests;

public class TriageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly FakeHarnessRegistry _harnessRegistry;
    private readonly FakeAgentHarness _fakeHarness;
    private readonly FakeMarkdownSyncService _markdownSyncService;
    private readonly Workspace _workspace;

    public TriageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-triage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _fakeHarness = new FakeAgentHarness();
        _harnessRegistry = new FakeHarnessRegistry(_fakeHarness);
        _markdownSyncService = new FakeMarkdownSyncService();

        _workspace = new Workspace
        {
            Id = 5,
            Name = "Triage Test Workspace",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = TriageStage.DefaultStageId,
                    Name = "Untrusted Intake Triage Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.ReadOnly,
                    SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
                    PermittedTools = DefaultAgentTools.ReadOnly
                }
            }
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (Exception ex)
            {
                // Justification: Best-effort test temp directory cleanup
                _ = ex.Message;
            }
        }
    }

    [Fact]
    public async Task TriageExternalReportAsync_EnforcesReadOnlyAndRestrictedTier_CreatesSuggestedTask()
    {
        // Arrange
        AgentSessionContext? capturedContext = null;
        _fakeHarness.OnRunStage = ctx =>
        {
            capturedContext = ctx;
            ctx.PipelineContext?.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
            {
                new("Fix null reference in Parser", "Check for null tokens before accessing properties", "bug")
            });
            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 50, 50, 100),
                summary: "Triage completed, proposed 1 task.");
        };

        var triageService = new NightCodeForge.Agents.Tests.Fixtures.TestTriageServiceBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            HarnessRegistry = _harnessRegistry,
            MarkdownSyncService = _markdownSyncService
        }.Build();

        var request = new TriageReportRequest(
            Title: "Crash when input file is empty",
            Description: "Parser throws NullReferenceException on empty files.",
            BaseBranch: "main");

        // Act
        var result = await triageService.TriageExternalReportAsync(_workspace, request);

        // Assert
        Assert.NotNull(capturedContext);
        Assert.Equal(AgentMode.ReadOnly, capturedContext.RequiredMode);
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, capturedContext.Profile.SecurityTier);
        Assert.Equal("main", capturedContext.ForgeTask.BaseBranch);
        Assert.Equal(TaskPriority.Critical, capturedContext.ForgeTask.Priority);

        Assert.Single(result);
        var created = result[0];
        Assert.Equal("Fix null reference in Parser", created.Title);
        Assert.Equal(TaskOverallStatus.Suggested, created.Status);
        Assert.Equal(MergeMode.ManualReviewRequired, created.MergeMode);
        Assert.Equal("main", created.BaseBranch);

        // Verify task was persisted in repository
        var inRepo = await _taskRepo.GetByIdAsync(created.Id);
        Assert.NotNull(inRepo);
        Assert.Equal(TaskOverallStatus.Suggested, inRepo.Status);
    }

    [Fact]
    public async Task TriageExternalReportAsync_MirrorsProposedTasksToDocsTasksFolder()
    {
        // Arrange
        _fakeHarness.OnRunStage = ctx =>
        {
            ctx.PipelineContext?.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
            {
                new("Add timeout support to HTTP client", "Allow configuring connection and response timeout", "feature")
            });
            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 50, 50, 100),
                summary: "Proposed feature task.");
        };

        var triageService = new NightCodeForge.Agents.Tests.Fixtures.TestTriageServiceBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            HarnessRegistry = _harnessRegistry,
            MarkdownSyncService = _markdownSyncService
        }.Build();

        var request = new TriageReportRequest(
            Title: "Configurable HTTP Timeout",
            Description: "Need configurable timeout for slow connections.");

        // Act
        var result = await triageService.TriageExternalReportAsync(_workspace, request);

        // Assert
        Assert.Single(result);
        var docsTasksDir = Path.Combine(_tempDir, "docs", "tasks");
        Assert.True(Directory.Exists(docsTasksDir));

        var mdFiles = Directory.GetFiles(docsTasksDir, "*.md");
        Assert.Single(mdFiles);

        var fileContent = await File.ReadAllTextAsync(mdFiles[0]);
        Assert.Contains("Add timeout support to HTTP client", fileContent);
    }

    [Fact]
    public async Task TriageExternalReportAsync_WhenNoProposalsMade_ReturnsEmptyList()
    {
        // Arrange
        _fakeHarness.OnRunStage = ctx =>
        {
            // No proposals set in DataBag
            return AgentRunResult.Completed(
                new AgentRunMetrics(50, 20, 20, 40),
                summary: "Report analyzed: already resolved in current codebase.");
        };

        var triageService = new NightCodeForge.Agents.Tests.Fixtures.TestTriageServiceBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            HarnessRegistry = _harnessRegistry,
            MarkdownSyncService = _markdownSyncService
        }.Build();

        var request = new TriageReportRequest(
            Title: "Invalid report",
            Description: "Already fixed.");

        // Act
        var result = await triageService.TriageExternalReportAsync(_workspace, request);

        // Assert
        Assert.Empty(result);
        var allTasks = await _taskRepo.GetByWorkspaceIdAsync(_workspace.Id);
        Assert.Empty(allTasks);
    }

    [Fact]
    public async Task TriageExternalReportAsync_ThrowsAgentProfileNotFoundException_WhenNoTriageProfileConfigured()
    {
        var emptyWorkspace = new Workspace
        {
            Id = 6,
            Name = "Empty Workspace",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        };
        await _workspaceRepo.CreateAsync(emptyWorkspace);

        var triageService = new NightCodeForge.Agents.Tests.Fixtures.TestTriageServiceBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            HarnessRegistry = _harnessRegistry,
            MarkdownSyncService = _markdownSyncService
        }.Build();

        var request = new TriageReportRequest(
            Title: "Crash when input file is empty",
            Description: "Parser throws NullReferenceException on empty files.",
            BaseBranch: "main");

        var ex = await Assert.ThrowsAsync<AgentProfileNotFoundException>(() =>
            triageService.TriageExternalReportAsync(emptyWorkspace, request));
        Assert.Equal(TriageStage.DefaultStageId, ex.ProfileId);
        Assert.Equal(6, ex.WorkspaceId);
    }

    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId => "crucible";
        public Func<AgentSessionContext, AgentRunResult>? OnRunStage { get; set; }

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            var res = OnRunStage != null
                ? OnRunStage(context)
                : AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Default run");
            return Task.FromResult(res);
        }
    }

    private class FakeHarnessRegistry : IHarnessRegistry
    {
        private readonly IAgentHarness _harness;
        public FakeHarnessRegistry(IAgentHarness harness) => _harness = harness;
        public IAgentHarness GetHarness(string harnessId) => _harness;
        public void RegisterHarness(IAgentHarness harness) { }
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness)
        {
            harness = _harness;
            return true;
        }
        public IEnumerable<IAgentHarness> GetAllHarnesses() => new[] { _harness };
    }

    private class FakeMarkdownSyncService : ITaskMarkdownSyncService
    {
        public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) =>
            $"---\ntitle: \"{task.Title}\"\nstatus: {task.Status}\n---\n\n{task.Description}";

        public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) =>
            (new TaskFrontmatter(), string.Empty);

        public string ComputeContentHash(string content) => string.Empty;

        public Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(workspacePath, "task.md"));

        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StartWatching(int workspaceId, string workspacePath) { }
        public void StopWatching(int workspaceId) { }
        public void Dispose() { }
    }
}
