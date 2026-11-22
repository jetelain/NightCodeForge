using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Agents.Benchmarking.Verification;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Benchmarking;

/// <summary>
/// Dependency injection registrations for the standalone Crucible benchmark engine.
/// </summary>
public static class BenchmarkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Crucible benchmark dataset loader, leak-free workspace manager,
    /// provenance provider, multi-stack verifier, optional LLM judge, and runner.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeBenchmarking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BenchmarkWorkspaceOptions>();
        services.TryAddSingleton<IBenchmarkDatasetLoader, BenchmarkDatasetLoader>();
        services.TryAddSingleton<IBenchmarkWorkspaceManager, BenchmarkWorkspaceManager>();
        services.TryAddSingleton<IBenchmarkJudgeClientFactory, DefaultBenchmarkJudgeClientFactory>();
        services.TryAddSingleton<IBenchmarkLlmJudge, BenchmarkLlmJudge>();
        services.TryAddScoped<IBenchmarkVerifier, BenchmarkVerifier>();
        services.TryAddSingleton<IBenchmarkProvenanceProvider, BenchmarkProvenanceProvider>();
        services.TryAddScoped<ICrucibleBenchmarkRunner, CrucibleBenchmarkRunner>();

        return services;
    }

    /// <summary>
    /// Replaces the database-backed <see cref="ICrucibleConversationCoordinator"/> with the
    /// stateless <see cref="InMemoryCrucibleConversationCoordinator"/> for standalone benchmark execution.
    /// </summary>
    public static IServiceCollection UseInMemoryCrucibleConversationCoordinator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Scoped<ICrucibleConversationCoordinator, InMemoryCrucibleConversationCoordinator>());
        return services;
    }
}
