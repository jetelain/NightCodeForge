using System.Net;
using System.Net.Http.Headers;

namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Telemetry context emitted to notification callbacks when an HTTP attempt fails and enters backoff delay.
/// </summary>
public record RetryNotificationContext(
    Uri? RequestUri,
    HttpMethod Method,
    HttpRequestHeaders? Headers,
    int Attempt,
    int MaxRetries,
    TimeSpan Delay,
    TimeSpan ElapsedDuration,
    TimeSpan MaxDuration,
    string ErrorMessage,
    HttpStatusCode? StatusCode = null);

