using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Web.Tailscale;

namespace NightCodeForge.Web.Tests;

public class TailscaleServeTests
{
    private const string RunningStatus = """{"BackendState":"Running","Self":{"DNSName":"mybox.tail1234.ts.net."}}""";
    private const string LoginStatus = """{"BackendState":"NeedsLogin"}""";

    private sealed class FakeCli : ITailscaleCliRunner
    {
        public List<string> Calls { get; } = new();
        public string Status { get; set; } = RunningStatus;
        public int VersionExitCode { get; set; }

        public Task<TailscaleCliResult> RunAsync(IReadOnlyList<string> arguments, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', arguments);
            Calls.Add(line);
            return Task.FromResult(line switch
            {
                "version" => new TailscaleCliResult(VersionExitCode, string.Empty, VersionExitCode == 0 ? string.Empty : "not found"),
                "status --json" => new TailscaleCliResult(0, Status, string.Empty),
                _ => new TailscaleCliResult(0, string.Empty, string.Empty)
            });
        }
    }

    private static TailscaleServeHostedService Create(TailscaleOptions options, FakeCli cli, string[] addresses, string? urlsConfig = null)
    {
        var features = new FeatureCollection();
        var feature = new Mock<IServerAddressesFeature>();
        var list = new List<string>(addresses);
        feature.SetupGet(f => f.Addresses).Returns(list);
        features.Set(feature.Object);
        var server = new Mock<IServer>();
        server.SetupGet(s => s.Features).Returns(features);

        var monitor = new Mock<IOptionsMonitor<TailscaleOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(urlsConfig is null ? [] : new Dictionary<string, string?> { ["Urls"] = urlsConfig })
            .Build();

        return new TailscaleServeHostedService(
            monitor.Object, server.Object, config, new Mock<IHostApplicationLifetime>().Object, cli,
            NullLogger<TailscaleServeHostedService>.Instance);
    }

    [Fact]
    public async Task ServiceMode_GeneratesResetServeAndAdvertiseCommands()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true }, cli, ["http://127.0.0.1:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Equal(
            ["version", "status --json", "serve reset", "serve --service=svc:ncf --https=443 127.0.0.1:5270", "serve advertise svc:ncf"],
            cli.Calls);
    }

    [Fact]
    public async Task DeviceMode_WhenServiceNameEmpty_ServesWithoutService()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true, ServiceName = null, ListenPort = 8443 }, cli, ["http://localhost:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Equal(["version", "status --json", "serve reset", "serve --https=8443 127.0.0.1:5270"], cli.Calls);
    }

    [Fact]
    public async Task AdvertiseDisabled_SkipsAdvertise()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true, Advertise = false }, cli, ["http://localhost:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.DoesNotContain(cli.Calls, c => c.StartsWith("serve advertise"));
    }

    [Fact]
    public async Task AutoServeDisabled_DoesNothing()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = false }, cli, ["http://localhost:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task MissingCli_SkipsActivationWithoutThrowing()
    {
        var cli = new FakeCli { VersionExitCode = -1 };
        var svc = Create(new TailscaleOptions { AutoServe = true }, cli, ["http://localhost:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Equal(["version"], cli.Calls);
    }

    [Fact]
    public async Task NotAuthenticated_SkipsActivation()
    {
        var cli = new FakeCli { Status = LoginStatus };
        var svc = Create(new TailscaleOptions { AutoServe = true }, cli, ["http://localhost:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.DoesNotContain(cli.Calls, c => c.StartsWith("serve"));
    }

    [Fact]
    public async Task NoResolvablePort_ThrowsDomainException()
    {
        var svc = Create(new TailscaleOptions { AutoServe = true }, new FakeCli(), []);

        await Assert.ThrowsAsync<TailscaleServeException>(() => svc.ApplyConfigurationAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PortFallsBackToUrlsConfiguration()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true, ServiceName = null }, cli, [], "http://+:6001;https://+:6002");

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Contains("serve --https=443 127.0.0.1:6001", cli.Calls);
    }

    [Fact]
    public async Task StopAsync_ServiceMode_DrainsClearsAndResets()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true }, cli, ["http://localhost:5270"]);
        await svc.ApplyConfigurationAsync(CancellationToken.None);
        cli.Calls.Clear();

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(["serve drain svc:ncf", "serve clear svc:ncf", "serve reset"], cli.Calls);
    }

    [Fact]
    public async Task StopAsync_WithoutActiveRoute_DoesNothing()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = false }, cli, ["http://localhost:5270"]);

        await svc.StopAsync(CancellationToken.None);

        Assert.Empty(cli.Calls);
    }

    [Fact]
    public void Resolver_PrefersHttpOverHttps()
    {
        var target = TailscaleTargetResolver.Resolve(["https://localhost:7203", "http://localhost:5273"]);

        Assert.Equal(new TailscaleTarget("http", 5273), target);
    }

    [Fact]
    public void Resolver_HttpsOnly_UsesInsecureHttpsTarget()
    {
        var target = TailscaleTargetResolver.Resolve(["https://localhost:7203"]);

        Assert.Equal("https+insecure://127.0.0.1:7203", target.ToServeTarget());
    }

    [Fact]
    public void StatusParser_BuildsHostNames()
    {
        Assert.Equal("mybox.tail1234.ts.net", TailscaleStatusParser.GetServeHostName(RunningStatus, null));
        Assert.Equal("ncf.tail1234.ts.net", TailscaleStatusParser.GetServeHostName(RunningStatus, "svc:ncf"));
        Assert.True(TailscaleStatusParser.IsAuthenticated(RunningStatus));
        Assert.False(TailscaleStatusParser.IsAuthenticated(LoginStatus));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"BackendState\":123,\"Self\":\"invalid\"}")]
    public void StatusParser_MalformedOrEmptyJson_ReturnsFalseAndNullWithoutThrowing(string malformedJson)
    {
        Assert.False(TailscaleStatusParser.IsAuthenticated(malformedJson));
        Assert.Null(TailscaleStatusParser.GetNodeDnsName(malformedJson));
        Assert.Null(TailscaleStatusParser.GetServeHostName(malformedJson, "svc:ncf"));
    }

    [Fact]
    public async Task ProtocolAndServiceName_AreNormalizedToLowercaseAndTrimmed()
    {
        var cli = new FakeCli();
        var svc = Create(
            new TailscaleOptions { AutoServe = true, Protocol = " HTTPS ", ServiceName = " svc:ncf ", ListenPort = 443 },
            cli,
            ["http://127.0.0.1:5270"]);

        await svc.ApplyConfigurationAsync(CancellationToken.None);

        Assert.Contains("serve --service=svc:ncf --https=443 127.0.0.1:5270", cli.Calls);
        Assert.Contains("serve advertise svc:ncf", cli.Calls);
    }

    [Fact]
    public void CommandLine_MapsFlagsToConfiguration()
    {
        var values = TailscaleCommandLine.ToConfiguration(["--tailscale", "--tailscale-service", "svc:foo", "--tailscale-port", "8443"]);

        Assert.Equal("True", values["Tailscale:AutoServe"]);
        Assert.Equal("svc:foo", values["Tailscale:ServiceName"]);
        Assert.Equal("8443", values["Tailscale:ListenPort"]);
    }
}

