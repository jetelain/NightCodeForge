using System.Net;
using System.Net.Sockets;
using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TransientErrorDetectorTests
{
    [Fact]
    public void IsTransient_ReturnsTrue_ForExactReportedFrenchNetworkError()
    {
        var innerSocket = new SocketException((int)SocketError.ConnectionReset);
        var ioException = new IOException(
            "Unable to read data from the transport connection: Une connexion existante a dû être fermée par l’hôte distant..",
            innerSocket);

        Assert.True(TransientErrorDetector.IsTransient(ioException));

        var wrappedInHttp = new HttpRequestException("Error while copying content to a stream.", ioException);
        Assert.True(TransientErrorDetector.IsTransient(wrappedInHttp));
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForHttpIOExceptionResponseEnded()
    {
        var ex = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        Assert.True(TransientErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_ForIOExceptionMatchedOnlyByMessageText()
    {
        Assert.False(TransientErrorDetector.IsTransient(new IOException("Unable to read data from the transport connection: forcibly closed.")));
        Assert.False(TransientErrorDetector.IsTransient(new IOException("Broken pipe")));
    }

    [Fact]
    public void IsTransient_ReturnsTrue_WhenConnectionRefusedIsWrappedAsConnectionError()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var httpEx = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused", refused);

        Assert.True(TransientErrorDetector.IsTransient(httpEx));
    }

    [Fact]
    public void IsTransient_ReturnsTrue_ForConnectionErrorWithoutRefusal()
    {
        var reset = new SocketException((int)SocketError.ConnectionReset);
        var httpEx = new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset", reset);

        Assert.True(TransientErrorDetector.IsTransient(httpEx));
    }

    [Theory]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkReset)]
    [InlineData(SocketError.Shutdown)]
    [InlineData(SocketError.NotConnected)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    [InlineData(SocketError.OperationAborted)]
    [InlineData(SocketError.Interrupted)]
    public void IsTransient_ReturnsTrue_ForTransientSocketErrors(SocketError socketError)
    {
        var ex = new SocketException((int)socketError);
        Assert.True(TransientErrorDetector.IsTransient(ex));
    }

    [Theory]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.InvalidArgument)]
    public void IsTransient_ReturnsFalse_ForNonTransientSocketErrors(SocketError socketError)
    {
        var ex = new SocketException((int)socketError);
        Assert.False(TransientErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_ForOperationsPausedException()
    {
        Assert.False(TransientErrorDetector.IsTransient(new NightCodeForge.Core.Exceptions.OperationsPausedException()));
        Assert.False(TransientErrorDetector.IsTransient(new HttpRequestException("Error", new NightCodeForge.Core.Exceptions.OperationsPausedException())));
        Assert.False(TransientErrorDetector.IsTransient(new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection error",
            new IOException("Aborted", new NightCodeForge.Core.Exceptions.OperationsPausedException()))));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_WhenConnectionErrorWrapsNonTransientSocketErrorOrRetriedException()
    {
        var nonTransientSocket = new SocketException((int)SocketError.AccessDenied);
        var wrappedSocketEx = new HttpRequestException(HttpRequestError.ConnectionError, "Permission denied", nonTransientSocket);
        Assert.False(TransientErrorDetector.IsTransient(wrappedSocketEx));

        var innerRetried = new SocketException((int)SocketError.ConnectionReset);
        innerRetried.Data[ResilienceConstants.RetriedKey] = true;
        var wrappedRetriedEx = new HttpRequestException(HttpRequestError.ConnectionError, "Reset", innerRetried);
        Assert.False(TransientErrorDetector.IsTransient(wrappedRetriedEx));

        var deeplyCancelled = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Cancelled",
            new IOException("Stream cancelled", new OperationCanceledException()));
        Assert.False(TransientErrorDetector.IsTransient(deeplyCancelled));
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void IsTransient_ReturnsFalse_ForHttpStatusExceptions_BecauseHandlerOwnsStatusRetries(HttpStatusCode statusCode)
    {
        var ex = new HttpRequestException("Server error", inner: null, statusCode: statusCode);
        Assert.False(TransientErrorDetector.IsTransient(ex));
        Assert.True(TransientErrorDetector.IsTransientStatusCode(statusCode));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void IsTransient_ReturnsFalse_ForNonTransientHttpStatusCodes(HttpStatusCode statusCode)
    {
        var ex = new HttpRequestException("Error", inner: null, statusCode: statusCode);
        Assert.False(TransientErrorDetector.IsTransient(ex));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void IsTransientStatusCode_Is500Inclusive_AndExcludesClientErrors(HttpStatusCode statusCode)
    {
        Assert.Equal(statusCode == HttpStatusCode.InternalServerError, TransientErrorDetector.IsTransientStatusCode(statusCode));
    }

    [Fact]
    public void IsTransient_And_IsHttpTimeout_Behavior_ForTimeoutAndCancellationExceptions()
    {
        var timeoutEx = new TimeoutException("Request timeout");
        Assert.True(TransientErrorDetector.IsHttpTimeout(timeoutEx));
        Assert.True(TransientErrorDetector.IsTransient(timeoutEx));

        var httpClientTimeoutEx = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.",
            new TimeoutException("The operation was canceled."));
        Assert.True(TransientErrorDetector.IsHttpTimeout(httpClientTimeoutEx));
        Assert.True(TransientErrorDetector.IsTransient(httpClientTimeoutEx));

        // Caller cancellation (without inner TimeoutException) must return false
        var callerTaskCanceledEx = new TaskCanceledException("HttpClient timeout");
        Assert.False(TransientErrorDetector.IsHttpTimeout(callerTaskCanceledEx));
        Assert.False(TransientErrorDetector.IsTransient(callerTaskCanceledEx));

        var callerOpCanceledEx = new OperationCanceledException("Cancelled");
        Assert.False(TransientErrorDetector.IsHttpTimeout(callerOpCanceledEx));
        Assert.False(TransientErrorDetector.IsTransient(callerOpCanceledEx));
    }

    [Fact]
    public void IsHttpTimeout_ReturnsFalse_And_IsTransient_ReturnsTrue_ForSocketAndConnectTimeouts()
    {
        var socketTimedOut = new SocketException((int)SocketError.TimedOut);
        Assert.False(TransientErrorDetector.IsHttpTimeout(socketTimedOut));
        Assert.True(TransientErrorDetector.IsTransient(socketTimedOut));

        var connectError = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection timed out",
            new TimeoutException("Connect timeout"));
        Assert.False(TransientErrorDetector.IsHttpTimeout(connectError));
        Assert.True(TransientErrorDetector.IsTransient(connectError));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_ForTimeoutException_MarkedAsAlreadyRetried()
    {
        var timeoutEx = new TimeoutException("The request timed out after 900 seconds (2 attempt(s) exhausted).");
        timeoutEx.Data[ResilienceConstants.RetriedKey] = true;

        Assert.True(TransientErrorDetector.IsHttpTimeout(timeoutEx));
        Assert.False(TransientErrorDetector.IsTransient(timeoutEx));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_WhenExceptionMarkedAsAlreadyRetried()
    {
        var innerSocket = new SocketException((int)SocketError.ConnectionReset);
        var ex = new IOException("Transport error", innerSocket);
        ex.Data[ResilienceConstants.RetriedKey] = true;

        Assert.False(TransientErrorDetector.IsTransient(ex));
    }

    [Fact]
    public void IsTransient_ReturnsTrue_WhenNestedInAggregateException()
    {
        var inner = new SocketException((int)SocketError.ConnectionReset);
        var agg = new AggregateException("Batch failure", new InvalidOperationException("Unrelated"), inner);
        Assert.True(TransientErrorDetector.IsTransient(agg));
    }

    [Fact]
    public void IsTransient_ReturnsFalse_ForNonTransientExceptions()
    {
        Assert.False(TransientErrorDetector.IsTransient(null));
        Assert.False(TransientErrorDetector.IsTransient(new ArgumentNullException("param")));
        Assert.False(TransientErrorDetector.IsTransient(new InvalidOperationException("Invalid state")));
        Assert.False(TransientErrorDetector.IsTransient(new FileNotFoundException("File missing")));
        Assert.False(TransientErrorDetector.IsTransient(new HttpRequestException("Not found", inner: null, statusCode: HttpStatusCode.NotFound)));
        Assert.False(TransientErrorDetector.IsTransient(new HttpRequestException("Bad request", inner: null, statusCode: HttpStatusCode.BadRequest)));
        Assert.False(TransientErrorDetector.IsTransient(new HttpRequestException("Forbidden", inner: null, statusCode: HttpStatusCode.Forbidden)));
    }
}
