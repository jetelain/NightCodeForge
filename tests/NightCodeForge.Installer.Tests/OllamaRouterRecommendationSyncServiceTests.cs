namespace NightCodeForge.Installer.Tests;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;
using NightCodeForge.Installer.Core.Services;

public sealed class OllamaRouterRecommendationSyncServiceTests
{
    private const string FullRecommendationsJson = """
        {
          "generatedAt": "2026-11-11T10:30:00.0000000+00:00",
          "roles": {
            "basic": {
              "role": "basic",
              "model": "gemma4:e4b",
              "recommendedContextWindow": 131072,
              "description": "Fast auxiliary tasks",
              "nodes": [
                { "target": "Local", "contextCeiling": 131072, "baselineContext": null, "tokensPerSecond": 68.4, "vramMB": 16384 }
              ]
            },
            "agentic_chat": {
              "role": "agentic_chat",
              "model": "qwen3.8:27b",
              "recommendedContextWindow": 262144,
              "description": "Interactive ideation and chat",
              "nodes": [
                { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
              ]
            },
            "software_architect": {
              "role": "software_architect",
              "model": "qwen3.8:27b",
              "recommendedContextWindow": 262144,
              "description": "Architecture, task review, and code review",
              "nodes": [
                { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
              ]
            },
            "senior_developer": {
              "role": "senior_developer",
              "model": "qwen3.8:27b",
              "recommendedContextWindow": 262144,
              "description": "Complex code implementation",
              "nodes": [
                { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
                { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
              ]
            },
            "junior_developer": {
              "role": "junior_developer",
              "model": "qwen3.6:35b-a3b",
              "recommendedContextWindow": 131072,
              "description": "High-speed MoE implementation",
              "nodes": [
                { "target": "Local", "contextCeiling": 131072, "baselineContext": 65536, "tokensPerSecond": 54.0, "vramMB": 16384 }
              ]
            }
          }
        }
        """;

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_CreatesConfigJson_WithExpectedMappingAndContextFloor()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        Uri? capturedUri = null;

        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedUri = req.RequestUri;
            return Task.FromResult(TestArchiveHelper.JsonResponse(FullRecommendationsJson));
        });
        using var httpClient = new HttpClient(handler);

        var detectorMock = CreateDetectorMock(installDir);
        var service = new OllamaRouterRecommendationSyncService(httpClient, detectorMock.Object);
        var events = new List<InstallerProgressEvent>();

        var summary = await service.SyncRecommendationsToConfigAsync(
            installDirectory: null,
            routerBaseUrl: null,
            progress: new SynchronousTestProgress<InstallerProgressEvent>(events.Add));

        Assert.Equal("http://localhost:11434/api/router/recommendations", capturedUri?.ToString());
        Assert.Equal("OllamaRouter", summary.ProviderType);
        Assert.Equal("http://localhost:11434", summary.Endpoint);
        Assert.Equal("qwen3.8:27b", summary.ArchitectModelId);
        Assert.Equal("qwen3.8:27b", summary.CoderModelId);
        Assert.Equal("qwen3.8:27b", summary.DefaultModelId);
        Assert.Equal(262144, summary.ContextWindowTokens);
        Assert.Equal(5, summary.Roles.Count);

        var configPath = Path.Combine(installDir, "config", "config.json");
        Assert.Equal(configPath, summary.ConfigFilePath);
        Assert.True(File.Exists(configPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(installDir, "config"), "*.tmp"));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath)) as JsonObject;
        Assert.NotNull(root);
        var llm = root["LlmProvider"] as JsonObject;
        Assert.NotNull(llm);
        Assert.Equal("OllamaRouter", llm["ProviderType"]?.GetValue<string>());
        Assert.Equal("http://localhost:11434", llm["Endpoint"]?.GetValue<string>());
        Assert.Equal("qwen3.8:27b", llm["ArchitectModelId"]?.GetValue<string>());
        Assert.Equal("qwen3.8:27b", llm["CoderModelId"]?.GetValue<string>());
        Assert.Equal("qwen3.8:27b", llm["DefaultModelId"]?.GetValue<string>());
        Assert.Equal(262144, llm["ContextWindowTokens"]?.GetValue<int>());
        Assert.NotEmpty(events);
    }

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_PreservesUnrelatedSections_AndNormalizesCamelCaseDuplicates()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");

        await File.WriteAllTextAsync(configPath, """
            {
              // Preserved user comment
              "Logging": {
                "LogLevel": {
                  "Default": "Information"
                }
              },
              "Tailscale": {
                "Enabled": true
              },
              "llmProvider": {
                "providerType": "Ollama",
                "endpoint": "http://old:11434",
                "ModelId": "Qwen3.8-27B:latest",
                "maxConcurrentInferenceTasks": 3,
                "OpenAiCompatible": {
                  "Endpoint": "https://api.openai.com/v1",
                },
              },
            }
            """);

        const string partialRolesJson = """
            {
              "generatedAt": "2026-11-12T08:00:00+00:00",
              "roles": {
                "basic": { "role": "basic", "model": "gemma4:e4b", "recommendedContextWindow": 65536, "nodes": [] },
                "junior_developer": { "role": "junior_developer", "model": "qwen3.6:35b-a3b", "recommendedContextWindow": 65536, "nodes": [] }
              }
            }
            """;

        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(TestArchiveHelper.JsonResponse(partialRolesJson)));
        using var httpClient = new HttpClient(handler);
        var service = new OllamaRouterRecommendationSyncService(httpClient, CreateDetectorMock(installDir).Object);

        var summary = await service.SyncRecommendationsToConfigAsync(
            installDirectory: installDir,
            routerBaseUrl: "http://127.0.0.1:11434/");

        Assert.Null(summary.ArchitectModelId);
        Assert.Equal("qwen3.6:35b-a3b", summary.CoderModelId);
        Assert.Equal("qwen3.6:35b-a3b", summary.DefaultModelId);
        Assert.Equal(InstallerConstants.DefaultContextWindowTokens, summary.ContextWindowTokens);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath)) as JsonObject;
        Assert.NotNull(root);
        Assert.NotNull(root["Logging"]);
        Assert.True(root["Tailscale"]?["Enabled"]?.GetValue<bool>());
        Assert.False(root.ContainsKey("llmProvider"));

        var llm = root["LlmProvider"] as JsonObject;
        Assert.NotNull(llm);
        Assert.False(llm.ContainsKey("providerType"));
        Assert.False(llm.ContainsKey("endpoint"));
        Assert.False(llm.ContainsKey("ModelId"));
        Assert.False(llm.ContainsKey("ArchitectModelId"));
        Assert.Equal("OllamaRouter", llm["ProviderType"]?.GetValue<string>());
        Assert.Equal("http://127.0.0.1:11434", llm["Endpoint"]?.GetValue<string>());
        Assert.Equal("qwen3.6:35b-a3b", llm["CoderModelId"]?.GetValue<string>());
        Assert.Equal("qwen3.6:35b-a3b", llm["DefaultModelId"]?.GetValue<string>());
        Assert.Equal(131072, llm["ContextWindowTokens"]?.GetValue<int>());
        Assert.Equal(3, llm["maxConcurrentInferenceTasks"]?.GetValue<int>());
        Assert.Equal("https://api.openai.com/v1", llm["OpenAiCompatible"]?["Endpoint"]?.GetValue<string>());
    }

    [Fact]
    public void MapRecommendations_AppliesPriorityFallbacksAndContextFloor()
    {
        const string json = """
            {
              "generatedAt": "2026-11-12T08:00:00+00:00",
              "roles": {
                "Agentic_Chat": { "role": "agentic_chat", "model": " chat:latest ", "recommendedContextWindow": 131072, "nodes": [] },
                "Software_Architect": { "role": "software_architect", "model": " arch:latest ", "recommendedContextWindow": 65536, "nodes": [] },
                "Junior_Developer": { "role": "junior_developer", "model": " junior:latest ", "recommendedContextWindow": 131072, "nodes": [] }
              }
            }
            """;

        var dto = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.RouterRecommendationsDto);
        Assert.NotNull(dto);

        var mapped = OllamaRouterRecommendationSyncService.MapRecommendations(dto);
        Assert.Equal("arch:latest", mapped.ArchitectModelId);
        Assert.Equal("junior:latest", mapped.CoderModelId);
        Assert.Equal("chat:latest", mapped.DefaultModelId);
        Assert.Equal(131072, mapped.ContextWindowTokens);

        // Missing software_architect falls back to agentic_chat
        var noArchDto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["agentic_chat"] = new("agentic_chat", "chat-only:latest", 262144, null, [])
            });
        var mappedNoArch = OllamaRouterRecommendationSyncService.MapRecommendations(noArchDto);
        Assert.Equal("chat-only:latest", mappedNoArch.ArchitectModelId);
        Assert.Null(mappedNoArch.CoderModelId);
        Assert.Equal("chat-only:latest", mappedNoArch.DefaultModelId);
        Assert.Equal(131072, mappedNoArch.ContextWindowTokens);

        // Blank-model software_architect must not supply ContextWindowTokens
        var blankArchDto = new RouterRecommendationsDto(
            DateTimeOffset.UtcNow,
            new Dictionary<string, RouterRoleRecommendationDto>
            {
                ["software_architect"] = new("software_architect", "   ", 262144, null, []),
                ["agentic_chat"] = new("agentic_chat", "chat-fallback:latest", 131072, null, [])
            });
        var mappedBlankArch = OllamaRouterRecommendationSyncService.MapRecommendations(blankArchDto);
        Assert.Equal("chat-fallback:latest", mappedBlankArch.ArchitectModelId);
        Assert.Equal(InstallerConstants.DefaultContextWindowTokens, mappedBlankArch.ContextWindowTokens);
    }

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_FailsFastOnHttpErrorsEmptyRolesInvalidUrlAndCorruptConfig()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        var detectorMock = CreateDetectorMock(installDir);

        // 1. Invalid URLs (scheme, query, fragment, missing host)
        using var dummyClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(TestArchiveHelper.JsonResponse(FullRecommendationsJson))));
        var invalidUrlService = new OllamaRouterRecommendationSyncService(dummyClient, detectorMock.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => invalidUrlService.SyncRecommendationsToConfigAsync(installDir, "not-a-url"));
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => invalidUrlService.SyncRecommendationsToConfigAsync(installDir, "http://localhost:11434?x=1"));
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => invalidUrlService.SyncRecommendationsToConfigAsync(installDir, "http://localhost:11434#frag"));
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => invalidUrlService.SyncRecommendationsToConfigAsync(installDir, "http:///path"));

        // 2. HTTP 404 -> Fetch returns null, Sync throws OllamaRouterSyncException
        using var notFoundClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var notFoundService = new OllamaRouterRecommendationSyncService(notFoundClient, detectorMock.Object);
        Assert.Null(await notFoundService.FetchRecommendationsAsync());
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => notFoundService.SyncRecommendationsToConfigAsync(installDir));

        // 3. HTTP 200 with JSON null payload -> throws explicit empty/null exception (not HTTP 404)
        using var nullBodyClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(TestArchiveHelper.JsonResponse("null"))));
        var nullBodyService = new OllamaRouterRecommendationSyncService(nullBodyClient, detectorMock.Object);
        var nullEx = await Assert.ThrowsAsync<OllamaRouterSyncException>(() => nullBodyService.SyncRecommendationsToConfigAsync(installDir));
        Assert.Contains("empty or null", nullEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("404", nullEx.Message, StringComparison.Ordinal);

        // 4. HTTP 500
        using var errorClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))));
        var errorService = new OllamaRouterRecommendationSyncService(errorClient, detectorMock.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => errorService.FetchRecommendationsAsync());

        // 5. Empty or basic-only recommendations
        using var emptyClient = new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(TestArchiveHelper.JsonResponse("""{"generatedAt":null,"roles":{"basic":{"role":"basic","model":"gemma4:e4b","recommendedContextWindow":131072,"nodes":[]}}}"""))));
        var emptyService = new OllamaRouterRecommendationSyncService(emptyClient, detectorMock.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => emptyService.SyncRecommendationsToConfigAsync(installDir));

        // 6. Corrupt existing config.json is not overwritten
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");
        const string corruptContent = "{ invalid json [";
        await File.WriteAllTextAsync(configPath, corruptContent);

        var okService = new OllamaRouterRecommendationSyncService(dummyClient, detectorMock.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => okService.SyncRecommendationsToConfigAsync(installDir));
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(configPath));

        // 7. Non-object LlmProvider section throws OllamaRouterSyncException
        await File.WriteAllTextAsync(configPath, """{"LlmProvider": "not-an-object"}""");
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => okService.SyncRecommendationsToConfigAsync(installDir));
    }

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_HandlesTimeoutCancellationEmptyInstallDirAndIoErrors()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        var detectorMock = CreateDetectorMock(installDir);

        // 1. Internal timeout (TaskCanceledException when caller token is not cancelled) -> OllamaRouterSyncException
        using var timeoutClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new TaskCanceledException("Simulated HTTP timeout")));
        var timeoutService = new OllamaRouterRecommendationSyncService(timeoutClient, detectorMock.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => timeoutService.FetchRecommendationsAsync());

        // 2. Caller cancellation -> propagates OperationCanceledException
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var okClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse(FullRecommendationsJson))));
        var cancelService = new OllamaRouterRecommendationSyncService(okClient, detectorMock.Object);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelService.FetchRecommendationsAsync(cancellationToken: cts.Token));

        // 3. Unresolvable install directory throws OllamaRouterSyncException
        var emptyDetector = new Mock<IInstallationDetector>();
        emptyDetector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(new InstallationDiscoveryResult(
            NightCodeForge: new InstalledComponentInfo(false, "   ", null, false, false, false, false, null),
            OllamaRouter: new InstalledComponentInfo(false, "router", null, false, false, false, false, null),
            RecommendedSourceMode: InstallSourceMode.Online,
            DetectedBundleDirectory: null,
            DetectedBundleFiles: []));
        var missingDirService = new OllamaRouterRecommendationSyncService(okClient, emptyDetector.Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => missingDirService.SyncRecommendationsToConfigAsync());

        // 4. I/O failure when config.json path is occupied by a directory throws OllamaRouterSyncException
        var blockedInstallDir = workspace.CreateSubdirectory("blocked-ncf");
        Directory.CreateDirectory(Path.Combine(blockedInstallDir, "config", "config.json"));
        var ioService = new OllamaRouterRecommendationSyncService(okClient, CreateDetectorMock(blockedInstallDir).Object);
        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => ioService.SyncRecommendationsToConfigAsync(blockedInstallDir));
    }

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_WhenConfigHasDuplicateJsonKeys_ThrowsOllamaRouterSyncExceptionWithoutOverwriting()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-dup");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");
        const string duplicateKeyJson = """{"LlmProvider": {"Endpoint": "http://localhost:11434"}, "LlmProvider": {"Endpoint": "http://localhost:22435"}}""";
        await File.WriteAllTextAsync(configPath, duplicateKeyJson);

        using var okClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse(FullRecommendationsJson))));
        var service = new OllamaRouterRecommendationSyncService(okClient, CreateDetectorMock(installDir).Object);

        await Assert.ThrowsAsync<OllamaRouterSyncException>(() => service.SyncRecommendationsToConfigAsync(installDir));
        Assert.Equal(duplicateKeyJson, await File.ReadAllTextAsync(configPath));
    }

    [Fact]
    public async Task SyncRecommendationsToConfigAsync_WhenRouterBaseUrlNull_PreservesAndQueriesConfiguredEndpoint()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-custom-endpoint");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");
        await File.WriteAllTextAsync(configPath, """{"LlmProvider": {"Endpoint": "http://127.0.0.1:22435/"}}""");

        Uri? requestedUri = null;
        using var client = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            requestedUri = req.RequestUri;
            return Task.FromResult(TestArchiveHelper.JsonResponse(FullRecommendationsJson));
        }));

        var service = new OllamaRouterRecommendationSyncService(client, CreateDetectorMock(installDir).Object);
        var summary = await service.SyncRecommendationsToConfigAsync(installDir, routerBaseUrl: null);

        Assert.Equal("http://127.0.0.1:22435/api/router/recommendations", requestedUri?.ToString());
        Assert.Equal("http://127.0.0.1:22435", summary.Endpoint);

        var savedJson = await File.ReadAllTextAsync(configPath);
        var root = JsonNode.Parse(savedJson)!.AsObject();
        Assert.Equal("http://127.0.0.1:22435", root["LlmProvider"]?["Endpoint"]?.GetValue<string>());
    }

    private static Mock<IInstallationDetector> CreateDetectorMock(string installDir)
    {
        var mock = new Mock<IInstallationDetector>();
        mock.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(new InstallationDiscoveryResult(
            NightCodeForge: new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            OllamaRouter: new InstalledComponentInfo(true, Path.Combine(installDir, "router"), "1.0.0", false, true, true, true, null),
            RecommendedSourceMode: InstallSourceMode.Online,
            DetectedBundleDirectory: null,
            DetectedBundleFiles: []));
        return mock;
    }

    private sealed class SynchronousTestProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
