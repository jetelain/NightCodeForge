using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;

namespace NightCodeForge.Infrastructure.Resilience;

/// <summary>
/// Extension methods for configuring transient HTTP error retry policies on IHttpClientBuilder instances.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Binds <typeparamref name="TOptions"/> from <see cref="IConfiguration"/> section <paramref name="sectionName"/>
    /// as the base configuration layer so explicit <c>services.Configure&lt;TOptions&gt;</c> delegates always take precedence
    /// regardless of registration order.
    /// </summary>
    public static IServiceCollection BindConfigurationSectionAsDefault<TOptions>(
        this IServiceCollection services,
        string sectionName)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        services.AddOptions<TOptions>();
        if (!services.Any(d => d.ServiceType == typeof(IConfigureOptions<TOptions>) &&
                               (d.ImplementationType == typeof(ConfigurationSectionConfigureOptions<TOptions>) ||
                                d.ImplementationFactory?.Method.ReturnType == typeof(ConfigurationSectionConfigureOptions<TOptions>))))
        {
            Func<IServiceProvider, ConfigurationSectionConfigureOptions<TOptions>> factory =
                sp => new ConfigurationSectionConfigureOptions<TOptions>(sp.GetService<IConfiguration>(), sectionName);
            services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<TOptions>, ConfigurationSectionConfigureOptions<TOptions>>(factory));
        }

        return services;
    }

    /// <summary>
    /// Attaches the TransientHttpErrorRetryHandler to the HTTP client pipeline, enabling automatic
    /// retries with exponential backoff and random jitter for transient socket, transport, and 5xx errors.
    /// </summary>
    public static IHttpClientBuilder AddTransientHttpErrorRetry(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.BindConfigurationSectionAsDefault<RetryPolicyOptions>(RetryPolicyOptions.SectionName);
        builder.Services.TryAddTransient<TransientHttpErrorRetryHandler>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TransientHttpErrorRetryHandler>>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            var options = sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value;
            var opsControl = sp.GetService<IOperationsControlService>();
            return opsControl != null
                ? new TransientHttpErrorRetryHandler(logger, timeProvider, options, opsControl)
                : new TransientHttpErrorRetryHandler(logger, timeProvider, options);
        });

        return builder.AddHttpMessageHandler<TransientHttpErrorRetryHandler>();
    }

    private sealed class ConfigurationSectionConfigureOptions<TOptions> : IConfigureOptions<TOptions>
        where TOptions : class
    {
        private readonly IConfiguration? _configuration;
        private readonly string _sectionName;

        public ConfigurationSectionConfigureOptions(IConfiguration? configuration, string sectionName)
        {
            _configuration = configuration;
            _sectionName = sectionName;
        }

        public void Configure(TOptions options)
        {
            _configuration?.GetSection(_sectionName).Bind(options);
        }
    }
}
