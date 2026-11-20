using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Ideation;
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
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow.Filtering;

namespace NightCodeForge.Agents.Tests.Fixtures;

public sealed class TestIdeationCoordinatorBuilder : IDisposable
{
    private ServiceProvider? _serviceProvider;

    public IWorkspacePathResolver PathResolver { get; set; } = CreateDefaultPathResolver();
    public ILlmChatClientFactory? ChatClientFactory { get; set; }
    public IChatClient? BaseChatClient { get; set; }
    public IExecutionEnvironmentProvider EnvProvider { get; set; } = new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object);
    public IWebSearchService WebSearchService { get; set; } = new Mock<IWebSearchService>().Object;
    public Func<IServiceProvider, IWebSearchService>? WebSearchServiceFactory { get; set; }
    public IEditorConfigService EditorConfigService { get; set; } = new EditorConfigService();
    public IEditorConfigFileApplier EditorConfigFileApplier { get; set; } = new EditorConfigFileApplier();
    public AgentsMdService AgentsMdService { get; set; } = new();
    public IAgentActivityStreamer ActivityStreamer { get; set; } = new InMemoryAgentActivityStreamer();
    public SessionTranscriptService TranscriptService { get; set; } = new();
    public IGlobalOptimizationSettingsService GlobalOptimizationSettings { get; set; } = new Mock<IGlobalOptimizationSettingsService>().Object;
    public IWorkspaceSkillsServiceFactory SkillsServiceFactory { get; set; } = new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance);
    public IWorkspaceRepository WorkspaceRepository { get; set; } = new InMemoryWorkspaceRepository();
    public ITaskRepository TaskRepository { get; set; } = new InMemoryTaskRepository();
    public ITaskMarkdownSyncService MarkdownSyncService { get; set; } = new Mock<ITaskMarkdownSyncService>().Object;
    public IMcpClientManager McpManager { get; set; } = CreateDefaultMcpManager();
    public IWorkspaceSymbolService SymbolService { get; set; } = new Mock<IWorkspaceSymbolService>().Object;
    public IBacklogPlanningContext? BacklogContext { get; set; }
    public Func<IServiceProvider, ITaskMarkdownValidator>? TaskMarkdownValidatorFactory { get; set; }
    public Func<IServiceProvider, IBacklogMutationToolService>? BacklogMutationServiceFactory { get; set; }
    public ITranscriptAnalysisService TranscriptAnalysisService { get; set; } = new TranscriptAnalysisService();
    public ILlmGatewayService GatewayService { get; set; } = CreateDefaultGatewayService();
    public ILlmCapabilityResolver? CapabilityResolver { get; set; }
    public IOptions<LlmProviderOptions> LlmOptions { get; set; } = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions());
    public IOperationsControlService OperationsControlService { get; set; } = CreateDefaultOperationsControlService();
    public IBackgroundTaskRunner BackgroundTaskRunner { get; set; } = new BackgroundTaskRunner(NullLogger<BackgroundTaskRunner>.Instance);
    public ILlmModelPreloadService PreloadService { get; set; } = CreateDefaultPreloadService();
    public IAgentWorkspaceContextService? WorkspaceContextService { get; set; }
    public ICanonicalToolsetFactory? ToolsetFactory { get; set; }
    public IHarnessAgentFactory? HarnessAgentFactory { get; set; }
    public ILogger<IdeationConversationCoordinator> Logger { get; set; } = NullLogger<IdeationConversationCoordinator>.Instance;
    public ILogger<HarnessAgentFactory> HarnessAgentLogger { get; set; } = NullLogger<HarnessAgentFactory>.Instance;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    public bool ValidateScopesOnBuild { get; set; } = true;

    public ServiceProvider? BuiltServiceProvider => _serviceProvider;
    public IServiceScopeFactory? BuiltScopeFactory => _serviceProvider?.GetRequiredService<IServiceScopeFactory>();

    private static IWorkspacePathResolver CreateDefaultPathResolver()
    {
        var mock = new Mock<IWorkspacePathResolver>();
        mock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        mock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns<int>(id => $"~/.nightcodeforge/wks/{id}/clone");
        return mock.Object;
    }

    private static IMcpClientManager CreateDefaultMcpManager()
    {
        var mock = new Mock<IMcpClientManager>();
        mock.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerDefinition>());
        return mock.Object;
    }

    private static ILlmGatewayService CreateDefaultGatewayService()
    {
        var mock = new Mock<ILlmGatewayService>();
        mock.Setup(m => m.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(131072);
        mock.Setup(m => m.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return mock.Object;
    }

    private static IOperationsControlService CreateDefaultOperationsControlService()
    {
        var mock = new Mock<IOperationsControlService>();
        mock.Setup(m => m.IsPaused).Returns(false);
        mock.Setup(m => m.ReclaimStatus).Returns(OperationsReclaimStatus.Idle);
        return mock.Object;
    }

    private static ILlmModelPreloadService CreateDefaultPreloadService()
    {
        var mock = new Mock<ILlmModelPreloadService>();
        mock.Setup(p => p.PreloadModelAsync(It.IsAny<string>(), It.IsAny<ModelPreloadOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string model, ModelPreloadOptions? opts, CancellationToken _) =>
                new ModelPreloadResult(true, model, opts?.Target, opts?.KeepAliveSeconds, opts?.ContextIntentTokens, opts?.ContextIntentTokens));
        return mock.Object;
    }

    public IdeationConversationCoordinator Build()
    {
        _serviceProvider?.Dispose();

        var chatClientFactory = ChatClientFactory;
        if (chatClientFactory == null)
        {
            var client = BaseChatClient ?? new Mock<IChatClient>().Object;
            chatClientFactory = new TestCrucibleHarnessBuilder.TestSingleChatClientFactory(client);
        }

        var capabilityResolver = CapabilityResolver ?? new OllamaCapabilityResolver(LlmOptions, EmptyLlmContextWindowCache.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(PathResolver);
        services.AddSingleton(chatClientFactory);
        services.AddSingleton(EnvProvider);
        services.AddSingleton(EditorConfigService);
        services.AddSingleton(EditorConfigFileApplier);
        services.AddSingleton(AgentsMdService);
        services.AddSingleton(ActivityStreamer);
        services.AddSingleton(TranscriptService);
        services.AddSingleton(GlobalOptimizationSettings);
        services.AddSingleton(SkillsServiceFactory);
        services.AddSingleton(McpManager);
        services.AddSingleton(SymbolService);
        services.AddSingleton(TranscriptAnalysisService);
        services.AddSingleton(GatewayService);
        services.AddSingleton(capabilityResolver);
        services.AddSingleton(LlmOptions);
        services.AddSingleton(OperationsControlService);
        services.AddSingleton(BackgroundTaskRunner);
        services.AddSingleton(PreloadService);
        services.AddSingleton(TimeProvider);
        services.AddSingleton<ICliOutputFilterRegistry>(CliOutputFilterRegistry.Default);
        services.AddSingleton(Logger);
        services.AddSingleton(HarnessAgentLogger);
        services.AddSingleton(NullLogger<CanonicalTools>.Instance);
        services.AddSingleton<ILogger<CanonicalTools>>(NullLogger<CanonicalTools>.Instance);
        services.AddSingleton<ILogger<AgentWorkspaceContextService>>(NullLogger<AgentWorkspaceContextService>.Instance);

        services.AddScoped<IWorkspaceRepository>(_ => WorkspaceRepository);
        services.AddScoped<ITaskRepository>(_ => TaskRepository);
        services.AddScoped<ITaskMarkdownSyncService>(_ => MarkdownSyncService);

        if (WebSearchServiceFactory != null)
        {
            services.AddScoped(WebSearchServiceFactory);
        }
        else
        {
            services.AddScoped<IWebSearchService>(_ => WebSearchService);
        }

        if (TaskMarkdownValidatorFactory != null)
        {
            services.AddScoped(TaskMarkdownValidatorFactory);
        }
        else
        {
            services.AddScoped<ITaskMarkdownValidator>(sp =>
                BacklogContext?.TaskMarkdownValidator ?? TestBacklogContext.CreateDefault().TaskMarkdownValidator);
        }

        if (BacklogMutationServiceFactory != null)
        {
            services.AddScoped(BacklogMutationServiceFactory);
        }
        else
        {
            services.AddScoped<IBacklogMutationToolService>(sp =>
                BacklogContext?.BacklogMutationService ?? TestBacklogContext.CreateDefault().BacklogMutationService);
        }

        if (BacklogContext != null)
        {
            services.AddScoped<IBacklogPlanningContext>(_ => BacklogContext);
        }
        else
        {
            services.AddScoped<IBacklogPlanningContext>(sp =>
            {
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                var validator = sp.GetRequiredService<ITaskMarkdownValidator>();
                var mutation = sp.GetRequiredService<IBacklogMutationToolService>();
                var mock = new Mock<IBacklogPlanningContext>();
                mock.SetupGet(c => c.ScopeFactory).Returns(scopeFactory);
                mock.SetupGet(c => c.TaskMarkdownValidator).Returns(validator);
                mock.SetupGet(c => c.BacklogMutationService).Returns(mutation);
                mock.SetupGet(c => c.CanonicalToolsLogger).Returns(NullLogger<CanonicalTools>.Instance);
                mock.Setup(c => c.BuildBacklogDigestAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(string.Empty);
                mock.Setup(c => c.BuildWorkspaceAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(string.Empty);
                mock.Setup(c => c.BuildTaskAttachmentsDigestAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(string.Empty);
                return mock.Object;
            });
        }

        if (ToolsetFactory != null)
        {
            services.AddScoped<ICanonicalToolsetFactory>(_ => ToolsetFactory);
        }
        else
        {
            services.AddScoped<ICanonicalToolsetFactory>(sp => new CanonicalToolsetFactory(
                sp.GetRequiredService<IWorkspacePathResolver>(),
                sp.GetRequiredService<IExecutionEnvironmentProvider>(),
                sp.GetRequiredService<IEditorConfigService>(),
                sp.GetRequiredService<IEditorConfigFileApplier>(),
                sp.GetRequiredService<IWebSearchService>(),
                sp.GetRequiredService<IWorkspaceSkillsServiceFactory>(),
                sp.GetRequiredService<IMcpClientManager>(),
                sp.GetRequiredService<IBacklogPlanningContext>(),
                sp.GetRequiredService<ICliOutputFilterRegistry>()));
        }

        if (WorkspaceContextService != null)
        {
            services.AddScoped<IAgentWorkspaceContextService>(_ => WorkspaceContextService);
        }
        else
        {
            services.AddScoped<IAgentWorkspaceContextService>(sp => new AgentWorkspaceContextService(
                sp.GetRequiredService<IWorkspacePathResolver>(),
                sp.GetRequiredService<AgentsMdService>(),
                sp.GetRequiredService<IWorkspaceSymbolService>(),
                sp.GetRequiredService<ITaskMarkdownSyncService>(),
                sp.GetRequiredService<IBacklogPlanningContext>(),
                sp.GetRequiredService<ITranscriptAnalysisService>(),
                sp.GetRequiredService<ILogger<AgentWorkspaceContextService>>()));
        }

        if (HarnessAgentFactory != null)
        {
            services.AddScoped<IHarnessAgentFactory>(_ => HarnessAgentFactory);
        }
        else
        {
            services.AddScoped<IHarnessAgentFactory>(sp => new HarnessAgentFactory(
                sp.GetRequiredService<ILlmChatClientFactory>(),
                sp.GetRequiredService<ILlmGatewayService>(),
                sp.GetRequiredService<ILlmCapabilityResolver>(),
                sp.GetRequiredService<IGlobalOptimizationSettingsService>(),
                sp.GetRequiredService<IOptions<LlmProviderOptions>>(),
                sp.GetRequiredService<ILogger<HarnessAgentFactory>>()));
        }

        services.AddSingleton<IdeationConversationCoordinator>();

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = ValidateScopesOnBuild,
            ValidateOnBuild = ValidateScopesOnBuild
        });

        return _serviceProvider.GetRequiredService<IdeationConversationCoordinator>();
    }

    public async Task<Workspace> SeedWorkspaceAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var existing = await WorkspaceRepository.GetByIdAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            await WorkspaceRepository.UpdateAsync(workspace, cancellationToken).ConfigureAwait(false);
            return workspace;
        }

        return await WorkspaceRepository.CreateAsync(workspace, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdeationResult> ConverseAsync(
        IdeationConversationCoordinator coordinator,
        Workspace workspace,
        string prompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        OllamaRoutingTarget routingTarget = OllamaRoutingTarget.Auto,
        CancellationToken cancellationToken = default)
    {
        await SeedWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);

        var options = new IdeationSessionOptions(
            RoutingTarget: routingTarget,
            CustomSystemPrompt: customSystemPrompt,
            ExplicitProfile: agentProfile);

        var conversation = await TaskRepository.StartIdeationConversationAsync(
            workspace.Id,
            title: "Test Ideation",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (conversationHistory is { Count: > 0 })
        {
            foreach (var (roleStr, content) in conversationHistory)
            {
                var turnRole = string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? StageTurnRole.Assistant
                    : StageTurnRole.User;
                await TaskRepository.AddConversationTurnAsync(
                    conversation.Id,
                    turnRole,
                    content: content,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        return await coordinator.SendMessageAsync(
            new IdeationSendRequest(workspace.Id, conversation.Id, prompt, options),
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        IdeationConversationCoordinator coordinator,
        Workspace workspace,
        string prompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await SeedWorkspaceAsync(workspace, cancellationToken).ConfigureAwait(false);

        var options = new IdeationSessionOptions(ExplicitProfile: agentProfile);
        var conversation = await TaskRepository.StartIdeationConversationAsync(
            workspace.Id,
            title: "Test Ideation Stream",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (conversationHistory is { Count: > 0 })
        {
            foreach (var (roleStr, content) in conversationHistory)
            {
                var turnRole = string.Equals(roleStr, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? StageTurnRole.Assistant
                    : StageTurnRole.User;
                await TaskRepository.AddConversationTurnAsync(
                    conversation.Id,
                    turnRole,
                    content: content,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }

        var updates = System.Threading.Channels.Channel.CreateUnbounded<IdeationStreamUpdate>();
        using var sub = coordinator.Subscribe(conversation.Id, u => updates.Writer.TryWrite(u));

        var sendTask = Task.Run(async () =>
        {
            try
            {
                await coordinator.SendMessageAsync(
                    new IdeationSendRequest(workspace.Id, conversation.Id, prompt, options),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                updates.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        await sendTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider != null)
        {
            await _serviceProvider.DisposeAsync().ConfigureAwait(false);
            _serviceProvider = null;
        }
    }

    public void Dispose()
    {
        if (_serviceProvider != null)
        {
            _serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _serviceProvider = null;
        }
    }
}
