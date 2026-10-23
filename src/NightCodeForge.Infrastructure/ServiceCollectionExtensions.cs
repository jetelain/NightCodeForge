using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
using NightCodeForge.Infrastructure.Resilience;

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
        // 1. Database & SignalR Core
        services.AddSignalR();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(dbConnectionString, sqliteOptions =>
                sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

        // 2. Repositories & Stores
        services.TryAddScoped<ITaskRepository, EfCoreTaskRepository>();
        services.TryAddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        services.TryAddScoped<IWorkflowRepository, EfCoreWorkflowRepository>();
        services.TryAddScoped<ITaskDependencyStore, EfCoreTaskDependencyStore>();
        services.TryAddSingleton<IGlobalOptimizationSettingsService, GlobalOptimizationSettingsService>();
        services.TryAddSingleton<IWebSearchSettingsService, WebSearchSettingsService>();

        // 3. Concurrency & DAG Engine
        services.TryAddSingleton<IWorkspaceLockProvider, WorkspaceLockProvider>();
        services.TryAddSingleton<ITaskCancellationManager, TaskCancellationManager>();
        services.TryAddScoped<IDependencyResolutionEngine, DependencyResolutionEngine>();

        // 4. Git Infrastructure
        services.AddGitInfrastructure();

        // 5. Workflow Pipeline & Orchestration
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPromptTemplateRenderer, ScribanPromptTemplateRenderer>();
        services.TryAddSingleton<IWorkflowStageRegistry, WorkflowStageRegistry>();
        services.TryAddScoped<IWorkflowStageFactory, WorkflowStageFactory>();
        services.TryAddScoped<IWorkflowPipelineProvider, DatabaseWorkflowPipelineProvider>();
        services.TryAddScoped<IConflictResolutionVerifier, ConflictResolutionVerifier>();
        services.TryAddScoped<ITaskOrchestrator, TaskOrchestrator>();
        services.TryAddScoped<IStartupRecoveryService, StartupRecoveryService>();

        // 6. Execution Environments, Process Runner & Path Resolution
        services.AddOptions<Execution.ExecutionEnvironmentProviderOptions>();
        services.AddOptions<Execution.ContainerExecutorOptions>();
        services.AddOptions<Inference.OllamaRouterOptions>();

        services.TryAddSingleton<NightCodeForge.Core.Execution.IBackgroundTaskRunner, NightCodeForge.Core.Execution.BackgroundTaskRunner>();
        services.TryAddSingleton<IOperationsTelemetryService, Telemetry.OperationsTelemetryService>();
        services.TryAddSingleton<NightCodeForge.Core.Execution.SystemProcessRunner>();
        services.TryAddSingleton<IProcessRunner>(sp => new Execution.TelemetryProcessRunner(
            sp.GetRequiredService<NightCodeForge.Core.Execution.SystemProcessRunner>(),
            sp.GetRequiredService<IOperationsTelemetryService>(),
            sp.GetRequiredService<ILogger<Execution.TelemetryProcessRunner>>()));
        services.TryAddSingleton<IWslMetadataService, Execution.WslMetadataService>();
        services.TryAddSingleton<IHostSdkDiscoveryService, Execution.HostSdkDiscoveryService>();
        services.TryAddSingleton<IHostUserIdentityResolver, Execution.HostUserIdentityResolver>();
        services.TryAddSingleton<IWorkspacePathResolver, Execution.WorkspacePathResolver>();
        services.TryAddSingleton<Execution.BareHostExecutionEnvironment>();
        services.TryAddSingleton<IExecutionEnvironment>(sp => sp.GetRequiredService<Execution.BareHostExecutionEnvironment>());
        services.TryAddTransient<Execution.WslExecutionEnvironment>();
        services.TryAddTransient<Execution.NightCodeForgeContainerExecutor>();
        services.TryAddScoped<IExecutionEnvironmentProvider, Execution.ExecutionEnvironmentProvider>();

        // 7. OllamaRouter Services
        services.AddHttpClient<IOllamaModelCatalogService, Inference.OllamaModelCatalogService>()
            .AddTransientHttpErrorRetry();
        services.AddHttpClient<IOllamaModelPreloadService, Inference.OllamaModelPreloadService>()
            .AddTransientHttpErrorRetry();
        services.AddHttpClient<IOllamaRouterGatewayService, Inference.OllamaRouterGatewayService>()
            .AddTransientHttpErrorRetry();

        // 8. Markdown ForgeTask Mirroring
        services.TryAddSingleton<ITaskMarkdownSyncService>(sp =>
            new TaskMarkdownSyncService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IWorkspacePathResolver>()));

        // 9. Deterministic Quality Gate Stages
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicBuildStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicTestStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicFormatStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicSastStage>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicTestWithCoverageGate>();
        services.TryAddTransient<NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage>();

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
