using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

public class OllamaModelPreloadService : ILlmModelPreloadService
{
    private readonly HttpClient _httpClient;
    private readonly IOperationsControlService _operationsControlService;
    private readonly ILogger<OllamaModelPreloadService> _logger;

    public OllamaModelPreloadService(
        HttpClient httpClient,
        IOperationsControlService operationsControlService,
        IOptions<LlmEndpointOptions> options,
        ILogger<OllamaModelPreloadService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LlmEndpointHttpClient.Configure(httpClient, options);
    }

    public async Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        if (_operationsControlService.IsPaused)
        {
            _logger.LogWarning("Rejecting JIT model preload for '{ModelName}' because global operations are paused.", modelName);
            throw new OperationsPausedException($"Cannot preload model '{modelName}' while global operations are paused.");
        }

        try
        {
            _logger.LogInformation("Triggering JIT model preload for '{ModelName}' with keep_alive={KeepAlive}", modelName, keepAlive);
            var payload = new { model = modelName, keep_alive = keepAlive };
            var response = await _httpClient.PostAsJsonAsync(OllamaApiPaths.Generate, payload, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "JIT model preload failed for '{ModelName}'", modelName);
            return false;
        }
    }
}
