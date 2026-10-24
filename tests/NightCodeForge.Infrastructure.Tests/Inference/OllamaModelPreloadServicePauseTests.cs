using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaModelPreloadServicePauseTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    [Fact]
    public async Task PreloadModelAsync_WhenOperationsPaused_ThrowsOperationsPausedException()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(true);

        bool httpCalled = false;
        var httpClient = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            httpCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var options = Options.Create(new OllamaRouterOptions { BaseUrl = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        await Assert.ThrowsAsync<OperationsPausedException>(() =>
            service.PreloadModelAsync("qwen2.5-coder:32b"));

        Assert.False(httpCalled);
    }

    [Fact]
    public async Task PreloadModelAsync_WhenOperationsNotPaused_SendsPostRequest()
    {
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);

        bool httpCalled = false;
        var httpClient = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            httpCalled = true;
            Assert.Contains("api/generate", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var options = Options.Create(new OllamaRouterOptions { BaseUrl = "http://localhost:11434" });
        var service = new OllamaModelPreloadService(
            httpClient,
            opsMock.Object,
            options,
            NullLogger<OllamaModelPreloadService>.Instance);

        var result = await service.PreloadModelAsync("qwen2.5-coder:32b");

        Assert.True(result);
        Assert.True(httpCalled);
    }
}
