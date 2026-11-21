namespace NightCodeForge.Installer.Core.Serialization;

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NightCodeForge.Installer.Core.Models;

public sealed record GitHubReleaseDto(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("assets")] List<GitHubReleaseAssetDto> Assets);

public sealed record GitHubReleaseAssetDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size);

public sealed record RouterRecommendationsDto(
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("roles")] Dictionary<string, RouterRoleRecommendationDto> Roles);

public sealed record RouterRoleRecommendationDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("nodes")] List<RouterNodeRoleProfileDto> Nodes);

public sealed record RouterNodeRoleProfileDto(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("contextCeiling")] int ContextCeiling,
    [property: JsonPropertyName("baselineContext")] int? BaselineContext,
    [property: JsonPropertyName("tokensPerSecond")] double TokensPerSecond,
    [property: JsonPropertyName("vramMB")] int? VramMb);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubReleaseDto))]
[JsonSerializable(typeof(UninstallManifest))]
[JsonSerializable(typeof(RouterRecommendationsDto))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
public partial class InstallerJsonContext : JsonSerializerContext
{
}
