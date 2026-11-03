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
        public Action? OnSend { get; set; }

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
            OnSend?.Invoke();
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
    public async Task SendAsync_RetriesConnectionRefused_AndSucceedsWhenServerOnline()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueException(new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"status\":\"ready\"}");

        using var client = new HttpClient(CreateHandler(mockInner, maxRetries: 3));

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
    }

    [Fact]
    public async Task SendAsync_ExhaustsWhenMaxRetryDurationExceeded()
    {
        var virtualTime = new AutoAdvancingFakeTimeProvider();
        var mockInner = new MockHttpMessageHandler();
        for (int i = 0; i < 10; i++)
        {
            mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "Busy");
        }

        var notified = new List<RetryNotificationContext>();
        var options = new LlmRetryPolicyOptions
        {
            MaxRetries = 20,
            BaseDelay = TimeSpan.FromSeconds(10),
            MaxDelay = TimeSpan.FromSeconds(30),
            MaxRetryDuration = TimeSpan.FromSeconds(25),
            OnRetry = ctx => notified.Add(ctx)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            virtualTime,
            options);

        using var client = new HttpClient(handler);
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(mockInner.Invocations.Count < 10, $"Invocations was {mockInner.Invocations.Count}");
        Assert.NotEmpty(notified);
        Assert.All(notified, n => Assert.True(n.ElapsedDuration + n.Delay <= TimeSpan.FromSeconds(25) + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task SendAsync_InvokesNotificationCallback_WithAccurateContext()
    {
        var virtualTime = new AutoAdvancingFakeTimeProvider();
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "{\"error\":\"fleet offline\"}");
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"status\":\"ok\"}");

        var notified = new List<RetryNotificationContext>();
        var requestOptionNotified = new List<RetryNotificationContext>();
        var options = new RetryPolicyOptions
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(5),
            MaxRetryDuration = TimeSpan.FromSeconds(10),
            OnRetry = ctx => notified.Add(ctx)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            virtualTime,
            options);

        using var client = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat");
        request.Headers.Add("X-Test-Trace", "xyz");
        request.Options.Set(TransientHttpErrorRetryHandler.RetryCallbackKey, ctx => requestOptionNotified.Add(ctx));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(notified);
        Assert.Single(requestOptionNotified);
        var n = notified[0];
        Assert.Equal("http://localhost:11434/api/chat", n.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, n.Method);
        Assert.NotNull(n.Headers);
        Assert.Equal("xyz", n.Headers!.GetValues("X-Test-Trace").Single());
        Assert.Equal(1, n.Attempt);
        Assert.Equal(3, n.MaxRetries);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, n.StatusCode);
        Assert.Contains("fleet offline", n.ErrorMessage);
        Assert.True(n.Delay > TimeSpan.Zero);
    }

    [Fact]
    public async Task SendAsync_RespectsRetryAfterHeader_DeltaSeconds_AndDate_ClampedToMaxDuration()
    {
        var virtualTime = new AutoAdvancingFakeTimeProvider();
        var mockInner = new MockHttpMessageHandler();

        // Attempt 1: 429 with Delta = 12s
        var resp429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"rate limit\"}")
        };
        resp429.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        mockInner.EnqueueRawResponse(resp429);

        // Attempt 2: 503 with HTTP-Date 60s in the future, which exceeds remaining MaxRetryDuration (30s - 12s = 18s)
        var resp503 = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"maintenance\"}")
        };
        resp503.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(virtualTime.GetUtcNow().AddSeconds(72));
        mockInner.EnqueueRawResponse(resp503);

        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"result\":\"ok\"}");

        var captured = new List<RetryNotificationContext>();
        var options = new RetryPolicyOptions
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(2),
            MaxRetryDuration = TimeSpan.FromSeconds(30),
            OnRetry = ctx => captured.Add(ctx)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            virtualTime,
            options);

        using var client = new HttpClient(handler);
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/status"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, captured.Count);
        Assert.Equal(TimeSpan.FromSeconds(12), captured[0].Delay);
        // Second attempt has 18s remaining (30s - 12s), so the 60s Retry-After date is clamped to 18s
        Assert.Equal(TimeSpan.FromSeconds(18), captured[1].Delay);
    }

    [Fact]
    public async Task SendAsync_WhenOperationsPaused_AbortsRetryImmediately()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "Busy");

        var mockOps = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(true);

        var options = new RetryPolicyOptions
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(5)
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options,
            mockOps.Object);

        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<NightCodeForge.Core.Exceptions.OperationsPausedException>(() =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:11434/api/tags")));
        Assert.Empty(mockInner.Invocations);
    }

    [Fact]
    public async Task SendAsync_WhenOperationsPausedMidBackoff_CancelsDelayAndThrowsOperationsPausedException()
    {
        var mockInner = new MockHttpMessageHandler();
        mockInner.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "Busy");
        mockInner.EnqueueResponse(HttpStatusCode.OK, "OK");

        bool isPaused = false;
        var mockOps = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(() => isPaused);

        var options = new RetryPolicyOptions
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromSeconds(30),
            MaxRetryDuration = TimeSpan.FromMinutes(5),
            OnRetry = _ctx =>
            {
                // Simulate user triggering pause right as the handler enters backoff
                _ = Task.Run(async () =>
                {
                    await Task.Delay(15);
                    isPaused = true;
                    mockOps.Raise(m => m.OnOperationsStateChanged += null, true);
                });
            }
        };

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            TimeProvider.System,
            options,
            mockOps.Object);

        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<NightCodeForge.Core.Exceptions.OperationsPausedException>(() =>
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

    [Fact]
    public async Task SendAsync_SlowFirstAttemptBeforeOutage_DoesNotPrematurelyConsumeMaxRetryDuration()
    {
        var virtualTime = new AutoAdvancingFakeTimeProvider();
        var mockInner = new MockHttpMessageHandler();
        mockInner.OnSend = () =>
        {
            if (mockInner.Invocations.Count == 1)
            {
                // First attempt takes 6 minutes before failing with 502 Bad Gateway
                virtualTime.Advance(TimeSpan.FromMinutes(6));
            }
        };
        mockInner.EnqueueResponse(HttpStatusCode.BadGateway, "Upstream rebooting");
        mockInner.EnqueueResponse(HttpStatusCode.OK, "{\"status\":\"recovered\"}");

        var options = new LlmRetryPolicyOptions(); // 5-minute MaxRetryDuration

        var handler = new TransientHttpErrorRetryHandler(
            mockInner,
            NullLogger.Instance,
            virtualTime,
            options);

        using var client = new HttpClient(handler);
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/chat"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, mockInner.Invocations.Count);
    }

    private sealed class AutoAdvancingFakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        private long _timestampTicks;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestampTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            if (delta > TimeSpan.Zero)
            {
                _utcNow = _utcNow.Add(delta);
                _timestampTicks += delta.Ticks;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime);
                callback(state);
            }
            else if (dueTime == TimeSpan.Zero)
            {
                callback(state);
            }

            return new NoOpTimer();
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
