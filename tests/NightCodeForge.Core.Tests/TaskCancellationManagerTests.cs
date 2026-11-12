using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Concurrency;

namespace NightCodeForge.Core.Tests;

public class TaskCancellationManagerTests
{
    private readonly TaskCancellationManager _manager = new(NullLogger<TaskCancellationManager>.Instance);

    [Fact]
    public void Register_WhenAlreadyRegistered_DoesNotCancelExistingSource()
    {
        using var first = _manager.Register(1);
        using var second = _manager.Register(1);

        Assert.False(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void Unregister_WithStaleSource_KeepsNewerRegistration()
    {
        using var first = _manager.Register(1);
        using var second = _manager.Register(1);

        _manager.Unregister(1, first);

        Assert.True(_manager.Cancel(1));
        Assert.True(second.IsCancellationRequested);
        Assert.False(first.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_MarksExplicitRequest_ButParentCancellationDoesNot()
    {
        using var parent = new CancellationTokenSource();
        using var cts = _manager.Register(7, parent.Token);

        parent.Cancel();
        Assert.True(cts.IsCancellationRequested);
        Assert.False(_manager.IsCancelRequested(7));

        using var cts2 = _manager.Register(8);
        _manager.Cancel(8);
        Assert.True(_manager.IsCancelRequested(8));
    }

    [Fact]
    public void Cancel_WithoutRegistration_ReturnsFalse()
    {
        Assert.False(_manager.Cancel(99));
    }

    [Fact]
    public void NotifyHostShuttingDown_SetsIsHostShuttingDownTrue()
    {
        Assert.False(_manager.IsHostShuttingDown);

        _manager.NotifyHostShuttingDown();

        Assert.True(_manager.IsHostShuttingDown);
    }

    [Fact]
    public void Register_WithPreCancelledParentToken_DoesNotFalseFlagIsHostShuttingDown()
    {
        using var parentCts = new CancellationTokenSource();
        parentCts.Cancel();

        using var cts = _manager.Register(10, parentCts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.False(_manager.IsHostShuttingDown);
    }

    [Fact]
    public void NotifyHostShuttingDown_WhenCalledMultipleTimes_LogsOnlyOnce()
    {
        var loggerMock = new Moq.Mock<Microsoft.Extensions.Logging.ILogger<TaskCancellationManager>>();
        var manager = new TaskCancellationManager(loggerMock.Object);

        manager.NotifyHostShuttingDown();
        manager.NotifyHostShuttingDown();
        manager.NotifyHostShuttingDown();

        Assert.True(manager.IsHostShuttingDown);
        loggerMock.Verify(
            l => l.Log(
                Microsoft.Extensions.Logging.LogLevel.Information,
                Moq.It.IsAny<Microsoft.Extensions.Logging.EventId>(),
                Moq.It.Is<Moq.It.IsAnyType>((v, _) => v.ToString()!.Contains("Host shutdown notification received")),
                Moq.It.IsAny<Exception?>(),
                Moq.It.IsAny<Func<Moq.It.IsAnyType, Exception?, string>>()),
            Moq.Times.Once);
    }
}
