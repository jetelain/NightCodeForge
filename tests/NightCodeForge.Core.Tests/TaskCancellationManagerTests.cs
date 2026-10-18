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
}
