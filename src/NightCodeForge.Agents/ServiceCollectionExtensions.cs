using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents;

/// <summary>
/// Service collection extension methods registering all NightCodeForge agent harnesses, canonical tools, and optimizations.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNightCodeForgeAgents(this IServiceCollection services)
    {
        // 1. Streaming & Realtime Telemetry
        services.TryAddSingleton<IAgentActivityStreamer, InMemoryAgentActivityStreamer>();

        // 2. Canonical Tools & Context Services
        services.TryAddSingleton<IEditorConfigService, EditorConfigService>();
        services.TryAddSingleton<IEditorConfigFileApplier, EditorConfigFileApplier>();
        services.TryAddSingleton<IWebSearchService, MockWebSearchService>();
        services.TryAddSingleton<IWorkspaceSymbolService, RoslynSymbolIndexProvider>();
        services.TryAddSingleton<SkillsService>();
        services.TryAddSingleton<AgentsMdService>();
        services.TryAddSingleton<IMcpClientManager, McpClientManager>();

        // 3. Transcripts & Diagnostics
        services.TryAddSingleton<SessionTranscriptService>();
        services.TryAddScoped<TranscriptDiagnosticService>();

        // 4. Ideation Service
        services.TryAddScoped<IIdeationService, IdeationService>();

        // 5. Agent Harnesses
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
        services.TryAddSingleton<Inference.IOllamaChatClientFactory, Inference.DefaultOllamaChatClientFactory>();
        services.TryAddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<Inference.IOllamaChatClientFactory>();
            return factory.CreateClient();
        });

        return services;
    }
}
