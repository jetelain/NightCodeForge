using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Tests.Verification;

public sealed class BenchmarkCustomImageBuilderTests
{
    [Fact]
    public void PythonChildImage_DockerfileInstallsRequiredCliTools_AndIsOutsideManagedBaseImageCheck()
    {
        Assert.False(ContainerMountAndDriftBuilder.IsManagedBaseImage(BenchmarkContainerImages.Python311ChildImage));

        var dockerfile = BenchmarkCustomImageBuilder.GetDockerfile(BenchmarkContainerImages.Python311ChildImage);
        Assert.Contains($"FROM {BenchmarkContainerImages.Python311BaseImage}", dockerfile);
        foreach (var package in new[] { "ripgrep", "git", "jq", "procps", "curl", "ca-certificates", "unzip" })
        {
            Assert.Contains(package, dockerfile);
        }
    }

    [Fact]
    public async Task EnsureImageAsync_SkipsBuildWhenImageInspectSucceeds()
    {
        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("inspect")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "[]", string.Empty));

        var builder = new BenchmarkCustomImageBuilder(
            processRunner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { UseWslForContainers = false, AutoDetectWsl = false }),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        await builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage);

        processRunner.Verify(r => r.RunAsync(
            It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("build")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureImageAsync_SerializesConcurrentCallsPerTag_AndBuildsOnce()
    {
        var inspectCalls = 0;
        var buildCalls = 0;
        var imageExists = false;

        var processRunner = new Mock<IProcessRunner>();
        processRunner
            .Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("inspect")),
                It.IsAny<CancellationToken>()))
            .Returns<ProcessStartSpec, CancellationToken>((_, _) =>
            {
                Interlocked.Increment(ref inspectCalls);
                return Task.FromResult(new ProcessExecutionResult(imageExists ? 0 : 1, string.Empty, imageExists ? string.Empty : "No such image"));
            });

        processRunner
            .Setup(r => r.RunAsync(
                It.Is<ProcessStartSpec>(s => s.ArgumentList != null && s.ArgumentList.Contains("build")),
                It.IsAny<CancellationToken>()))
            .Returns<ProcessStartSpec, CancellationToken>(async (_, ct) =>
            {
                Interlocked.Increment(ref buildCalls);
                await Task.Delay(50, ct);
                imageExists = true;
                return new ProcessExecutionResult(0, "built", string.Empty);
            });

        var builder = new BenchmarkCustomImageBuilder(
            processRunner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { UseWslForContainers = false, AutoDetectWsl = false }),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        await Task.WhenAll(
            builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage),
            builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage),
            builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage));

        Assert.Equal(3, inspectCalls);
        Assert.Equal(1, buildCalls);
    }

    [Fact]
    public async Task EnsureImageAsync_FailsFastWithBuildStderr_AndRoutesThroughWslOrDirectDocker()
    {
        ProcessStartSpec? capturedWslInspect = null;
        ProcessStartSpec? capturedWslBuild = null;

        var wslRunner = new Mock<IProcessRunner>();
        wslRunner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) =>
            {
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("inspect"))
                {
                    capturedWslInspect = spec;
                }
                else if (spec.ArgumentList != null && spec.ArgumentList.Contains("build"))
                {
                    capturedWslBuild = spec;
                }
            })
            .Returns<ProcessStartSpec, CancellationToken>((spec, _) =>
            {
                if (spec.ArgumentList != null && spec.ArgumentList.Contains("inspect"))
                {
                    return Task.FromResult(new ProcessExecutionResult(1, string.Empty, "not found"));
                }

                return Task.FromResult(new ProcessExecutionResult(1, string.Empty, "E: Unable to locate package ripgrep"));
            });

        var wslBuilder = new BenchmarkCustomImageBuilder(
            wslRunner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions
            {
                UseWslForContainers = true,
                DefaultWslDistribution = "Ubuntu-24.04",
                WslExecutable = "wsl.exe"
            }),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        var ex = await Assert.ThrowsAsync<BenchmarkException>(() =>
            wslBuilder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage));

        Assert.Contains(BenchmarkContainerImages.Python311ChildImage, ex.Message);
        Assert.Contains("Unable to locate package ripgrep", ex.Message);

        Assert.NotNull(capturedWslInspect);
        Assert.Equal("wsl.exe", capturedWslInspect!.FileName);
        Assert.Contains("Ubuntu-24.04", capturedWslInspect.ArgumentList!);
        Assert.Contains("docker", capturedWslInspect.ArgumentList!);

        Assert.NotNull(capturedWslBuild);
        Assert.Equal("wsl.exe", capturedWslBuild!.FileName);
        Assert.Contains("build", capturedWslBuild.ArgumentList!);

        ProcessStartSpec? capturedDirectInspect = null;
        var directRunner = new Mock<IProcessRunner>();
        directRunner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedDirectInspect = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, "[]", string.Empty));

        var directBuilder = new BenchmarkCustomImageBuilder(
            directRunner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions
            {
                UseWslForContainers = false,
                AutoDetectWsl = false
            }),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        await directBuilder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage);
        Assert.NotNull(capturedDirectInspect);
        Assert.Equal("docker", capturedDirectInspect!.FileName);
    }

    [Fact]
    public async Task EnsureImageAsync_ThrowsBenchmarkExceptionForUnregisteredImage()
    {
        var builder = new BenchmarkCustomImageBuilder(
            new Mock<IProcessRunner>(MockBehavior.Strict).Object,
            Options.Create(new ExecutionEnvironmentProviderOptions()),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        Assert.False(builder.IsManagedBenchmarkImage("python:3.11-bookworm"));
        await Assert.ThrowsAsync<BenchmarkException>(() => builder.EnsureImageAsync("python:3.11-bookworm"));
    }

    [Fact]
    public async Task EnsureImageAsync_WhenWslUncWorkspacesDirectoryConfigured_RoutesDockerThroughWsl()
    {
        ProcessStartSpec? capturedInspect = null;
        var runner = new Mock<IProcessRunner>();
        runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .Callback<ProcessStartSpec, CancellationToken>((spec, _) => capturedInspect = spec)
            .ReturnsAsync(new ProcessExecutionResult(0, "[]", string.Empty));

        var builder = new BenchmarkCustomImageBuilder(
            runner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions
            {
                UseWslForContainers = false,
                AutoDetectWsl = false,
                WslExecutable = "wsl.exe"
            }),
            Options.Create(new BenchmarkWorkspaceOptions
            {
                WorkspacesDirectory = @"\\wsl.localhost\Ubuntu-24.04\home\user\wks"
            }),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        await builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage);

        Assert.NotNull(capturedInspect);
        Assert.Equal("wsl.exe", capturedInspect!.FileName);
        Assert.Contains("-d", capturedInspect.ArgumentList!);
        Assert.Contains("Ubuntu-24.04", capturedInspect.ArgumentList!);
        Assert.Contains("docker", capturedInspect.ArgumentList!);
    }

    [Fact]
    public async Task EnsureImageAsync_WrapsProcessRunnerExceptionInBenchmarkException()
    {
        var runner = new Mock<IProcessRunner>();
        runner
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker binary missing"));

        var builder = new BenchmarkCustomImageBuilder(
            runner.Object,
            Options.Create(new ExecutionEnvironmentProviderOptions { UseWslForContainers = false, AutoDetectWsl = false }),
            Options.Create(new BenchmarkWorkspaceOptions()),
            NullLogger<BenchmarkCustomImageBuilder>.Instance);

        var ex = await Assert.ThrowsAsync<BenchmarkException>(() =>
            builder.EnsureImageAsync(BenchmarkContainerImages.Python311ChildImage));

        Assert.Contains(BenchmarkContainerImages.Python311ChildImage, ex.Message);
        Assert.Contains("docker binary missing", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }
}
