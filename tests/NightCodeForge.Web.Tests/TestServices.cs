using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Pmad.Markitdown;

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
        services.TryAddSingleton<IIdeationConversationCoordinator>(sp =>
            new MockIdeationConversationCoordinator(sp.GetRequiredService<ITaskRepository>()));
        services.TryAddSingleton<ITaskAttachmentRepository>(sp =>
            new InMemoryTaskAttachmentRepository(sp.GetRequiredService<ITaskRepository>()));
        services.TryAddSingleton<ITaskMarkdownSyncService>(new MockTaskMarkdownSyncService());
        services.TryAddSingleton<ITaskJobQueue>(new Mock<ITaskJobQueue>().Object);
        services.TryAddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        services.TryAddSingleton<IGitWorkspaceManager>(new Mock<IGitWorkspaceManager>().Object);
        services.TryAddSingleton<IGitHubTaskMirrorService>(new Mock<IGitHubTaskMirrorService>().Object);
        services.TryAddSingleton<IGitHubPullRequestDeliveryService>(new Mock<IGitHubPullRequestDeliveryService>().Object);
        services.TryAddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { AirGappedMode = false }));
        services.TryAddSingleton<IWorkspacePathResolver>(_ =>
        {
            var mock = new Mock<IWorkspacePathResolver>();
            mock.Setup(p => p.GetAttachmentsPath(It.IsAny<int>()))
                .Returns<int>(id => Path.Combine(Path.GetTempPath(), $"ncf-test-wks-{id}", "attachments"));
            mock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
            return mock.Object;
        });
        services.TryAddSingleton<IAttachmentIntakeService>(sp => new AttachmentIntakeService(
            sp.GetRequiredService<ITaskAttachmentRepository>(),
            sp.GetRequiredService<IWorkspaceRepository>(),
            sp.GetRequiredService<IWorkspacePathResolver>(),
            new Mock<IProcessRunner>().Object,
            new Markitdown(new MarkitdownOptions { IncludeTitle = true, CleanHtml = true, FilterHiddenText = true }),
            new HttpClient(),
            sp.GetRequiredService<IOptions<SecurityOptions>>(),
            Options.Create(new AttachmentIntakeOptions()),
            NullLogger<AttachmentIntakeService>.Instance));
        services.TryAddSingleton<IAttachmentUiService>(sp => new AttachmentUiService(
            sp.GetRequiredService<IAttachmentIntakeService>(),
            sp.GetRequiredService<ITaskAttachmentRepository>(),
            sp.GetRequiredService<ITaskRepository>(),
            sp.GetRequiredService<IWorkspaceRepository>(),
            sp.GetRequiredService<IWorkspacePathResolver>(),
            sp.GetRequiredService<IIdeationConversationCoordinator>(),
            NullLogger<AttachmentUiService>.Instance));
        services.TryAddSingleton<ICompanionTriageDraftStore>(_ => new CompanionTriageDraftStore(TimeProvider.System));
        services.TryAddSingleton<IdeationRefinementService>(sp => new IdeationRefinementService(sp.GetRequiredService<ITaskRepository>()));
        services.TryAddSingleton<IIdeationProposalService>(sp => new IdeationProposalService(
            sp.GetRequiredService<ITaskRepository>(),
            sp.GetRequiredService<IWorkflowRepository>(),
            sp.GetRequiredService<ITaskAttachmentRepository>(),
            sp.GetRequiredService<ITaskMarkdownSyncService>(),
            sp.GetRequiredService<IIdeationConversationCoordinator>(),
            NullLogger<IdeationProposalService>.Instance));
        services.TryAddSingleton<IdeationSessionState>(sp => new IdeationSessionState(
            sp.GetRequiredService<IIdeationConversationCoordinator>(),
            sp.GetRequiredService<IIdeationProposalService>(),
            sp.GetRequiredService<IdeationRefinementService>(),
            sp.GetRequiredService<ICompanionTriageDraftStore>(),
            sp.GetRequiredService<IAttachmentUiService>(),
            NullLogger<IdeationSessionState>.Instance));
        services.TryAddSingleton<IAcpHarnessCatalogService>(_ =>
        {
            var mock = new Mock<IAcpHarnessCatalogService>();
            var defaultStatus = new AcpCatalogProviderStatus(
                KnownAcpCatalogIds.Antigravity,
                AcpHarnessCatalogService.AntigravityDisplayName,
                IsInstalled: true,
                IsAuthenticated: true,
                DetectedExecutablePath: "~/.local/bin/agy",
                ExecutionTargetDescription: "WSL2 (Ubuntu) + Container Bind-Mount",
                ServerVersion: "1.5.3",
                AccountTierName: "Google AI Ultra",
                DiagnosticMessage: null,
                AvailableModels: AcpHarnessCatalogService.DefaultAntigravityModels);
            mock.Setup(s => s.GetCatalogStatusAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([defaultStatus]);
            mock.Setup(s => s.ProbeProviderAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(defaultStatus);
            return mock.Object;
        });
        services.TryAddSingleton<WorkspaceStateService>();
        services.TryAddSingleton(_ => WorkerStateTestHelper.Create(new Mock<IOperationsTelemetryService>().Object));
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
            sp.GetRequiredService<ITaskAttachmentRepository>(),
            sp.GetRequiredService<IOptions<SecurityOptions>>(),
            NullLogger<TaskCreationUiService>.Instance));
    }

    public static IdeationSessionState CreateIdeationSessionState(
        IIdeationConversationCoordinator? coordinator = null,
        ITaskRepository? taskRepository = null,
        IWorkflowRepository? workflowRepository = null,
        ITaskAttachmentRepository? attachmentRepository = null,
        ITaskMarkdownSyncService? markdownSync = null,
        ICompanionTriageDraftStore? companionDraftStore = null,
        IAttachmentUiService? attachmentUiService = null)
    {
        var tasks = taskRepository ?? new InMemoryTaskRepository();
        var effectiveCoordinator = coordinator ?? new MockIdeationConversationCoordinator(tasks);
        var workflows = workflowRepository ?? new InMemoryWorkflowRepository();
        var attachments = attachmentRepository ?? new InMemoryTaskAttachmentRepository(tasks);
        var proposalService = new IdeationProposalService(
            tasks,
            workflows,
            attachments,
            markdownSync ?? new MockTaskMarkdownSyncService(),
            effectiveCoordinator,
            NullLogger<IdeationProposalService>.Instance);

        var effectiveAttachmentUi = attachmentUiService;
        if (effectiveAttachmentUi == null)
        {
            var mock = new Mock<IAttachmentUiService>();
            mock.Setup(a => a.LoadWorkspaceAttachmentsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<TaskAttachment>());
            mock.Setup(a => a.ExtractMessageCitations(It.IsAny<string?>()))
                .Returns<string?>(text => AttachmentPresentationHelper.ExtractMessageCitations(text));
            effectiveAttachmentUi = mock.Object;
        }

        return new IdeationSessionState(
            effectiveCoordinator,
            proposalService,
            new IdeationRefinementService(tasks),
            companionDraftStore ?? new CompanionTriageDraftStore(TimeProvider.System),
            effectiveAttachmentUi,
            NullLogger<IdeationSessionState>.Instance);
    }
}
