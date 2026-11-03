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
    /// Attaches the TransientHttpErrorRetryHandler to the HTTP client pipeline, enabling automatic
    /// retries with exponential backoff and random jitter for transient socket, transport, and 5xx errors.
    /// </summary>
    public static IHttpClientBuilder AddTransientHttpErrorRetry(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<RetryPolicyOptions>();
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
}
