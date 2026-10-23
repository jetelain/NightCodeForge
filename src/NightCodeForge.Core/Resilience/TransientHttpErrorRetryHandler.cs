using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Resilience;

/// <summary>
/// HTTP delegating handler providing automatic retry with exponential backoff and random jitter
/// for transient network failures, socket resets, timeouts, and 5xx/429/408 HTTP status codes.
/// </summary>
public class TransientHttpErrorRetryHandler : DelegatingHandler
{
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RetryPolicyOptions _options;

    /// <summary>
    /// DI-friendly constructor when used with IHttpClientBuilder.AddHttpMessageHandler.
    /// </summary>
    public TransientHttpErrorRetryHandler(
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Explicit constructor for wrapping an inner HttpMessageHandler directly (e.g. SocketsHttpHandler).
    /// </summary>
    public TransientHttpErrorRetryHandler(
        HttpMessageHandler innerHandler,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions options) : base(innerHandler)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? contentBytes = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        int attempt = 0;
        var maxRetries = Math.Max(0, _options.MaxRetries);

        while (true)
        {
            attempt++;
            var currentRequest = CloneRequest(request, contentBytes);

            try
            {
                var response = await base.SendAsync(currentRequest, cancellationToken);

                if (attempt <= maxRetries && TransientErrorDetector.IsTransientStatusCode(response.StatusCode))
                {
                    var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "HTTP request {Method} {Uri} failed with transient status {StatusCode} (Body: {Body}). Retrying attempt {Attempt}/{MaxRetries} after {DelayMs}ms delay...",
                        request.Method, request.RequestUri, response.StatusCode, body, attempt, maxRetries, delay.TotalMilliseconds);

                    response.Dispose();
                    await Task.Delay(delay, _timeProvider, cancellationToken);
                    continue;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogError(
                        "HTTP request {Method} {Uri} failed with 500 InternalServerError after {Attempt} attempt(s) (Body: {Body}).",
                        request.Method, request.RequestUri, attempt, body);
                    return response;
                }

                return response;
            }
            catch (Exception ex) when (TransientErrorDetector.IsTransient(ex))
            {
                if (attempt <= maxRetries)
                {
                    var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                    var errorMsg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;

                    _logger.LogWarning(
                        ex,
                        "HTTP request {Method} {Uri} failed with transient network error: {ErrorMessage}. Retrying attempt {Attempt}/{MaxRetries} after {DelayMs}ms delay...",
                        request.Method, request.RequestUri, errorMsg, attempt, maxRetries, delay.TotalMilliseconds);

                    await Task.Delay(delay, _timeProvider, cancellationToken);
                }
                else
                {
                    // Tag exception to prevent outer callers (e.g. CrucibleAgentHarness) from multiplying retries
                    ex.Data[ResilienceConstants.RetriedKey] = true;
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Clones an HttpRequestMessage for retry attempts. Request content is buffered into memory
    /// as a byte array to allow repeatable sends. This in-memory buffering is suitable and expected
    /// for Ollama and inference JSON payloads.
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage request, byte[]? contentBytes)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var (headerName, headerValues) in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(headerName, headerValues);
        }

        foreach (var option in request.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (contentBytes != null)
        {
            var byteContent = new ByteArrayContent(contentBytes);
            if (request.Content != null)
            {
                foreach (var (headerName, headerValues) in request.Content.Headers)
                {
                    byteContent.Headers.TryAddWithoutValidation(headerName, headerValues);
                }
            }
            clone.Content = byteContent;
        }

        return clone;
    }
}
