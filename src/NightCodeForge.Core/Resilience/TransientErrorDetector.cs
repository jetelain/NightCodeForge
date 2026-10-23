using System.Net;
using System.Net.Sockets;

namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Detector for transient network, socket, transport, and HTTP failures that should trigger automatic retry with jitter.
/// </summary>
public static class TransientErrorDetector
{
    /// <summary>
    /// Evaluates whether an exception represents a transient network, socket, or HTTP error.
    /// Returns false if the exception was already retried by an inner handler or is an overall request timeout.
    /// </summary>
    public static bool IsTransient(Exception? ex)
    {
        if (ex == null) return false;

        // Skip exceptions that have already been retried and exhausted by an inner resilience handler
        if (ex.Data.Contains(ResilienceConstants.RetriedKey))
        {
            return false;
        }

        // Do not retry overall request timeouts or explicit cancellations
        if (ex is OperationCanceledException or TaskCanceledException)
        {
            return false;
        }

        // Refused connections indicate a misconfigured or stopped endpoint: fail fast
        if (IsConnectionRefused(ex))
        {
            return false;
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
    /// Checks whether a socket error code represents a transient connectivity or connection reset condition.
    /// Excludes ConnectionRefused (to fail fast on endpoint misconfigurations) and TryAgain (WSAEWOULDBLOCK).
    /// </summary>
    public static bool IsTransientSocketError(SocketError error) =>
        error is SocketError.ConnectionReset                  // 10054 (WSAECONNRESET)
              or SocketError.ConnectionAborted                // 10053 (WSAECONNABORTED)
              or SocketError.TimedOut                         // 10060 (WSAETIMEDOUT)
              or SocketError.HostUnreachable                  // 10065 (WSAEHOSTUNREACH)
              or SocketError.NetworkUnreachable               // 10051 (WSAENETUNREACH)
              or SocketError.NetworkReset                     // 10052 (WSAENETRESET)
              or SocketError.Shutdown                         // 10058 (WSAESHUTDOWN)
              or SocketError.NotConnected;

    private static bool IsConnectionRefused(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }
}
