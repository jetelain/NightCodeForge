using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public static class TestServices
{
    public static ToastService CreateToastService() =>
        new(new BackgroundTaskRunner(NullLogger<BackgroundTaskRunner>.Instance));

    public static IOperationsControlService CreateOperationsControlService(bool isPaused = false)
    {
        var mock = new Mock<IOperationsControlService>();
        mock.Setup(m => m.IsPaused).Returns(isPaused);
        mock.Setup(m => m.ReclaimStatus).Returns(NightCodeForge.Core.Domain.OperationsReclaimStatus.Idle);
        mock.Setup(m => m.GetDeferredTaskCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return mock.Object;
    }

    public static void AddDefaultWebUiServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IWorkspaceRepository>(new InMemoryWorkspaceRepository());
        services.TryAddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        services.TryAddSingleton<ITaskRepository>(new InMemoryTaskRepository());
        services.TryAddSingleton<ITaskMarkdownSyncService>(new MockTaskMarkdownSyncService());
        services.TryAddSingleton<ITaskJobQueue>(new Mock<ITaskJobQueue>().Object);
        services.TryAddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        services.TryAddSingleton<IGitWorkspaceManager>(new Mock<IGitWorkspaceManager>().Object);
        services.TryAddSingleton<IGitHubTaskMirrorService>(new Mock<IGitHubTaskMirrorService>().Object);
        services.TryAddSingleton<IGitHubPullRequestDeliveryService>(new Mock<IGitHubPullRequestDeliveryService>().Object);
        services.TryAddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { AirGappedMode = false }));
        services.TryAddSingleton<WorkspaceStateService>();
        services.TryAddSingleton<ITaskReviewService>(sp => new TaskReviewService(
            sp.GetRequiredService<ITaskRepository>(),
            sp.GetRequiredService<IWorkspaceRepository>(),
            new WorkspaceLockProvider(),
            sp.GetRequiredService<IGitWorkspaceManager>(),
            sp.GetRequiredService<IGitHubPullRequestDeliveryService>(),
            sp.GetRequiredService<ITaskOrchestrator>(),
            sp.GetRequiredService<ITaskMarkdownSyncService>(),
            NullLogger<TaskReviewService>.Instance));
        services.TryAddSingleton<ITaskCreationUiService>(sp => new TaskCreationUiService(
            sp.GetRequiredService<ITaskRepository>(),
            sp.GetRequiredService<IWorkspaceRepository>(),
            sp.GetRequiredService<IWorkflowRepository>(),
            sp.GetRequiredService<ITaskMarkdownSyncService>(),
            sp.GetRequiredService<IGitHubTaskMirrorService>(),
            sp.GetRequiredService<IGitHubPullRequestDeliveryService>(),
            sp.GetRequiredService<ITaskJobQueue>(),
            sp.GetRequiredService<IOptions<SecurityOptions>>(),
            NullLogger<TaskCreationUiService>.Instance));
    }
}
