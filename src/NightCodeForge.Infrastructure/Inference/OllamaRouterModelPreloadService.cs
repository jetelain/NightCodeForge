using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Infrastructure.Inference;

public class OllamaRouterModelPreloadService : ILlmModelPreloadService
{
    private const string DefaultStageId = "preload";

    private readonly HttpClient _httpClient;
    private readonly IOperationsControlService _operationsControlService;
    private readonly IAgentActivityStreamer _streamer;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly ILogger<OllamaRouterModelPreloadService> _logger;

    public OllamaRouterModelPreloadService(
        HttpClient httpClient,
        IOperationsControlService operationsControlService,
        IAgentActivityStreamer streamer,
        IOperationsTelemetryService telemetryService,
        IOptions<LlmEndpointOptions> options,
        ILogger<OllamaRouterModelPreloadService> logger)
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

        var path = OllamaApiPaths.RouterModelPreload(modelName);

        _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
            Model: modelName,
            SelectedTarget: options?.Target,
            Success: false,
            NumCtx: options?.ContextIntentTokens,
            KeepAliveSeconds: options?.KeepAliveSeconds,
            DurationMs: null,
            Reason: "JIT model preload initiated.",
            Timestamp: DateTime.UtcNow,
            IsActive: true));

        var requestBody = new RouterModelPreloadRequest
        {
            KeepAliveSeconds = options?.KeepAliveSeconds,
            ContextIntentTokens = options?.ContextIntentTokens,
            Target = options?.Target
        };

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        int? effectiveContextIntent = options?.ContextIntentTokens;
        try
        {
            response = await _httpClient.PostAsJsonAsync(path, requestBody, cancellationToken);

            // Step 12.29 §3.2: If explicit ContextIntentTokens returned 503 Service Unavailable,
            // retry once with contextIntentTokens = null (soft intent) before reporting qualification failure.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && options?.ContextIntentTokens != null)
            {
                _logger.LogWarning(
                    "JIT preload for '{ModelName}' with explicit contextIntentTokens={Tokens} returned 503. Retrying with soft intent (contextIntentTokens=null).",
                    modelName, options.ContextIntentTokens);

                effectiveContextIntent = null;
                var softBody = new RouterModelPreloadRequest
                {
                    KeepAliveSeconds = options.KeepAliveSeconds,
                    ContextIntentTokens = null,
                    Target = options.Target
                };

                response.Dispose();
                response = await _httpClient.PostAsJsonAsync(path, softBody, cancellationToken);
            }

            RouterModelPreloadResponse? preloadData = null;
            if (response.Content != null)
            {
                try
                {
                    preloadData = await response.Content.ReadFromJsonAsync<RouterModelPreloadResponse>(cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Failed to parse RouterModelPreloadResponse for '{ModelName}' (HTTP {Status})", modelName, (int)response.StatusCode);
                }
            }

            sw.Stop();

            var candidates = preloadData?.Candidates?.Select(c => new ModelPreloadCandidate(
                Target: c.Target,
                Eligible: c.Eligible,
                InFlightRequests: c.InFlightRequests,
                IsResident: c.IsResident,
                ContextCeiling: c.ContextCeiling,
                ServerClass: c.ServerClass,
                Busy: c.Busy,
                SkipReason: c.SkipReason
            )).ToList() ?? (IReadOnlyList<ModelPreloadCandidate>)Array.Empty<ModelPreloadCandidate>();

            bool isSuccess = response.IsSuccessStatusCode && preloadData?.LoadAccepted == true;

            var result = new ModelPreloadResult(
                Success: isSuccess,
                Model: !string.IsNullOrWhiteSpace(preloadData?.Model) ? preloadData.Model : modelName,
                SelectedTarget: preloadData?.SelectedTarget,
                KeepAliveSeconds: preloadData?.KeepAliveSeconds ?? options?.KeepAliveSeconds,
                NumCtx: preloadData?.NumCtx,
                ContextIntentTokens: preloadData?.ContextIntentTokens ?? effectiveContextIntent,
                LoadAccepted: preloadData?.LoadAccepted,
                ResidencyConfirmed: preloadData?.ResidencyConfirmed,
                PreloadDurationMs: preloadData?.PreloadDurationMs ?? sw.ElapsedMilliseconds,
                Candidates: candidates,
                Reason: preloadData?.Reason ?? (isSuccess ? "Model preloaded successfully" : $"Preload returned HTTP {response.StatusCode}"));

            await HandlePreloadOutcomeAsync(result, modelName, options, sw.ElapsedMilliseconds, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                Model: modelName,
                SelectedTarget: options?.Target,
                Success: false,
                NumCtx: effectiveContextIntent,
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
            _logger.LogWarning(ex, "JIT model preload HTTP request failed for '{ModelName}'", modelName);

            var failResult = new ModelPreloadResult(
                Success: false,
                Model: modelName,
                SelectedTarget: options?.Target,
                LoadAccepted: false,
                PreloadDurationMs: sw.ElapsedMilliseconds,
                Reason: ex.Message);

            await HandlePreloadOutcomeAsync(failResult, modelName, options, sw.ElapsedMilliseconds, cancellationToken);
            return failResult;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task HandlePreloadOutcomeAsync(
        ModelPreloadResult result,
        string modelName,
        ModelPreloadOptions? options,
        long elapsedMs,
        CancellationToken cancellationToken)
    {
        var durationMs = result.PreloadDurationMs ?? elapsedMs;

        if (result.Success)
        {
            var tokensStr = result.NumCtx.HasValue ? $"{result.NumCtx.Value.ToString("N0", CultureInfo.InvariantCulture)} tokens" : "dynamic";
            var targetStr = result.SelectedTarget ?? "unknown";
            var keepAliveStr = result.KeepAliveSeconds.HasValue ? result.KeepAliveSeconds.Value.ToString(CultureInfo.InvariantCulture) : "fleet policy";
            var noticeMsg = $"JIT Model Preload: '{modelName}' resident on node '{targetStr}' ({tokensStr}, keep-alive: {keepAliveStr}, {durationMs.ToString("N0", CultureInfo.InvariantCulture)} ms)";

            _logger.LogInformation("{PreloadNotice}", noticeMsg);

            _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                Model: modelName,
                SelectedTarget: result.SelectedTarget,
                Success: true,
                NumCtx: result.NumCtx,
                KeepAliveSeconds: result.KeepAliveSeconds,
                DurationMs: durationMs,
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
                        [AgentActivityMetadataKeys.SelectedTarget] = result.SelectedTarget,
                        [AgentActivityMetadataKeys.NumCtx] = result.NumCtx,
                        [AgentActivityMetadataKeys.KeepAliveSeconds] = result.KeepAliveSeconds,
                        [AgentActivityMetadataKeys.DurationMs] = durationMs
                    }), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast JIT preload success event.");
            }
        }
        else
        {
            var skipReasons = result.Candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.SkipReason))
                .Select(c => $"{c.Target}: {c.SkipReason}");
            var reasonDetail = string.Join("; ", skipReasons);
            var warningMsg = string.IsNullOrWhiteSpace(reasonDetail)
                ? $"JIT Model Preload Warning: '{modelName}' could not be warmed up ({result.Reason ?? "unknown failure"})."
                : $"JIT Model Preload Warning: '{modelName}' could not be warmed up ({result.Reason ?? "No eligible targets"}). {reasonDetail}";

            _logger.LogWarning("{PreloadWarning}", warningMsg);

            _telemetryService.RecordModelPreload(new ModelPreloadTelemetryRecord(
                Model: modelName,
                SelectedTarget: result.SelectedTarget,
                Success: false,
                NumCtx: result.NumCtx,
                KeepAliveSeconds: result.KeepAliveSeconds,
                DurationMs: durationMs,
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
                _logger.LogDebug(ex, "Failed to broadcast JIT preload warning event.");
            }
        }
    }

    private sealed class RouterModelPreloadRequest
    {
        [JsonPropertyName("keepAliveSeconds")]
        public int? KeepAliveSeconds { get; set; }

        [JsonPropertyName("contextIntentTokens")]
        public int? ContextIntentTokens { get; set; }

        [JsonPropertyName("target")]
        public string? Target { get; set; }
    }

    private sealed class RouterModelPreloadCandidateResponse
    {
        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("eligible")]
        public bool Eligible { get; set; }

        [JsonPropertyName("inFlightRequests")]
        public int InFlightRequests { get; set; }

        [JsonPropertyName("isResident")]
        public bool IsResident { get; set; }

        [JsonPropertyName("contextCeiling")]
        public int ContextCeiling { get; set; }

        [JsonPropertyName("serverClass")]
        public bool ServerClass { get; set; }

        [JsonPropertyName("busy")]
        public bool Busy { get; set; }

        [JsonPropertyName("skipReason")]
        public string? SkipReason { get; set; }
    }

    private sealed class RouterModelPreloadResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("selectedTarget")]
        public string? SelectedTarget { get; set; }

        [JsonPropertyName("keepAliveSeconds")]
        public int? KeepAliveSeconds { get; set; }

        [JsonPropertyName("numCtx")]
        public int? NumCtx { get; set; }

        [JsonPropertyName("contextIntentTokens")]
        public int? ContextIntentTokens { get; set; }

        [JsonPropertyName("loadAccepted")]
        public bool LoadAccepted { get; set; }

        [JsonPropertyName("residencyConfirmed")]
        public bool ResidencyConfirmed { get; set; }

        [JsonPropertyName("preloadDurationMs")]
        public long? PreloadDurationMs { get; set; }

        [JsonPropertyName("candidates")]
        public List<RouterModelPreloadCandidateResponse>? Candidates { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }
    }
}
