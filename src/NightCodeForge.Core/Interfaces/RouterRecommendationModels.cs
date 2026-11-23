using System.Text.Json.Serialization;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Canonical role identifiers exposed by OllamaRouter's recommendation endpoint.
/// </summary>
public static class OllamaRouterRoleNames
{
    public const string Basic = "basic";
    public const string AgenticChat = "agentic_chat";
    public const string SoftwareArchitect = "software_architect";
    public const string SeniorDeveloper = "senior_developer";
    public const string JuniorDeveloper = "junior_developer";
}

/// <summary>
/// Custom OS protocol URI constants used to launch the OllamaRouter desktop companion app.
/// </summary>
public static class OllamaRouterProtocolUris
{
    public const string Scheme = "ollamarouter";
    public const string OpenApp = "ollamarouter://open";
    public const string SetupWizard = "ollamarouter://wizard";
}

/// <summary>
/// Per-node benchmark profile for a recommended native model in <c>GET /api/router/recommendations</c>.
/// </summary>
public sealed record RouterNodeRoleProfileDto(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("baselineContext")] int? BaselineContext,
    [property: JsonPropertyName("tokensPerSecond")] double TokensPerSecond,
    [property: JsonPropertyName("vramMB")] int? VramMB);

/// <summary>
/// Single role recommendation entry returned by OllamaRouter's <c>GET /api/router/recommendations</c>.
/// </summary>
public sealed record RouterRoleRecommendationDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("nodes")] IReadOnlyList<RouterNodeRoleProfileDto>? Nodes);

/// <summary>
/// Fleet recommendation matrix served by OllamaRouter's <c>GET /api/router/recommendations</c>.
/// </summary>
public sealed record RouterRecommendationsDto(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset? GeneratedAt,
    [property: JsonPropertyName("roles")] IReadOnlyDictionary<string, RouterRoleRecommendationDto>? Roles);
