using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Triage;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class Step125BBacklogDifficultyToolTests : IDisposable
{
    private const int WorkspaceId = 10;
    private readonly string _tempDir;
    private readonly InMemoryTaskRepository _repository = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepository = new();
    private readonly Mock<ITaskMarkdownSyncService> _markdownSync = new();
    private readonly Mock<IAgentActivityStreamer> _streamer = new();
    private readonly Mock<IGitHubPullRequestDeliveryService> _prDelivery = new();
    private readonly WorkspaceLockProvider _locks = new();
    private readonly BacklogMutationToolService _service;
    private readonly BacklogToolScope _scope;

    public Step125BBacklogDifficultyToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_125b_agents_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _scope = new BacklogToolScope(WorkspaceId, _tempDir);

        _workspaceRepository.CreateAsync(new Workspace
        {
            Id = WorkspaceId,
            Name = "Agents Test Workspace",
            RepositoryPath = _tempDir,
            DefaultBaseBranch = "main"
        }).GetAwaiter().GetResult();

        _service = new BacklogMutationToolService(
            _repository,
            new DependencyResolutionEngine(new Mock<ITaskDependencyStore>().Object),
            _markdownSync.Object,
            _locks,
            _streamer.Object,
            new BacklogMergeModeValidator(
                _workspaceRepository,
                _prDelivery.Object,
                Options.Create(new SecurityOptions())),
            new InMemoryTaskAttachmentRepository(_repository),
            Mock.Of<ITaskLifecycleMutationCoordinator>(),
            NullLogger<BacklogMutationToolService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch (Exception ex)
        {
            _ = ex.Message;
        }
    }

    [Fact]
    public async Task ProposeTasksAsync_GetTaskAsync_AndEditTaskAsync_HandleDifficultyEndToEnd()
    {
        var module = new BacklogManagementToolModule(new CanonicalToolScope(_tempDir, WorkspaceId: WorkspaceId));
        module.BacklogMutationService = _service;

        // 1. Propose single task with explicit "difficult" difficulty
        var proposeResult = await module.ProposeTasksAsync(
            title: "Build Distributed Lock",
            description: "Need lock fencing tokens",
            priority: TaskPriority.High,
            difficulty: "difficult");

        using var createdDoc = JsonDocument.Parse(proposeResult);
        var taskId = createdDoc.RootElement.GetProperty("id").GetInt32();
        Assert.Equal("Difficult", createdDoc.RootElement.GetProperty("difficulty").GetString());

        var stored = await _repository.GetByIdAsync(taskId);
        Assert.NotNull(stored);
        Assert.Equal(TaskDifficulty.Difficult, stored!.Difficulty);

        // 2. Edit difficulty to "expert"
        var editResult = await module.EditTaskAsync(taskId, difficulty: "expert");
        Assert.Contains("difficulty Difficult -> Expert", editResult);

        var updated = await _repository.GetByIdAsync(taskId);
        Assert.Equal(TaskDifficulty.Expert, updated!.Difficulty);

        var history = await _repository.GetHistoryAsync(taskId);
        Assert.Contains(history, h => h.Difficulty == TaskDifficulty.Expert && (h.ChangeReason?.Contains("difficulty Difficult -> Expert") ?? false));

        // 3. GetTask returns updated difficulty
        var getResult = await module.GetTaskAsync(taskId);
        using var getDoc = JsonDocument.Parse(getResult);
        Assert.Equal("Expert", getDoc.RootElement.GetProperty("difficulty").GetString());
    }

    [Fact]
    public async Task ProposeAndEditTask_WithInvalidDifficulty_ThrowsArgumentException()
    {
        var created = await _repository.CreateAsync(new ForgeTask
        {
            WorkspaceId = WorkspaceId,
            Title = "Existing",
            Status = TaskOverallStatus.Suggested
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.EditTaskAsync(_scope, new TaskEditRequest(created.Id, Difficulty: "impossible"), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.EditTaskAsync(_scope, new TaskEditRequest(created.Id, Difficulty: "99"), CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ProposeTasksAsync(_scope, new[]
            {
                new TaskProposal("Bad Task", "Desc", difficulty: "nightmare")
            }, CancellationToken.None));
    }

    [Fact]
    public async Task ProposeTasksAsync_JsonBatch_ParsesDifficultyPropertyPerProposal()
    {
        var module = new BacklogManagementToolModule(new CanonicalToolScope(_tempDir, WorkspaceId: WorkspaceId));
        var jsonBatch = """
        [
          { "title": "Task A", "description": "Standard item" },
          { "title": "Task B", "description": "Complex item", "difficulty": "expert" }
        ]
        """;

        await module.ProposeTasksAsync(tasks: jsonBatch);

        Assert.Equal(2, module.PendingProposals.Count);
        Assert.Null(module.PendingProposals[0].Difficulty);
        Assert.Equal("expert", module.PendingProposals[1].Difficulty);
    }

    private sealed class StaticJsonChatClient : IChatClient
    {
        private readonly string _response;
        public StaticJsonChatClient(string response) => _response = response;
        public ChatClientMetadata Metadata => new("FakeClient");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task IdeationService_ConverseAsync_CapturesDifficultyFieldInProposals()
    {
        var ideationJson = """
        Here are the tasks:
        ```json
        [
          {
            "title": "Design Auth Pipeline",
            "description": "Implement OAuth2 + PKCE",
            "priority": 2,
            "difficulty": "expert"
          }
        ]
        ```
        """;

        var service = new TestIdeationServiceBuilder
        {
            BaseChatClient = new StaticJsonChatClient(ideationJson),
            PathResolver = TestPathResolver.Create()
        }.Build();

        var ws = await _workspaceRepository.GetByIdAsync(WorkspaceId);
        var ideationProfile = AgentProfile.CreateDefaultIdeationProfile(WorkspaceId);
        ws!.AgentProfiles.Add(ideationProfile);

        var result = await service.ConverseAsync(ws, "Plan auth");
        var proposal = Assert.Single(result.ProposedTasks);
        Assert.Equal("expert", proposal.Difficulty);
    }

    private sealed class TriageProposalHarness : IAgentHarness
    {
        public string HarnessId => StandardHarnessIds.Crucible;

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            context.PipelineContext?.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
            {
                new("Triaged Difficult Task", "Details", difficulty: "difficult"),
                new("Triaged Invalid Diff Fallback", "Details", difficulty: "unrecognized")
            });

            return Task.FromResult(AgentRunResult.Completed(
                new AgentRunMetrics(50, 20, 20, 40),
                summary: "Triaged"));
        }
    }

    [Fact]
    public async Task TriageService_MapsProposedDifficultyToSuggestedTasks()
    {
        var harness = new TriageProposalHarness();
        var registry = new HarnessRegistry();
        registry.RegisterHarness(harness);

        var triageProfile = AgentProfile.CreateStandardGlobalProfiles("test-model")
            .Single(p => p.Id == StandardStageIds.Triage);
        _workspaceRepository.SeedGlobalProfiles([triageProfile]);

        var triage = new TestTriageServiceBuilder
        {
            TaskRepository = _repository,
            WorkspaceRepository = _workspaceRepository,
            HarnessRegistry = registry,
            MarkdownSyncService = _markdownSync.Object
        }.Build();

        var workspace = await _workspaceRepository.GetByIdAsync(WorkspaceId);
        var createdTasks = await triage.TriageExternalReportAsync(
            workspace!,
            new TriageReportRequest("Bug report", "Bug report body", "main"),
            CancellationToken.None);

        Assert.Equal(2, createdTasks.Count);
        Assert.Equal(TaskDifficulty.Difficult, createdTasks[0].Difficulty);
        Assert.Equal(TaskDifficulty.Standard, createdTasks[1].Difficulty);
    }

    [Fact]
    public async Task ProposeTasksAsync_TopLevelDifficultyFallback_AndAliasesWorkDirectlyAndViaDispatcher()
    {
        var module = new BacklogManagementToolModule(new CanonicalToolScope(_tempDir, WorkspaceId: WorkspaceId));
        var jsonBatch = """
        [
          { "title": "Task Inheriting Top-Level", "description": "Fallback applies" },
          { "title": "Task Explicit Standard", "description": "Explicit stays", "difficulty": "standard" },
          { "title": "Task Snake Alias", "description": "Alias works", "task_difficulty": "expert" },
          { "title": "Task Complexity Alias", "description": "Complexity works", "complexity": "difficult" }
        ]
        """;

        await module.ProposeTasksAsync(tasks: jsonBatch, difficulty: "difficult");

        Assert.Equal(4, module.PendingProposals.Count);
        Assert.Equal("difficult", module.PendingProposals[0].Difficulty);
        Assert.Equal("standard", module.PendingProposals[1].Difficulty);
        Assert.Equal("expert", module.PendingProposals[2].Difficulty);
        Assert.Equal("difficult", module.PendingProposals[3].Difficulty);

        var tools = TestCanonicalToolsBuilder.Create(
            _tempDir,
            pathResolver: TestPathResolver.Create(),
            isReadOnly: true,
            workspaceId: WorkspaceId);
        var serviceMock = new Mock<IBacklogMutationToolService>();
        serviceMock.Setup(s => s.EditTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<TaskEditRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("edited");
        serviceMock.Setup(s => s.ProposeTasksAsync(It.IsAny<BacklogToolScope>(), It.IsAny<IReadOnlyList<TaskProposal>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("[{\"id\":201,\"title\":\"Tier Alias\"}]");
        tools.BacklogMutationService = serviceMock.Object;

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync(
            CanonicalToolNames.EditTask,
            "{\"task_id\": 12, \"complexity\": \"expert\"}");

        serviceMock.Verify(s => s.EditTaskAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<TaskEditRequest>(r => r.TaskId == 12 && r.Difficulty == "expert"),
            It.IsAny<CancellationToken>()), Times.Once);

        await dispatcher.ExecuteAsync(
            CanonicalToolNames.ProposeTasks,
            "{\"tasks\": [{\"title\": \"Tier Alias\", \"description\": \"Details\", \"tier\": \"difficult\"}]}");

        serviceMock.Verify(s => s.ProposeTasksAsync(
            It.IsAny<BacklogToolScope>(),
            It.Is<IReadOnlyList<TaskProposal>>(list => list.Count == 1 && list[0].Difficulty == "difficult"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
