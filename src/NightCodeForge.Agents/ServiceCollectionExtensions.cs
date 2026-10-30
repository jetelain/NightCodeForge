using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Infrastructure.Resilience;

namespace NightCodeForge.Agents;

/// <summary>
/// Service collection extension methods registering all NightCodeForge agent harnesses, canonical tools, and optimizations.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNightCodeForgeAgents(this IServiceCollection services, LlmProviderType providerType)
    {
        // 1. Streaming & Realtime Telemetry
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAgentActivityStreamer, InMemoryAgentActivityStreamer>();
        services.AddOptions<RetryPolicyOptions>();

        // 2. Canonical Tools & Context Services
        services.TryAddSingleton<IEditorConfigService, EditorConfigService>();
        services.TryAddSingleton<IEditorConfigFileApplier, EditorConfigFileApplier>();
        services.AddHttpClient<OllamaWebSearchService>()
            .AddTransientHttpErrorRetry();
        services.TryAddScoped<IWebSearchService>(sp => sp.GetRequiredService<OllamaWebSearchService>());
        services.TryAddSingleton<IWorkspaceSymbolService, RoslynSymbolIndexProvider>();
        services.TryAddSingleton<ISkillsService, SkillsService>();
        services.TryAddSingleton<SkillsService>();
        services.TryAddSingleton<IBundledSkillsProvider, BundledSkillsProvider>();
        services.TryAddSingleton<IWorkspaceSkillsServiceFactory, WorkspaceSkillsServiceFactory>();
        services.TryAddSingleton<AgentsMdService>();
        services.TryAddSingleton<IMcpClientManager, McpClientManager>();

        // 3. Transcripts & Diagnostics
        services.TryAddSingleton<SessionTranscriptService>();
        services.TryAddScoped<TranscriptDiagnosticService>();

        // 4. Ideation & Triage Services
        services.TryAddScoped<IIdeationService, IdeationService>();
        services.TryAddScoped<NightCodeForge.Core.Workflow.ITriageService, NightCodeForge.Agents.Triage.TriageService>();

        // 5. Agent Harnesses
        services.AddOptions<Acp.AcpHarnessOptions>();
        services.AddOptions<PiRpc.PiRpcHarnessOptions>();
        services.TryAddScoped<CrucibleAgentHarness>();
        services.TryAddScoped<PiRpcAgentHarness>();
        services.TryAddScoped<AcpAgentHarness>();

        // 6. Harness Registry
        services.TryAddScoped<IHarnessRegistry>(sp =>
        {
            var registry = new HarnessRegistry();
            registry.RegisterHarness(sp.GetRequiredService<CrucibleAgentHarness>());
            registry.RegisterHarness(sp.GetRequiredService<PiRpcAgentHarness>());
            registry.RegisterHarness(sp.GetRequiredService<AcpAgentHarness>());
            return registry;
        });

        // 7. Local LLM Chat Client & Factory (Ollama / OllamaRouter via OllamaSharp with tuned timeout)
        switch (providerType)
        {
            case LlmProviderType.Ollama:
                services.TryAddSingleton<ILlmCapabilityResolver, Inference.OllamaCapabilityResolver>();
                services.TryAddSingleton<Inference.ILlmChatClientFactory, Inference.StandaloneOllamaChatClientFactory>();
                break;
            case LlmProviderType.OllamaRouter:
                services.TryAddSingleton<ILlmCapabilityResolver, Inference.OllamaCapabilityResolver>();
                services.TryAddSingleton<Inference.ILlmChatClientFactory, Inference.OllamaRouterChatClientFactory>();
                break;
            case LlmProviderType.OpenAiCompatible:
                services.TryAddSingleton<IDefaultModelSelector, OpenAiDefaultModelSelector>();
                services.TryAddSingleton<ILlmCapabilityResolver, Inference.OpenAiCapabilityResolver>();
                services.TryAddSingleton<Inference.ILlmChatClientFactory, Inference.OpenAiCompatibleChatClientFactory>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "Unsupported LLM provider type.");
        }
        services.TryAddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<Inference.ILlmChatClientFactory>();
            return factory.CreateClient();
        });

        return services;
    }
}
