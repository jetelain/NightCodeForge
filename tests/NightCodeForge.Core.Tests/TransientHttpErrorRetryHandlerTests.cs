using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TransientHttpErrorRetryHandlerTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Invocations { get; } = new();

        public void EnqueueResponse(HttpStatusCode statusCode, string content = "")
        {
            _responses.Enqueue(req => new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }

        public void EnqueueRawResponse(HttpResponseMessage response)
        {
            _responses.Enqueue(req => response);
        }

        public void EnqueueException(Exception ex)
        {
            _responses.Enqueue(req => throw ex);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Invocations.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No more mock responses configured.");
            }

            var factory = _responses.Dequeue();
            return Task.FromResult(factory(request));
        }
    }

    [Fact]
    public async Task SendAsync_RetriesOnTransient503_AndSucceeds()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "Busy");
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        var options = new RetryPolicyOptions
        {
            MaxRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/generate")
        {
            Content = new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-Header", "Value123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
        // Verify headers were preserved across attempts
        Assert.True(mockInner.Invocations[1].Headers.Contains("X-Test-Header"));
    }

    [Fact]
    public async Task SendAsync_RetriesOnReportedFrenchNetworkConnectionError_AndSucceeds()
    {
        var mockInner = new MockHttpMessageHandler();
        var transportEx = new IOException(
            "Unable to read data from the transport connection: Une connexion existante a dû être fermée par l’hôte distant..",
            new SocketException((int)SocketError.ConnectionReset));

        mockInner.EnqueueException(transportEx);
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"result\":\"recovered\"}");

        var options = new RetryPolicyOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryNonTransientStatusCode_Like404()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.NotFound, "Not Found");

        var options = new RetryPolicyOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/nonexistent");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(mockInner.Invocations);
    }

    [Fact]
    public async Task SendAsync_ExhaustsRetries_WhenPersistentTransientErrorOccurs()
    {
        var mockInner = new MockHttpMessageHandler();
        var transportEx = new IOException(
            "Unable to read data from the transport connection: Une connexion existante a dû être fermée par l’hôte distant..",
            new SocketException((int)SocketError.ConnectionReset));

        mockInner.EnqueueException(transportEx);
        mockInner.EnqueueException(transportEx);
        mockInner.EnqueueException(transportEx);

        var options = new RetryPolicyOptions
        {
            MaxRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags");

        var ex = await Assert.ThrowsAsync<IOException>(() => client.SendAsync(request));
        Assert.Contains("transport connection", ex.Message);
        // Initial attempt + 2 retries = 3 total invocations
        Assert.Equal(3, mockInner.Invocations.Count);
        // Exception must be marked as retried so outer layers (e.g. Crucible harness) do not multiply retries
        Assert.True(ex.Data.Contains(ResilienceConstants.RetriedKey));
    }

    [Fact]
    public async Task SendAsync_RetriesOn500_WhenTransientMemoryPressure_AndSucceeds()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.InternalServerError, "{\"error\":\"CUDA out of memory\"}");
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"result\":\"ok\"}");

        var options = new RetryPolicyOptions
        {
            MaxRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/generate");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
    }

    [Fact]
    public async Task SendAsync_RetriesOn500_RegardlessOfBody_UntilExhausted()
    {
        var mockInner = new MockHttpMessageHandler();
        for (var i = 0; i < 4; i++)
        {
            mockInner.EnqueueResponse(HttpStatusCode.InternalServerError, "{\"error\":\"model runner has unexpectedly stopped\"}");
        }

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 3));
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/generate"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(4, mockInner.Invocations.Count);
        Assert.Contains("unexpectedly stopped", await response.Content.ReadAsStringAsync());
    }


    private static TransientHttpErrorRetryHandler CreateHandler(HttpMessageHandler inner, int maxRetries) =>
        new(inner, NullLogger.Instance, TimeProvider.System, new RetryPolicyOptions
        {
            MaxRetries = maxRetries,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        });

    [Fact]
    public async Task SendAsync_ExhaustedTransientStatus_ReturnsResponse_AndCallerExceptionIsNotRetriedAgain()
    {
        var mockInner = new MockHttpMessageHandler();
        for (var i = 0; i < 3; i++)
        {
            mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "Busy");
        }

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 2));
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, mockInner.Invocations.Count);

        // The exception a caller builds from the exhausted response must not be retried by outer layers
        var callerEx = Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
        Assert.False(TransientErrorDetector.IsTransient(callerEx));
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryConnectionRefused()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueException(new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 3));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags")));
        Assert.Single(mockInner.Invocations);
    }

    [Fact]
    public async Task SendAsync_When503ErrorResponse_EnrichesReasonPhraseAndExceptionMessage()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"No enabled routing target is available\"}");

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 0));
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("No enabled routing target is available", response.ReasonPhrase);

        var ex = Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
        Assert.Contains("No enabled routing target is available", ex.Message);
    }

    [Fact]
    public async Task SendAsync_WhenResponseContentIsNull_DoesNotThrowNullReference()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueRawResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = null });

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 0));
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task SendAsync_WhenTransient503WithNullContent_DoesNotThrowNullReferenceDuringRetry()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueRawResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = null });
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 1));
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
    }
}
