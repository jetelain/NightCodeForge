using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class IdeationLifecycleTests
{
    [Fact]
    public void ForIdeation_WhenConversationIdProvided_ReturnsDeterministicSessionId()
    {
        var sessionId1 = LlmSessionIds.ForIdeation(workspaceId: 5, conversationId: 123);
        var sessionId2 = LlmSessionIds.ForIdeation(workspaceId: 5, conversationId: 123);

        Assert.Equal("ncf-ideation-ws5-conv123", sessionId1);
        Assert.Equal(sessionId1, sessionId2);
    }

    [Fact]
    public void ForIdeation_WhenConversationIdNull_ReturnsUniqueSessionIds()
    {
        var sessionId1 = LlmSessionIds.ForIdeation(workspaceId: 5, conversationId: null);
        var sessionId2 = LlmSessionIds.ForIdeation(workspaceId: 5, conversationId: null);

        Assert.StartsWith("ncf-ideation-ws5-", sessionId1);
        Assert.StartsWith("ncf-ideation-ws5-", sessionId2);
        Assert.NotEqual(sessionId1, sessionId2);
    }

    [Fact]
    public async Task ConverseStreamingAsync_AccumulatesDeltaWithoutReleasingSessionAffinity()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        var taskRepo = new InMemoryTaskRepository();

        var ws = new Workspace { Id = 7, Name = "Ideation Test WS", RepositoryPath = Path.GetTempPath() };
        ws.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(7));

        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, title: "Test Chat");
        var expectedSessionId = LlmSessionIds.ForIdeation(ws.Id, conv.Id);

        var baselineUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 1,
            InputTokens: 500,
            CachedInputTokens: 200,
            OutputTokens: 100,
            ElapsedMs: 1000.0,
            LastSeenAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CloudValue: 0.02,
            ElectricityCost: 0.001,
            Savings: 0.019,
            ActualCloudCost: 0.0,
            Cost: 0.001,
            Currency: "$",
            PerTarget: null);

        var finalUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 3,
            InputTokens: 1500,
            CachedInputTokens: 800,
            OutputTokens: 300,
            ElapsedMs: 2800.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.06,
            ElectricityCost: 0.003,
            Savings: 0.057,
            ActualCloudCost: 0.0005,
            Cost: 0.0035,
            Currency: "$",
            PerTarget: null);

        mockGateway.SetupSequence(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(baselineUsage)
            .ReturnsAsync(finalUsage);

        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(131072);

        var chatClientMock = new Mock<IChatClient>();
        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[]
            {
                new ChatResponseUpdate(ChatRole.Assistant, "I have analyzed your workspace.")
            }.ToAsyncEnumerable());

        var service = new TestIdeationServiceBuilder
        {
            GatewayService = mockGateway.Object,
            TaskRepository = taskRepo,
            BaseChatClient = chatClientMock.Object
        }.Build();

        IdeationResult? finalResult = null;
        await foreach (var update in service.ConverseStreamingAsync(
            workspace: ws,
            userPrompt: "Explore architecture",
            conversationId: conv.Id,
            cancellationToken: CancellationToken.None))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed)
            {
                finalResult = update.FinalResult;
            }
        }

        // Verify result contains delta
        Assert.NotNull(finalResult);
        Assert.NotNull(finalResult.UsageDelta);
        Assert.Equal(1000, finalResult.UsageDelta.InputTokens);
        Assert.Equal(600, finalResult.UsageDelta.CachedInputTokens);
        Assert.Equal(200, finalResult.UsageDelta.OutputTokens);
        Assert.Equal(0.0025, finalResult.UsageDelta.Cost!.Value, precision: 6);
        Assert.Equal(0.0005, finalResult.UsageDelta.ActualCloudCost!.Value, precision: 6);

        // Verify conversation was updated in repository
        var updatedConv = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(updatedConv);
        Assert.Equal(1000, updatedConv.InputTokens);
        Assert.Equal(200, updatedConv.OutputTokens);
        Assert.Equal(600, updatedConv.CachedInputTokens);
        Assert.Equal(0.0025, updatedConv.Cost!.Value, precision: 6);
        Assert.Equal("$", updatedConv.Currency);

        // Crucial verification: session affinity MUST NOT be released mid-chat!
        mockGateway.Verify(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReleaseConversationSessionAsync_InvokesGatewayAffinityRelease()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        mockGateway.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new TestIdeationServiceBuilder
        {
            GatewayService = mockGateway.Object
        }.Build();

        var released = await service.ReleaseConversationSessionAsync(workspaceId: 8, conversationId: 99);

        Assert.True(released);
        mockGateway.Verify(g => g.ReleaseSessionAffinityAsync("ncf-ideation-ws8-conv99", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConverseStreamingAsync_WhenCancelledMidStream_CapturesAndPersistsPartialDeltaInFinally()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        var taskRepo = new InMemoryTaskRepository();

        var ws = new Workspace { Id = 9, Name = "Cancel Ideation WS", RepositoryPath = Path.GetTempPath() };
        ws.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(9));

        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, title: "Cancelled Chat");
        var expectedSessionId = LlmSessionIds.ForIdeation(ws.Id, conv.Id);

        var finalUsage = new RouterSessionUsage(
            SessionId: expectedSessionId,
            Requests: 1,
            InputTokens: 800,
            CachedInputTokens: 400,
            OutputTokens: 120,
            ElapsedMs: 900.0,
            LastSeenAt: DateTimeOffset.UtcNow,
            CloudValue: 0.03,
            ElectricityCost: 0.0015,
            Savings: 0.0285,
            ActualCloudCost: 0.0,
            Cost: 0.0015,
            Currency: "$",
            PerTarget: null);

        mockGateway.SetupSequence(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouterSessionUsage?)null)
            .ReturnsAsync(finalUsage);

        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(131072);

        using var cts = new CancellationTokenSource();

        async IAsyncEnumerable<ChatResponseUpdate> CancellableStream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Partial response before cancel...");
            await Task.Yield();
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }

        var chatClientMock = new Mock<IChatClient>();
        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(CancellableStream());

        var service = new TestIdeationServiceBuilder
        {
            GatewayService = mockGateway.Object,
            TaskRepository = taskRepo,
            BaseChatClient = chatClientMock.Object
        }.Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.ConverseStreamingAsync(
                workspace: ws,
                userPrompt: "Start exploration",
                conversationId: conv.Id,
                cancellationToken: cts.Token))
            {
            }
        });

        var updatedConv = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(updatedConv);
        Assert.Equal(800, updatedConv.InputTokens);
        Assert.Equal(120, updatedConv.OutputTokens);
        Assert.Equal(400, updatedConv.CachedInputTokens);
        Assert.Equal(0.0015, updatedConv.Cost!.Value, precision: 6);
        Assert.Equal("$", updatedConv.Currency);
    }

    [Fact]
    public async Task ConverseStreamingAsync_WhenNonRouterProviderReturnsNullUsage_AccumulatesSdkMeasuredTokensOntoConversation()
    {
        var mockGateway = new Mock<ILlmGatewayService>();
        var taskRepo = new InMemoryTaskRepository();

        var ws = new Workspace { Id = 11, Name = "Standalone Ollama Ideation WS", RepositoryPath = Path.GetTempPath() };
        ws.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(11));

        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, title: "Non-Router Chat");
        var expectedSessionId = LlmSessionIds.ForIdeation(ws.Id, conv.Id);

        mockGateway.Setup(g => g.GetSessionUsageAsync(expectedSessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RouterSessionUsage?)null);
        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(65536);

        var usageContent = new UsageContent(new UsageDetails
        {
            InputTokenCount = 1450,
            OutputTokenCount = 320,
            TotalTokenCount = 1770
        });
        var responseUpdate = new ChatResponseUpdate(ChatRole.Assistant, "Standalone response")
        {
            Contents = [new TextContent("Standalone response"), usageContent]
        };

        var chatClientMock = new Mock<IChatClient>();
        chatClientMock
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Returns(new[] { responseUpdate }.ToAsyncEnumerable());

        var service = new TestIdeationServiceBuilder
        {
            GatewayService = mockGateway.Object,
            TaskRepository = taskRepo,
            BaseChatClient = chatClientMock.Object
        }.Build();

        IdeationResult? finalResult = null;
        await foreach (var update in service.ConverseStreamingAsync(
            workspace: ws,
            userPrompt: "Test non-router token fallback",
            conversationId: conv.Id,
            cancellationToken: CancellationToken.None))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed)
            {
                finalResult = update.FinalResult;
            }
        }

        Assert.NotNull(finalResult);
        Assert.NotNull(finalResult.UsageDelta);
        Assert.Equal(1450, finalResult.UsageDelta.InputTokens);
        Assert.Equal(320, finalResult.UsageDelta.OutputTokens);
        Assert.Null(finalResult.UsageDelta.Cost);

        var updatedConv = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(updatedConv);
        Assert.Equal(1450, updatedConv.InputTokens);
        Assert.Equal(320, updatedConv.OutputTokens);
        Assert.Null(updatedConv.Cost);
    }
}
