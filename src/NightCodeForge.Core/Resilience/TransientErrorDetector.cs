using System.Net;
using System.Net.Sockets;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Detector for transient network, socket, transport, and HTTP failures that should trigger automatic retry with jitter.
/// </summary>
public static class TransientErrorDetector
{
    /// <summary>
    /// Evaluates whether an exception represents a transient network, socket, or HTTP error.
    /// Returns false if the exception was already retried by an inner handler, is an overall request timeout,
    /// or represents a pause / explicit cancellation.
    /// </summary>
    public static bool IsTransient(Exception? ex)
    {
        if (ex == null) return false;

        // Skip exceptions (or wrapped inner exceptions) that have already been retried, cancelled, or paused
        if (HasNonRetryableMarkerOrCancellation(ex))
        {
            return false;
        }

        if (IsHttpTimeout(ex))
        {
            return true;
        }

        if (HttpErrorDetailHelper.TryGetStreamTimeoutDiagnostics(ex, out var streamDiag) &&
            streamDiag is { Phase: StreamTimeoutPhase.MidStreamConnectionClosed })
        {
            return true;
        }

        // Unpack aggregate exceptions
        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(IsTransient);
        }

        if (ex is SocketException socketEx)
        {
            return IsTransientSocketError(socketEx.SocketErrorCode);
        }

        if (ex is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded })
        {
            return true;
        }

        if (ex is HttpRequestException httpEx)
        {
            // HTTP status failures are retried exclusively by TransientHttpErrorRetryHandler.
            // Callers must not retry them again once the handler has exhausted its attempts.
            if (httpEx.StatusCode.HasValue)
            {
                return false;
            }

            if (httpEx.HttpRequestError is HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.NameResolutionError)
            {
                if (httpEx.InnerException is SocketException innerSocketEx)
                {
                    return IsTransientSocketError(innerSocketEx.SocketErrorCode);
                }

                return true;
            }

            if (httpEx.InnerException != null && IsTransient(httpEx.InnerException))
            {
                return true;
            }
        }

        if (ex is IOException ioEx && ioEx.InnerException != null && IsTransient(ioEx.InnerException))
        {
            return true;
        }

        // Recursively inspect inner exceptions (e.g. Wrapped exceptions from MAF or SocketsHttpHandler)
        if (ex.InnerException != null && IsTransient(ex.InnerException))
        {
            return true;
        }

        return false;
    }

    private static readonly string[] NonRetryablePromptErrorMarkers =
    [
        "no user query found in messages",
        "chat prompt error"
    ];

    /// <summary>
    /// Checks whether an HTTP status code represents a transient server or rate limiting condition.
    /// HTTP 500 is included because Ollama reports many transient conditions (memory pressure, model load, runner crash) as 500.
    /// </summary>
    public static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout           // 408
                   or HttpStatusCode.TooManyRequests          // 429
                   or HttpStatusCode.InternalServerError      // 500
                   or HttpStatusCode.BadGateway               // 502
                   or HttpStatusCode.ServiceUnavailable       // 503
                   or HttpStatusCode.GatewayTimeout;          // 504

    /// <summary>
    /// Checks whether an HTTP 500 error body contains a deterministic prompt or chat template rendering error
    /// that will never succeed on retry.
    /// </summary>
    public static bool IsNonRetryablePromptError(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode != HttpStatusCode.InternalServerError || string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        foreach (var marker in NonRetryablePromptErrorMarkers)
        {
            if (responseBody.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether an HTTP 503 response body represents a structured OllamaRouter preload qualification rejection
    /// (which should be handled immediately by the caller's soft-intent fallback rather than retried with backoff).
    /// </summary>
    public static bool IsNonRetryablePreloadQualificationError(HttpStatusCode statusCode, string? responseBody)
    {
        if (statusCode != HttpStatusCode.ServiceUnavailable || string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        return responseBody.Contains("\"loadAccepted\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether a socket error code represents a transient connectivity, DNS, or connection reset condition.
    /// Includes ConnectionRefused (to wait during local service restarts) and transient DNS/interface transitions.
    /// </summary>
    public static bool IsTransientSocketError(SocketError error) =>
        error is SocketError.ConnectionReset                  // 10054 (WSAECONNRESET)
              or SocketError.ConnectionAborted                // 10053 (WSAECONNABORTED)
              or SocketError.ConnectionRefused                // 10061 (WSAECONNREFUSED)
              or SocketError.TimedOut                         // 10060 (WSAETIMEDOUT)
              or SocketError.HostUnreachable                  // 10065 (WSAEHOSTUNREACH)
              or SocketError.NetworkUnreachable               // 10051 (WSAENETUNREACH)
              or SocketError.NetworkReset                     // 10052 (WSAENETRESET)
              or SocketError.Shutdown                         // 10058 (WSAESHUTDOWN)
              or SocketError.NotConnected                     // 10057 (WSAENOTCONN)
              or SocketError.TryAgain                         // 11002 (WSATRY_AGAIN)
              or SocketError.HostNotFound                     // 11001 (WSAHOST_NOT_FOUND)
              or SocketError.NoData                           // 11004 (WSANO_DATA)
              or SocketError.OperationAborted                 // 995   (WSA_OPERATION_ABORTED)
              or SocketError.Interrupted;                     // 10004 (WSAEINTR)

    /// <summary>
    /// Checks whether an exception chain represents an HTTP/LLM request timeout (e.g. HttpClient.Timeout or TimeoutException),
    /// excluding pre-connection socket/DNS errors and operations pause.
    /// </summary>
    public static bool IsHttpTimeout(Exception? ex)
    {
        if (ex == null) return false;

        bool hasTimeout = false;
        var queue = new Queue<Exception>();
        queue.Enqueue(ex);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (current is OperationsPausedException)
            {
                return false;
            }

            if (current is SocketException)
            {
                return false;
            }

            if (current is HttpRequestException httpEx &&
                httpEx.HttpRequestError is HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError
                    or HttpRequestError.NameResolutionError)
            {
                return false;
            }

            if (current is TimeoutException)
            {
                hasTimeout = true;
            }
            else if (current is LlmStreamTimeoutException streamTimeoutEx &&
                     streamTimeoutEx.Diagnostics.Phase is StreamTimeoutPhase.PreHeaderPrefill
                         or StreamTimeoutPhase.FirstTokenPrefill
                         or StreamTimeoutPhase.MidStreamIdleStall)
            {
                hasTimeout = true;
            }
            else if (current.Data.Contains(StreamTimeoutDiagnostics.DataKey) &&
                     current.Data[StreamTimeoutDiagnostics.DataKey] is StreamTimeoutDiagnostics diag &&
                     diag.Phase is StreamTimeoutPhase.PreHeaderPrefill
                         or StreamTimeoutPhase.FirstTokenPrefill
                         or StreamTimeoutPhase.MidStreamIdleStall)
            {
                hasTimeout = true;
            }

            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (inner != null) queue.Enqueue(inner);
                }
            }
            else if (current.InnerException != null)
            {
                queue.Enqueue(current.InnerException);
            }
        }

        return hasTimeout;
    }

    private static bool HasNonRetryableMarkerOrCancellation(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(ResilienceConstants.RetriedKey))
            {
                return true;
            }

            if (current is OperationsPausedException)
            {
                return true;
            }

            if (current is OperationCanceledException or TaskCanceledException)
            {
                if (!IsHttpTimeout(ex) && !HttpErrorDetailHelper.TryGetStreamTimeoutDiagnostics(ex, out _))
                {
                    return true;
                }
            }

            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (HasNonRetryableMarkerOrCancellation(inner))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
