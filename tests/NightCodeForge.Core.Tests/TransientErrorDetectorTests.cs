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
    public void IsTransient_ReturnsFalse_WhenConnectionRefusedIsWrappedAsConnectionError()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var httpEx = new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused", refused);

        Assert.False(TransientErrorDetector.IsTransient(httpEx));
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
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkReset)]
    [InlineData(SocketError.Shutdown)]
    [InlineData(SocketError.NotConnected)]
    public void IsTransient_ReturnsTrue_ForTransientSocketErrors(SocketError socketError)
    {
        var ex = new SocketException((int)socketError);
        Assert.True(TransientErrorDetector.IsTransient(ex));
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TryAgain)]
    public void IsTransient_ReturnsFalse_ForNonTransientSocketErrors(SocketError socketError)
    {
        var ex = new SocketException((int)socketError);
        Assert.False(TransientErrorDetector.IsTransient(ex));
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
    public void IsTransient_ReturnsFalse_ForTimeoutAndCancellationExceptions()
    {
        Assert.False(TransientErrorDetector.IsTransient(new TimeoutException("Request timeout")));
        Assert.False(TransientErrorDetector.IsTransient(new TaskCanceledException("HttpClient timeout")));
        Assert.False(TransientErrorDetector.IsTransient(new OperationCanceledException("Cancelled")));
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
