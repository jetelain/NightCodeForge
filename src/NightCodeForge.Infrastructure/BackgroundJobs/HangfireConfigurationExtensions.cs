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
        var dbPath = sqliteConnectionString;
        if (dbPath.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = dbPath.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                {
                    dbPath = trimmed["Data Source=".Length..].Trim();
                    break;
                }
            }
        }

        services.AddHangfire(config =>
        {
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSQLiteStorage(dbPath, storageOptions ?? new SQLiteStorageOptions
                {
                    QueuePollInterval = TimeSpan.FromSeconds(1)
                });
        });

        services.AddScoped<ITaskJobQueue, HangfireTaskJobQueue>();

        return services;
    }

    /// <summary>
    /// Configures the two isolated Hangfire processing servers via IServiceCollection:
    /// 1. NCF-LLM-Inference-Worker (WorkerCount = 1) on queues [llm-urgent, llm-code-review, llm-implementation, llm-task-review, llm-inference]
    /// 2. NCF-Utility-Worker (WorkerCount = 4) on queue [default]
    /// </summary>
    public static IServiceCollection AddNightCodeForgeHangfireServers(this IServiceCollection services)
    {
        services.AddHangfireServer(options =>
        {
            var llmOptions = CreateLlmInferenceServerOptions();
            options.ServerName = llmOptions.ServerName;
            options.WorkerCount = llmOptions.WorkerCount;
            options.Queues = llmOptions.Queues;
        });

        services.AddHangfireServer(options =>
        {
            var utilityOptions = CreateUtilityServerOptions();
            options.ServerName = utilityOptions.ServerName;
            options.WorkerCount = utilityOptions.WorkerCount;
            options.Queues = utilityOptions.Queues;
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
            Queues = HangfireQueues.InferenceQueues
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
