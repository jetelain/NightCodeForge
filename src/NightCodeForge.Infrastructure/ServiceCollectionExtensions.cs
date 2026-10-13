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
        services.TryAddScoped<IIssueRepository, EfCoreIssueRepository>();
        services.TryAddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        services.TryAddScoped<IIssueDependencyStore, EfCoreIssueDependencyStore>();

        // 3. Concurrency & DAG Engine
        services.TryAddSingleton<IWorkspaceLockProvider, WorkspaceLockProvider>();
        services.TryAddScoped<IDependencyResolutionEngine, DependencyResolutionEngine>();

        // 4. Git Infrastructure
        services.AddGitInfrastructure();

        // 5. Workflow Pipeline & Orchestration
        services.TryAddScoped<IWorkflowPipelineProvider, DefaultWorkflowPipelineProvider>();
        services.TryAddScoped<IIssueOrchestrator, IssueOrchestrator>();
        services.TryAddScoped<IStartupRecoveryService, StartupRecoveryService>();

        // 6. Execution Environments
        services.TryAddSingleton<IExecutionEnvironment, Execution.BareHostExecutionEnvironment>();
        services.TryAddTransient<Execution.WslExecutionEnvironment>();

        // 7. OllamaRouter Services
        services.AddHttpClient<IOllamaModelCatalogService, Inference.OllamaModelCatalogService>();
        services.AddHttpClient<IOllamaModelPreloadService, Inference.OllamaModelPreloadService>();

        // 8. Markdown Issue Mirroring
        services.TryAddScoped<IIssueMarkdownSyncService, IssueMarkdownSyncService>();

        return services;
    }
}
