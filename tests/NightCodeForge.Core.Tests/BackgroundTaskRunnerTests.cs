using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Execution;

namespace NightCodeForge.Core.Tests;

public class BackgroundTaskRunnerTests
{
    [Fact]
    public async Task RunTracked_ExecutesWorkSuccessfully()
    {
        var loggerMock = new Mock<ILogger<BackgroundTaskRunner>>();
        var runner = new BackgroundTaskRunner(loggerMock.Object);

        var executed = new TaskCompletionSource<bool>();
        runner.RunTracked(async () =>
        {
            await Task.Delay(10);
            executed.SetResult(true);
        }, "TestOperation");

        var completed = await Task.WhenAny(executed.Task, Task.Delay(2000));
        Assert.Same(executed.Task, completed);
        Assert.True(await executed.Task);
    }

    [Fact]
    public async Task RunTracked_CatchesAndLogsUnhandledException_WithoutRethrowing()
    {
        var loggerMock = new Mock<ILogger<BackgroundTaskRunner>>();
        var runner = new BackgroundTaskRunner(loggerMock.Object);

        var executed = new TaskCompletionSource<bool>();
        runner.RunTracked(async () =>
        {
            await Task.Yield();
            executed.SetResult(true);
            throw new InvalidOperationException("Simulated background error");
        }, "FailingOperation");

        await Task.WhenAny(executed.Task, Task.Delay(2000));
        Assert.True(executed.Task.IsCompletedSuccessfully);

        // Allow runner's continuation/catch block to execute
        await Task.Delay(100);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("FailingOperation")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunTracked_HandlesCleanCancellation_WithoutLoggingError()
    {
        var loggerMock = new Mock<ILogger<BackgroundTaskRunner>>();
        var runner = new BackgroundTaskRunner(loggerMock.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var executed = new TaskCompletionSource<bool>();
        runner.RunTracked(async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            executed.SetResult(true);
        }, "CancelledOperation", cts.Token);

        await Task.Delay(100);

        // Error should NOT be logged for clean cancellation
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void RunTracked_ThrowsOnNullWork()
    {
        var loggerMock = new Mock<ILogger<BackgroundTaskRunner>>();
        var runner = new BackgroundTaskRunner(loggerMock.Object);

        Assert.Throws<ArgumentNullException>(() => runner.RunTracked((Func<Task>)null!, "Op"));
        Assert.Throws<ArgumentNullException>(() => runner.RunTracked((Func<CancellationToken, Task>)null!, "Op"));
    }
}
