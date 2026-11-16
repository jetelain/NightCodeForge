using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class AcpHarnessCatalogServiceTests
{
    private readonly Mock<IProcessRunner> _processRunner = new();
    private readonly Mock<IWslMetadataService> _wslMetadata = new();

    public AcpHarnessCatalogServiceTests()
    {
        _wslMetadata.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
    }

    private AcpHarnessCatalogService CreateSut(Func<string, bool>? fileExists = null)
        => new(_processRunner.Object, _wslMetadata.Object, fileExists ?? (_ => false), NullLogger<AcpHarnessCatalogService>.Instance);

    [Fact]
    public async Task ProbeProviderAsync_WhenAgyNotDetected_ReturnsNotInstalledStatus()
    {
        _processRunner.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "not found"));

        var sut = CreateSut();
        var status = await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity);

        Assert.False(status.IsInstalled);
        Assert.False(status.IsAuthenticated);
        Assert.Null(status.DetectedExecutablePath);
        Assert.NotEmpty(status.AvailableModels);
    }

    [Fact]
    public async Task ProbeProviderAsync_WhenInstalledAndAuthenticated_ParsesVersionTierAndModels_AndCaches()
    {
        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("command -v agy"))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "~/.local/bin/agy\n", string.Empty));

        ProcessStartSpec? capturedTokenSpec = null;
        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.StartsWith("test -f ", StringComparison.Ordinal))),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedTokenSpec = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var probeOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"Antigravity ACP Server\",\"version\":\"1.5.3\"},\"_meta\":{\"antigravity\":{\"tier\":{\"name\":\"Google AI Ultra\"}}}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"result\":{\"sessionId\":\"probe-1\",\"configOptions\":[{\"id\":\"thought_level\",\"options\":[{\"value\":\"low\"},{\"value\":\"medium\"},{\"value\":\"high\"}]},{\"id\":\"model\",\"options\":[{\"value\":\"gemini-4-argon\",\"name\":\"Gemini 4 Argon\"},{\"value\":\"claude-opus-5-5\",\"name\":\"Claude Opus 5.5\"}]}]}}") + "\n";

        var sessionStarts = 0;
        ProcessStartSpec? capturedProbeSpec = null;
        _processRunner.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns<ProcessStartSpec>(spec =>
            {
                sessionStarts++;
                capturedProbeSpec = spec;
                return new FakeProbeStreamSession(probeOutput);
            });

        var sut = CreateSut();
        var status = await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity);

        Assert.True(status.IsInstalled);
        Assert.True(status.IsAuthenticated);
        Assert.NotNull(capturedTokenSpec);
        Assert.Contains("sh", capturedTokenSpec!.ArgumentList!);
        Assert.Contains("-c", capturedTokenSpec.ArgumentList!);
        Assert.Contains("test -f ~/.gemini/antigravity-cli/antigravity-oauth-token", capturedTokenSpec.ArgumentList!);

        Assert.NotNull(capturedProbeSpec);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("wsl.exe", capturedProbeSpec!.FileName);
            Assert.Equal(
                ["-d", "Ubuntu", "--", "sh", "-c", "exec \"$HOME/.local/bin/agy\" acp --dangerously-skip-permissions"],
                capturedProbeSpec.ArgumentList);
        }
        Assert.Equal("1.5.3", status.ServerVersion);
        Assert.Equal("Google AI Ultra", status.AccountTierName);
        Assert.Equal(2, status.AvailableModels.Count);
        Assert.Equal("gemini-4-argon", status.AvailableModels[0].ModelId);
        Assert.Equal(["low", "medium", "high"], status.AvailableModels[0].SupportedThoughtLevels);

        // Cached call does not re-spawn probe session
        var cached = await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity, forceRefresh: false);
        Assert.Equal(status, cached);
        Assert.Equal(1, sessionStarts);

        // forceRefresh re-probes
        await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity, forceRefresh: true);
        Assert.Equal(2, sessionStarts);
    }

    [Fact]
    public async Task ProbeProviderAsync_WhenSessionNewReturnsAuthError_ReturnsUnauthenticatedWithDiagnosticHint()
    {
        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("command -v agy"))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "~/.local/bin/agy\n", string.Empty));

        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.StartsWith("test -f ", StringComparison.Ordinal))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, string.Empty));

        var probeOutput = string.Join('\n',
            "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1,\"agentInfo\":{\"name\":\"Antigravity ACP Server\",\"version\":\"1.5.3\"}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":\"session-1\",\"error\":{\"code\":-32001,\"message\":\"Authentication required\"}}") + "\n";

        _processRunner.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new FakeProbeStreamSession(probeOutput));

        var sut = CreateSut();
        var status = await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity);

        Assert.True(status.IsInstalled);
        Assert.False(status.IsAuthenticated);
        Assert.Equal("1.5.3", status.ServerVersion);
        Assert.Contains("Authentication required", status.DiagnosticMessage);
    }

    [Fact]
    public async Task ProbeProviderAsync_WhenInitializeReturnsError_AndParseModelsPreservesSuffixedModelThoughtLevels()
    {
        var sessionResult = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"configOptions\":[{\"id\":\"thought_level\",\"options\":[{\"value\":\"low\"},{\"value\":\"medium\"},{\"value\":\"high\"}]},{\"id\":\"model\",\"options\":[{\"value\":\"custom-oss-medium\",\"name\":\"Custom OSS\"},{\"value\":\"gemini-4-argon\",\"name\":\"Gemini 4 Argon\"}]}],\"models\":{\"availableModels\":[{\"modelId\":\"custom-oss-medium\"},{\"modelId\":\"gemini-4-argon-low\"},{\"modelId\":\"gemini-4-argon-high\"}]}}");

        var parsed = AcpHarnessCatalogService.ParseModelsFromSessionResult(sessionResult);
        var customOss = Assert.Single(parsed, m => m.ModelId == "custom-oss-medium");
        Assert.Equal(["medium"], customOss.SupportedThoughtLevels);
        var argon = Assert.Single(parsed, m => m.ModelId == "gemini-4-argon");
        Assert.Equal(["low", "high"], argon.SupportedThoughtLevels);

        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.Contains("command -v agy"))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "~/.local/bin/agy\n", string.Empty));

        _processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null && s.ArgumentList.Any(a => a.StartsWith("test -f ", StringComparison.Ordinal))),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, string.Empty));

        var probeOutput = "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"error\":{\"code\":-32600,\"message\":\"Unsupported protocol\"}}\n";
        _processRunner.Setup(r => r.StartSession(It.IsAny<ProcessStartSpec>()))
            .Returns(() => new FakeProbeStreamSession(probeOutput));

        var sut = CreateSut();
        var status = await sut.ProbeProviderAsync(KnownAcpCatalogIds.Antigravity);

        Assert.True(status.IsInstalled);
        Assert.False(status.IsAuthenticated);
        Assert.Contains("Authentication required", status.DiagnosticMessage);
    }

    private sealed class FakeProbeStreamSession : IProcessStreamSession
    {
        private readonly MemoryStream _stdinStream = new();
        private readonly MemoryStream _stdoutStream;
        private readonly MemoryStream _stderrStream = new();

        public FakeProbeStreamSession(string stdoutText)
        {
            _stdoutStream = new MemoryStream(Encoding.UTF8.GetBytes(stdoutText));
            StandardInput = new StreamWriter(_stdinStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            StandardOutput = new StreamReader(_stdoutStream, Encoding.UTF8, leaveOpen: true);
            StandardError = new StreamReader(_stderrStream, Encoding.UTF8, leaveOpen: true);
        }

        public int? ExitCode => 0;
        public bool HasExited => true;
        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public IAsyncEnumerable<ProcessStreamChunk> ReadAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public void Kill() { }

        public void Dispose()
        {
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            _stdinStream.Dispose();
            _stdoutStream.Dispose();
            _stderrStream.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
