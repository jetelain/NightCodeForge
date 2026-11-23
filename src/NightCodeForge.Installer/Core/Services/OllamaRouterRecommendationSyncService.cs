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
    private const double ProgressFetchPercent = 20.0;
    private const double ProgressPersistPercent = 70.0;
    private const double ProgressCompletePercent = 100.0;

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
        var candidateInstallDir = TryResolveInstallDirectory(installDirectory);
        var effectiveBaseUrl = ResolveEffectiveBaseUrl(routerBaseUrl, candidateInstallDir);
        Report(progress, $"Fetching role recommendations from {effectiveBaseUrl}...", InstallerLogLevel.Info, ProgressFetchPercent);

        var dto = await FetchRecommendationsAsync(effectiveBaseUrl, cancellationToken).ConfigureAwait(false);
        if (dto is null)
        {
            throw new OllamaRouterSyncException(
                $"OllamaRouter recommendations endpoint '{effectiveBaseUrl}{InstallerConstants.OllamaRouterRecommendationsApiPath}' returned HTTP 404. Ensure OllamaRouter is running.");
        }

        var mapped = MapRecommendations(dto);

        var resolvedInstallDir = candidateInstallDir ?? ResolveInstallDirectory(installDirectory);
        var configFilePath = Path.Combine(
            resolvedInstallDir,
            InstallerConstants.ConfigDirectoryName,
            InstallerConstants.UserConfigFileName);

        Report(progress, $"Updating '{configFilePath}' with calibrated OllamaRouter models...", InstallerLogLevel.Info, ProgressPersistPercent);
        await PersistConfigOverlayAsync(resolvedInstallDir, effectiveBaseUrl, mapped, cancellationToken)
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
            ProgressCompletePercent);

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

    private string? TryResolveInstallDirectory(string? explicitInstallDirectory)
    {
        var raw = string.IsNullOrWhiteSpace(explicitInstallDirectory)
            ? _installationDetector.Detect().NightCodeForge.InstallDirectory
            : explicitInstallDirectory;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Invalid directory path is re-validated loudly in ResolveInstallDirectory after endpoint resolution.
            _ = ex;
            return null;
        }
    }

    private static string ResolveEffectiveBaseUrl(string? explicitRouterBaseUrl, string? installDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitRouterBaseUrl))
        {
            return NormalizeBaseUrl(explicitRouterBaseUrl);
        }

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            var userConfigPath = Path.Combine(
                installDirectory,
                InstallerConstants.ConfigDirectoryName,
                InstallerConstants.UserConfigFileName);

            var root = InstalledAppConfigReader.TryReadJsonObject(userConfigPath);
            if (root is not null &&
                InstalledAppConfigReader.TryGetPropertyIgnoreCase(root, InstallerConstants.LlmProviderSectionName, out var providerNode) &&
                providerNode is JsonObject providerObj &&
                InstalledAppConfigReader.TryGetPropertyIgnoreCase(providerObj, InstallerConstants.LlmEndpointKey, out var endpointNode) &&
                endpointNode is JsonValue endpointVal &&
                endpointVal.TryGetValue<string>(out var configuredEndpoint) &&
                !string.IsNullOrWhiteSpace(configuredEndpoint) &&
                TryNormalizeHttpBaseUrl(configuredEndpoint, out var normalizedExisting))
            {
                return normalizedExisting;
            }
        }

        return InstallerConstants.DefaultOllamaRouterEndpoint;
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

    private static Task PersistConfigOverlayAsync(
        string installDirectory,
        string endpoint,
        MappedInstallerRecommendations mapped,
        CancellationToken cancellationToken)
    {
        return InstallerUserConfigWriter.UpdateConfigAsync(
            installDirectory,
            (root, configFilePath) =>
            {
                var llmNode = InstallerUserConfigWriter.MergeAndNormalizeSection(
                    root,
                    InstallerConstants.LlmProviderSectionName,
                    configFilePath,
                    CreateSyncException);

                InstallerUserConfigWriter.SetStringProperty(llmNode, InstallerConstants.LlmProviderTypeKey, InstallerConstants.OllamaRouterProviderTypeName);
                InstallerUserConfigWriter.SetStringProperty(llmNode, InstallerConstants.LlmEndpointKey, endpoint);
                InstallerUserConfigWriter.RemoveMatchingKeys(llmNode, InstallerConstants.LlmLegacyModelIdKey);
                InstallerUserConfigWriter.SetOptionalStringProperty(llmNode, InstallerConstants.LlmArchitectModelIdKey, mapped.ArchitectModelId);
                InstallerUserConfigWriter.SetOptionalStringProperty(llmNode, InstallerConstants.LlmCoderModelIdKey, mapped.CoderModelId);
                InstallerUserConfigWriter.SetOptionalStringProperty(llmNode, InstallerConstants.LlmDefaultModelIdKey, mapped.DefaultModelId);
                InstallerUserConfigWriter.SetIntProperty(llmNode, InstallerConstants.LlmContextWindowTokensKey, mapped.ContextWindowTokens);
            },
            CreateSyncException,
            cancellationToken);
    }

    private static Exception CreateSyncException(string message, Exception? innerException) =>
        innerException is null
            ? new OllamaRouterSyncException(message)
            : new OllamaRouterSyncException(message, innerException);

    private static string NormalizeBaseUrl(string? routerBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(routerBaseUrl))
        {
            return InstallerConstants.DefaultOllamaRouterEndpoint;
        }

        if (!TryNormalizeHttpBaseUrl(routerBaseUrl, out var normalized))
        {
            throw new OllamaRouterSyncException(
                $"Invalid OllamaRouter base URL '{routerBaseUrl}'. Expected an absolute HTTP or HTTPS URL without query or fragment.");
        }

        return normalized;
    }

    private static bool TryNormalizeHttpBaseUrl(string rawUrl, out string normalized)
    {
        var trimmed = rawUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = trimmed;
        return true;
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
