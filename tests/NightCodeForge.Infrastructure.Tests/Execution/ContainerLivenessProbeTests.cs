using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Execution;

public class ContainerLivenessProbeTests
{
    [Theory]
    [InlineData("true\n", 0, "", true)]
    [InlineData("false\n", 0, "", false)]
    [InlineData("", 1, "Error response from daemon: No such container: ncf-sandbox-1", false)]
    [InlineData("", 1, "Error: No such object: ncf-sandbox-1", false)]
    [InlineData("", 1, "Cannot connect to the Docker daemon", null)]
    public async Task ContainerMountAndDriftBuilder_IsContainerRunningAsync_InterpretsInspectOutcome(
        string stdout,
        int exitCode,
        string stderr,
        bool? expected)
    {
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(exitCode, stdout, stderr));

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-1"
        };

        var actual = await ContainerMountAndDriftBuilder.IsContainerRunningAsync(runnerMock.Object, options);

        Assert.Equal(expected, actual);
        runnerMock.Verify(r => r.RunAsync(
            It.Is<ProcessStartSpec>(s =>
                s.ArgumentList != null &&
                s.ArgumentList.Contains("inspect") &&
                s.ArgumentList.Contains("{{.State.Running}}") &&
                s.ArgumentList.Contains("ncf-sandbox-1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ContainerMountAndDriftBuilder_IsContainerRunningAsync_ReturnsNullForCustomProcessFactory_AndFalseForEmptyName()
    {
        var runnerMock = new Mock<IProcessRunner>(MockBehavior.Strict);

        var customFactoryOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-1",
            CustomProcessFactory = () => new Process()
        };
        Assert.Null(await ContainerMountAndDriftBuilder.IsContainerRunningAsync(runnerMock.Object, customFactoryOptions));

        var emptyNameOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = "  "
        };
        Assert.False(await ContainerMountAndDriftBuilder.IsContainerRunningAsync(runnerMock.Object, emptyNameOptions));
    }

    [Fact]
    public async Task NightCodeForgeContainerExecutor_IsContainerRunningAsync_DelegatesAndHandlesRunnerExceptions()
    {
        var runnerMock = new Mock<IProcessRunner>();
        runnerMock
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, "true\n", string.Empty));

        var identityMock = new Mock<IHostUserIdentityResolver>();
        identityMock.Setup(i => i.ResolveUserIdentity(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string>())).Returns("1000:1000");
        var telemetryMock = new Mock<IOperationsTelemetryService>();

        var options = new ContainerExecutorOptions
        {
            ContainerNameOrId = "ncf-sandbox-42",
            UserUidGid = "1000:1000"
        };

        using var executor = new NightCodeForgeContainerExecutor(
            options,
            runnerMock.Object,
            NullLogger<NightCodeForgeContainerExecutor>.Instance,
            identityMock.Object,
            telemetryMock.Object);

        Assert.True(await executor.IsContainerRunningAsync());

        runnerMock
            .Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("docker unavailable"));

        Assert.Null(await executor.IsContainerRunningAsync());
    }
}
