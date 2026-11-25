using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Benchmark.Cli.Hosting;

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
        services.TryAddSingleton<IBenchmarkToolchainPreflightValidator, BenchmarkToolchainPreflightValidator>();
        services.TryAddScoped<IBenchmarkVerifier, BenchmarkVerifier>();
        services.TryAddSingleton<IBenchmarkProvenanceProvider, BenchmarkProvenanceProvider>();
        services.TryAddScoped<ICrucibleBenchmarkRunner, CrucibleBenchmarkRunner>();
        services.AddNightCodeForgeBenchmarkDatasetImporters();
        services.TryAddScoped<IBenchmarkDatasetValidator, BenchmarkDatasetValidator>();

        return services;
    }

    /// <summary>
    /// Registers the standalone external dataset importers and importer registry without requiring
    /// execution, container, or LLM dependencies.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeBenchmarkDatasetImporters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBenchmarkDatasetImporter, SweSharpBenchImporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBenchmarkDatasetImporter, SweBenchMultilingualImporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBenchmarkDatasetImporter, MultiSweBenchImporter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBenchmarkDatasetImporter, SweBenchVerifiedImporter>());
        services.TryAddSingleton<IBenchmarkDatasetImporterRegistry, BenchmarkDatasetImporterRegistry>();

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
