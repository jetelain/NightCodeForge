namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Native AOT implementation of <see cref="IOllamaRouterRecommendationSyncService"/> using
/// <see cref="InstallerJsonContext"/> and <see cref="JsonObject"/> for atomic read-modify-write
/// persistence into <c>&lt;InstallDir&gt;/config/config.json</c>.
/// </summary>
public sealed class OllamaRouterRecommendationSyncService : IOllamaRouterRecommendationSyncService
{
    private const string StepSync = "OllamaRouter Sync";
    private const string JsonMediaType = "application/json";

    private static readonly SemaphoreSlim ConfigFileLock = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly IInstallationDetector _installationDetector;

    public OllamaRouterRecommendationSyncService(
        HttpClient httpClient,
        IInstallationDetector installationDetector)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
    }

    public async Task<RouterRecommendationsDto?> FetchRecommendationsAsync(
        string? routerBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveBaseUrl = NormalizeBaseUrl(routerBaseUrl);
        var requestUri = $"{effectiveBaseUrl}{InstallerConstants.OllamaRouterRecommendationsApiPath}";

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(InstallerConstants.OllamaRouterRecommendationsTimeoutSeconds));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.UserAgent.ParseAdd(InstallerConstants.HttpUserAgent);
            request.Headers.Accept.ParseAdd(JsonMediaType);

            using var response = await _httpClient.SendAsync(request, linkedCts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new OllamaRouterSyncException(
                    $"OllamaRouter recommendations request to '{requestUri}' failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync(
                stream,
                InstallerJsonContext.Default.RouterRecommendationsDto,
                linkedCts.Token).ConfigureAwait(false);

            return dto ?? throw new OllamaRouterSyncException(
                $"OllamaRouter recommendations endpoint '{requestUri}' returned an empty or null JSON payload.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OllamaRouterSyncException(
                $"Timed out querying OllamaRouter recommendations from '{requestUri}'.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            throw new OllamaRouterSyncException(
                $"Failed to query or parse OllamaRouter recommendations from '{requestUri}': {ex.Message}",
                ex);
        }
    }

    public async Task<RouterRoleRecommendationsSummary> SyncRecommendationsToConfigAsync(
        string? installDirectory = null,
        string? routerBaseUrl = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveBaseUrl = NormalizeBaseUrl(routerBaseUrl);
        Report(progress, $"Fetching role recommendations from {effectiveBaseUrl}...", InstallerLogLevel.Info, 20);

        var dto = await FetchRecommendationsAsync(effectiveBaseUrl, cancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new OllamaRouterSyncException(
                $"OllamaRouter recommendations endpoint '{effectiveBaseUrl}{InstallerConstants.OllamaRouterRecommendationsApiPath}' returned HTTP 404. Ensure OllamaRouter is running.");
        }

        var mapped = MapRecommendations(dto);

        var resolvedInstallDir = ResolveInstallDirectory(installDirectory);
        var configDir = Path.Combine(resolvedInstallDir, InstallerConstants.ConfigDirectoryName);
        var configFilePath = Path.Combine(configDir, InstallerConstants.UserConfigFileName);

        Report(progress, $"Updating '{configFilePath}' with calibrated OllamaRouter models...", InstallerLogLevel.Info, 70);
        await PersistConfigOverlayAsync(configDir, configFilePath, effectiveBaseUrl, mapped, cancellationToken)
            .ConfigureAwait(false);

        var summary = new RouterRoleRecommendationsSummary(
            ProviderType: InstallerConstants.OllamaRouterProviderTypeName,
            Endpoint: effectiveBaseUrl,
            ArchitectModelId: mapped.ArchitectModelId,
            CoderModelId: mapped.CoderModelId,
            DefaultModelId: mapped.DefaultModelId,
            ContextWindowTokens: mapped.ContextWindowTokens,
            GeneratedAt: dto.GeneratedAt,
            ConfigFilePath: configFilePath,
            Roles: mapped.Roles);

        Report(
            progress,
            string.Format(
                CultureInfo.InvariantCulture,
                "Synced OllamaRouter recommendations (Architect: {0}, Coder: {1}, Default: {2}, Context: {3:N0} tokens) into '{4}'.",
                summary.ArchitectModelId ?? "(inherit)",
                summary.CoderModelId ?? "(inherit)",
                summary.DefaultModelId ?? "(auto)",
                summary.ContextWindowTokens,
                configFilePath),
            InstallerLogLevel.Info,
            100);

        return summary;
    }

    internal static MappedInstallerRecommendations MapRecommendations(RouterRecommendationsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.Roles is null || dto.Roles.Count == 0)
        {
            throw new OllamaRouterSyncException(
                "OllamaRouter returned an empty recommendation matrix. Run the Router Setup Wizard to benchmark models first.");
        }

        var softwareArchitect = FindRole(dto.Roles, InstallerConstants.RoleSoftwareArchitect);
        var agenticChat = FindRole(dto.Roles, InstallerConstants.RoleAgenticChat);
        var seniorDeveloper = FindRole(dto.Roles, InstallerConstants.RoleSeniorDeveloper);
        var juniorDeveloper = FindRole(dto.Roles, InstallerConstants.RoleJuniorDeveloper);

        var softwareArchitectModel = NormalizeModelId(softwareArchitect?.Model);
        var agenticChatModel = NormalizeModelId(agenticChat?.Model);
        var seniorDeveloperModel = NormalizeModelId(seniorDeveloper?.Model);
        var juniorDeveloperModel = NormalizeModelId(juniorDeveloper?.Model);

        var architectModelId = softwareArchitectModel ?? agenticChatModel;
        var coderModelId = seniorDeveloperModel ?? juniorDeveloperModel;
        var defaultModelId = agenticChatModel ?? architectModelId ?? coderModelId;

        if (string.IsNullOrWhiteSpace(architectModelId) &&
            string.IsNullOrWhiteSpace(coderModelId) &&
            string.IsNullOrWhiteSpace(defaultModelId))
        {
            throw new OllamaRouterSyncException(
                "OllamaRouter returned no usable role recommendations for architect, coder, or chat roles. Run the Router Setup Wizard to benchmark models first.");
        }

        var contextWindowTokens = softwareArchitectModel is not null && softwareArchitect is { RecommendedContextWindow: > 0 }
            ? Math.Max(InstallerConstants.DefaultContextWindowTokens, softwareArchitect.RecommendedContextWindow)
            : InstallerConstants.DefaultContextWindowTokens;

        var roleSummaries = dto.Roles
            .Where(static kvp => kvp.Value is not null && !string.IsNullOrWhiteSpace(kvp.Value.Model))
            .Select(kvp => MapRoleSummary(kvp.Key, kvp.Value))
            .ToList();

        return new MappedInstallerRecommendations(
            ArchitectModelId: architectModelId,
            CoderModelId: coderModelId,
            DefaultModelId: defaultModelId,
            ContextWindowTokens: contextWindowTokens,
            Roles: roleSummaries);
    }

    private string ResolveInstallDirectory(string? explicitInstallDirectory)
    {
        var raw = string.IsNullOrWhiteSpace(explicitInstallDirectory)
            ? _installationDetector.Detect().NightCodeForge.InstallDirectory
            : explicitInstallDirectory;

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new OllamaRouterSyncException("NightCodeForge installation directory could not be resolved.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OllamaRouterSyncException(
                $"Invalid NightCodeForge installation directory '{raw}': {ex.Message}",
                ex);
        }
    }

    private static async Task PersistConfigOverlayAsync(
        string configDir,
        string configFilePath,
        string endpoint,
        MappedInstallerRecommendations mapped,
        CancellationToken cancellationToken)
    {
        await ConfigFileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var root = await ReadOrCreateRootObjectAsync(configFilePath, cancellationToken).ConfigureAwait(false);
                var llmNode = MergeAndNormalizeSection(root, InstallerConstants.LlmProviderSectionName, configFilePath);

                SetStringProperty(llmNode, InstallerConstants.LlmProviderTypeKey, InstallerConstants.OllamaRouterProviderTypeName);
                SetStringProperty(llmNode, InstallerConstants.LlmEndpointKey, endpoint);
                RemoveMatchingKeys(llmNode, InstallerConstants.LlmLegacyModelIdKey);
                SetOptionalStringProperty(llmNode, InstallerConstants.LlmArchitectModelIdKey, mapped.ArchitectModelId);
                SetOptionalStringProperty(llmNode, InstallerConstants.LlmCoderModelIdKey, mapped.CoderModelId);
                SetOptionalStringProperty(llmNode, InstallerConstants.LlmDefaultModelIdKey, mapped.DefaultModelId);
                SetIntProperty(llmNode, InstallerConstants.LlmContextWindowTokensKey, mapped.ContextWindowTokens);

                Directory.CreateDirectory(configDir);
                var tempFilePath = Path.Combine(configDir, $"{InstallerConstants.UserConfigFileName}.{Guid.NewGuid():N}.tmp");

                try
                {
                    await using (var stream = new FileStream(
                        tempFilePath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        InstallerConstants.DownloadBufferSizeBytes,
                        useAsync: true))
                    {
                        await JsonSerializer.SerializeAsync(
                            stream,
                            root,
                            InstallerJsonContext.Default.JsonObject,
                            cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    File.Move(tempFilePath, configFilePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempFilePath))
                    {
                        try
                        {
                            File.Delete(tempFilePath);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // Justification: Best-effort cleanup of temp config file must not mask primary failure.
                            _ = ex;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OllamaRouterSyncException(
                    $"Failed to read or update configuration file '{configFilePath}': {ex.Message}",
                    ex);
            }
        }
        finally
        {
            ConfigFileLock.Release();
        }
    }

    private static async Task<JsonObject> ReadOrCreateRootObjectAsync(
        string configFilePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(configFilePath))
        {
            return new JsonObject();
        }

        var existingJson = await File.ReadAllTextAsync(configFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            return new JsonObject();
        }

        try
        {
            var parsed = JsonNode.Parse(
                existingJson,
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
            if (parsed is not JsonObject root)
            {
                throw new OllamaRouterSyncException(
                    $"Configuration file '{configFilePath}' does not contain a root JSON object.");
            }

            return root;
        }
        catch (JsonException ex)
        {
            throw new OllamaRouterSyncException(
                $"Configuration file '{configFilePath}' contains invalid JSON and cannot be safely updated.",
                ex);
        }
    }

    private static JsonObject MergeAndNormalizeSection(JsonObject parent, string canonicalKey, string configFilePath)
    {
        var matchingEntries = parent
            .Where(kvp => string.Equals(kvp.Key, canonicalKey, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();

        if (matchingEntries.Count == 0)
        {
            var created = new JsonObject();
            parent[canonicalKey] = created;
            return created;
        }

        foreach (var (key, _) in matchingEntries)
        {
            parent.Remove(key);
        }

        var merged = new JsonObject();
        foreach (var (_, value) in matchingEntries)
        {
            if (value is null)
            {
                continue;
            }

            if (value is not JsonObject sectionObject)
            {
                throw new OllamaRouterSyncException(
                    $"Configuration section '{canonicalKey}' in '{configFilePath}' must be a JSON object.");
            }

            foreach (var (childKey, childValue) in sectionObject)
            {
                RemoveMatchingKeys(merged, childKey);
                merged[childKey] = childValue?.DeepClone();
            }
        }

        parent[canonicalKey] = merged;
        return merged;
    }

    private static void SetStringProperty(JsonObject parent, string key, string value)
    {
        RemoveMatchingKeys(parent, key);
        parent[key] = JsonValue.Create(value);
    }

    private static void SetOptionalStringProperty(JsonObject parent, string key, string? value)
    {
        RemoveMatchingKeys(parent, key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent[key] = JsonValue.Create(value);
        }
    }

    private static void SetIntProperty(JsonObject parent, string key, int value)
    {
        RemoveMatchingKeys(parent, key);
        parent[key] = JsonValue.Create(value);
    }

    private static void RemoveMatchingKeys(JsonObject parent, string targetKey)
    {
        var keysToRemove = parent
            .Select(kvp => kvp.Key)
            .Where(key => string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keysToRemove)
        {
            parent.Remove(key);
        }
    }

    private static string NormalizeBaseUrl(string? routerBaseUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(routerBaseUrl)
            ? InstallerConstants.DefaultOllamaRouterEndpoint
            : routerBaseUrl.Trim().TrimEnd('/');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OllamaRouterSyncException(
                $"Invalid OllamaRouter base URL '{routerBaseUrl}'. Expected an absolute HTTP or HTTPS URL without query or fragment.");
        }

        return trimmed;
    }

    private static RouterRoleRecommendationDto? FindRole(
        Dictionary<string, RouterRoleRecommendationDto> roles,
        string roleName)
    {
        if (roles.TryGetValue(roleName, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in roles)
        {
            if (string.Equals(key, roleName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static RouterRoleAssignmentSummary MapRoleSummary(string roleKey, RouterRoleRecommendationDto dto)
    {
        var effectiveRole = string.IsNullOrWhiteSpace(dto.Role) ? roleKey : dto.Role.Trim();
        var nodes = (dto.Nodes ?? [])
            .Where(static n => n is not null)
            .Select(static n => new RouterNodeCeilingSummary(
                Target: string.IsNullOrWhiteSpace(n.Target) ? string.Empty : n.Target.Trim(),
                ContextCeiling: n.ContextCeiling,
                BaselineContext: n.BaselineContext,
                TokensPerSecond: n.TokensPerSecond,
                VramMb: n.VramMb))
            .ToList();

        return new RouterRoleAssignmentSummary(
            Role: effectiveRole,
            Model: dto.Model.Trim(),
            RecommendedContextWindow: dto.RecommendedContextWindow,
            Description: dto.Description,
            Nodes: nodes);
    }

    private static string? NormalizeModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string message,
        InstallerLogLevel level,
        double? percent) =>
        progress?.Report(new InstallerProgressEvent(StepSync, message, level, percent));

    internal sealed record MappedInstallerRecommendations(
        string? ArchitectModelId,
        string? CoderModelId,
        string? DefaultModelId,
        int ContextWindowTokens,
        IReadOnlyList<RouterRoleAssignmentSummary> Roles);
}
