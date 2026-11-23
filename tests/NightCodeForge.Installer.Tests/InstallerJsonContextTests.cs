namespace NightCodeForge.Installer.Tests;

using System.Text.Json;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

public sealed class InstallerJsonContextTests
{
    [Fact]
    public void GitHubReleaseDto_RoundTripsThroughSourceGeneratedContext()
    {
        const string json = """
        {
          "tag_name": "v1.2.3",
          "name": "Release v1.2.3",
          "assets": [
            {
              "name": "NightCodeForge-Web-win-x64.zip",
              "browser_download_url": "https://example.test/NightCodeForge-Web-win-x64.zip",
              "size": 123456
            },
            {
              "name": "SHA256SUMS.txt",
              "browser_download_url": "https://example.test/SHA256SUMS.txt",
              "size": 256
            }
          ]
        }
        """;

        var dto = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.GitHubReleaseDto);
        Assert.NotNull(dto);
        Assert.Equal("v1.2.3", dto.TagName);
        Assert.Equal(2, dto.Assets.Count);
        Assert.Equal("NightCodeForge-Web-win-x64.zip", dto.Assets[0].Name);
        Assert.Equal("https://example.test/NightCodeForge-Web-win-x64.zip", dto.Assets[0].BrowserDownloadUrl);
        Assert.Equal(123456, dto.Assets[0].Size);

        var serialized = JsonSerializer.Serialize(dto, InstallerJsonContext.Default.GitHubReleaseDto);
        Assert.Contains("\"tag_name\"", serialized, StringComparison.Ordinal);
        Assert.Contains("\"browser_download_url\"", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RouterRecommendationsDto_DeserializesRoleProfilesAndVramMb()
    {
        const string json = """
        {
          "generatedAt": "2026-11-11T10:30:00.0000000+00:00",
          "roles": {
            "software_architect": {
              "role": "software_architect",
              "model": "qwen3.8:27b",
              "recommendedContextWindow": 262144,
              "description": "Architecture, task review, and code review",
              "nodes": [
                {
                  "target": "Local",
                  "contextCeiling": 49152,
                  "baselineContext": null,
                  "tokensPerSecond": 24.1,
                  "vramMB": 16384
                }
              ]
            }
          }
        }
        """;

        var dto = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.RouterRecommendationsDto);
        Assert.NotNull(dto);
        Assert.NotNull(dto.Roles);
        Assert.True(dto.Roles.TryGetValue("software_architect", out var architect));
        Assert.Equal("qwen3.8:27b", architect.Model);
        Assert.Equal(262144, architect.RecommendedContextWindow);
        Assert.NotNull(architect.Nodes);
        Assert.Single(architect.Nodes);
        Assert.Equal(16384, architect.Nodes[0].VramMb);
        Assert.Equal(24.1, architect.Nodes[0].TokensPerSecond);
    }

    [Fact]
    public void RouterRecommendationsDto_DeserializesEmptyMatrixWithNullGeneratedAt()
    {
        const string json = """
        {
          "generatedAt": null,
          "roles": {}
        }
        """;

        var dto = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.RouterRecommendationsDto);
        Assert.NotNull(dto);
        Assert.Null(dto.GeneratedAt);
        Assert.NotNull(dto.Roles);
        Assert.Empty(dto.Roles);
    }

    [Fact]
    public void UninstallManifest_SerializesAndDeserializesWithoutReflection()
    {
        var manifest = new UninstallManifest(
            Version: "1.0.0",
            InstallDirectory: @"C:\Users\Dev\AppData\Local\NightCodeForge",
            InstalledAtUtc: new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
            OllamaRouterDirectory: @"C:\Users\Dev\AppData\Local\OllamaRouter",
            Platform: "Windows");

        var json = JsonSerializer.Serialize(manifest, InstallerJsonContext.Default.UninstallManifest);
        var roundTripped = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.UninstallManifest);

        Assert.Equal(manifest, roundTripped);
    }
}
