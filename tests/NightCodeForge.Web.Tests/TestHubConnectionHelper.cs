using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

/// <summary>
/// Helper to construct OrchestratorHubClientConnection for tests using test doubles.
/// </summary>
public static class TestHubConnectionHelper
{
    public static OrchestratorHubClientConnection Create(
        NavigationManager? nav = null,
        ILogger<OrchestratorHubClientConnection>? logger = null,
        WorkerStateService? workerStateService = null,
        bool autoStartNoOp = true)
    {
        nav ??= new TestNavigationManager();
        logger ??= Mock.Of<ILogger<OrchestratorHubClientConnection>>();
        workerStateService ??= new WorkerStateService(Mock.Of<IOperationsTelemetryService>());

        if (autoStartNoOp)
        {
            return new TestOrchestratorHubClientConnection(nav, logger, workerStateService);
        }

        return new OrchestratorHubClientConnection(nav, logger, workerStateService);
    }

    private sealed class TestOrchestratorHubClientConnection : OrchestratorHubClientConnection
    {
        public TestOrchestratorHubClientConnection(
            NavigationManager nav,
            ILogger<OrchestratorHubClientConnection> logger,
            WorkerStateService workerState)
            : base(nav, logger, workerState)
        {
        }

        public override Task StartAsync() => Task.CompletedTask;
    }
}

public sealed class TestNavigationManager : NavigationManager
{
    public TestNavigationManager(string baseUri = "http://localhost/")
    {
        Initialize(baseUri, baseUri);
    }
}
