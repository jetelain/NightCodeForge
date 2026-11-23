using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Inference;

/// <summary>
/// Normalized NightCodeForge role-to-model assignment derived from OllamaRouter recommendations.
/// </summary>
public sealed record MappedRouterRecommendations(
    string? DefaultModelId,
    string? ArchitectModelId,
    string? CoderModelId,
    int ContextWindowTokens,
    DateTimeOffset? GeneratedAt)
{
    public bool HasRecommendations =>
        !string.IsNullOrWhiteSpace(DefaultModelId) ||
        !string.IsNullOrWhiteSpace(ArchitectModelId) ||
        !string.IsNullOrWhiteSpace(CoderModelId);

    public bool ShouldDifferentiateRoles =>
        !string.IsNullOrWhiteSpace(ArchitectModelId) ||
        !string.IsNullOrWhiteSpace(CoderModelId);
}

/// <summary>
/// Maps OllamaRouter's <c>GET /api/router/recommendations</c> payload onto NightCodeForge's
/// <see cref="LlmProviderOptions"/> role fields and context window ceiling.
/// </summary>
public static class OllamaRouterRecommendationMapper
{
    public static MappedRouterRecommendations Map(RouterRecommendationsDto? dto)
    {
        if (dto?.Roles is null || dto.Roles.Count == 0)
        {
            return new MappedRouterRecommendations(
                DefaultModelId: null,
                ArchitectModelId: null,
                CoderModelId: null,
                ContextWindowTokens: LlmProviderOptions.DefaultContextWindowTokens,
                GeneratedAt: dto?.GeneratedAt);
        }

        var softwareArchitect = FindRole(dto.Roles, OllamaRouterRoleNames.SoftwareArchitect);
        var agenticChat = FindRole(dto.Roles, OllamaRouterRoleNames.AgenticChat);
        var seniorDeveloper = FindRole(dto.Roles, OllamaRouterRoleNames.SeniorDeveloper);
        var juniorDeveloper = FindRole(dto.Roles, OllamaRouterRoleNames.JuniorDeveloper);

        var softwareArchitectModel = NormalizeModelId(softwareArchitect?.Model);
        var agenticChatModel = NormalizeModelId(agenticChat?.Model);
        var seniorDeveloperModel = NormalizeModelId(seniorDeveloper?.Model);
        var juniorDeveloperModel = NormalizeModelId(juniorDeveloper?.Model);

        var architectModelId = softwareArchitectModel ?? agenticChatModel;
        var coderModelId = seniorDeveloperModel ?? juniorDeveloperModel;
        var defaultModelId = agenticChatModel ?? architectModelId ?? coderModelId;

        var contextWindowTokens = softwareArchitectModel is not null && softwareArchitect is { RecommendedContextWindow: > 0 }
            ? Math.Max(LlmProviderOptions.DefaultContextWindowTokens, softwareArchitect.RecommendedContextWindow)
            : LlmProviderOptions.DefaultContextWindowTokens;

        return new MappedRouterRecommendations(
            DefaultModelId: defaultModelId,
            ArchitectModelId: architectModelId,
            CoderModelId: coderModelId,
            ContextWindowTokens: contextWindowTokens,
            GeneratedAt: dto.GeneratedAt);
    }

    public static string NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return LlmProviderOptions.DefaultEndpoint;
        }

        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                $"Invalid OllamaRouter endpoint URL '{endpoint}'. Expected an absolute HTTP or HTTPS URL without query or fragment.",
                nameof(endpoint));
        }

        return trimmed;
    }

    public static void ApplyToOptions(
        LlmProviderOptions options,
        MappedRouterRecommendations mapped,
        string? endpointOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mapped);

        if (!mapped.HasRecommendations)
        {
            throw new InvalidOperationException("No OllamaRouter role recommendations are available to apply.");
        }

        options.ProviderType = LlmProviderType.OllamaRouter;

        options.Endpoint = !string.IsNullOrWhiteSpace(endpointOverride)
            ? NormalizeEndpoint(endpointOverride)
            : NormalizeEndpoint(options.Endpoint);

        options.DefaultModelId = NormalizeModelId(mapped.DefaultModelId);
        options.ArchitectModelId = NormalizeModelId(mapped.ArchitectModelId);
        options.CoderModelId = NormalizeModelId(mapped.CoderModelId);
        options.ContextWindowTokens = Math.Max(LlmProviderOptions.DefaultContextWindowTokens, mapped.ContextWindowTokens);
    }

    private static RouterRoleRecommendationDto? FindRole(
        IReadOnlyDictionary<string, RouterRoleRecommendationDto> roles,
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

    private static string? NormalizeModelId(string? modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
}
