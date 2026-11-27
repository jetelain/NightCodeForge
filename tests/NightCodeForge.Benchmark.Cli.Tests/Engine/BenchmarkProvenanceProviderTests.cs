using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkProvenanceProviderTests
{
    [Theory]
    [InlineData(ExecutionMode.ContainerSandbox, true)]
    [InlineData(ExecutionMode.WslDirect, true)]
    [InlineData(ExecutionMode.BareHost, false)]
    public async Task CaptureAsync_OnWindows_DistinguishesHostAndContainerDotNetVersionsByExecutionMode(
        ExecutionMode executionMode,
        bool expectContainerDotNet)
    {
        var processRunner = new Mock<IProcessRunner>();
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "git"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "deadbeef", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "11.0.100", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "10.0.115", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "csnav"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "1.0.0", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "codecondenser"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "2.0.0", string.Empty));

        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        containerFactory.SetupGet(f => f.DefaultWslDistribution).Returns("Ubuntu-24.04");

        var provider = new BenchmarkProvenanceProvider(
            processRunner.Object,
            containerFactory.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: true,
            NullLogger<BenchmarkProvenanceProvider>.Instance);

        var provenance = await provider.CaptureAsync(
            LlmProviderType.Ollama,
            "http://localhost:11434",
            enableStackSkills: true,
            optimizationFlags: null,
            executionMode);

        Assert.Equal("11.0.100", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetHost]);
        Assert.Equal("1.0.0", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyCsNav]);
        Assert.Equal("2.0.0", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyCodeCondenser]);

        if (expectContainerDotNet)
        {
            Assert.Equal("10.0.115", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetContainer]);
            processRunner.Verify(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s =>
                    s.FileName == "wsl.exe" &&
                    s.ArgumentList != null &&
                    s.ArgumentList.Contains("Ubuntu-24.04")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            Assert.False(provenance.DetectedToolVersions.ContainsKey(BenchmarkProvenanceProvider.ToolKeyDotNetContainer));
            processRunner.Verify(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe"),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task CaptureAsync_OnLinuxContainerSandbox_ProbesMountedSystemDotNetAndFallsBackToHost()
    {
        var processRunner = new Mock<IProcessRunner>();
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "git"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "abc123", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "11.0.100", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "/usr/share/dotnet/dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "10.0.115", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "csnav" || s.FileName == "codecondenser"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "not found"));

        var provider = new BenchmarkProvenanceProvider(
            processRunner.Object,
            Mock.Of<IContainerEnvironmentFactory>(),
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: false,
            NullLogger<BenchmarkProvenanceProvider>.Instance);

        var withSystemDotNet = await provider.CaptureAsync(
            LlmProviderType.Ollama,
            "http://localhost:11434",
            enableStackSkills: false,
            optimizationFlags: null,
            ExecutionMode.ContainerSandbox);

        Assert.Equal("11.0.100", withSystemDotNet.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetHost]);
        Assert.Equal("10.0.115", withSystemDotNet.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetContainer]);

        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "/usr/share/dotnet/dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "missing"));

        var fallbackToHost = await provider.CaptureAsync(
            LlmProviderType.Ollama,
            "http://localhost:11434",
            enableStackSkills: false,
            optimizationFlags: null,
            ExecutionMode.ContainerSandbox);

        Assert.Equal("11.0.100", fallbackToHost.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetContainer]);
    }

    [Fact]
    public async Task CaptureAsync_OnWindows_IgnoresWslLoginBannerLinesAndUsesWslTimeout()
    {
        var processRunner = new Mock<IProcessRunner>();
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "git"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "deadbeef", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "11.0.100", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "Welcome to Ubuntu 24.04 LTS\n10.0.115\n", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "csnav" || s.FileName == "codecondenser"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "missing"));

        var containerFactory = new Mock<IContainerEnvironmentFactory>();
        containerFactory.SetupGet(f => f.DefaultWslDistribution).Returns("Ubuntu-24.04");

        var provider = new BenchmarkProvenanceProvider(
            processRunner.Object,
            containerFactory.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: true,
            NullLogger<BenchmarkProvenanceProvider>.Instance);

        var provenance = await provider.CaptureAsync(
            LlmProviderType.Ollama,
            "http://localhost:11434",
            enableStackSkills: false,
            optimizationFlags: null,
            ExecutionMode.ContainerSandbox);

        Assert.Equal("11.0.100", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetHost]);
        Assert.Equal("10.0.115", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetContainer]);
        processRunner.Verify(r => r.RunAsync(
            It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe" && s.TimeoutSeconds == 10),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CaptureAsync_OnWindows_DoesNotFallbackToHostDotNetWhenWslProbeFails()
    {
        var processRunner = new Mock<IProcessRunner>();
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "git"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "deadbeef", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "dotnet"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "11.0.100", string.Empty));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "wsl.exe"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "WSL probe failed"));
        processRunner.Setup(r => r.RunAsync(It.Is<ProcessStartSpec>(s => s.FileName == "csnav" || s.FileName == "codecondenser"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(1, string.Empty, "missing"));

        var provider = new BenchmarkProvenanceProvider(
            processRunner.Object,
            Mock.Of<IContainerEnvironmentFactory>(),
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            isWindowsOverride: true,
            NullLogger<BenchmarkProvenanceProvider>.Instance);

        var provenance = await provider.CaptureAsync(
            LlmProviderType.Ollama,
            "http://localhost:11434",
            enableStackSkills: false,
            optimizationFlags: null,
            ExecutionMode.ContainerSandbox);

        Assert.Equal("11.0.100", provenance.DetectedToolVersions[BenchmarkProvenanceProvider.ToolKeyDotNetHost]);
        Assert.False(provenance.DetectedToolVersions.ContainsKey(BenchmarkProvenanceProvider.ToolKeyDotNetContainer));
    }
}
