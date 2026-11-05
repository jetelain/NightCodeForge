using System.Net;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Resilience;

/// <summary>
/// HTTP delegating handler providing automatic retry with exponential backoff and random jitter
/// for transient network failures, socket resets, timeouts, and 5xx/429/408 HTTP status codes.
/// Supports multi-minute outage resilience with elapsed duration limits and live notification callbacks.
/// </summary>
public class TransientHttpErrorRetryHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<Action<RetryNotificationContext>> RetryCallbackKey =
        new("NightCodeForge.Resilience.RetryCallback");

    public static readonly HttpRequestOptionsKey<Func<RetryNotificationContext, CancellationToken, Task>> AsyncRetryCallbackKey =
        new("NightCodeForge.Resilience.AsyncRetryCallback");

    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RetryPolicyOptions _options;
    private readonly IOperationsControlService? _operationsControlService;

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
        _operationsControlService = null;
    }

    /// <summary>
    /// DI-friendly constructor with operations pause control.
    /// </summary>
    public TransientHttpErrorRetryHandler(
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions options,
        IOperationsControlService operationsControlService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
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
        _operationsControlService = null;
    }

    /// <summary>
    /// Explicit constructor for wrapping an inner HttpMessageHandler with operations pause control.
    /// </summary>
    public TransientHttpErrorRetryHandler(
        HttpMessageHandler innerHandler,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions options,
        IOperationsControlService operationsControlService) : base(innerHandler)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? contentBytes = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        int attempt = 0;
        int timeoutRetries = 0;
        var maxRetries = Math.Max(0, _options.MaxRetries);
        var maxDuration = _options.MaxRetryDuration;
        long? outageStartTimestamp = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_operationsControlService?.IsPaused == true)
            {
                throw new OperationsPausedException();
            }

            attempt++;
            var currentRequest = CloneRequest(request, contentBytes);

            CancellationTokenSource? attemptTimeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            CancellationToken attemptToken;

            bool hasPerAttemptTimeout = _options.PerAttemptTimeout.HasValue &&
                                        _options.PerAttemptTimeout.Value > TimeSpan.Zero &&
                                        _options.PerAttemptTimeout.Value != Timeout.InfiniteTimeSpan;

            if (hasPerAttemptTimeout)
            {
                attemptTimeoutCts = new CancellationTokenSource(_options.PerAttemptTimeout!.Value, _timeProvider);
                if (cancellationToken.CanBeCanceled)
                {
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(attemptTimeoutCts.Token, cancellationToken);
                    attemptToken = linkedCts.Token;
                }
                else
                {
                    attemptToken = attemptTimeoutCts.Token;
                }
            }
            else
            {
                attemptToken = cancellationToken;
            }

            try
            {
                var response = await base.SendAsync(currentRequest, attemptToken);
                linkedCts?.Dispose();
                attemptTimeoutCts?.Dispose();

                string? body = null;
                bool bodyLoaded = false;
                async ValueTask<string?> GetResponseBodyAsync()
                {
                    if (!bodyLoaded)
                    {
                        body = response.Content != null ? await response.Content.ReadAsStringAsync(cancellationToken) : null;
                        bodyLoaded = true;
                    }
                    return body;
                }

                bool isTransientStatus = TransientErrorDetector.IsTransientStatusCode(response.StatusCode);
                if (isTransientStatus && response.StatusCode == HttpStatusCode.InternalServerError)
                {
                    var responseBody = await GetResponseBodyAsync();
                    if (TransientErrorDetector.IsNonRetryablePromptError(response.StatusCode, responseBody))
                    {
                        isTransientStatus = false;
                    }
                }

                if (isTransientStatus)
                {
                    outageStartTimestamp ??= _timeProvider.GetTimestamp();
                    var elapsed = _timeProvider.GetElapsedTime(outageStartTimestamp.Value);
                    if (attempt <= maxRetries && elapsed < maxDuration)
                    {
                        var calculatedDelay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                        var delay = ApplyRetryAfterHeader(response, calculatedDelay);

                        var remaining = maxDuration - elapsed;
                        if (remaining > TimeSpan.Zero && delay > remaining)
                        {
                            delay = remaining;
                        }

                        var responseBody = await GetResponseBodyAsync();
                        var errorMsg = !string.IsNullOrWhiteSpace(responseBody) ? responseBody : response.ReasonPhrase ?? response.StatusCode.ToString();

                        _logger.LogWarning(
                            "HTTP request {Method} {Uri} failed with transient status {StatusCode} (Body: {Body}). Retrying attempt {Attempt}/{MaxRetries} after {DelayMs}ms delay (elapsed {ElapsedMs}ms / {MaxDurationMs}ms)...",
                            request.Method, request.RequestUri, response.StatusCode, responseBody, attempt, maxRetries, delay.TotalMilliseconds, elapsed.TotalMilliseconds, maxDuration.TotalMilliseconds);

                        var notifyContext = new RetryNotificationContext(
                            request.RequestUri,
                            request.Method,
                            request.Headers,
                            attempt,
                            maxRetries,
                            delay,
                            elapsed,
                            maxDuration,
                            errorMsg,
                            response.StatusCode);

                        response.Dispose();
                        currentRequest.Dispose();

                        await NotifyRetryAsync(request, notifyContext, cancellationToken);
                        await WaitBackoffAsync(delay, cancellationToken);
                        continue;
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await GetResponseBodyAsync();
                    _logger.LogError(
                        "HTTP request {Method} {Uri} failed with status {StatusCode} ({StatusCodeInt}) after {Attempt} attempt(s) (Body: {Body}).",
                        request.Method, request.RequestUri, response.StatusCode, (int)response.StatusCode, attempt, responseBody);

                    TryEnrichReasonPhrase(response, responseBody);
                    return response;
                }

                return response;
            }
            catch (Exception ex)
            {
                currentRequest.Dispose();

                bool attemptTimedOut = attemptTimeoutCts?.IsCancellationRequested == true;
                linkedCts?.Dispose();
                attemptTimeoutCts?.Dispose();

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (_operationsControlService?.IsPaused == true || ex is OperationsPausedException)
                {
                    throw new OperationsPausedException();
                }

                bool isTimeout = attemptTimedOut || TransientErrorDetector.IsHttpTimeout(ex);

                if (isTimeout)
                {
                    outageStartTimestamp ??= _timeProvider.GetTimestamp();
                    var elapsed = _timeProvider.GetElapsedTime(outageStartTimestamp.Value);

                    if (timeoutRetries < _options.MaxTimeoutRetries && attempt <= maxRetries && elapsed < maxDuration)
                    {
                        timeoutRetries++;
                        var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                        var remaining = maxDuration - elapsed;
                        if (remaining > TimeSpan.Zero && delay > remaining)
                        {
                            delay = remaining;
                        }

                        int? timeoutSeconds = _options.PerAttemptTimeout.HasValue
                            ? (int)Math.Round(_options.PerAttemptTimeout.Value.TotalSeconds)
                            : (HttpErrorDetailHelper.TryExtractTimeoutSeconds(ex, out var extractedSec) ? extractedSec : null);
                        var errorMsg = timeoutSeconds.HasValue
                            ? $"LLM request timed out after {timeoutSeconds.Value}s"
                            : "LLM request timed out";

                        _logger.LogWarning(
                            ex,
                            "HTTP request {Method} {Uri} timed out ({ErrorMessage}). Retrying attempt {Attempt}/{MaxRetries} (timeout retry {TimeoutRetry}/{MaxTimeoutRetries}) after {DelayMs}ms delay (elapsed {ElapsedMs}ms / {MaxDurationMs}ms)...",
                            request.Method, request.RequestUri, errorMsg, attempt, maxRetries, timeoutRetries, _options.MaxTimeoutRetries, delay.TotalMilliseconds, elapsed.TotalMilliseconds, maxDuration.TotalMilliseconds);

                        var notifyContext = new RetryNotificationContext(
                            request.RequestUri,
                            request.Method,
                            request.Headers,
                            attempt,
                            maxRetries,
                            delay,
                            elapsed,
                            maxDuration,
                            errorMsg,
                            null);

                        await NotifyRetryAsync(request, notifyContext, cancellationToken);
                        await WaitBackoffAsync(delay, cancellationToken);
                        continue;
                    }
                    else
                    {
                        int? timeoutSeconds = _options.PerAttemptTimeout.HasValue
                            ? (int)Math.Round(_options.PerAttemptTimeout.Value.TotalSeconds)
                            : (HttpErrorDetailHelper.TryExtractTimeoutSeconds(ex, out var extractedSec) ? extractedSec : null);
                        var attemptsTried = attempt;
                        var timeoutMessage = timeoutSeconds.HasValue
                            ? $"The request timed out after {timeoutSeconds.Value} seconds ({attemptsTried} attempt(s) exhausted)."
                            : $"The request timed out ({attemptsTried} attempt(s) exhausted).";
                        var exhaustedEx = new TimeoutException(timeoutMessage, ex);
                        exhaustedEx.Data[ResilienceConstants.RetriedKey] = true;
                        throw exhaustedEx;
                    }
                }

                if (TransientErrorDetector.IsTransient(ex))
                {
                    outageStartTimestamp ??= _timeProvider.GetTimestamp();
                    var elapsed = _timeProvider.GetElapsedTime(outageStartTimestamp.Value);
                    if (attempt <= maxRetries && elapsed < maxDuration)
                    {
                        var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, _options.BaseDelay, _options.MaxDelay);
                        var remaining = maxDuration - elapsed;
                        if (remaining > TimeSpan.Zero && delay > remaining)
                        {
                            delay = remaining;
                        }

                        var errorMsg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;

                        _logger.LogWarning(
                            ex,
                            "HTTP request {Method} {Uri} failed with transient network error: {ErrorMessage}. Retrying attempt {Attempt}/{MaxRetries} after {DelayMs}ms delay (elapsed {ElapsedMs}ms / {MaxDurationMs}ms)...",
                            request.Method, request.RequestUri, errorMsg, attempt, maxRetries, delay.TotalMilliseconds, elapsed.TotalMilliseconds, maxDuration.TotalMilliseconds);

                        var notifyContext = new RetryNotificationContext(
                            request.RequestUri,
                            request.Method,
                            request.Headers,
                            attempt,
                            maxRetries,
                            delay,
                            elapsed,
                            maxDuration,
                            errorMsg,
                            null);

                        await NotifyRetryAsync(request, notifyContext, cancellationToken);
                        await WaitBackoffAsync(delay, cancellationToken);
                        continue;
                    }
                    else
                    {
                        ex.Data[ResilienceConstants.RetriedKey] = true;
                        throw;
                    }
                }

                throw;
            }
        }
    }

    private async Task WaitBackoffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_operationsControlService == null)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken);
            return;
        }

        if (_operationsControlService.IsPaused)
        {
            throw new OperationsPausedException();
        }

        using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnStateChanged(bool isPaused)
        {
            if (isPaused)
            {
                try
                {
                    pauseCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Justification: pauseCts may already be disposed if delay completed concurrently.
                }
            }
        }

        _operationsControlService.OnOperationsStateChanged += OnStateChanged;
        try
        {
            if (_operationsControlService.IsPaused)
            {
                throw new OperationsPausedException();
            }

            await Task.Delay(delay, _timeProvider, pauseCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _operationsControlService.IsPaused)
        {
            throw new OperationsPausedException();
        }
        finally
        {
            _operationsControlService.OnOperationsStateChanged -= OnStateChanged;
        }
    }

    private TimeSpan ApplyRetryAfterHeader(HttpResponseMessage response, TimeSpan calculatedDelay)
    {
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
        {
            return calculatedDelay;
        }

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
        {
            return calculatedDelay;
        }

        TimeSpan? retryAfterDelay = null;
        if (retryAfter.Delta.HasValue)
        {
            retryAfterDelay = retryAfter.Delta.Value;
        }
        else if (retryAfter.Date.HasValue)
        {
            var diff = retryAfter.Date.Value - _timeProvider.GetUtcNow();
            if (diff > TimeSpan.Zero)
            {
                retryAfterDelay = diff;
            }
        }

        if (retryAfterDelay.HasValue && retryAfterDelay.Value > calculatedDelay)
        {
            return retryAfterDelay.Value;
        }

        return calculatedDelay;
    }

    private async Task NotifyRetryAsync(HttpRequestMessage request, RetryNotificationContext context, CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(RetryCallbackKey, out var requestCallback) && requestCallback != null)
        {
            try
            {
                requestCallback(context);
            }
            catch (Exception ex) when (ex is not OperationsPausedException)
            {
                _logger.LogWarning(ex, "Retry callback threw an exception.");
            }
        }

        if (request.Options.TryGetValue(AsyncRetryCallbackKey, out var requestAsyncCallback) && requestAsyncCallback != null)
        {
            try
            {
                await requestAsyncCallback(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationsPausedException)
            {
                _logger.LogWarning(ex, "Async retry callback threw an exception.");
            }
        }

        if (_options.OnRetry != null)
        {
            try
            {
                _options.OnRetry(context);
            }
            catch (Exception ex) when (ex is not OperationsPausedException)
            {
                _logger.LogWarning(ex, "Options OnRetry callback threw an exception.");
            }
        }

        if (_options.OnRetryAsync != null)
        {
            try
            {
                await _options.OnRetryAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationsPausedException)
            {
                _logger.LogWarning(ex, "Options OnRetryAsync callback threw an exception.");
            }
        }
    }

    private static void TryEnrichReasonPhrase(HttpResponseMessage response, string? body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body)) return;

            var cleanError = HttpErrorDetailHelper.ExtractErrorMessageFromBody(body);
            if (string.IsNullOrWhiteSpace(cleanError)) return;

            var sanitized = HttpErrorDetailHelper.SanitizeForReasonPhrase(cleanError);
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                var prefix = !string.IsNullOrWhiteSpace(response.ReasonPhrase) ? response.ReasonPhrase : response.StatusCode.ToString();
                response.ReasonPhrase = $"{prefix}: {sanitized}";
            }
        }
        catch (Exception)
        {
            // Justification: Must 100% safe, to not hide the root cause
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
