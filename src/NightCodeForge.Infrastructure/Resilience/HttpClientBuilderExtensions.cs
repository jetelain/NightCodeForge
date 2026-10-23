using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
            new TransientHttpErrorRetryHandler(
                sp.GetRequiredService<ILogger<TransientHttpErrorRetryHandler>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOptions<RetryPolicyOptions>>().Value));

        return builder.AddHttpMessageHandler<TransientHttpErrorRetryHandler>();
    }
}
