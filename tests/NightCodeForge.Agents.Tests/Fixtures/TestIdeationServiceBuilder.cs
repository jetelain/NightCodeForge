namespace NightCodeForge.Agents.Tests.Fixtures;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

public class TestIdeationServiceBuilder
{
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IOllamaChatClientFactory? ChatClientFactory { get; set; }
    public IChatClient? BaseChatClient { get; set; }
    public IExecutionEnvironmentProvider EnvProvider { get; set; } = new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object);
    public IWebSearchService WebSearchService { get; set; } = new Mock<IWebSearchService>().Object;
    public IEditorConfigService EditorConfigService { get; set; } = new EditorConfigService();
    public IEditorConfigFileApplier EditorConfigFileApplier { get; set; } = new EditorConfigFileApplier();
    public AgentsMdService AgentsMdService { get; set; } = new();
    public IAgentActivityStreamer ActivityStreamer { get; set; } = new InMemoryAgentActivityStreamer();
    public SessionTranscriptService TranscriptService { get; set; } = new();
    public IGlobalOptimizationSettingsService GlobalOptimizationSettings { get; set; } = new Mock<IGlobalOptimizationSettingsService>().Object;
    public SkillsService SkillsService { get; set; } = new();
    public IWorkspaceRepository WorkspaceRepository { get; set; } = new InMemoryWorkspaceRepository();
    public IOptions<LlmProviderOptions> LlmOptions { get; set; } = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions());
    public ILogger<IdeationService> Logger { get; set; } = NullLogger<IdeationService>.Instance;
    public ILogger<CanonicalTools> CanonicalToolsLogger { get; set; } = NullLogger<CanonicalTools>.Instance;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    public IdeationService Build()
    {
        var factory = ChatClientFactory;
        if (factory == null)
        {
            var client = BaseChatClient ?? new Mock<IChatClient>().Object;
            factory = new TestCrucibleHarnessBuilder.TestSingleChatClientFactory(client);
        }

        return new IdeationService(
            PathResolver,
            factory,
            EnvProvider,
            WebSearchService,
            EditorConfigService,
            EditorConfigFileApplier,
            AgentsMdService,
            ActivityStreamer,
            TranscriptService,
            GlobalOptimizationSettings,
            SkillsService,
            WorkspaceRepository,
            LlmOptions,
            Logger,
            CanonicalToolsLogger,
            TimeProvider);
    }
}
