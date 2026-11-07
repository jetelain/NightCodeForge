using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

/// <summary>
/// Test helper to create WorkerStateService instances with mock or stub options.
/// </summary>
public static class WorkerStateTestHelper
{
    public static WorkerStateService Create(
        IOperationsTelemetryService telemetryService,
        IOptions<LlmProviderOptions>? llmOptions = null)
    {
        return new WorkerStateService(
            telemetryService,
            llmOptions ?? Options.Create(new LlmProviderOptions()));
    }

    public static WorkerStateService Create(
        IOperationsTelemetryService telemetryService,
        int maxConcurrentTasks)
    {
        return new WorkerStateService(
            telemetryService,
            Options.Create(new LlmProviderOptions { MaxConcurrentInferenceTasks = maxConcurrentTasks }));
    }
}

