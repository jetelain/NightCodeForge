using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Configuration extensions setting up Hangfire SQLite storage and dedicated background processing servers.
/// </summary>
public static class HangfireConfigurationExtensions
{
    /// <summary>
    /// Configures Hangfire with SQLite storage and registers the task job queue service.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeHangfire(
        this IServiceCollection services,
        string sqliteConnectionString = "Data Source=nightcodeforge_hangfire.db;",
        SQLiteStorageOptions? storageOptions = null)
    {
        services.AddHangfire(config =>
        {
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSQLiteStorage(sqliteConnectionString, storageOptions ?? new SQLiteStorageOptions
                {
                    QueuePollInterval = TimeSpan.FromSeconds(1)
                });
        });

        services.AddScoped<ITaskJobQueue, HangfireTaskJobQueue>();

        return services;
    }

    /// <summary>
    /// Configures the two isolated Hangfire processing servers via IServiceCollection:
    /// 1. NCF-LLM-Inference-Worker (WorkerCount = 1) on queues [llm-urgent, llm-inference]
    /// 2. NCF-Utility-Worker (WorkerCount = 4) on queue [default]
    /// </summary>
    public static IServiceCollection AddNightCodeForgeHangfireServers(this IServiceCollection services)
    {
        services.AddHangfireServer(options =>
        {
            options.ServerName = "NCF-LLM-Inference-Worker";
            options.WorkerCount = 1;
            options.Queues = new[] { HangfireQueues.LlmUrgent, HangfireQueues.LlmInference };
        });

        services.AddHangfireServer(options =>
        {
            options.ServerName = "NCF-Utility-Worker";
            options.WorkerCount = 4;
            options.Queues = new[] { HangfireQueues.Default };
        });

        return services;
    }

    /// <summary>
    /// Returns the BackgroundJobServerOptions for the single-threaded LLM inference worker (WorkerCount = 1).
    /// </summary>
    public static BackgroundJobServerOptions CreateLlmInferenceServerOptions() =>
        new()
        {
            ServerName = "NCF-LLM-Inference-Worker",
            WorkerCount = 1,
            Queues = new[] { HangfireQueues.LlmUrgent, HangfireQueues.LlmInference }
        };

    /// <summary>
    /// Returns the BackgroundJobServerOptions for the utility and Git operations worker (WorkerCount = 4).
    /// </summary>
    public static BackgroundJobServerOptions CreateUtilityServerOptions() =>
        new()
        {
            ServerName = "NCF-Utility-Worker",
            WorkerCount = 4,
            Queues = new[] { HangfireQueues.Default }
        };

#pragma warning disable CS0618 // Type or member is obsolete
    /// <summary>
    /// Launches the two isolated Hangfire servers via IApplicationBuilder:
    /// 1. NCF-LLM-Inference-Worker (WorkerCount = 1) on queues [llm-urgent, llm-inference]
    /// 2. NCF-Utility-Worker (WorkerCount = 4) on queue [default]
    /// </summary>
    public static IApplicationBuilder UseNightCodeForgeHangfireServers(this IApplicationBuilder app)
    {
        app.UseHangfireServer(CreateLlmInferenceServerOptions());
        app.UseHangfireServer(CreateUtilityServerOptions());
        return app;
    }
#pragma warning restore CS0618
}
