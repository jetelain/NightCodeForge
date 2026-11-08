using System.IO;
using System.Text.Json.Nodes;
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
    private const string RunningStatus = """{"BackendState":"Running","Self":{"DNSName":"mybox.tail1234.ts.net.","TailscaleIPs":["100.101.102.103","fd7a:115c:a1e0::ab12"]},"CurrentTailnet":{"Name":"tail1234.ts.net"}}""";
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
                "version" => new TailscaleCliResult(VersionExitCode, VersionExitCode == 0 ? "tailscale 1.76.0\n" : string.Empty, VersionExitCode == 0 ? string.Empty : "not found"),
                "status --json" => new TailscaleCliResult(0, Status, string.Empty),
                _ => new TailscaleCliResult(0, string.Empty, string.Empty)
            });
        }
    }

    private static TailscaleServeHostedService Create(
        TailscaleOptions options,
        FakeCli cli,
        string[] addresses,
        string? urlsConfig = null,
        Action<Action<TailscaleOptions, string?>>? registerOnChange = null,
        Func<TailscaleOptions>? currentValueProvider = null)
    {
        var features = new FeatureCollection();
        var feature = new Mock<IServerAddressesFeature>();
        var list = new List<string>(addresses);
        feature.SetupGet(f => f.Addresses).Returns(list);
        features.Set(feature.Object);
        var server = new Mock<IServer>();
        server.SetupGet(s => s.Features).Returns(features);

        var monitor = new Mock<IOptionsMonitor<TailscaleOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => currentValueProvider != null ? currentValueProvider() : options);
        monitor.Setup(m => m.OnChange(It.IsAny<Action<TailscaleOptions, string?>>()))
            .Callback<Action<TailscaleOptions, string?>>(listener => registerOnChange?.Invoke(listener))
            .Returns(Mock.Of<IDisposable>());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(urlsConfig is null ? [] : new Dictionary<string, string?> { ["Urls"] = urlsConfig })
            .Build();

        var lifetime = new Mock<IHostApplicationLifetime>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        lifetime.SetupGet(l => l.ApplicationStarted).Returns(cts.Token);

        return new TailscaleServeHostedService(
            monitor.Object, server.Object, config, lifetime.Object, cli,
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

    [Fact]
    public void StatusParser_ExtractsTailscaleIpv4_AndTailnetDomain()
    {
        Assert.Equal("100.101.102.103", TailscaleStatusParser.GetTailscaleIpv4(RunningStatus));
        Assert.Equal("tail1234.ts.net", TailscaleStatusParser.GetTailnetDomain(RunningStatus));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"BackendState\":\"Running\",\"Self\":{\"TailscaleIPs\":[\"fd7a:115c:a1e0::ab12\"]}}")]
    public void StatusParser_MalformedOrMissingIpv4_ReturnsNull(string json)
    {
        Assert.Null(TailscaleStatusParser.GetTailscaleIpv4(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("{\"Self\":{\"DNSName\":\"nodot\"}}")]
    public void StatusParser_MalformedOrMissingDomain_ReturnsNull(string json)
    {
        Assert.Null(TailscaleStatusParser.GetTailnetDomain(json));
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_WhenActive_ReturnsFullTelemetryAndMagicDnsUrl()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = true, ServiceName = "svc:ncf" }, cli, ["http://127.0.0.1:5270"]);
        await svc.ApplyConfigurationAsync(CancellationToken.None);

        var snapshot = await svc.GetStatusSnapshotAsync(CancellationToken.None);

        Assert.Equal(TailscaleDaemonState.Active, snapshot.State);
        Assert.Equal("tailscale 1.76.0", snapshot.CliVersion);
        Assert.Equal("mybox.tail1234.ts.net", snapshot.NodeDnsName);
        Assert.Equal("tail1234.ts.net", snapshot.TailnetDomain);
        Assert.Equal("100.101.102.103", snapshot.TailscaleIpv4);
        Assert.Equal("127.0.0.1:5270", snapshot.LoopbackTarget);
        Assert.Equal("https://ncf.tail1234.ts.net", snapshot.MagicDnsUrl);
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_WhenCliMissingOrUnauthenticated_ReturnsDaemonOfflineState()
    {
        var cli = new FakeCli { VersionExitCode = -1 };
        var svc = Create(new TailscaleOptions { AutoServe = true }, cli, ["http://127.0.0.1:5270"]);

        var snapshot = await svc.GetStatusSnapshotAsync(CancellationToken.None);

        Assert.Equal(TailscaleDaemonState.DaemonOfflineOrUnauthenticated, snapshot.State);
        Assert.Null(snapshot.CliVersion);
    }

    [Fact]
    public async Task GetStatusSnapshotAsync_WhenRouteInactive_ReturnsInactiveState_AndNullMagicDnsUrl()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = false }, cli, ["http://127.0.0.1:5270"]);

        var snapshot = await svc.GetStatusSnapshotAsync(CancellationToken.None);

        Assert.Equal(TailscaleDaemonState.Inactive, snapshot.State);
        Assert.Equal("100.101.102.103", snapshot.TailscaleIpv4);
        Assert.Null(snapshot.MagicDnsUrl);
    }

    [Fact]
    public async Task ApplyConfigurationAsync_WithOverrideOptions_AppliesImmediatelyWithoutWaitingForFileWatcher()
    {
        var cli = new FakeCli();
        var svc = Create(new TailscaleOptions { AutoServe = false }, cli, ["http://127.0.0.1:5270"]);

        // Calling with in-memory override turns it on immediately
        await svc.ApplyConfigurationAsync(new TailscaleOptions { AutoServe = true, ServiceName = null, ListenPort = 8443 }, CancellationToken.None);

        Assert.Contains("serve --https=8443 127.0.0.1:5270", cli.Calls);
        Assert.True(svc.GetCurrentOptions().AutoServe);
        Assert.Equal(8443, svc.GetCurrentOptions().ListenPort);

        var snapshot = await svc.GetStatusSnapshotAsync(CancellationToken.None);
        Assert.Equal(TailscaleDaemonState.Active, snapshot.State);
        Assert.Equal("https://mybox.tail1234.ts.net:8443", snapshot.MagicDnsUrl);
    }

    [Fact]
    public async Task OnOptionsChanged_WhenMatchingAlreadyAppliedOptions_SkipsRedundantTeardownAndReapply()
    {
        var cli = new FakeCli();
        Action<TailscaleOptions, string?>? changeCallback = null;
        var currentOptions = new TailscaleOptions { AutoServe = false };

        var svc = Create(
            currentOptions,
            cli,
            ["http://127.0.0.1:5270"],
            registerOnChange: cb => changeCallback = cb,
            currentValueProvider: () => currentOptions);

        await svc.StartAsync(CancellationToken.None);
        cli.Calls.Clear();

        var appliedOptions = new TailscaleOptions { AutoServe = true, ServiceName = "svc:ncf", ListenPort = 443, Advertise = true };
        await svc.ApplyConfigurationAsync(appliedOptions, CancellationToken.None);
        cli.Calls.Clear();

        // Simulate IOptionsMonitor firing with the exact same options just written to config/config.json
        currentOptions = appliedOptions.Clone();
        Assert.NotNull(changeCallback);
        changeCallback!(currentOptions, Options.DefaultName);

        // No redundant teardown or re-serve should happen
        Assert.Empty(cli.Calls);

        // Now simulate an external manual edit to config/config.json with a different port
        currentOptions = new TailscaleOptions { AutoServe = true, ServiceName = null, ListenPort = 9443 };
        changeCallback(currentOptions, Options.DefaultName);

        Assert.Contains("serve --https=9443 127.0.0.1:5270", cli.Calls);
    }

    [Fact]
    public async Task ConfigurationWriter_SavesTailscaleSection_PreservingExistingSections()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var configDir = Path.Combine(tempDir, "config");
            Directory.CreateDirectory(configDir);
            var configFile = Path.Combine(configDir, "config.json");

            // Seed initial config with another section
            var initialJson = """
            {
                "LlmProvider": {
                    "ProviderType": "Ollama",
                    "Endpoint": "http://localhost:11434"
                }
            }
            """;
            await File.WriteAllTextAsync(configFile, initialJson);

            var hostEnv = new Mock<IHostEnvironment>();
            hostEnv.SetupGet(e => e.ContentRootPath).Returns(tempDir);

            var writer = new TailscaleConfigurationWriter(hostEnv.Object, NullLogger<TailscaleConfigurationWriter>.Instance);

            var options = new TailscaleOptions
            {
                AutoServe = true,
                ServiceName = "svc:ncf",
                ListenPort = 443,
                Advertise = true
            };

            await writer.SaveTailscaleOptionsAsync(options);

            var savedText = await File.ReadAllTextAsync(configFile);
            var jsonNode = JsonNode.Parse(savedText)?.AsObject();
            Assert.NotNull(jsonNode);

            // Existing section preserved
            Assert.True(jsonNode.ContainsKey("LlmProvider"));
            Assert.Equal("Ollama", jsonNode["LlmProvider"]?["ProviderType"]?.GetValue<string>());

            // Tailscale section added/updated
            Assert.True(jsonNode.ContainsKey("Tailscale"));
            var tailscaleSection = jsonNode["Tailscale"]?.AsObject();
            Assert.NotNull(tailscaleSection);
            Assert.True(tailscaleSection["AutoServe"]?.GetValue<bool>());
            Assert.Equal("svc:ncf", tailscaleSection["ServiceName"]?.GetValue<string>());
            Assert.Equal(443, tailscaleSection["ListenPort"]?.GetValue<int>());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void QrCodeSvgGenerator_ProducesValidSvg_AndHandlesEmpty()
    {
        Assert.Empty(QrCodeSvgGenerator.GenerateSvg(""));
        Assert.Empty(QrCodeSvgGenerator.GenerateSvg("   "));

        var svg = QrCodeSvgGenerator.GenerateSvg("https://ncf.tail1234.ts.net");
        Assert.NotEmpty(svg);
        Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</svg>", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("viewBox", svg, StringComparison.OrdinalIgnoreCase);
    }
}

