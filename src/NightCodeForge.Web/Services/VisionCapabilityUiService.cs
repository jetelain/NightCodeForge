using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Web.Services;

/// <summary>
/// UI-ready vision capability assessment for an active model or workspace agent stage.
/// </summary>
public sealed record VisionCapabilityStatus(
    bool SupportsVision,
    string? ModelName,
    bool IsAcpHarness,
    string? WarningMessage);

/// <summary>
/// Evaluates multimodal vision capability outside Blazor Razor components so
/// Ideation, CreateTask, and model pickers can surface badges and fail-fast warnings cleanly.
/// </summary>
public interface IVisionCapabilityUiService
{
    Task<VisionCapabilityStatus> EvaluateModelVisionAsync(
        string? modelName,
        bool isAcpHarness = false,
        CancellationToken cancellationToken = default);

    Task<VisionCapabilityStatus> EvaluateWorkspaceStageVisionAsync(
        int workspaceId,
        string stageId,
        TaskDifficulty? difficulty = null,
        string? modelOverride = null,
        CancellationToken cancellationToken = default);

    Task<VisionCapabilityStatus> EvaluateWorkspaceTaskVisionAsync(
        int workspaceId,
        TaskDifficulty difficulty,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class VisionCapabilityUiService : IVisionCapabilityUiService
{
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly LlmProviderOptions _llmOptions;
    private readonly ILlmGatewayService _gatewayService;
    private readonly ILlmCapabilityResolver _capabilityResolver;
    private readonly ILogger<VisionCapabilityUiService> _logger;

    public VisionCapabilityUiService(
        IWorkspaceRepository workspaceRepository,
        IOptions<LlmProviderOptions> llmOptions,
        ILlmGatewayService gatewayService,
        ILlmCapabilityResolver capabilityResolver,
        ILogger<VisionCapabilityUiService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _llmOptions = (llmOptions ?? throw new ArgumentNullException(nameof(llmOptions))).Value;
        _gatewayService = gatewayService ?? throw new ArgumentNullException(nameof(gatewayService));
        _capabilityResolver = capabilityResolver ?? throw new ArgumentNullException(nameof(capabilityResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<VisionCapabilityStatus> EvaluateModelVisionAsync(
        string? modelName,
        bool isAcpHarness = false,
        CancellationToken cancellationToken = default)
    {
        var trimmedModel = !string.IsNullOrWhiteSpace(modelName)
            ? modelName.Trim()
            : _llmOptions.ModelId?.Trim();

        if (isAcpHarness)
        {
            return new VisionCapabilityStatus(
                SupportsVision: true,
                ModelName: trimmedModel,
                IsAcpHarness: true,
                WarningMessage: null);
        }

        if (string.IsNullOrWhiteSpace(trimmedModel))
        {
            return new VisionCapabilityStatus(
                SupportsVision: false,
                ModelName: null,
                IsAcpHarness: false,
                WarningMessage: "No active model is configured to verify vision support. Select or configure a vision-capable model before inspecting images.");
        }

        bool? staticOverride = ResolveStaticVisionOverride(trimmedModel);
        bool supportsVision;
        if (staticOverride.HasValue)
        {
            supportsVision = staticOverride.Value;
        }
        else if (_capabilityResolver.SupportsVision(trimmedModel))
        {
            supportsVision = true;
        }
        else
        {
            bool discoveredVision = false;
            try
            {
                discoveredVision = await _gatewayService
                    .ResolveVisionCapabilityAsync(trimmedModel, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to resolve vision capability for model '{Model}'.", trimmedModel);
            }

            supportsVision = discoveredVision || _capabilityResolver.SupportsVision(trimmedModel);
        }

        string? warning = supportsVision
            ? null
            : $"Active model '{trimmedModel}' does not support vision. Attached images cannot be inspected visually by this model.";

        return new VisionCapabilityStatus(
            SupportsVision: supportsVision,
            ModelName: trimmedModel,
            IsAcpHarness: false,
            WarningMessage: warning);
    }

    public async Task<VisionCapabilityStatus> EvaluateWorkspaceStageVisionAsync(
        int workspaceId,
        string stageId,
        TaskDifficulty? difficulty = null,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);

        AgentProfile? profile = difficulty.HasValue
            ? await _workspaceRepository.GetEffectiveAgentProfileAsync(workspaceId, stageId, difficulty.Value, cancellationToken).ConfigureAwait(false)
            : await _workspaceRepository.GetEffectiveAgentProfileAsync(workspaceId, stageId, cancellationToken).ConfigureAwait(false);

        bool isAcpHarness = string.IsNullOrWhiteSpace(modelOverride)
            && profile != null
            && string.Equals(profile.HarnessId, StandardHarnessIds.Acp, StringComparison.OrdinalIgnoreCase);

        // Explicit model overrides in Ideation always run against the Crucible/local LLM gateway unless an ACP model is not selected;
        // if an override is specified on a Crucible profile, inspect that model directly.
        var targetModel = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride.Trim()
            : profile?.ModelName;

        return await EvaluateModelVisionAsync(targetModel, isAcpHarness, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VisionCapabilityStatus> EvaluateWorkspaceTaskVisionAsync(
        int workspaceId,
        TaskDifficulty difficulty,
        CancellationToken cancellationToken = default)
    {
        var implStatus = await EvaluateWorkspaceStageVisionAsync(
            workspaceId,
            StandardStageIds.Implementation,
            difficulty,
            modelOverride: null,
            cancellationToken).ConfigureAwait(false);

        if (!implStatus.SupportsVision)
        {
            return implStatus;
        }

        var reviewStatus = await EvaluateWorkspaceStageVisionAsync(
            workspaceId,
            StandardStageIds.TaskReview,
            difficulty,
            modelOverride: null,
            cancellationToken).ConfigureAwait(false);

        return reviewStatus.SupportsVision ? implStatus : reviewStatus;
    }

    private bool? ResolveStaticVisionOverride(string modelName)
    {
        var staticProfile = ModelCapabilityResolver.FindProfile(_llmOptions, modelName);
        if (staticProfile?.SupportsVision is { } profileOverride)
        {
            return profileOverride;
        }

        if (_llmOptions.ProviderType == LlmProviderType.OpenAiCompatible)
        {
            var allowedOverride = _llmOptions.OpenAiCompatible?.AllowedModels?
                .FirstOrDefault(m => string.Equals(m?.ModelId?.Trim(), modelName, StringComparison.OrdinalIgnoreCase))?
                .SupportsVision;
            if (allowedOverride.HasValue)
            {
                return allowedOverride.Value;
            }
        }

        return null;
    }
}
