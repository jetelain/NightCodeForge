using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class CrucibleConversationCoordinatorLifecycleTests
{
    private readonly InMemoryTaskRepository _repository = new();
    private readonly Mock<ILlmGatewayService> _mockGateway = new();
    private readonly SessionTranscriptService _transcriptService = new();

    private CrucibleConversationCoordinator CreateCoordinator() =>
        new(_repository, _transcriptService, _mockGateway.Object, NullLogger<CrucibleConversationCoordinator>.Instance);

    private static AgentSessionContext CreateContext(int taskId = 42, string stageId = "stage-1", string? workspacePath = null)
    {
        var task = new ForgeTask { Id = taskId, Title = "Test Task" };
        var profile = new AgentProfile
        {
            Id = "test-profile",
            Name = "Test Profile",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "qwen2.5-coder:32b"
        };
        return new AgentSessionContext
        {
            ForgeTask = task,
            StageId = stageId,
            Profile = profile,
            Workspace = workspacePath != null ? new Workspace { Id = 1, Name = "Test WS", RepositoryPath = workspacePath } : null,
            WorkspacePath = workspacePath ?? string.Empty
        };
    }

    [Fact]
    public async Task BeginAsync_CapturesBaselineSnapshotForDerivedStageSessionId()
    {
        var context = CreateContext(taskId: 10, stageId: "implementation");
        var expectedSessionId = LlmSessionIds.ForStage(10, "implementation");

        var baselineUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 3,
            InputTokens: 1200,
            CachedInputTokens: 800,
            OutputTokens: 300,
            ElapsedMs: 2500.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.05,
            ElectricityCost: 0.002,
            Savings: 0.048,
            ActualCloudCost: 0.0,
            Cost: 0.002,
            Currency: "$",
            PerTarget: null);

        _mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(baselineUsage);

        var coordinator = CreateCoordinator();
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        Assert.Equal(expectedSessionId, state.SessionId);
        Assert.NotNull(state.BaselineUsage);
        Assert.Equal(1200, state.BaselineUsage.InputTokens);
        _mockGateway.Verify(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(AgentRunStatus.Completed)]
    [InlineData(AgentRunStatus.WaitingForHuman)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.Interrupted)]
    public async Task SaveTranscriptAsync_AcrossAllTerminationPaths_CapturesFinalUsageAccumulatesCostAndReleasesAffinity(
        AgentRunStatus status)
    {
        var context = CreateContext(taskId: 15, stageId: "review");
        var expectedSessionId = LlmSessionIds.ForStage(15, "review");

        var baselineUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 2,
            InputTokens: 1000,
            CachedInputTokens: 600,
            OutputTokens: 200,
            ElapsedMs: 1500.0,
            LastSeenAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            CloudValue: 0.04,
            ElectricityCost: 0.002,
            Savings: 0.038,
            ActualCloudCost: 0.0,
            Cost: 0.002,
            Currency: "$",
            PerTarget: null);

        var finalUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 5,
            InputTokens: 2500,
            CachedInputTokens: 1600,
            OutputTokens: 500,
            ElapsedMs: 4000.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.10,
            ElectricityCost: 0.005,
            Savings: 0.095,
            ActualCloudCost: 0.001,
            Cost: 0.006,
            Currency: "$",
            PerTarget: null);

        _mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(baselineUsage);

        var coordinator = CreateCoordinator();
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        // When ending, final usage is returned
        _mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(finalUsage);
        _mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var request = new CrucibleTranscriptRequest(
                Context: context,
                WorkspacePath: tempDir,
                ChatHistory: [],
                ElapsedMs: 2500,
                PromptTokens: 1500,
                CompletionTokens: 300,
                Status: status,
                Summary: "Finished stage run",
                TracingClient: null,
                HarnessOptions: null,
                ActiveSkills: null);

            var transcriptName = await coordinator.SaveTranscriptAsync(state, request, CancellationToken.None);

            // Assert delta computed on state
            Assert.NotNull(state.LastUsageDelta);
            Assert.Equal(1500, state.LastUsageDelta.InputTokens);
            Assert.Equal(1000, state.LastUsageDelta.CachedInputTokens);
            Assert.Equal(300, state.LastUsageDelta.OutputTokens);
            Assert.Equal(0.004, state.LastUsageDelta.Cost!.Value, precision: 6);
            Assert.Equal(0.001, state.LastUsageDelta.ActualCloudCost!.Value, precision: 6);
            Assert.Equal(0.003, state.LastUsageDelta.ElectricityCost!.Value, precision: 6);
            Assert.Equal(0.057, state.LastUsageDelta.CloudSavings!.Value, precision: 6);
            Assert.Equal("$", state.LastUsageDelta.Currency);

            // Assert Conversation entity was updated
            var conv = await _repository.GetConversationWithTurnsAsync(state.Conversation.Id);
            Assert.NotNull(conv);
            Assert.Equal(1500, conv.InputTokens);
            Assert.Equal(300, conv.OutputTokens);
            Assert.Equal(1000, conv.CachedInputTokens);
            Assert.Equal(0.004, conv.Cost!.Value, precision: 6);
            Assert.Equal(0.001, conv.ActualCloudCost!.Value, precision: 6);
            Assert.Equal(0.003, conv.ElectricityCost!.Value, precision: 6);
            Assert.Equal(0.057, conv.CloudSavings!.Value, precision: 6);
            Assert.Equal("$", conv.Currency);

            // Assert affinity release was called
            _mockGateway.Verify(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void TaskOrchestrator_MapsAgentRunMetricsCostFieldsToStageExecutionRecord()
    {
        var metrics = new AgentRunMetrics(
            DurationMs: 1200,
            PromptTokens: 3000,
            CompletionTokens: 500,
            TotalTokens: 3500,
            CachedInputTokens: 2000,
            Cost: 0.0075,
            ActualCloudCost: 0.002,
            ElectricityCost: 0.0055,
            CloudSavings: 0.045,
            Currency: "$");

        var agentResult = AgentRunResult.Completed(metrics, summary: "Implementation completed");

        // Use reflection or invoke MapAgentRunResultToStageResult
        var stageMock = new Mock<IWorkflowStage>();
        stageMock.Setup(s => s.StageId).Returns("implementation");
        stageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);

        var profile = new AgentProfile
        {
            Id = "prof-1",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "qwen2.5-coder:32b"
        };

        var method = typeof(TaskOrchestrator).GetMethod("MapAgentRunResultToStageResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var stageResult = (StageExecutionResult)method.Invoke(null, [agentResult, stageMock.Object, profile, DateTime.UtcNow.AddSeconds(-2)])!;

        Assert.NotNull(stageResult.ExecutionRecord);
        Assert.Equal(2000, stageResult.ExecutionRecord.CachedInputTokens);
        Assert.Equal(0.0075, stageResult.ExecutionRecord.Cost);
        Assert.Equal(0.002, stageResult.ExecutionRecord.ActualCloudCost);
        Assert.Equal(0.0055, stageResult.ExecutionRecord.ElectricityCost);
        Assert.Equal(0.045, stageResult.ExecutionRecord.CloudSavings);
        Assert.Equal("$", stageResult.ExecutionRecord.Currency);
    }

    [Fact]
    public async Task CrucibleAgentHarness_RunStageAsync_PropagatesSessionUsageDeltaAndFallbackTokensToAgentRunMetrics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-harness-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var expectedSessionId = LlmSessionIds.ForStage(77, StandardStageIds.Implementation);
            var finalUsage = new RouterSessionUsage(
                SessionId: expectedSessionId,
                Requests: 4,
                InputTokens: 4200,
                CachedInputTokens: 3100,
                OutputTokens: 650,
                ElapsedMs: 3200.0,
                LastSeenAt: DateTimeOffset.UtcNow,
                CloudValue: 0.08,
                ElectricityCost: 0.004,
                Savings: 0.076,
                ActualCloudCost: 0.001,
                Cost: 0.005,
                Currency: "$",
                PerTarget: null);

            _mockGateway.SetupSequence(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((RouterSessionUsage?)null)
                .ReturnsAsync(finalUsage);
            _mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            _mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(131072);

            var chatClientMock = new Mock<IChatClient>();
            chatClientMock
                .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .Returns(new[]
                {
                    new ChatResponseUpdate(ChatRole.Assistant, "Implementation completed. <summary>Done</summary>")
                }.ToAsyncEnumerable());

            var harness = new TestCrucibleHarnessBuilder
            {
                TaskRepository = _repository,
                GatewayService = _mockGateway.Object,
                BaseChatClient = chatClientMock.Object
            }.Build();

            var context = CreateContext(taskId: 77, stageId: StandardStageIds.Implementation, workspacePath: tempDir);

            var result = await harness.RunStageAsync(context, CancellationToken.None);

            Assert.Equal(AgentRunStatus.Completed, result.Status);
            Assert.Equal(4200, result.Metrics.PromptTokens);
            Assert.Equal(650, result.Metrics.CompletionTokens);
            Assert.Equal(4850, result.Metrics.TotalTokens);
            Assert.Equal(3100, result.Metrics.CachedInputTokens);
            Assert.Equal(0.005, result.Metrics.Cost!.Value, precision: 6);
            Assert.Equal(0.001, result.Metrics.ActualCloudCost!.Value, precision: 6);
            Assert.Equal(0.004, result.Metrics.ElectricityCost!.Value, precision: 6);
            Assert.Equal(0.076, result.Metrics.CloudSavings!.Value, precision: 6);
            Assert.Equal("$", result.Metrics.Currency);
            _mockGateway.Verify(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveTranscriptAsync_WhenNonRouterProviderReturnsNullUsage_AccumulatesSdkTokensOntoConversation()
    {
        var context = CreateContext(taskId: 88, stageId: "implementation");
        var expectedSessionId = LlmSessionIds.ForStage(88, "implementation");

        _mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouterSessionUsage?)null);
        _mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var coordinator = CreateCoordinator();
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-nonrouter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var request = new CrucibleTranscriptRequest(
                Context: context,
                WorkspacePath: tempDir,
                ChatHistory: [],
                ElapsedMs: 1200,
                PromptTokens: 2100,
                CompletionTokens: 450,
                Status: AgentRunStatus.Completed,
                Summary: "Completed on standalone Ollama",
                TracingClient: null,
                HarnessOptions: null,
                ActiveSkills: null);

            await coordinator.SaveTranscriptAsync(state, request, CancellationToken.None);

            var conv = await _repository.GetConversationWithTurnsAsync(state.Conversation.Id);
            Assert.NotNull(conv);
            Assert.Equal(2100, conv.InputTokens);
            Assert.Equal(450, conv.OutputTokens);
            Assert.Null(conv.Cost);
            Assert.Null(conv.Currency);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveTranscriptAsync_WhenRouterReportsHigherTokensThanSdk_ReconcilesConversationAndStateWithHigherRouterTotal()
    {
        var context = CreateContext(taskId: 89, stageId: "implementation");
        var expectedSessionId = LlmSessionIds.ForStage(89, "implementation");

        var finalUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 3,
            InputTokens: 5400,
            CachedInputTokens: 3200,
            OutputTokens: 900,
            ElapsedMs: 2100.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.05,
            ElectricityCost: 0.002,
            Savings: 0.048,
            ActualCloudCost: 0.0,
            Cost: 0.002,
            Currency: "$",
            PerTarget: null);

        _mockGateway.SetupSequence(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouterSessionUsage?)null)
            .ReturnsAsync(finalUsage);
        _mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var coordinator = CreateCoordinator();
        var state = await coordinator.BeginAsync(context, StandardHarnessIds.Crucible, CancellationToken.None);

        var tempDir = Path.Combine(Path.GetTempPath(), "crucible-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // SDK only reported 1200 prompt / 200 completion (e.g. missed a call), while router recorded 5400 / 900
            var request = new CrucibleTranscriptRequest(
                Context: context,
                WorkspacePath: tempDir,
                ChatHistory: [],
                ElapsedMs: 2100,
                PromptTokens: 1200,
                CompletionTokens: 200,
                Status: AgentRunStatus.Completed,
                Summary: "Completed with router reconciliation",
                TracingClient: null,
                HarnessOptions: null,
                ActiveSkills: null);

            var transcriptName = await coordinator.SaveTranscriptAsync(state, request, CancellationToken.None);
            Assert.NotNull(transcriptName);

            var transcriptPath = Path.Combine(tempDir, "docs", "transcripts", transcriptName);
            var transcriptContent = await File.ReadAllTextAsync(transcriptPath);
            Assert.Contains("5400", transcriptContent);
            Assert.Contains("900", transcriptContent);

            var conv = await _repository.GetConversationWithTurnsAsync(state.Conversation.Id);
            Assert.NotNull(conv);
            Assert.Equal(5400, conv.InputTokens);
            Assert.Equal(900, conv.OutputTokens);
            Assert.Equal(3200, conv.CachedInputTokens);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}

