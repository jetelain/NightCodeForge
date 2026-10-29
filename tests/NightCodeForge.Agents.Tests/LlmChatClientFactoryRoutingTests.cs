using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public class LlmChatClientFactoryRoutingTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static readonly LlmProviderOptions Options = new() { Endpoint = "http://localhost:11434", ModelId = "m:1" };

    private static T Create<T>(Func<IOptions<LlmProviderOptions>, ILoggerFactoryShim, T> _) => throw new NotSupportedException();

    private interface ILoggerFactoryShim { }

    private static OllamaRouterChatClientFactory RouterFactory() => new(
        Microsoft.Extensions.Options.Options.Create(Options), NullLoggerFactory.Instance,
        new Mock<IOperationsTelemetryService>().Object, new Mock<IOperationsControlService>().Object,
        TimeProvider.System, Microsoft.Extensions.Options.Options.Create(new RetryPolicyOptions()));

    private static StandaloneOllamaChatClientFactory StandaloneFactory() => new(
        Microsoft.Extensions.Options.Options.Create(Options), NullLoggerFactory.Instance,
        new Mock<IOperationsTelemetryService>().Object, new Mock<IOperationsControlService>().Object,
        TimeProvider.System, Microsoft.Extensions.Options.Options.Create(new RetryPolicyOptions()));

    private static HttpRequestMessage Capture(Action<HttpClient> configure, OllamaRoutingTarget target, string? sessionId, bool router)
    {
        using var client = new HttpClient();
        if (router)
        {
            OllamaRoutingHeaders.Apply(client, target, sessionId);
        }
        configure(client);
        var message = new HttpRequestMessage();
        foreach (var header in client.DefaultRequestHeaders)
        {
            message.Headers.Add(header.Key, header.Value);
        }
        return message;
    }

    [Fact]
    public void RoutingHeaders_Apply_SetsTargetAndSession()
    {
        var request = Capture(_ => { }, OllamaRoutingTarget.Local, "ncf-task1-stage", router: true);

        Assert.Equal("Local", request.Headers.GetValues(OllamaRoutingHeaders.Target).Single());
        Assert.Equal("ncf-task1-stage", request.Headers.GetValues(OllamaRoutingHeaders.SessionId).Single());
    }

    [Fact]
    public void RoutingHeaders_Apply_AutoWithoutSession_SetsNoHeader()
    {
        var request = Capture(_ => { }, OllamaRoutingTarget.Auto, null, router: true);

        Assert.False(request.Headers.Contains(OllamaRoutingHeaders.Target));
        Assert.False(request.Headers.Contains(OllamaRoutingHeaders.SessionId));
    }

    [Fact]
    public async Task OllamaChatClientFactory_WithRouterConfigure_SendsSessionHeaderOnTheWire()
    {
        var handler = new CaptureHandler();
        var raw = OllamaChatClientFactory.CreateOllama(
            Options, "m:1", NullLogger.Instance, TimeProvider.System, new RetryPolicyOptions(), OllamaRoutingTarget.Remote, handler,
            http => OllamaRoutingHeaders.Apply(http, OllamaRoutingTarget.Remote, "sess-42"));

        try { await raw.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]); } catch (Exception) { /* empty body is irrelevant: only the request is inspected */ }

        Assert.NotNull(handler.Request);
        Assert.Equal("sess-42", handler.Request!.Headers.GetValues(OllamaRoutingHeaders.SessionId).Single());
        Assert.Equal("Remote", handler.Request.Headers.GetValues(OllamaRoutingHeaders.Target).Single());
    }

    [Fact]
    public async Task StandaloneFactory_SendsNoRouterHeaders()
    {
        var handler = new CaptureHandler();
        var raw = OllamaChatClientFactory.CreateOllama(
            Options, "m:1", NullLogger.Instance, TimeProvider.System, new RetryPolicyOptions(), OllamaRoutingTarget.Remote, handler,
            _ => { });

        try { await raw.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]); } catch (Exception) { /* empty body is irrelevant: only the request is inspected */ }

        Assert.NotNull(handler.Request);
        Assert.False(handler.Request!.Headers.Contains(OllamaRoutingHeaders.SessionId));
        Assert.False(handler.Request.Headers.Contains(OllamaRoutingHeaders.Target));
    }

    [Fact]
    public void Factories_CacheSessionLessClients_ButNotSessionBoundOnes()
    {
        using var factory = RouterFactory();

        Assert.Same(factory.CreateClient("m:1"), factory.CreateClient("m:1"));
        Assert.NotSame(
            factory.CreateClient("m:1", OllamaRoutingTarget.Auto, "s1"),
            factory.CreateClient("m:1", OllamaRoutingTarget.Auto, "s1"));
        Assert.NotSame(factory.CreateClient("m:1"), factory.CreateClient("m:1", OllamaRoutingTarget.Auto, "s1"));
    }

    [Fact]
    public void StandaloneFactory_CreatesClientsWithSession_WithoutFailing()
    {
        using var factory = StandaloneFactory();
        Assert.NotNull(factory.CreateClient("m:1", OllamaRoutingTarget.Cloud, "s1"));
    }

    [Fact]
    public void LlmSessionIds_ForStage_IsStablePerTaskAndStage()
    {
        Assert.Equal(LlmSessionIds.ForStage(7, "implement"), LlmSessionIds.ForStage(7, "implement"));
        Assert.NotEqual(LlmSessionIds.ForStage(7, "implement"), LlmSessionIds.ForStage(8, "implement"));
        Assert.NotEqual(LlmSessionIds.ForStage(7, "implement"), LlmSessionIds.ForStage(7, "review"));
        Assert.Throws<ArgumentException>(() => LlmSessionIds.ForStage(7, " "));
    }
}
