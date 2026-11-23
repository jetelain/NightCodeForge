namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Native AOT service probing OpenAI-compatible <c>/models</c> endpoints and persisting
/// <c>LlmProvider</c> + <c>OpenAiCompatible</c> settings to <c>config/config.json</c>.
/// </summary>
public sealed class OpenAiProviderConfigurationService : IOpenAiProviderConfigurationService
{
    private const string JsonMediaType = "application/json";
    private const string HeaderOpenAiOrganization = "OpenAI-Organization";
    private const string BearerScheme = "Bearer";
    private const string PathV1 = "v1";
    private const string PathOpenAi = "openai";
    private const double MaxSafeDecimalDouble = 7.9e28;

    private readonly HttpClient _httpClient;
    private readonly IInstallationDetector _installationDetector;
    private readonly IInstallerSecretStore _secretStore;

    public OpenAiProviderConfigurationService(
        HttpClient httpClient,
        IInstallationDetector installationDetector,
        IInstallerSecretStore secretStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
    }

    public async Task<OpenAiModelDiscoveryResult> DiscoverModelsAsync(
        string endpoint,
        string? apiKeyOverride,
        string? organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!InstallerEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalizedEndpoint))
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: null,
                ErrorMessage: "Enter a valid absolute HTTP or HTTPS endpoint URL (for example, https://api.openai.com/v1).");
        }

        if (!Uri.TryCreate(normalizedEndpoint.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: null,
                ErrorMessage: "Enter a valid absolute HTTP or HTTPS endpoint URL.");
        }

        string? effectiveApiKey;
        try
        {
            effectiveApiKey = !string.IsNullOrWhiteSpace(apiKeyOverride)
                ? apiKeyOverride.Trim()
                : _secretStore.ResolveEffectiveOpenAiApiKey(normalizedEndpoint);
        }
        catch (InstallerSecretStoreException ex)
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: null,
                ErrorMessage: ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(effectiveApiKey) &&
            !KeySharpInstallerSecretStore.TryValidateApiKeyHeaderValue(effectiveApiKey, out effectiveApiKey))
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: null,
                ErrorMessage: "Invalid API key format. API keys must not contain line breaks, whitespace, or unsupported HTTP header characters.");
        }

        if (!string.IsNullOrWhiteSpace(organizationId) && organizationId.Any(char.IsControl))
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: null,
                ErrorMessage: "Organization ID must not contain control characters or line breaks.");
        }

        var candidates = GetCandidateUris(baseUri).ToList();
        var anyValidEmptyModelsResponse = false;
        var notFoundOrUnimplementedCount = 0;
        var probedCount = 0;
        string? lastError = null;

        foreach (var uri in candidates)
        {
            probedCount++;
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.OpenAiDiscoveryTimeoutSeconds));

                using var request = CreateProbeRequest(uri, effectiveApiKey, organizationId);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                    if (!TryParseModelsJson(json, out var models))
                    {
                        lastError = $"Endpoint at {uri} returned HTTP {(int)response.StatusCode} but did not return a valid OpenAI-compatible JSON models list.";
                        continue;
                    }

                    if (models.Count > 0)
                    {
                        return new OpenAiModelDiscoveryResult(
                            IsSuccess: true,
                            IsDiscoveryImplemented: true,
                            Models: models,
                            Message: $"Connected to {normalizedEndpoint}: discovered {models.Count} model(s).",
                            ErrorMessage: null);
                    }

                    anyValidEmptyModelsResponse = true;
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                {
                    notFoundOrUnimplementedCount++;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return new OpenAiModelDiscoveryResult(
                        IsSuccess: false,
                        IsDiscoveryImplemented: false,
                        Models: [],
                        Message: null,
                        ErrorMessage: "Authentication failed (HTTP 401 Unauthorized). Check your API key.");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new OpenAiModelDiscoveryResult(
                        IsSuccess: false,
                        IsDiscoveryImplemented: false,
                        Models: [],
                        Message: null,
                        ErrorMessage: "Access forbidden (HTTP 403 Forbidden). Verify your API key permissions and Organization ID.");
                }

                lastError = $"Remote endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) at {uri}.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastError = $"Timed out connecting to {uri}.";
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Connection failed at {uri}: {ex.Message}";
            }
        }

        if (anyValidEmptyModelsResponse)
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: true,
                IsDiscoveryImplemented: true,
                Models: [],
                Message: "Endpoint reachable (HTTP 200), but 0 models were returned. Enter the model ID manually.",
                ErrorMessage: null);
        }

        if (probedCount > 0 && notFoundOrUnimplementedCount == probedCount)
        {
            return new OpenAiModelDiscoveryResult(
                IsSuccess: true,
                IsDiscoveryImplemented: false,
                Models: [],
                Message: "Endpoint reachable, but /models discovery is not implemented (HTTP 404/501). Enter the model ID manually.",
                ErrorMessage: null);
        }

        return new OpenAiModelDiscoveryResult(
            IsSuccess: false,
            IsDiscoveryImplemented: false,
            Models: [],
            Message: null,
            ErrorMessage: lastError ?? $"Unable to connect to endpoint '{normalizedEndpoint}'.");
    }

    public async Task<OpenAiProviderConfigurationSummary> SaveConfigurationAsync(
        OpenAiProviderSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!InstallerEndpointNormalizer.TryNormalizeEndpoint(request.Endpoint, out var normalizedEndpoint))
        {
            throw new OpenAiConfigurationException(
                "Invalid OpenAI-compatible endpoint URL. Expected an absolute HTTP or HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(request.DefaultModelId))
        {
            throw new OpenAiConfigurationException(
                "A default model ID is required before saving OpenAI-compatible provider settings.");
        }

        var installDir = ResolveInstallDirectory(request.InstallDirectory);

        var defaultModelId = request.DefaultModelId.Trim();
        var architectModelId = string.IsNullOrWhiteSpace(request.ArchitectModelId)
            ? null
            : request.ArchitectModelId.Trim();
        var coderModelId = string.IsNullOrWhiteSpace(request.CoderModelId)
            ? null
            : request.CoderModelId.Trim();
        var organizationId = string.IsNullOrWhiteSpace(request.OrganizationId)
            ? null
            : request.OrganizationId.Trim();
        var contextTokens = request.ContextWindowTokens > 0
            ? request.ContextWindowTokens
            : InstallerConstants.DefaultContextWindowTokens;

        bool apiKeyStoredInKeyring;
        if (!string.IsNullOrWhiteSpace(request.ApiKeyInput))
        {
            try
            {
                _secretStore.SetOpenAiApiKey(normalizedEndpoint, request.ApiKeyInput.Trim());
                apiKeyStoredInKeyring = true;
            }
            catch (Exception ex) when (ex is InstallerSecretStoreException or ArgumentException)
            {
                throw new OpenAiConfigurationException(ex.Message, ex);
            }
        }
        else
        {
            try
            {
                apiKeyStoredInKeyring = _secretStore.IsKeyringAvailable() &&
                                        _secretStore.HasOpenAiApiKey(normalizedEndpoint);
            }
            catch (InstallerSecretStoreException)
            {
                // Justification: Reading keyring status when saving without a new key must not block env-var or unauthenticated local setups.
                apiKeyStoredInKeyring = false;
            }
        }

        var configFilePath = await InstallerUserConfigWriter.UpdateConfigAsync(
            installDir,
            (root, path) =>
            {
                var llmNode = InstallerUserConfigWriter.MergeAndNormalizeSection(
                    root,
                    InstallerConstants.LlmProviderSectionName,
                    path,
                    CreateConfigurationException);

                InstallerUserConfigWriter.SetStringProperty(
                    llmNode,
                    InstallerConstants.LlmProviderTypeKey,
                    InstallerConstants.OpenAiCompatibleProviderTypeName);
                InstallerUserConfigWriter.RemoveMatchingKeys(llmNode, InstallerConstants.LlmLegacyModelIdKey);
                InstallerUserConfigWriter.SetStringProperty(llmNode, InstallerConstants.LlmDefaultModelIdKey, defaultModelId);
                InstallerUserConfigWriter.SetOptionalStringProperty(llmNode, InstallerConstants.LlmArchitectModelIdKey, architectModelId);
                InstallerUserConfigWriter.SetOptionalStringProperty(llmNode, InstallerConstants.LlmCoderModelIdKey, coderModelId);
                InstallerUserConfigWriter.SetIntProperty(llmNode, InstallerConstants.LlmContextWindowTokensKey, contextTokens);

                var openAiNode = InstallerUserConfigWriter.MergeAndNormalizeSection(
                    llmNode,
                    InstallerConstants.LlmOpenAiCompatibleSectionName,
                    path,
                    CreateConfigurationException);

                InstallerUserConfigWriter.RemoveMatchingKeys(openAiNode, InstallerConstants.LlmLegacyApiKeyPropertyName);
                InstallerUserConfigWriter.SetStringProperty(openAiNode, InstallerConstants.LlmEndpointKey, normalizedEndpoint);
                InstallerUserConfigWriter.SetOptionalStringProperty(openAiNode, InstallerConstants.LlmOpenAiOrganizationIdKey, organizationId);

                switch (request.AllowedModelsMode)
                {
                    case OpenAiAllowedModelsPersistenceMode.WriteManualModels:
                        var allowedModels = BuildManualAllowedModelsArray(
                            defaultModelId,
                            architectModelId,
                            coderModelId,
                            contextTokens);
                        InstallerUserConfigWriter.RemoveMatchingKeys(openAiNode, InstallerConstants.LlmOpenAiAllowedModelsKey);
                        openAiNode[InstallerConstants.LlmOpenAiAllowedModelsKey] = allowedModels;
                        break;
                    case OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery:
                        InstallerUserConfigWriter.RemoveMatchingKeys(openAiNode, InstallerConstants.LlmOpenAiAllowedModelsKey);
                        break;
                    case OpenAiAllowedModelsPersistenceMode.PreserveExisting:
                        break;
                }
            },
            CreateConfigurationException,
            cancellationToken).ConfigureAwait(false);

        return new OpenAiProviderConfigurationSummary(
            ProviderName: string.IsNullOrWhiteSpace(request.ProviderName) ? OpenAiProviderPresetCatalog.CustomPreset.Name : request.ProviderName.Trim(),
            Endpoint: normalizedEndpoint,
            DefaultModelId: defaultModelId,
            ArchitectModelId: architectModelId,
            CoderModelId: coderModelId,
            ContextWindowTokens: contextTokens,
            ApiKeyStoredInKeyring: apiKeyStoredInKeyring,
            ConfigFilePath: configFilePath);
    }

    public ExistingOpenAiProviderConfig? TryReadExistingConfiguration(string? installDirectory)
    {
        var rawDir = string.IsNullOrWhiteSpace(installDirectory)
            ? _installationDetector.Detect().NightCodeForge.InstallDirectory
            : installDirectory;
        if (string.IsNullOrWhiteSpace(rawDir))
        {
            return null;
        }

        string userConfigPath;
        try
        {
            userConfigPath = Path.Combine(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawDir.Trim())),
                InstallerConstants.ConfigDirectoryName,
                InstallerConstants.UserConfigFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Malformed directory path cannot contain a readable configuration.
            _ = ex;
            return null;
        }

        var root = InstalledAppConfigReader.TryReadJsonObject(userConfigPath);
        if (root is null ||
            !InstalledAppConfigReader.TryGetPropertyIgnoreCase(root, InstallerConstants.LlmProviderSectionName, out var providerNode) ||
            providerNode is not JsonObject providerObj)
        {
            return null;
        }

        if (!TryGetString(providerObj, InstallerConstants.LlmProviderTypeKey, out var providerType) ||
            !string.Equals(providerType, InstallerConstants.OpenAiCompatibleProviderTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var endpoint = InstallerConstants.DefaultOpenAiEndpoint;
        string? organizationId = null;
        var hasManualAllowedModels = false;
        if (InstalledAppConfigReader.TryGetPropertyIgnoreCase(providerObj, InstallerConstants.LlmOpenAiCompatibleSectionName, out var openAiNode) &&
            openAiNode is JsonObject openAiObj)
        {
            if (TryGetString(openAiObj, InstallerConstants.LlmEndpointKey, out var configuredEndpoint) &&
                !string.IsNullOrWhiteSpace(configuredEndpoint))
            {
                endpoint = configuredEndpoint;
            }

            TryGetString(openAiObj, InstallerConstants.LlmOpenAiOrganizationIdKey, out organizationId);
            hasManualAllowedModels = InstalledAppConfigReader.TryGetPropertyIgnoreCase(
                                         openAiObj,
                                         InstallerConstants.LlmOpenAiAllowedModelsKey,
                                         out var allowedNode) &&
                                     allowedNode is JsonArray { Count: > 0 };
        }

        if (!TryGetString(providerObj, InstallerConstants.LlmDefaultModelIdKey, out var defaultModelId))
        {
            TryGetString(providerObj, InstallerConstants.LlmLegacyModelIdKey, out defaultModelId);
        }

        TryGetString(providerObj, InstallerConstants.LlmArchitectModelIdKey, out var architectModelId);
        TryGetString(providerObj, InstallerConstants.LlmCoderModelIdKey, out var coderModelId);
        int? contextWindowTokens = TryGetPositiveInt(providerObj, InstallerConstants.LlmContextWindowTokensKey, out var configuredContext)
            ? configuredContext
            : null;

        return new ExistingOpenAiProviderConfig(
            Endpoint: endpoint,
            OrganizationId: organizationId,
            DefaultModelId: defaultModelId,
            ArchitectModelId: architectModelId,
            CoderModelId: coderModelId,
            ContextWindowTokens: contextWindowTokens,
            HasManualAllowedModels: hasManualAllowedModels,
            ConfigFilePath: userConfigPath);
    }

    internal static IReadOnlyList<OpenAiDiscoveredModel> ParseModelsJson(string jsonContent) =>
        TryParseModelsJson(jsonContent, out var models) ? models : [];

    internal static bool TryParseModelsJson(string jsonContent, out IReadOnlyList<OpenAiDiscoveredModel> models)
    {
        if (string.IsNullOrWhiteSpace(jsonContent))
        {
            models = [];
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            JsonElement modelsArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                modelsArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var dataProp) &&
                     dataProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = dataProp;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("models", out var modelsProp) &&
                     modelsProp.ValueKind == JsonValueKind.Array)
            {
                modelsArray = modelsProp;
            }
            else
            {
                models = [];
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsedModels = new List<OpenAiDiscoveredModel>();

            foreach (var item in modelsArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? modelId = null;
                if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    modelId = idProp.GetString();
                }
                else if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    modelId = nameProp.GetString();
                }

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    continue;
                }

                var trimmedId = modelId.Trim();
                if (!seen.Add(trimmedId))
                {
                    continue;
                }

                var displayName = trimmedId;
                if (item.TryGetProperty("display_name", out var dispProp) &&
                    dispProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(dispProp.GetString()))
                {
                    displayName = dispProp.GetString()!.Trim();
                }
                else if (item.TryGetProperty("name", out var dNameProp) &&
                         dNameProp.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(dNameProp.GetString()))
                {
                    displayName = dNameProp.GetString()!.Trim();
                }

                int? contextLength = null;
                if (TryGetPositiveInt(item, "context_length", out var cl))
                {
                    contextLength = cl;
                }
                else if (TryGetPositiveInt(item, "max_model_len", out var mml))
                {
                    contextLength = mml;
                }
                else if (TryGetPositiveInt(item, "context_window", out var cw))
                {
                    contextLength = cw;
                }

                decimal? promptCost = null;
                decimal? completionCost = null;
                if (item.TryGetProperty("pricing", out var pricingProp) && pricingProp.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetDecimalProperty(pricingProp, "prompt", out var pCost))
                    {
                        promptCost = NormalizeCostPerMillion(pCost);
                    }

                    if (TryGetDecimalProperty(pricingProp, "completion", out var cCost))
                    {
                        completionCost = NormalizeCostPerMillion(cCost);
                    }
                }

                parsedModels.Add(new OpenAiDiscoveredModel(trimmedId, displayName, contextLength, promptCost, completionCost));
            }

            parsedModels.Sort(static (a, b) => string.Compare(a.ModelId, b.ModelId, StringComparison.OrdinalIgnoreCase));
            models = parsedModels;
            return true;
        }
        catch (JsonException)
        {
            // Justification: Remote non-JSON HTTP 200 payloads (such as HTML error or login pages) are rejected as invalid model discovery responses.
            models = [];
            return false;
        }
    }

    internal static IReadOnlyList<Uri> GetCandidateUris(Uri baseUri)
    {
        if (!baseUri.AbsoluteUri.EndsWith('/'))
        {
            baseUri = new Uri(baseUri.AbsoluteUri + "/");
        }

        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<Uri>();

        void AddUri(Uri uri)
        {
            if (distinct.Add(uri.AbsoluteUri))
            {
                results.Add(uri);
            }
        }

        var path = baseUri.AbsolutePath.TrimEnd('/');
        var isVersionedBase = path.EndsWith("/" + PathV1, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(path.TrimStart('/'), PathV1, StringComparison.OrdinalIgnoreCase) ||
                              path.EndsWith("/" + PathOpenAi, StringComparison.OrdinalIgnoreCase);

        if (isVersionedBase)
        {
            AddUri(new Uri(baseUri, "models"));
        }
        else
        {
            AddUri(new Uri(baseUri, "v1/models"));
            AddUri(new Uri(baseUri, "api/v1/models"));
            AddUri(new Uri(baseUri, "models"));
        }

        if (Uri.TryCreate(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute, out var authorityBase))
        {
            AddUri(new Uri(authorityBase, "api/v1/models"));
            AddUri(new Uri(authorityBase, "v1/models"));
            AddUri(new Uri(authorityBase, "models"));
        }

        return results;
    }

    private static HttpRequestMessage CreateProbeRequest(Uri requestUri, string? apiKey, string? organizationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
        request.Headers.Accept.ParseAdd(JsonMediaType);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, apiKey);
        }

        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            request.Headers.TryAddWithoutValidation(HeaderOpenAiOrganization, organizationId.Trim());
        }

        return request;
    }

    private string ResolveInstallDirectory(string? explicitInstallDirectory)
    {
        var raw = string.IsNullOrWhiteSpace(explicitInstallDirectory)
            ? _installationDetector.Detect().NightCodeForge.InstallDirectory
            : explicitInstallDirectory;

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new OpenAiConfigurationException("NightCodeForge installation directory could not be resolved.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OpenAiConfigurationException(
                $"Invalid NightCodeForge installation directory '{raw}': {ex.Message}",
                ex);
        }
    }

    private static JsonArray BuildManualAllowedModelsArray(
        string defaultModelId,
        string? architectModelId,
        string? coderModelId,
        int contextWindowTokens)
    {
        var array = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddModel(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return;
            }

            var trimmed = modelId.Trim();
            if (!seen.Add(trimmed))
            {
                return;
            }

            array.Add((JsonNode)new JsonObject
            {
                [InstallerConstants.LlmOpenAiAllowedModelIdKey] = trimmed,
                [InstallerConstants.LlmOpenAiDisplayNameKey] = trimmed,
                [InstallerConstants.LlmContextWindowTokensKey] = contextWindowTokens,
                [InstallerConstants.LlmOpenAiMaxOutputTokensKey] = InstallerConstants.DefaultOpenAiMaxOutputTokens
            });
        }

        AddModel(defaultModelId);
        AddModel(architectModelId);
        AddModel(coderModelId);
        return array;
    }

    private static bool TryGetString(JsonObject obj, string key, out string? value)
    {
        if (InstalledAppConfigReader.TryGetPropertyIgnoreCase(obj, key, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var raw) &&
            !string.IsNullOrWhiteSpace(raw))
        {
            value = raw.Trim();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetPositiveInt(JsonObject obj, string key, out int value)
    {
        if (InstalledAppConfigReader.TryGetPropertyIgnoreCase(obj, key, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<int>(out var raw) &&
            raw > 0)
        {
            value = raw;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetPositiveInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetInt32(out value) && value > 0)
            {
                return true;
            }

            if (prop.TryGetDouble(out var d) && d > 0 && d <= int.MaxValue)
            {
                value = (int)d;
                return true;
            }
        }
        else if (prop.ValueKind == JsonValueKind.String)
        {
            if (int.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
            {
                return true;
            }

            if (double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && sd > 0 && sd <= int.MaxValue)
            {
                value = (int)sd;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetDecimalProperty(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number)
        {
            if (prop.TryGetDecimal(out value))
            {
                return true;
            }

            if (prop.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Abs(d) <= MaxSafeDecimalDouble)
            {
                value = (decimal)d;
                return true;
            }
        }
        else if (prop.ValueKind == JsonValueKind.String)
        {
            if (decimal.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd) && !double.IsNaN(sd) && !double.IsInfinity(sd) && Math.Abs(sd) <= MaxSafeDecimalDouble)
            {
                value = (decimal)sd;
                return true;
            }
        }

        return false;
    }

    private static decimal? NormalizeCostPerMillion(decimal rawCost)
    {
        if (rawCost < 0)
        {
            return null;
        }

        if (rawCost == 0)
        {
            return 0m;
        }

        return rawCost < 0.01m ? rawCost * 1_000_000m : rawCost;
    }

    private static Exception CreateConfigurationException(string message, Exception? innerException) =>
        innerException is null
            ? new OpenAiConfigurationException(message)
            : new OpenAiConfigurationException(message, innerException);
}
