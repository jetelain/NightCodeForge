using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Inference;

public class OllamaModelPreloadService : ILlmModelPreloadService
{
    private const string LocalGpuTarget = "Local GPU";
    private const string DefaultStageId = "preload";
    private const string KeepAliveIndefinite = "-1";

    private readonly HttpClient _httpClient;
    private readonly IOperationsControlService _operationsControlService;
    private readonly IAgentActivityStreamer _streamer;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger<OllamaModelPreloadService> _logger;

    public OllamaModelPreloadService(
        HttpClient httpClient,
        IOperationsControlService operationsControlService,
        IAgentActivityStreamer streamer,
        IOperationsTelemetryService telemetryService,
        IOptions<LlmEndpointOptions> options,
        ILogger<OllamaModelPreloadService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _streamer = streamer ?? throw new ArgumentNullException(nameof(streamer));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LlmEndpointHttpClient.Configure(httpClient, options);
    }

    public async Task<ModelPreloadResult> PreloadModelAsync(
        string modelName,
        ModelPreloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new ModelPreloadResult(false, modelName ?? string.Empty, Reason: "Model name cannot be empty.");
        }

        if (_operationsControlService.IsPaused)
        {
            _logger.LogWarning("Rejecting JIT model preload for '{ModelName}' because global operations are paused.", modelName);
            throw new OperationsPausedException($"Cannot preload model '{modelName}' while global operations are paused.");
        }

        var keepAliveStr = options?.KeepAliveSeconds switch
        {
            -1 => KeepAliveIndefinite,
            > 0 => $"{options.KeepAliveSeconds.Value.ToString(CultureInfo.InvariantCulture)}s",
            _ => WorkflowDefaults.ModelPreloadKeepAlive
        };

        _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
            Model: modelName,
            SelectedTarget: LocalGpuTarget,
            Success: false,
            NumCtx: options?.ContextIntentTokens,
            KeepAliveSeconds: options?.KeepAliveSeconds,
            DurationMs: null,
            Reason: "JIT model preload initiated.",
            Timestamp: DateTime.UtcNow,
            IsActive: true));

        var sw = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("Triggering JIT model preload for '{ModelName}' with keep_alive={KeepAlive}", modelName, keepAliveStr);
            var payload = new { model = modelName, keep_alive = keepAliveStr };
            using var response = await _httpClient.PostAsJsonAsync(OllamaApiPaths.Generate, payload, cancellationToken);
            sw.Stop();

            var isSuccess = response.IsSuccessStatusCode;
            var result = new ModelPreloadResult(
                Success: isSuccess,
                Model: modelName,
                SelectedTarget: LocalGpuTarget,
                KeepAliveSeconds: options?.KeepAliveSeconds,
                ContextIntentTokens: options?.ContextIntentTokens,
                LoadAccepted: isSuccess,
                PreloadDurationMs: sw.ElapsedMilliseconds,
                Reason: isSuccess ? "Model preloaded successfully" : $"Generate returned HTTP {response.StatusCode}");

            if (isSuccess)
            {
                var noticeMsg = $"JIT Model Preload: '{modelName}' resident on node '{LocalGpuTarget}' (keep-alive: {keepAliveStr}, {sw.ElapsedMilliseconds.ToString("N0", CultureInfo.InvariantCulture)} ms)";
                _logger.LogInformation("{PreloadNotice}", noticeMsg);

                _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                    Model: modelName,
                    SelectedTarget: LocalGpuTarget,
                    Success: true,
                    NumCtx: options?.ContextIntentTokens,
                    KeepAliveSeconds: options?.KeepAliveSeconds,
                    DurationMs: sw.ElapsedMilliseconds,
                    Reason: result.Reason,
                    Timestamp: DateTime.UtcNow,
                    IsActive: false));

                try
                {
                    await _streamer.BroadcastAsync(new AgentActivityEvent(
                        SessionId: Guid.NewGuid().ToString("N"),
                        TaskId: options?.TaskId ?? 0,
                        StageId: options?.StageId ?? DefaultStageId,
                        Kind: AgentActivityKind.OptimizationAction,
                        Content: noticeMsg,
                        Timestamp: DateTime.UtcNow,
                        Metadata: new Dictionary<string, object?>
                        {
                            [AgentActivityMetadataKeys.ActionType] = AgentActivityMetadataKeys.ActionTypeJitModelPreload,
                            [AgentActivityMetadataKeys.Model] = modelName,
                            [AgentActivityMetadataKeys.SelectedTarget] = LocalGpuTarget,
                            [AgentActivityMetadataKeys.KeepAlive] = keepAliveStr,
                            [AgentActivityMetadataKeys.DurationMs] = sw.ElapsedMilliseconds
                        }), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to broadcast standalone JIT preload success event.");
                }
            }
            else
            {
                var warningMsg = $"JIT Model Preload Warning: '{modelName}' failed ({result.Reason}).";
                _logger.LogWarning("{PreloadWarning}", warningMsg);

                _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                    Model: modelName,
                    SelectedTarget: LocalGpuTarget,
                    Success: false,
                    NumCtx: options?.ContextIntentTokens,
                    KeepAliveSeconds: options?.KeepAliveSeconds,
                    DurationMs: sw.ElapsedMilliseconds,
                    Reason: warningMsg,
                    Timestamp: DateTime.UtcNow,
                    IsActive: false));

                try
                {
                    await _streamer.BroadcastAsync(new AgentActivityEvent(
                        SessionId: Guid.NewGuid().ToString("N"),
                        TaskId: options?.TaskId ?? 0,
                        StageId: options?.StageId ?? DefaultStageId,
                        Kind: AgentActivityKind.Notice,
                        Content: warningMsg,
                        Timestamp: DateTime.UtcNow,
                        Metadata: new Dictionary<string, object?>
                        {
                            [AgentActivityMetadataKeys.Model] = modelName,
                            [AgentActivityMetadataKeys.Reason] = result.Reason,
                            [AgentActivityMetadataKeys.Warning] = true
                        }), cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to broadcast standalone JIT preload warning event.");
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                Model: modelName,
                SelectedTarget: LocalGpuTarget,
                Success: false,
                NumCtx: options?.ContextIntentTokens,
                KeepAliveSeconds: options?.KeepAliveSeconds,
                DurationMs: sw.ElapsedMilliseconds,
                Reason: "JIT model preload cancelled.",
                Timestamp: DateTime.UtcNow,
                IsActive: false));
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "JIT model preload failed for '{ModelName}'", modelName);

            var failResult = new ModelPreloadResult(
                Success: false,
                Model: modelName,
                SelectedTarget: LocalGpuTarget,
                LoadAccepted: false,
                PreloadDurationMs: sw.ElapsedMilliseconds,
                Reason: ex.Message);

            _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                Model: modelName,
                SelectedTarget: LocalGpuTarget,
                Success: false,
                NumCtx: options?.ContextIntentTokens,
                KeepAliveSeconds: options?.KeepAliveSeconds,
                DurationMs: sw.ElapsedMilliseconds,
                Reason: ex.Message,
                Timestamp: DateTime.UtcNow,
                IsActive: false));

            return failResult;
        }
    }
}
