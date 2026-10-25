using Hangfire;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.BackgroundJobs;

/// <summary>
/// Configuration extensions setting up Hangfire SQLite storage and dedicated background processing servers.
/// </summary>
public static class HangfireConfigurationExtensions
{
    /// <summary>
    /// Default timeout for clean shutdown of individual Hangfire background processing servers.
    /// </summary>
    public static readonly TimeSpan DefaultHangfireShutdownTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Default stop timeout given to workers to finish current job before aborting during server stop.
    /// </summary>
    public static readonly TimeSpan DefaultHangfireStopTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Default host shutdown timeout coordinating hosted service stops.
    /// </summary>
    public static readonly TimeSpan DefaultHostShutdownTimeout = TimeSpan.FromSeconds(15);

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
                .UseSQLiteStorage(dbPath, storageOptions ?? CreateDefaultStorageOptions());
        });

        services.AddScoped<ITaskJobQueue, HangfireTaskJobQueue>();

        return services;
    }

    /// <summary>
    /// Configures the two isolated Hangfire processing servers via IServiceCollection:
    /// 1. NCF-LLM-Inference-Worker (WorkerCount = 1, IsLightweightServer = true) on queues [llm-urgent, llm-code-review, llm-implementation, llm-task-review, llm-inference]
    /// 2. NCF-Utility-Worker (WorkerCount = 4, IsLightweightServer = false) on queue [default]
    /// Also wraps the registered hosted services in <see cref="GracefulHangfireServerHostedService"/> to handle cancellation tokens gracefully.
    /// </summary>
    public static IServiceCollection AddNightCodeForgeHangfireServers(this IServiceCollection services)
    {
        services.AddHangfireServer(options =>
        {
            CopyServerOptions(options, CreateLlmInferenceServerOptions());
        });

        services.AddHangfireServer(options =>
        {
            CopyServerOptions(options, CreateUtilityServerOptions());
        });

        WrapHangfireHostedServices(services);

        return services;
    }

    /// <summary>
    /// Returns the BackgroundJobServerOptions for the single-threaded LLM inference worker (WorkerCount = 1).
    /// Marked as lightweight to prevent running duplicate storage maintenance processes.
    /// </summary>
    public static BackgroundJobServerOptions CreateLlmInferenceServerOptions() =>
        new()
        {
            ServerName = "NCF-LLM-Inference-Worker",
            WorkerCount = 1,
            Queues = HangfireQueues.InferenceQueues,
            IsLightweightServer = true,
            ShutdownTimeout = DefaultHangfireShutdownTimeout,
            StopTimeout = DefaultHangfireStopTimeout
        };

    /// <summary>
    /// Returns the BackgroundJobServerOptions for the utility and Git operations worker (WorkerCount = 4).
    /// Handles storage-wide background maintenance processes.
    /// </summary>
    public static BackgroundJobServerOptions CreateUtilityServerOptions() =>
        new()
        {
            ServerName = "NCF-Utility-Worker",
            WorkerCount = 4,
            Queues = new[] { HangfireQueues.Default },
            IsLightweightServer = false,
            ShutdownTimeout = DefaultHangfireShutdownTimeout,
            StopTimeout = DefaultHangfireStopTimeout
        };

    private static void CopyServerOptions(BackgroundJobServerOptions target, BackgroundJobServerOptions source)
    {
        target.ServerName = source.ServerName;
        target.WorkerCount = source.WorkerCount;
        target.Queues = source.Queues;
        target.IsLightweightServer = source.IsLightweightServer;
        target.ShutdownTimeout = source.ShutdownTimeout;
        target.StopTimeout = source.StopTimeout;
    }

    private static void WrapHangfireHostedServices(IServiceCollection services)
    {
        for (int i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationFactory != null)
            {
                var originalFactory = descriptor.ImplementationFactory;
                services[i] = ServiceDescriptor.Describe(
                    typeof(IHostedService),
                    sp =>
                    {
                        var instance = (IHostedService)originalFactory(sp);
                        if (instance is BackgroundJobServerHostedService && instance is not GracefulHangfireServerHostedService)
                        {
                            var logger = sp.GetRequiredService<ILogger<GracefulHangfireServerHostedService>>();
                            return new GracefulHangfireServerHostedService(instance, logger);
                        }
                        return instance;
                    },
                    descriptor.Lifetime);
            }
        }
    }

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
    /// <summary>
    /// Creates the default SQLiteStorageOptions with 4-hour InvisibilityTimeout to support atomic cohorts.
    /// </summary>
    public static SQLiteStorageOptions CreateDefaultStorageOptions() => new()
    {
        QueuePollInterval = TimeSpan.FromSeconds(1),
        InvisibilityTimeout = TimeSpan.FromHours(4)
    };
#pragma warning restore CS0618
}

