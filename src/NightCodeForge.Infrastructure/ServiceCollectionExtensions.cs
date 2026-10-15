using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure;

/// <summary>
/// Extension methods for configuring NightCodeForge infrastructure, persistence, workflow orchestration, and background jobs.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core and infrastructure services required for NightCodeForge.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeCoreAndInfrastructure(
        this IServiceCollection services,
        string dbConnectionString = "Data Source=nightcodeforge.db;")
    {
        // 1. Database
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(dbConnectionString));

        // 2. Repositories & Stores
        services.TryAddScoped<ITaskRepository, EfCoreTaskRepository>();
        services.TryAddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        services.TryAddScoped<ITaskDependencyStore, EfCoreTaskDependencyStore>();
        services.TryAddSingleton<IGlobalOptimizationSettingsService, GlobalOptimizationSettingsService>();
        services.TryAddSingleton<IWebSearchSettingsService, WebSearchSettingsService>();

        // 3. Concurrency & DAG Engine
        services.TryAddSingleton<IWorkspaceLockProvider, WorkspaceLockProvider>();
        services.TryAddScoped<IDependencyResolutionEngine, DependencyResolutionEngine>();

        // 4. Git Infrastructure
        services.AddGitInfrastructure();

        // 5. Workflow Pipeline & Orchestration
        services.TryAddScoped<IWorkflowPipelineProvider, DefaultWorkflowPipelineProvider>();
        services.TryAddScoped<ITaskOrchestrator, TaskOrchestrator>();
        services.TryAddScoped<IStartupRecoveryService, StartupRecoveryService>();

        // 6. Execution Environments, Process Runner & Path Resolution
        services.TryAddSingleton<IProcessRunner, Execution.SystemProcessRunner>();
        services.TryAddSingleton<IWslMetadataService, Execution.WslMetadataService>();
        services.TryAddSingleton<IHostSdkDiscoveryService, Execution.HostSdkDiscoveryService>();
        services.TryAddSingleton<IWorkspacePathResolver, Execution.WorkspacePathResolver>();
        services.TryAddSingleton<IExecutionEnvironment, Execution.BareHostExecutionEnvironment>();
        services.TryAddTransient<Execution.WslExecutionEnvironment>();
        services.TryAddScoped<IExecutionEnvironmentProvider, Execution.ExecutionEnvironmentProvider>();

        // 7. OllamaRouter Services
        services.AddHttpClient<IOllamaModelCatalogService, Inference.OllamaModelCatalogService>();
        services.AddHttpClient<IOllamaModelPreloadService, Inference.OllamaModelPreloadService>();
        services.AddHttpClient<IOllamaRouterGatewayService, Inference.OllamaRouterGatewayService>();

        // 8. Markdown ForgeTask Mirroring
        services.TryAddScoped<ITaskMarkdownSyncService, TaskMarkdownSyncService>();

        // 9. Deterministic Quality Gate Stages
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicBuildStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicTestStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicSastStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicCoverageGate>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicPreImplementationStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicPostImplementationStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicPostReviewStage>();

        services.TryAddScoped<IWorkflowPipeline>(sp => Default3AgentPipeline.Create(
            preImplStage: sp.GetService<NightCodeForge.Core.Workflow.Stages.DeterministicPreImplementationStage>(),
            postImplStage: sp.GetService<NightCodeForge.Core.Workflow.Stages.DeterministicPostImplementationStage>(),
            postReviewStage: sp.GetService<NightCodeForge.Core.Workflow.Stages.DeterministicPostReviewStage>(),
            envProvider: sp.GetService<IExecutionEnvironmentProvider>(),
            executionEnv: sp.GetService<IExecutionEnvironment>(),
            preloadService: sp.GetService<IOllamaModelPreloadService>()
        ));

        // 10. Streaming & Activity Streamer
        services.TryAddSingleton<NightCodeForge.Core.Streaming.IAgentActivityStreamer, NightCodeForge.Core.Streaming.InMemoryAgentActivityStreamer>();

        return services;
    }

    /// <summary>
    /// Registers the SignalR real-time activity bridge hosted service.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeSignalRBridge(this IServiceCollection services)
    {
        services.AddHostedService<Hubs.SignalRActivityBridgeService>();
        return services;
    }
}
