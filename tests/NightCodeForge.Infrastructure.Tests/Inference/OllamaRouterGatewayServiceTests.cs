using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Inference;

public class OllamaRouterGatewayServiceTests
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

    private static OllamaRouterGatewayService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:11434/")
        };

        var options = Options.Create(new OllamaRouterOptions
        {
            BaseUrl = "http://localhost:11434"
        });

        return new OllamaRouterGatewayService(httpClient, options, NullLogger<OllamaRouterGatewayService>.Instance);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenMonitorApiReportsDisabledLocalTarget_MarksLocalDisabled()
    {
        // Simulate monitor/api returning local=false (disabled), remote=true (online/idle), cloud=true
        var monitorJson = JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, bool>
            {
                ["local"] = false,
                ["remote"] = true,
                ["cloud"] = true
            },
            online = new Dictionary<string, bool>
            {
                ["local"] = true,
                ["remote"] = true,
                ["cloud"] = true
            },
            busy = new Dictionary<string, bool>
            {
                ["local"] = false,
                ["remote"] = false,
                ["cloud"] = false
            },
            cloudEnabled = true
        });

        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("monitor/api"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(monitorJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var status = await service.GetGatewayStatusAsync();

        Assert.True(status.IsConnected);
        Assert.True(status.IsRouterProxy);
        Assert.Equal(3, status.Targets.Count);

        var localTarget = status.Targets.First(t => t.TargetName == "Local GPU Target");
        Assert.Equal(TargetBackendState.Disabled, localTarget.State);
        Assert.Null(localTarget.LatencyMs);
        Assert.Contains("disabled in OllamaRouter", localTarget.Description, StringComparison.OrdinalIgnoreCase);

        var remoteTarget = status.Targets.First(t => t.TargetName == "Remote Server Target");
        Assert.Equal(TargetBackendState.Available, remoteTarget.State);
        Assert.NotNull(remoteTarget.LatencyMs);

        var cloudTarget = status.Targets.First(t => t.TargetName == "Cloud Overflow Target");
        Assert.Equal(TargetBackendState.Active, cloudTarget.State);
        Assert.NotNull(cloudTarget.LatencyMs);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenTargetIsBusy_MarksTargetBusy()
    {
        var monitorJson = JsonSerializer.Serialize(new
        {
            targets = new Dictionary<string, bool>
            {
                ["local"] = true,
                ["remote"] = true,
                ["cloud"] = true
            },
            online = new Dictionary<string, bool>
            {
                ["local"] = false, // local node offline
                ["remote"] = true,
                ["cloud"] = true
            },
            busy = new Dictionary<string, bool>
            {
                ["local"] = false,
                ["remote"] = true, // remote is busy executing
                ["cloud"] = false
            },
            cloudEnabled = true
        });

        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("monitor/api"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(monitorJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var status = await service.GetGatewayStatusAsync();

        var localTarget = status.Targets.First(t => t.TargetName == "Local GPU Target");
        Assert.Equal(TargetBackendState.Offline, localTarget.State);

        var remoteTarget = status.Targets.First(t => t.TargetName == "Remote Server Target");
        Assert.Equal(TargetBackendState.Busy, remoteTarget.State);
        Assert.Contains("workload", remoteTarget.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenMonitorApiFails_FallsBackToApiVersion()
    {
        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("monitor/api"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (req.RequestUri.PathAndQuery.Contains("api/version"))
            {
                var msg = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"version\":\"0.40.6\"}", Encoding.UTF8, "application/json")
                };
                msg.Headers.Add("x-ollamarouter-version", "1.2.0");
                return msg;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var status = await service.GetGatewayStatusAsync();

        Assert.True(status.IsConnected);
        Assert.True(status.IsRouterProxy);
        Assert.Equal(3, status.Targets.Count);
        Assert.Equal(TargetBackendState.Active, status.Targets.First(t => t.TargetName == "Local GPU Target").State);
    }

    [Fact]
    public async Task GetGatewayStatusAsync_WhenEndpointThrows_ReturnsOfflineTargets()
    {
        var service = CreateService(_ => throw new HttpRequestException("Connection refused"));

        var status = await service.GetGatewayStatusAsync();

        Assert.False(status.IsConnected);
        Assert.False(status.IsRouterProxy);
        Assert.All(status.Targets, t => Assert.Equal(TargetBackendState.Offline, t.State));
    }

    [Fact]
    public async Task GetResidentModelsAsync_WhenModelHasZeroVram_PreservesZeroVramAndCalculatesZeroPercentage()
    {
        var psJson = JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new
                {
                    name = "qwen2.5-coder:7b",
                    model = "qwen2.5-coder:7b",
                    size = 5_000_000_000L,
                    size_vram = 0L,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(5),
                    digest = "abc123"
                }
            }
        });

        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("api/ps"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(psJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var models = await service.GetResidentModelsAsync();

        Assert.Single(models);
        var model = models[0];
        Assert.Equal("qwen2.5-coder:7b", model.Name);
        Assert.Equal(5_000_000_000L, model.Size);
        Assert.Equal(0L, model.SizeVram);
        Assert.Equal("0 GB", model.SizeVramFormatted);
        Assert.Equal(0.0, model.VramPercentage);
        Assert.Equal("0%", model.VramPercentageFormatted);
    }

    [Fact]
    public async Task GetResidentModelsAsync_WhenModelHasPartialVram_CalculatesCorrectPercentage()
    {
        var psJson = JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new
                {
                    name = "deepseek-coder:33b",
                    model = "deepseek-coder:33b",
                    size = 20_000_000_000L,
                    size_vram = 10_000_000_000L,
                    expires_at = (DateTimeOffset?)null,
                    digest = "def456"
                }
            }
        });

        var service = CreateService(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("api/ps"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(psJson, Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var models = await service.GetResidentModelsAsync();

        Assert.Single(models);
        var model = models[0];
        Assert.Equal(50.0, model.VramPercentage);
        Assert.Equal("50%", model.VramPercentageFormatted);
        Assert.Equal("Indefinite", model.ExpiresAtFormatted);
    }
}
