using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class HarnessAgentFactoryTests
{
    private static HarnessAgentFactory CreateAgentFactory(
        IChatClient client,
        ILlmGatewayService? gateway = null,
        IGlobalOptimizationSettingsService? global = null)
    {
        var llmOptions = Options.Create(new LlmProviderOptions { ModelId = "configured-model" });
        return new HarnessAgentFactory(
            new TestCrucibleHarnessBuilder.TestSingleChatClientFactory(client),
            gateway ?? new Mock<ILlmGatewayService>().Object,
            new OllamaCapabilityResolver(llmOptions, EmptyLlmContextWindowCache.Instance),
            global ?? new Mock<IGlobalOptimizationSettingsService>().Object,
            llmOptions,
            NullLogger<HarnessAgentFactory>.Instance);
    }

    private static HarnessAgentRequest AgentRequest(AgentProfile profile, Func<ThinkingAndActivityTracingChatClient, IChatClient>? decorate = null, float? temperature = null) =>
        new(
            profile,
            "session-1",
            "agent-id",
            "Agent Name",
            "instructions",
            new List<AITool>(),
            MaxTurns: 7,
            CreateTracingClient: c => new ThinkingAndActivityTracingChatClient(c),
            DecorateTracingClient: decorate,
            DefaultTemperature: temperature);

    [Fact]
    public void AgentFactory_ResolveOptimizationFlags_PrefersExplicitThenProfileThenGlobal()
    {
        var global = new Mock<IGlobalOptimizationSettingsService>();
        var globalFlags = new LocalOptimizationFlags { DeterministicSymbolMap = true };
        global.Setup(g => g.GetFlags()).Returns(globalFlags);
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object, global: global.Object);
        var explicitFlags = new LocalOptimizationFlags();
        var profileFlags = new LocalOptimizationFlags();

        Assert.Same(explicitFlags, factory.ResolveOptimizationFlags(new AgentProfile { OptimizationFlags = profileFlags }, explicitFlags));
        Assert.Same(profileFlags, factory.ResolveOptimizationFlags(new AgentProfile { OptimizationFlags = profileFlags }, null));
        Assert.Same(globalFlags, factory.ResolveOptimizationFlags(new AgentProfile(), null));
    }

    [Fact]
    public async Task AgentFactory_Create_AssemblesAgentOptionsWithRequestedIdentityAndTurns()
    {
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object);
        var decorated = false;

        var setup = await factory.CreateAsync(
            AgentRequest(
                new AgentProfile { Id = "p", ModelName = "m1", ContextWindowTokens = 32768 },
                tracing => { decorated = true; return tracing; }),
            CancellationToken.None);

        Assert.True(decorated);
        Assert.Equal("m1", setup.EffectiveModelId);
        Assert.Equal("agent-id", setup.Options.Id);
        Assert.Equal("Agent Name", setup.Options.Name);
        Assert.Equal("instructions", setup.Options.HarnessInstructions);
        Assert.Equal(7, setup.Options.MaximumIterationsPerRequest);
        Assert.NotNull(setup.Options.CompactionStrategy);
        Assert.NotNull(setup.Agent);
    }

    [Fact]
    public async Task AgentFactory_Create_FallsBackToConfiguredModelAndDiscoversContextWindow()
    {
        var gateway = new Mock<ILlmGatewayService>();
        gateway.Setup(g => g.ResolveContextWindowAsync("configured-model", It.IsAny<CancellationToken>())).ReturnsAsync(8192);
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object, gateway.Object);

        var setup = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p" }), CancellationToken.None);

        Assert.Equal("configured-model", setup.EffectiveModelId);
        gateway.Verify(g => g.ResolveContextWindowAsync("configured-model", It.IsAny<CancellationToken>()), Times.Once);
        var piStrategy = Assert.IsType<PiStyleCrucibleCompactionStrategy>(setup.Options.CompactionStrategy);
        Assert.Equal(8192, piStrategy.Options.ContextWindowTokens);
        Assert.Equal("GatewayDiscovery", piStrategy.Options.ResolutionSource);
    }

    [Fact]
    public async Task AgentFactory_Create_SkipsGatewayWhenContextWindowIsExplicit()
    {
        var gateway = new Mock<ILlmGatewayService>();
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object, gateway.Object);

        var setup = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m", ContextWindowTokens = 4096 }), CancellationToken.None);

        gateway.Verify(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var piStrategy = Assert.IsType<PiStyleCrucibleCompactionStrategy>(setup.Options.CompactionStrategy);
        Assert.Equal(4096, piStrategy.Options.ContextWindowTokens);
        Assert.Equal("AgentProfile", piStrategy.Options.ResolutionSource);
    }

    [Fact]
    public async Task AgentFactory_Create_WiresMeasuredUsageFromTracingClientToCompactionStrategy()
    {
        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = 210_000,
                    OutputTokenCount = 450
                }
            });

        var factory = CreateAgentFactory(innerMock.Object);
        var setup = await factory.CreateAsync(
            AgentRequest(new AgentProfile { Id = "p", ModelName = "Qwen3.8-27B:latest", ContextWindowTokens = 262_144 }),
            CancellationToken.None);

        var piStrategy = Assert.IsType<PiStyleCrucibleCompactionStrategy>(setup.Options.CompactionStrategy);
        Assert.Equal(0, piStrategy.LastMeasuredInputTokens);

        await setup.TracingClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "Turn 1"),
            new ChatMessage(ChatRole.Assistant, "Turn 2")
        ]);

        Assert.Equal(210_000, piStrategy.LastMeasuredInputTokens);
        Assert.Equal(450, piStrategy.LastMeasuredOutputTokens);
        Assert.Equal(2, piStrategy.LastMeasuredMessageCount);
        Assert.Equal(1, piStrategy.LastMeasuredResponseMessageCount);
    }

    [Fact]
    public async Task AgentFactory_Create_AppliesDefaultTemperatureOnlyWhenUnset()
    {
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object);

        var unset = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m", ContextWindowTokens = 4096 }, temperature: 0.3f), CancellationToken.None);
        var set = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m", ContextWindowTokens = 4096, Temperature = 0.9f }, temperature: 0.3f), CancellationToken.None);
        var reasoning = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "o3-mini", ContextWindowTokens = 4096 }, temperature: 0.3f), CancellationToken.None);

        Assert.Equal(0.3f, unset.Options.ChatOptions!.Temperature);
        Assert.Equal(0.9f, set.Options.ChatOptions!.Temperature);
        Assert.NotEqual(0.3f, reasoning.Options.ChatOptions!.Temperature);
    }

    [Fact]
    public async Task Create_KeepsGoingWhenContextWindowDiscoveryFails()
    {
        var gateway = new Mock<ILlmGatewayService>();
        gateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object, gateway.Object);

        var setup = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m" }), CancellationToken.None);

        Assert.NotNull(setup.Agent);
    }

    [Fact]
    public async Task Create_PropagatesCancellationDuringContextWindowDiscovery()
    {
        var gateway = new Mock<ILlmGatewayService>();
        gateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object, gateway.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m" }), CancellationToken.None));
    }

    [Fact]
    public async Task Create_ExposesTracingClientAndWrapsLenientToolDispatchChatClient()
    {
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object);

        var setup = await factory.CreateAsync(AgentRequest(new AgentProfile { Id = "p", ModelName = "m", ContextWindowTokens = 4096 }), CancellationToken.None);

        Assert.Same(setup.TracingClient, setup.ActiveClient);
        Assert.NotNull(setup.TracingClient);
        Assert.NotNull(setup.TracingClient.GetService<Pmad.AI.Tools.LenientToolDispatchChatClient>());
    }

    [Fact]
    public async Task Create_PassesRequestedToolsToChatOptions()
    {
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object);
        var tool = AIFunctionFactory.Create(() => "x", "probe");
        var request = AgentRequest(new AgentProfile { Id = "p", ModelName = "m", ContextWindowTokens = 4096 }) with { Tools = new List<AITool> { tool } };

        var setup = await factory.CreateAsync(request, CancellationToken.None);

        Assert.Same(tool, Assert.Single(setup.Options.ChatOptions!.Tools!));
    }

    [Fact]
    public void ResolveOptimizationFlags_ThrowsForNullProfile()
    {
        Assert.Throws<ArgumentNullException>(() => CreateAgentFactory(new Mock<IChatClient>().Object).ResolveOptimizationFlags(null!, null));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var llm = Options.Create(new LlmProviderOptions());
        Assert.Throws<ArgumentNullException>(() => new HarnessAgentFactory(
            null!, new Mock<ILlmGatewayService>().Object, new OllamaCapabilityResolver(llm, EmptyLlmContextWindowCache.Instance),
            new Mock<IGlobalOptimizationSettingsService>().Object, llm, NullLogger<HarnessAgentFactory>.Instance));
    }

    [Fact]
    public async Task Create_AwaitsGatewayResolveThinkingCapabilityAsync_BeforeCreatingChatOptions()
    {
        var cache = new NightCodeForge.Infrastructure.Inference.LlmContextWindowCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var gateway = new Mock<ILlmGatewayService>();
        gateway
            .Setup(g => g.ResolveThinkingCapabilityAsync("custom-reasoner:latest", It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((model, _) =>
            {
                cache.SetThinkingCapability(model, new ModelThinkingCapability(
                    SupportsThinking: true,
                    StringLevels: ["low", "medium", "xhigh"],
                    SupportsBooleanOnly: false,
                    DefaultLevel: "medium"));
            })
            .ReturnsAsync(new ModelThinkingCapability(true, ["low", "medium", "xhigh"], false, "medium"));

        var llmOptions = Options.Create(new LlmProviderOptions { ModelId = "configured-model" });
        var factory = new HarnessAgentFactory(
            new TestCrucibleHarnessBuilder.TestSingleChatClientFactory(new Mock<IChatClient>().Object),
            gateway.Object,
            new OllamaCapabilityResolver(llmOptions, cache),
            new Mock<IGlobalOptimizationSettingsService>().Object,
            llmOptions,
            NullLogger<HarnessAgentFactory>.Instance);

        var setup = await factory.CreateAsync(
            AgentRequest(new AgentProfile { Id = "p", ModelName = "custom-reasoner:latest", ThinkingEffort = ThinkingEffort.High }),
            CancellationToken.None);

        gateway.Verify(g => g.ResolveThinkingCapabilityAsync("custom-reasoner:latest", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(setup.Options.ChatOptions?.AdditionalProperties);
        Assert.Equal("xhigh", setup.Options.ChatOptions!.AdditionalProperties!["think"]);
    }

    [Fact]
    public async Task Create_SetsMaxOutputTokens_FromProfileOverrideOrProviderDefault()
    {
        var factory = CreateAgentFactory(new Mock<IChatClient>().Object);

        // Default from LlmProviderOptions
        var defaultSetup = await factory.CreateAsync(
            AgentRequest(new AgentProfile { Id = "p1", ModelName = "configured-model" }),
            CancellationToken.None);

        Assert.Equal(16384, defaultSetup.Options.MaxOutputTokens);
        Assert.Equal(16384, defaultSetup.Options.ChatOptions?.MaxOutputTokens);

        // Explicit override on AgentProfile
        var overrideSetup = await factory.CreateAsync(
            AgentRequest(new AgentProfile { Id = "p2", ModelName = "configured-model", MaxOutputTokens = 2048 }),
            CancellationToken.None);

        Assert.Equal(2048, overrideSetup.Options.MaxOutputTokens);
        Assert.Equal(2048, overrideSetup.Options.ChatOptions?.MaxOutputTokens);
    }
}
