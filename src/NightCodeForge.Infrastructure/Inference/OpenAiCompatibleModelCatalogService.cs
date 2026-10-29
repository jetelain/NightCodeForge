using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Model catalog service for OpenAI-compatible inference endpoints (vLLM, LM Studio, OpenRouter, DeepSeek, OpenAI).
/// Probes <c>/api/v1/models</c> (and <c>/v1/models</c>) dynamically, falling back to manual model configuration
/// (<see cref="OpenAiCompatibleOptions.AllowedModels"/>) only if discovery is unimplemented (HTTP 404/501).
/// </summary>
public sealed class OpenAiCompatibleModelCatalogService : LlmModelCatalogServiceBase
{
    private readonly IOptions<LlmProviderOptions> _providerOptions;
    private readonly IOpenAiModelAllowlistCache _allowlistCache;

    public OpenAiCompatibleModelCatalogService(
        HttpClient httpClient,
        IOptions<LlmProviderOptions> providerOptions,
        IOpenAiModelAllowlistCache allowlistCache,
        IDefaultModelSelector defaultModelSelector,
        ILogger<OpenAiCompatibleModelCatalogService> logger)
        : base(httpClient, providerOptions, defaultModelSelector, logger)
    {
        _providerOptions = providerOptions ?? throw new ArgumentNullException(nameof(providerOptions));
        _allowlistCache = allowlistCache ?? throw new ArgumentNullException(nameof(allowlistCache));
    }

    protected override LlmProviderType ProviderType => LlmProviderType.OpenAiCompatible;

    public override async Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var probeResult = await ProbeEndpointAsync(cancellationToken);
            if (probeResult.IsSuccess && probeResult.VerifiedModels != null)
            {
                return probeResult.VerifiedModels;
            }

            if (!probeResult.IsDiscoveryImplemented)
            {
                // Discovery is not implemented (HTTP 404/501): return manual AllowedModels if present
                var manualModels = GetManualAllowedModels();
                if (manualModels.Count > 0)
                {
                    _allowlistCache.SetDiscoveredModels(manualModels);
                }
                return manualModels;
            }

            return Array.Empty<AvailableModelInfo>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Failed to query model catalog from OpenAI-compatible endpoint {BaseAddress}", HttpClient.BaseAddress);
            var manual = GetManualAllowedModels();
            if (manual.Count > 0)
            {
                _allowlistCache.SetDiscoveredModels(manual);
            }
            return manual;
        }
    }

    public override async Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default)
    {
        return await ProbeEndpointAsync(cancellationToken);
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await GetAvailableModelsAsync(cancellationToken);
    }

    private async Task<EndpointVerificationResult> ProbeEndpointAsync(CancellationToken cancellationToken)
    {
        var candidates = GetCandidateUris().ToList();
        if (candidates.Count == 0)
        {
            return new EndpointVerificationResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                ErrorMessage: "No valid endpoint URI configured for OpenAI-compatible provider.");
        }

        var anySuccessStatusCode = false;
        var notFoundOrUnimplementedCount = 0;
        var probedCount = 0;
        string? lastError = null;

        foreach (var uri in candidates)
        {
            try
            {
                probedCount++;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ProbeTimeout);

                using var request = CreateProbeRequest(uri);
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    anySuccessStatusCode = true;
                    var json = await response.Content.ReadAsStringAsync(cts.Token);
                    var defaultTokens = _providerOptions.Value.OpenAiCompatible?.DefaultDiscoveredContextTokens ?? LlmProviderOptions.DefaultContextWindowTokens;
                    var discovered = OpenAiModelsResponseParser.Parse(json, defaultTokens);

                    if (discovered.Count > 0)
                    {
                        var finalModels = ApplyAllowedModelsFilter(discovered);
                        _allowlistCache.SetDiscoveredModels(finalModels);

                        return new EndpointVerificationResult(
                            IsSuccess: true,
                            IsDiscoveryImplemented: true,
                            DiscoveredCount: discovered.Count,
                            VerifiedModels: finalModels);
                    }

                    // Candidate returned 200 OK but contained no models; continue probing other candidates
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                {
                    // Candidate path not found on this server, continue trying next candidate
                    notFoundOrUnimplementedCount++;
                    continue;
                }

                // Authentication or server error
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new EndpointVerificationResult(
                        IsSuccess: false,
                        IsDiscoveryImplemented: false,
                        ErrorMessage: "Authentication failed (HTTP 401 Unauthorized). Verify your API key in Settings.");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new EndpointVerificationResult(
                        IsSuccess: false,
                        IsDiscoveryImplemented: false,
                        ErrorMessage: "Access forbidden (HTTP 403 Forbidden). Verify your API key permissions and Organization ID.");
                }

                lastError = $"Remote endpoint returned HTTP {(int)response.StatusCode} {response.StatusCode} at {uri}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Logger.LogDebug(ex, "Probe failed for URI {Uri}", uri);
                lastError = $"Connection failed at {uri}: {ex.Message}";
            }
        }

        // Case 1: At least one candidate endpoint returned 200 OK, but 0 models were discovered across all candidates
        if (anySuccessStatusCode)
        {
            var manual = GetManualAllowedModels();
            if (manual.Count > 0)
            {
                _allowlistCache.SetDiscoveredModels(manual);
                return new EndpointVerificationResult(
                    IsSuccess: true,
                    IsDiscoveryImplemented: true,
                    DiscoveredCount: manual.Count,
                    VerifiedModels: manual,
                    WarningMessage: "Remote endpoint returned 0 models. Configured manual models are active.");
            }

            return new EndpointVerificationResult(
                IsSuccess: true,
                IsDiscoveryImplemented: true,
                DiscoveredCount: 0,
                VerifiedModels: [],
                WarningMessage: "Remote endpoint implements model discovery (HTTP 200) but returned 0 available models.");
        }

        // Case 2: All probed candidates returned 404 Not Found or 501 Not Implemented
        if (probedCount > 0 && notFoundOrUnimplementedCount == probedCount)
        {
            var manual = GetManualAllowedModels();
            if (manual.Count > 0)
            {
                _allowlistCache.SetDiscoveredModels(manual);
                return new EndpointVerificationResult(
                    IsSuccess: true,
                    IsDiscoveryImplemented: false,
                    DiscoveredCount: manual.Count,
                    VerifiedModels: manual,
                    WarningMessage: "Remote endpoint does not implement /api/v1/models (HTTP 404/501). Manual model configuration is active.");
            }

            return new EndpointVerificationResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                DiscoveredCount: 0,
                VerifiedModels: [],
                ErrorMessage: "The remote OpenAI-compatible endpoint does not implement /api/v1/models. Please manually configure allowed models and context window sizes under Settings.");
        }

        return new EndpointVerificationResult(
            IsSuccess: false,
            IsDiscoveryImplemented: false,
            ErrorMessage: lastError ?? $"Unable to reach endpoint {HttpClient.BaseAddress}");
    }

    private IEnumerable<Uri> GetCandidateUris()
    {
        var effective = _providerOptions.Value.EffectiveEndpoint;
        if (string.IsNullOrWhiteSpace(effective))
        {
            yield break;
        }

        Uri baseUri;
        if (HttpClient.BaseAddress != null)
        {
            baseUri = HttpClient.BaseAddress;
        }
        else if (Uri.TryCreate(effective.TrimEnd('/') + "/", UriKind.Absolute, out var parsed))
        {
            baseUri = parsed;
        }
        else
        {
            yield break;
        }

        // Ensure baseUri ends with a trailing slash so relative Uri constructor doesn't strip the final path segment
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = baseUri.AbsolutePath.TrimEnd('/');
        bool endsWithV1 = path.EndsWith("v1", StringComparison.OrdinalIgnoreCase);

        if (endsWithV1)
        {
            // Base URL already includes /v1 (e.g. https://api.openai.com/v1/ or https://openrouter.ai/api/v1/)
            var u1 = new Uri(baseUri, "models");
            if (distinct.Add(u1.AbsoluteUri)) yield return u1;
        }
        else
        {
            // Base URL is root (e.g. http://localhost:1234/ or http://localhost:8000/)
            var u1 = new Uri(baseUri, "v1/models");
            if (distinct.Add(u1.AbsoluteUri)) yield return u1;

            var u2 = new Uri(baseUri, "api/v1/models");
            if (distinct.Add(u2.AbsoluteUri)) yield return u2;

            var u3 = new Uri(baseUri, "models");
            if (distinct.Add(u3.AbsoluteUri)) yield return u3;
        }

        // Absolute paths relative to root authority
        if (Uri.TryCreate(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute, out var authorityBase))
        {
            var uAuth1 = new Uri(authorityBase, "api/v1/models");
            if (distinct.Add(uAuth1.AbsoluteUri)) yield return uAuth1;

            var uAuth2 = new Uri(authorityBase, "v1/models");
            if (distinct.Add(uAuth2.AbsoluteUri)) yield return uAuth2;

            var uAuth3 = new Uri(authorityBase, "models");
            if (distinct.Add(uAuth3.AbsoluteUri)) yield return uAuth3;
        }
    }

    private HttpRequestMessage CreateProbeRequest(Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        var apiKey = _providerOptions.Value.OpenAiCompatible?.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }

        var orgId = _providerOptions.Value.OpenAiCompatible?.OrganizationId;
        if (!string.IsNullOrWhiteSpace(orgId))
        {
            request.Headers.TryAddWithoutValidation("OpenAI-Organization", orgId.Trim());
        }

        return request;
    }

    private IReadOnlyList<AvailableModelInfo> ApplyAllowedModelsFilter(IReadOnlyList<AvailableModelInfo> discovered)
    {
        var allowed = _providerOptions.Value.OpenAiCompatible?.AllowedModels;
        if (allowed == null || allowed.Count == 0)
        {
            return discovered;
        }

        var result = new List<AvailableModelInfo>();
        var defaultTokens = _providerOptions.Value.OpenAiCompatible?.DefaultDiscoveredContextTokens ?? LlmProviderOptions.DefaultContextWindowTokens;

        foreach (var m in allowed)
        {
            if (string.IsNullOrWhiteSpace(m.ModelId)) continue;
            var trimmedId = m.ModelId.Trim();

            var matched = discovered.FirstOrDefault(d => string.Equals(d.ModelId, trimmedId, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                result.Add(new AvailableModelInfo(
                    ModelId: matched.ModelId,
                    DisplayName: !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName.Trim() : matched.DisplayName,
                    ContextWindowTokens: m.ContextWindowTokens > 0 ? m.ContextWindowTokens : (matched.ContextWindowTokens ?? defaultTokens),
                    SizeBytes: matched.SizeBytes,
                    ProviderType: LlmProviderType.OpenAiCompatible,
                    SupportsReasoning: m.SupportsReasoning || matched.SupportsReasoning,
                    PromptCostPerMillion: m.PromptCostPerMillion ?? matched.PromptCostPerMillion,
                    CompletionCostPerMillion: m.CompletionCostPerMillion ?? matched.CompletionCostPerMillion,
                    IsMoE: matched.IsMoE));
            }
            else
            {
                result.Add(new AvailableModelInfo(
                    ModelId: trimmedId,
                    DisplayName: !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName.Trim() : trimmedId,
                    ContextWindowTokens: m.ContextWindowTokens > 0 ? m.ContextWindowTokens : defaultTokens,
                    SizeBytes: null,
                    ProviderType: LlmProviderType.OpenAiCompatible,
                    SupportsReasoning: m.SupportsReasoning,
                    PromptCostPerMillion: m.PromptCostPerMillion,
                    CompletionCostPerMillion: m.CompletionCostPerMillion,
                    IsMoE: false));
            }
        }

        return result;
    }

    private IReadOnlyList<AvailableModelInfo> GetManualAllowedModels()
    {
        var allowed = _providerOptions.Value.OpenAiCompatible?.AllowedModels;
        if (allowed == null || allowed.Count == 0)
        {
            return Array.Empty<AvailableModelInfo>();
        }

        var defaultTokens = _providerOptions.Value.OpenAiCompatible?.DefaultDiscoveredContextTokens ?? LlmProviderOptions.DefaultContextWindowTokens;

        return allowed
            .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
            .Select(m => new AvailableModelInfo(
                ModelId: m.ModelId.Trim(),
                DisplayName: !string.IsNullOrWhiteSpace(m.DisplayName) ? m.DisplayName.Trim() : m.ModelId.Trim(),
                ContextWindowTokens: m.ContextWindowTokens > 0 ? m.ContextWindowTokens : defaultTokens,
                SizeBytes: null,
                ProviderType: LlmProviderType.OpenAiCompatible,
                SupportsReasoning: m.SupportsReasoning,
                PromptCostPerMillion: m.PromptCostPerMillion,
                CompletionCostPerMillion: m.CompletionCostPerMillion,
                IsMoE: false))
            .ToList();
    }
}
