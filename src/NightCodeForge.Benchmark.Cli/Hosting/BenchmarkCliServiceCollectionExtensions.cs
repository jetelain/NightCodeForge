using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NightCodeForge.Agents;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.Inference;

namespace NightCodeForge.Benchmark.Cli.Hosting;

/// <summary>
/// Wires the DB-free, Hangfire-free, SignalR-free dependency injection container for <c>ncf-bench</c>.
/// </summary>
public static class BenchmarkCliServiceCollectionExtensions
{
    public static IServiceCollection AddNightCodeForgeBenchmarkCliServices(
        this IServiceCollection services,
        BenchmarkCliHostConfig config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Endpoint);
        ArgumentNullException.ThrowIfNull(config.AllowedOpenAiModels);

        services.TryAddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        services.TryAddSingleton<IHostEnvironment, BenchmarkHostEnvironment>();
        services.AddLogging();

        services.Configure<LlmProviderOptions>(options =>
        {
            options.ProviderType = config.ProviderType;
            options.Endpoint = config.Endpoint;
            if (config.ProviderType == LlmProviderType.OpenAiCompatible)
            {
                options.OpenAiCompatible.Endpoint = config.Endpoint;
                options.OpenAiCompatible.AllowedModels = config.AllowedOpenAiModels
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(m => new OpenAiAllowedModel
                    {
                        ModelId = m,
                        DisplayName = m
                    })
                    .ToList();
            }
        });

        services.Configure<LlmEndpointOptions>(options =>
        {
            options.Endpoint = config.Endpoint;
        });

        if (!string.IsNullOrWhiteSpace(config.BenchmarkRootDirectory) ||
            !string.IsNullOrWhiteSpace(config.WorkspacesDirectory) ||
            config.UseWslExt4Workspaces)
        {
            services.Configure<BenchmarkWorkspaceOptions>(options =>
            {
                if (!string.IsNullOrWhiteSpace(config.BenchmarkRootDirectory))
                {
                    options.RootDirectory = config.BenchmarkRootDirectory;
                }

                if (!string.IsNullOrWhiteSpace(config.WorkspacesDirectory))
                {
                    options.WorkspacesDirectory = config.WorkspacesDirectory;
                }

                options.UseWslExt4Workspaces = config.UseWslExt4Workspaces;
            });
        }

        // 1. DB-free, SignalR-free, Hangfire-free execution infrastructure
        services.AddNightCodeForgeExecutionInfrastructure();

        // 2. Mock / in-memory repository and host control stubs
        services.TryAddSingleton<ITaskRepository, InMemoryTaskRepository>();
        services.TryAddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
        services.TryAddSingleton<ITaskAttachmentRepository>(sp =>
            new InMemoryTaskAttachmentRepository(sp.GetRequiredService<ITaskRepository>()));
        services.TryAddSingleton<IWorkflowRepository, InMemoryBenchmarkWorkflowRepository>();
        services.TryAddSingleton<ITaskCancellationManager, TaskCancellationManager>();
        services.TryAddSingleton<IOperationsControlService, NoOpBenchmarkOperationsControlService>();
        services.TryAddSingleton<IGlobalOptimizationSettingsService, InMemoryGlobalOptimizationSettingsService>();
        services.TryAddSingleton<IWebSearchSettingsService, InMemoryWebSearchSettingsService>();
        services.TryAddSingleton<ITaskMarkdownSyncService, NoOpTaskMarkdownSyncService>();
        services.TryAddSingleton<IApiKeySecretStore>(_ => new EnvironmentBenchmarkApiKeySecretStore(config.ApiKey));
        services.TryAddSingleton<ITaskDependencyStore, InMemoryTaskDependencyStore>();
        services.TryAddScoped<IDependencyResolutionEngine, DependencyResolutionEngine>();
        services.TryAddScoped<IBacklogMergeModeValidator, DisabledBenchmarkBacklogMergeModeValidator>();
        services.TryAddScoped<IBacklogMutationToolService, DisabledBenchmarkBacklogMutationToolService>();

        // 3. Provider-specific inference, Crucible/ACP agent harnesses, and Crucible benchmark engine
        services.AddNightCodeForgeInference(config.ProviderType);
        services.AddNightCodeForgeAgents(config.ProviderType);
        services.AddNightCodeForgeBenchmarking();
        services.UseInMemoryCrucibleConversationCoordinator();

        return services;
    }
}
