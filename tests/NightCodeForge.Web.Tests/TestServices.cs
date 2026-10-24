using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Execution;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public static class TestServices
{
    public static ToastService CreateToastService() =>
        new(new BackgroundTaskRunner(NullLogger<BackgroundTaskRunner>.Instance));

    public static NightCodeForge.Core.Interfaces.IOperationsControlService CreateOperationsControlService(bool isPaused = false)
    {
        var mock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>();
        mock.Setup(m => m.IsPaused).Returns(isPaused);
        mock.Setup(m => m.ReclaimStatus).Returns(NightCodeForge.Core.Domain.OperationsReclaimStatus.Idle);
        mock.Setup(m => m.GetDeferredTaskCountAsync(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return mock.Object;
    }
}
