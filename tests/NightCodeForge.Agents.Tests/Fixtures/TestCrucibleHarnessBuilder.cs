namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

public class TestCrucibleHarnessBuilder
{
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IOllamaChatClientFactory? ChatClientFactory { get; set; }
    public IChatClient? BaseChatClient { get; set; }
    public IExecutionEnvironmentProvider EnvProvider { get; set; } = new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object);
    public IMcpClientManager McpManager { get; set; } = CreateDefaultMcpManager();
    public IEditorConfigService EditorConfigService { get; set; } = new EditorConfigService();
    public IEditorConfigFileApplier EditorConfigFileApplier { get; set; } = new EditorConfigFileApplier();

    private static IMcpClientManager CreateDefaultMcpManager()
    {
        var mock = new Mock<IMcpClientManager>();
        mock.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerDefinition>());
        return mock.Object;
    }
    public IWebSearchService WebSearchService { get; set; } = new Mock<IWebSearchService>().Object;
    public IWorkspaceSymbolService SymbolService { get; set; } = new Mock<IWorkspaceSymbolService>().Object;
    public SkillsService SkillsService { get; set; } = new();
    public AgentsMdService AgentsMdService { get; set; } = new();
    public IAgentActivityStreamer ActivityStreamer { get; set; } = new InMemoryAgentActivityStreamer();
    public SessionTranscriptService TranscriptService { get; set; } = new();
    public IGlobalOptimizationSettingsService GlobalOptimizationSettings { get; set; } = new Mock<IGlobalOptimizationSettingsService>().Object;
    public ITaskRepository TaskRepository { get; set; } = new InMemoryTaskRepository();
    public IOptions<LlmProviderOptions> LlmOptions { get; set; } = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions());
    public IOptions<NightCodeForge.Core.Resilience.RetryPolicyOptions> RetryOptions { get; set; } = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.RetryPolicyOptions());
    public ILogger<CrucibleAgentHarness> Logger { get; set; } = NullLogger<CrucibleAgentHarness>.Instance;
    public ILogger<CanonicalTools> CanonicalToolsLogger { get; set; } = NullLogger<CanonicalTools>.Instance;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public CrucibleAgentHarness Build()
    {
        var factory = ChatClientFactory;
        if (factory == null)
        {
            var client = BaseChatClient ?? new Mock<IChatClient>().Object;
            factory = new TestSingleChatClientFactory(client);
        }

        return new CrucibleAgentHarness(
            PathResolver,
            factory,
            EnvProvider,
            McpManager,
            EditorConfigService,
            EditorConfigFileApplier,
            WebSearchService,
            SymbolService,
            SkillsService,
            AgentsMdService,
            ActivityStreamer,
            TranscriptService,
            GlobalOptimizationSettings,
            TaskRepository,
            LlmOptions,
            RetryOptions,
            Logger,
            CanonicalToolsLogger,
            TimeProvider);
    }

    public class TestSingleChatClientFactory : IOllamaChatClientFactory
    {
        private readonly IChatClient _client;
        public TestSingleChatClientFactory(IChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _client;
        public IChatClient CreateClient(AgentProfile? profile) => _client;
    }
}
