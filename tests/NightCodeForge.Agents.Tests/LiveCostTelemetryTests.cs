using System.Diagnostics;
using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class LiveCostTelemetryTests : IDisposable
{
    private readonly string _tempDir;

    public LiveCostTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "live-cost-telemetry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public async Task CrucibleAgentHarness_OnStageCompleted_PublishesCostMetadataInAgentActivityEvent()
    {
        var mockStreamer = new Mock<IAgentActivityStreamer>();
        var recordedEvents = new List<AgentActivityEvent>();
        mockStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => recordedEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { new TextContent("Done with stage.\n<approved>All done</approved>") }
                }
            }.ToAsyncEnumerable());

        var expectedSessionId = LlmSessionIds.ForStage(42, StandardStageIds.Implementation);

        var finalUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 2,
            InputTokens: 1200,
            CachedInputTokens: 800,
            OutputTokens: 300,
            ElapsedMs: 1500.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.162,
            ElectricityCost: 0.007,
            Savings: 0.120,
            ActualCloudCost: 0.035,
            Cost: 0.042,
            Currency: "$",
            PerTarget: null);

        int crucibleCallCount = 0;
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(32768);
        mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                crucibleCallCount++;
                return crucibleCallCount == 1 ? null : finalUsage;
            });
        mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = mockChatClient.Object,
            PathResolver = TestPathResolver.Create(),
            ActivityStreamer = mockStreamer.Object,
            GatewayService = mockGateway.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 42, Title = "Telemetry Test Task", Description = "Testing cost broadcast" },
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            Profile = new AgentProfile { Id = "coder", Mode = AgentMode.Full },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var stageCompletedEvent = recordedEvents.FirstOrDefault(e => e.Kind == AgentActivityKind.StageCompleted);
        Assert.NotNull(stageCompletedEvent);
        Assert.NotNull(stageCompletedEvent.Metadata);

        var meta = stageCompletedEvent.Metadata;
        Assert.True(meta.ContainsKey(AgentActivityMetadataKeys.Cost), "Cost metadata key missing");
        Assert.Equal(0.042, meta[AgentActivityMetadataKeys.Cost]);
        Assert.Equal("$", meta[AgentActivityMetadataKeys.Currency]);
        Assert.Equal(0.007, meta[AgentActivityMetadataKeys.ElectricityCost]);
        Assert.Equal(0.035, meta[AgentActivityMetadataKeys.ActualCloudCost]);
        Assert.Equal(0.120, meta[AgentActivityMetadataKeys.CloudSavings]);
        Assert.Equal(800, meta[AgentActivityMetadataKeys.CachedInputTokens]);
    }

    [Fact]
    public async Task IdeationService_OnStageCompleted_PublishesCostMetadataInAgentActivityEvent()
    {
        var mockStreamer = new Mock<IAgentActivityStreamer>();
        var recordedEvents = new List<AgentActivityEvent>();
        mockStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => recordedEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { new TextContent("I suggest building a login system.") }
                }
            }.ToAsyncEnumerable());

        var finalUsage = new RouterSessionUsage(
            SessionId: "ideation-sess-1",
            Requests: 1,
            InputTokens: 800,
            CachedInputTokens: 400,
            OutputTokens: 200,
            ElapsedMs: 800.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.100,
            ElectricityCost: 0.003,
            Savings: 0.080,
            ActualCloudCost: 0.017,
            Cost: 0.020,
            Currency: "$",
            PerTarget: null);

        int ideationCallCount = 0;
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(32768);
        mockGateway.Setup(g => g.GetSessionUsageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                ideationCallCount++;
                return ideationCallCount == 1 ? null : finalUsage;
            });
        mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var ideationService = new TestIdeationServiceBuilder
        {
            BaseChatClient = mockChatClient.Object,
            PathResolver = TestPathResolver.Create(),
            ActivityStreamer = mockStreamer.Object,
            GatewayService = mockGateway.Object
        }.Build();

        var workspace = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = _tempDir };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(1));

        var updates = new List<IdeationStreamUpdate>();
        await foreach (var update in ideationService.ConverseStreamingAsync(
            workspace,
            "Hello architect"))
        {
            updates.Add(update);
        }

        var stageCompletedEvent = recordedEvents.FirstOrDefault(e => e.Kind == AgentActivityKind.StageCompleted);
        Assert.NotNull(stageCompletedEvent);
        Assert.NotNull(stageCompletedEvent.Metadata);

        var meta = stageCompletedEvent.Metadata;
        Assert.True(meta.ContainsKey(AgentActivityMetadataKeys.Cost), "Cost metadata key missing");
        Assert.Equal(0.020, meta[AgentActivityMetadataKeys.Cost]);
        Assert.Equal("$", meta[AgentActivityMetadataKeys.Currency]);
        Assert.Equal(0.003, meta[AgentActivityMetadataKeys.ElectricityCost]);
        Assert.Equal(0.017, meta[AgentActivityMetadataKeys.ActualCloudCost]);
        Assert.Equal(0.080, meta[AgentActivityMetadataKeys.CloudSavings]);
        Assert.Equal(400, meta[AgentActivityMetadataKeys.CachedInputTokens]);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenNoCostData_DoesNotPublishCostMetadataInAgentActivityEvent()
    {
        var mockStreamer = new Mock<IAgentActivityStreamer>();
        var recordedEvents = new List<AgentActivityEvent>();
        mockStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => recordedEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var mockChatClient = new Mock<IChatClient>();
        mockChatClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate
                {
                    Contents = { new TextContent("Done with stage.\n<approved>All done</approved>") }
                }
            }.ToAsyncEnumerable());

        var expectedSessionId = LlmSessionIds.ForStage(43, StandardStageIds.Implementation);

        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(32768);
        mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouterSessionUsage?)null);
        mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = mockChatClient.Object,
            PathResolver = TestPathResolver.Create(),
            ActivityStreamer = mockStreamer.Object,
            GatewayService = mockGateway.Object
        }.Build();

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 43, Title = "Unpriced Test Task" },
            Workspace = new Workspace { Id = 1, RepositoryPath = _tempDir },
            Profile = new AgentProfile { Id = "coder", Mode = AgentMode.Full },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            WorkspacePath = _tempDir
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var stageCompletedEvent = recordedEvents.FirstOrDefault(e => e.Kind == AgentActivityKind.StageCompleted);
        Assert.NotNull(stageCompletedEvent);
        Assert.True(stageCompletedEvent.Metadata == null || !stageCompletedEvent.Metadata.ContainsKey(AgentActivityMetadataKeys.Cost));
    }
}
