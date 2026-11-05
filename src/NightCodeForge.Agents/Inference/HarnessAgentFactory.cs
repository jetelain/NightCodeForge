using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>Everything needed to assemble the <see cref="HarnessAgent"/> of one agent session.</summary>
/// <param name="CreateTracingClient">Creates the tracer sitting closest to the model.</param>
/// <param name="DecorateTracingClient">Optional middleware inserted between the tracer and the lenient tool-call client (e.g. history persistence).</param>
/// <param name="GrammarFlags">When <see cref="LocalOptimizationFlags.GuidedGrammarDecoding"/> is set, grammar constraints are applied to the chat options.</param>
/// <param name="DefaultTemperature">Temperature applied when neither the profile nor the resolver set one (reasoning models excluded).</param>
public sealed record HarnessAgentRequest(
    AgentProfile Profile,
    string SessionId,
    string AgentId,
    string AgentName,
    string Instructions,
    IList<AITool> Tools,
    int MaxTurns,
    Func<IChatClient, ThinkingAndActivityTracingChatClient> CreateTracingClient,
    Func<ThinkingAndActivityTracingChatClient, IChatClient>? DecorateTracingClient = null,
    LocalOptimizationFlags? GrammarFlags = null,
    float? DefaultTemperature = null);

/// <summary>The assembled agent of a session with its middlewares and options.</summary>
public sealed record HarnessAgentSetup(
    IChatClient BaseClient,
    ThinkingAndActivityTracingChatClient TracingClient,
    IChatClient ActiveClient,
    HarnessAgentOptions Options,
    HarnessAgent Agent,
    string? EffectiveModelId);

/// <summary>Factorizes model resolution, chat options, context window discovery, compaction and middleware assembly.</summary>
public interface IHarnessAgentFactory
{
    /// <summary>Resolves the effective optimization flags: explicit, then profile, then global settings.</summary>
    LocalOptimizationFlags ResolveOptimizationFlags(AgentProfile profile, LocalOptimizationFlags? explicitFlags);

    Task<HarnessAgentSetup> CreateAsync(HarnessAgentRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class HarnessAgentFactory : IHarnessAgentFactory
{
    private const string ReasoningModelMarkerO1 = "o1";
    private const string ReasoningModelMarkerO3 = "o3";

    private readonly ILlmChatClientFactory _chatClientFactory;
    private readonly ILlmGatewayService _gatewayService;
    private readonly ILlmCapabilityResolver _capabilityResolver;
    private readonly IGlobalOptimizationSettingsService _globalOptimizationSettings;
    private readonly LlmProviderOptions _llmOptions;
    private readonly ILogger<HarnessAgentFactory> _logger;

    public HarnessAgentFactory(
        ILlmChatClientFactory chatClientFactory,
        ILlmGatewayService gatewayService,
        ILlmCapabilityResolver capabilityResolver,
        IGlobalOptimizationSettingsService globalOptimizationSettings,
        IOptions<LlmProviderOptions> llmOptions,
        ILogger<HarnessAgentFactory> logger)
    {
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _gatewayService = gatewayService ?? throw new ArgumentNullException(nameof(gatewayService));
        _capabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
        _globalOptimizationSettings = globalOptimizationSettings ?? throw new ArgumentNullException(nameof(globalOptimizationSettings));
        ArgumentNullException.ThrowIfNull(llmOptions);
        _llmOptions = llmOptions.Value ?? throw new ArgumentNullException(nameof(llmOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LocalOptimizationFlags ResolveOptimizationFlags(AgentProfile profile, LocalOptimizationFlags? explicitFlags)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return explicitFlags ?? profile.OptimizationFlags ?? _globalOptimizationSettings.GetFlags() ?? new LocalOptimizationFlags();
    }

    public async Task<HarnessAgentSetup> CreateAsync(HarnessAgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;

        var baseClient = _chatClientFactory.CreateClient(profile, request.SessionId);
        var effectiveModel = !string.IsNullOrWhiteSpace(profile.ModelName)
            ? profile.ModelName
            : (baseClient.GetService<ChatClientMetadata>()?.DefaultModelId ?? _llmOptions.ModelId);

        // The tracer sits closest to the model (below persistence and compaction) so that it records the exact
        // requests sent to the LLM: everything MAF injects, plus the history after compaction.
        var tracingClient = request.CreateTracingClient(baseClient);
        var decorated = request.DecorateTracingClient?.Invoke(tracingClient) ?? tracingClient;
        var activeClient = new LenientToolCallChatClient(decorated);

        var chatOptions = _capabilityResolver.CreateChatOptions(profile, effectiveModel);
        chatOptions.Tools = request.Tools;

        if (request.GrammarFlags is { GuidedGrammarDecoding: true } grammarFlags)
        {
            GuidedGrammarHelper.ApplyGrammarConstraints(chatOptions, grammarFlags);
        }

        if (request.DefaultTemperature.HasValue
            && chatOptions.Temperature == null
            && profile.Temperature == null
            && (effectiveModel == null
                || (!effectiveModel.Contains(ReasoningModelMarkerO1, StringComparison.OrdinalIgnoreCase)
                    && !effectiveModel.Contains(ReasoningModelMarkerO3, StringComparison.OrdinalIgnoreCase))))
        {
            chatOptions.Temperature = request.DefaultTemperature.Value;
        }

        var (explicitContext, resolutionSource) = await ResolveContextWindowAsync(profile, effectiveModel, cancellationToken);

        var compactionStrategy = ModelCapabilityResolver.BuildCompactionStrategy(
            profile,
            _llmOptions,
            explicitContextWindowTokens: explicitContext,
            summarizerClient: baseClient,
            explicitResolutionSource: resolutionSource);

        if (compactionStrategy is PiStyleCrucibleCompactionStrategy piStrategy)
        {
            tracingClient.SetMeasuredUsageCallback(usage =>
                piStrategy.RecordMeasuredCallTokens(
                    usage.InputTokens,
                    usage.OutputTokens,
                    usage.SentMessageCount,
                    usage.ResponseMessageCount));
        }

        var options = new HarnessAgentOptions
        {
            Id = request.AgentId,
            Name = request.AgentName,
            HarnessInstructions = request.Instructions,
            ChatOptions = chatOptions,
            MaximumIterationsPerRequest = request.MaxTurns,
            CompactionStrategy = compactionStrategy,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var agent = new HarnessAgent(activeClient, options);
        return new HarnessAgentSetup(baseClient, tracingClient, activeClient, options, agent, effectiveModel);
    }

    private async Task<(int? ContextWindowTokens, string? ResolutionSource)> ResolveContextWindowAsync(
        AgentProfile profile,
        string? effectiveModel,
        CancellationToken cancellationToken)
    {
        if (profile.ContextWindowTokens.HasValue && profile.ContextWindowTokens.Value > 0)
        {
            return (profile.ContextWindowTokens.Value, "AgentProfile");
        }

        if (string.IsNullOrWhiteSpace(effectiveModel))
        {
            return (profile.ContextWindowTokens, null);
        }

        try
        {
            var discovered = await _gatewayService.ResolveContextWindowAsync(effectiveModel, cancellationToken);
            if (discovered > 0)
            {
                return (discovered, "GatewayDiscovery");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve context window for '{Model}', falling back to configured options", effectiveModel);
        }

        return (null, null);
    }
}

