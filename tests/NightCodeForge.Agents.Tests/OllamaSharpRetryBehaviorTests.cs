using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Resilience;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Verifies how the real OllamaSharp client surfaces failures that went through the
/// <see cref="TransientHttpErrorRetryHandler"/>, to guarantee that outer layers (e.g. the Crucible harness)
/// never retry an error the HTTP layer already retried.
/// </summary>
public class OllamaSharpRetryBehaviorTests
{
    private const int MaxRetries = 2;
    private const int ExpectedAttempts = MaxRetries + 1;

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _next;
        public int Invocations;

        public ScriptedHandler(Func<HttpResponseMessage> next) => _next = next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            return Task.FromResult(_next());
        }
    }

    private static IChatClient CreateClient(HttpMessageHandler inner) =>
        OllamaChatClientFactory.CreateOllama(
            new LlmProviderOptions { Endpoint = "http://localhost:11434", ModelId = "test-model" },
            modelId: null,
            NullLogger.Instance,
            TimeProvider.System,
            new RetryPolicyOptions
            {
                MaxRetries = MaxRetries,
                BaseDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(5)
            },
            OllamaRoutingTarget.Auto,
            inner);

    private static readonly IReadOnlyList<ChatMessage> Messages = new[] { new ChatMessage(ChatRole.User, "hello") };

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task GetResponseAsync_PersistentTransientStatus_IsRetriedOnlyByHandler_AndNotRetriableByCallers(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(() => new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error\":\"boom\"}")
        });
        using var client = CreateClient(inner);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(Messages));

        Assert.Equal(ExpectedAttempts, inner.Invocations);
        Assert.False(
            TransientErrorDetector.IsTransient(ex),
            $"Outer layers must not retry a {status} the handler already retried, but got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_PersistentTransientStatus_IsNotRetriableByCallers()
    {
        var inner = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"busy\"}")
        });
        using var client = CreateClient(inner);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Messages))
            {
            }
        });

        Assert.Equal(ExpectedAttempts, inner.Invocations);
        Assert.False(
            TransientErrorDetector.IsTransient(ex),
            $"Got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task GetResponseAsync_PersistentConnectionReset_ThrowsExceptionNotRetriableByCallers()
    {
        var inner = new ScriptedHandler(() => throw new IOException(
            "Unable to read data from the transport connection.",
            new SocketException((int)SocketError.ConnectionReset)));
        using var client = CreateClient(inner);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(Messages));

        Assert.Equal(ExpectedAttempts, inner.Invocations);
        Assert.False(
            TransientErrorDetector.IsTransient(ex),
            $"Got {ex.GetType().Name}: {ex.Message}");
    }

    [Fact]
    public async Task GetResponseAsync_ConnectionRefused_FailsFastWithSingleAttempt()
    {
        var inner = new ScriptedHandler(() => throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));
        using var client = CreateClient(inner);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(Messages));

        Assert.Equal(1, inner.Invocations);
        Assert.False(TransientErrorDetector.IsTransient(ex));
    }
}
