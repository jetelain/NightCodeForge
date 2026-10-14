using NightCodeForge.Agents.Triage;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class TriageServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly FakeHarnessRegistry _harnessRegistry;
    private readonly FakeAgentHarness _fakeHarness;
    private readonly FakeMarkdownSyncService _markdownSyncService;
    private readonly Workspace _workspace;

    public TriageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-triage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _issueRepo = new InMemoryIssueRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _fakeHarness = new FakeAgentHarness();
        _harnessRegistry = new FakeHarnessRegistry(_fakeHarness);
        _markdownSyncService = new FakeMarkdownSyncService();

        _workspace = new Workspace
        {
            Id = 5,
            Name = "Triage Test Workspace",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TriageExternalReportAsync_EnforcesReadOnlyAndRestrictedTier_CreatesSuggestedIssue()
    {
        // Arrange
        AgentSessionContext? capturedContext = null;
        _fakeHarness.OnRunStage = ctx =>
        {
            capturedContext = ctx;
            ctx.PipelineContext?.DataBag.Set("PendingProposals", new List<IssueProposal>
            {
                new("Fix null reference in Parser", "Check for null tokens before accessing properties", "bug")
            });
            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 50, 50, 100),
                summary: "Triage completed, proposed 1 issue.");
        };

        var triageService = new TriageService(
            _issueRepo,
            _workspaceRepo,
            _harnessRegistry,
            _markdownSyncService);

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
        Assert.Equal("main", capturedContext.Issue.BaseBranch);

        Assert.Single(result);
        var created = result[0];
        Assert.Equal("Fix null reference in Parser", created.Title);
        Assert.Equal(IssueOverallStatus.Suggested, created.Status);
        Assert.Equal(MergeMode.ManualReviewRequired, created.MergeMode);
        Assert.Equal("main", created.BaseBranch);

        // Verify issue was persisted in repository
        var inRepo = await _issueRepo.GetByIdAsync(created.Id);
        Assert.NotNull(inRepo);
        Assert.Equal(IssueOverallStatus.Suggested, inRepo.Status);
    }

    [Fact]
    public async Task TriageExternalReportAsync_MirrorsProposedIssuesToDocsIssuesFolder()
    {
        // Arrange
        _fakeHarness.OnRunStage = ctx =>
        {
            ctx.PipelineContext?.DataBag.Set("PendingProposals", new List<IssueProposal>
            {
                new("Add timeout support to HTTP client", "Allow configuring connection and response timeout", "feature")
            });
            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 50, 50, 100),
                summary: "Proposed feature issue.");
        };

        var triageService = new TriageService(
            _issueRepo,
            _workspaceRepo,
            _harnessRegistry,
            _markdownSyncService);

        var request = new TriageReportRequest(
            Title: "Configurable HTTP Timeout",
            Description: "Need configurable timeout for slow connections.");

        // Act
        var result = await triageService.TriageExternalReportAsync(_workspace, request);

        // Assert
        Assert.Single(result);
        var docsIssuesDir = Path.Combine(_tempDir, "docs", "issues");
        Assert.True(Directory.Exists(docsIssuesDir));

        var mdFiles = Directory.GetFiles(docsIssuesDir, "*.md");
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

        var triageService = new TriageService(
            _issueRepo,
            _workspaceRepo,
            _harnessRegistry,
            _markdownSyncService);

        var request = new TriageReportRequest(
            Title: "Invalid report",
            Description: "Already fixed.");

        // Act
        var result = await triageService.TriageExternalReportAsync(_workspace, request);

        // Assert
        Assert.Empty(result);
        var allIssues = await _issueRepo.GetByWorkspaceIdAsync(_workspace.Id);
        Assert.Empty(allIssues);
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

    private class FakeMarkdownSyncService : IIssueMarkdownSyncService
    {
        public string GenerateMarkdown(Issue issue, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) =>
            $"---\ntitle: \"{issue.Title}\"\nstatus: {issue.Status}\n---\n\n{issue.Description}";

        public (IssueFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) =>
            (new IssueFrontmatter(), string.Empty);

        public string ComputeContentHash(string content) => string.Empty;

        public Task<string> SyncIssueToDiskAsync(Issue issue, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.Combine(workspacePath, "issue.md"));

        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveOrCleanupIssueFileAsync(Issue issue, string workspacePath, bool archive = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StartWatching(int workspaceId, string workspacePath) { }
        public void StopWatching(int workspaceId) { }
        public void Dispose() { }
    }
}
