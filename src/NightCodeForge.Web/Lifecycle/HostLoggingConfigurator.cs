namespace NightCodeForge.Web.Lifecycle;

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;

/// <summary>
/// Centralizes Serilog configuration for both bootstrap and full host lifetimes,
/// keeping operational noise out of the console while ensuring all lifecycle,
/// listening, shutdown, and fatal diagnostic events appear in both console and file logs.
/// </summary>
public static class HostLoggingConfigurator
{
    public const string LogsDirectoryConfigKey = "Logging:LogsDirectory";
    public const string DefaultLogsDirectoryName = "logs";
    public const string LogFileNamePattern = "nightcodeforge-.log";
    public const int RetainedFileCountLimit = 31;
    public const string FileOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
    public const string ConsoleOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    public const string NightCodeForgeNamespacePrefix = "NightCodeForge";
    public const string LifecycleSourceContextPrefix = "NightCodeForge.Web.Lifecycle";
    public const string HostingLifetimeSourceContext = "Microsoft.Hosting.Lifetime";

    /// <summary>
    /// Resolves and ensures the existence of the rolling logs directory from configuration.
    /// </summary>
    public static string ResolveLogsDirectory(IConfiguration configuration, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var rawLogsDir = configuration[LogsDirectoryConfigKey];
        if (string.IsNullOrWhiteSpace(rawLogsDir))
        {
            rawLogsDir = DefaultLogsDirectoryName;
        }

        var logsDirectory = Path.IsPathRooted(rawLogsDir)
            ? rawLogsDir
            : Path.Combine(baseDirectory, rawLogsDir);

        Directory.CreateDirectory(logsDirectory);
        return logsDirectory;
    }

    /// <summary>
    /// Returns the daily rolling Serilog file path pattern inside <paramref name="logsDirectory"/>.
    /// </summary>
    public static string GetLogFilePath(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        return Path.Combine(logsDirectory, LogFileNamePattern);
    }

    /// <summary>
    /// Determines whether a Serilog event should be echoed to the console sink.
    /// Fatal events, all <c>NightCodeForge.Web.Lifecycle</c> events, and
    /// <c>Microsoft.Hosting.Lifetime</c> warnings/errors are surfaced to the console.
    /// </summary>
    public static bool ShouldWriteToConsole(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (logEvent.Level >= LogEventLevel.Fatal)
        {
            return true;
        }

        if (!logEvent.Properties.TryGetValue(Serilog.Core.Constants.SourceContextPropertyName, out var propertyValue) ||
            propertyValue is not ScalarValue { Value: string sourceContext })
        {
            return false;
        }

        if (sourceContext.Equals(LifecycleSourceContextPrefix, StringComparison.Ordinal) ||
            sourceContext.StartsWith(LifecycleSourceContextPrefix + ".", StringComparison.Ordinal))
        {
            return logEvent.Level >= LogEventLevel.Information;
        }

        return sourceContext.Equals(HostingLifetimeSourceContext, StringComparison.Ordinal) &&
               logEvent.Level >= LogEventLevel.Warning;
    }

    /// <summary>
    /// Configures Serilog with file and filtered console sinks for the web host.
    /// </summary>
    public static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider? services,
        string logFilePath)
    {
        return Configure(
            loggerConfiguration,
            configuration,
            services,
            logFilePath,
            consoleLc => consoleLc.WriteTo.Console(outputTemplate: ConsoleOutputTemplate));
    }

    internal static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        IServiceProvider? services,
        string logFilePath,
        Action<LoggerConfiguration> configureConsoleSink)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentNullException.ThrowIfNull(configureConsoleSink);

        loggerConfiguration
            .MinimumLevel.Warning()
            .MinimumLevel.Override(NightCodeForgeNamespacePrefix, LogEventLevel.Information)
            .ReadFrom.Configuration(configuration);

        if (services is not null)
        {
            loggerConfiguration.ReadFrom.Services(services);
        }

        return loggerConfiguration
            .Enrich.FromLogContext()
            .WriteTo.Logger(consoleLc => configureConsoleSink(consoleLc.Filter.ByIncludingOnly(ShouldWriteToConsole)))
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                shared: true,
                outputTemplate: FileOutputTemplate);
    }

    /// <summary>
    /// Creates a reloadable bootstrap logger so startup and pre-build failures are captured
    /// before <c>builder.Build()</c> finishes configuring the DI-backed Serilog pipeline.
    /// </summary>
    public static ReloadableLogger CreateBootstrapLogger(IConfiguration configuration, string logFilePath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        return Configure(new LoggerConfiguration(), configuration, services: null, logFilePath)
            .CreateBootstrapLogger();
    }
}
