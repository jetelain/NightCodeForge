namespace NightCodeForge.Installer.Tests;

using System.Net;
using System.Text.Json.Nodes;
using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class OpenAiProviderConfigurationServiceTests : IDisposable
{
    private readonly TempTestWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task DiscoverModelsAsync_SendsAuthAndOrgHeaders_AndParsesSortedModels()
    {
        Uri? capturedUri = null;
        string? capturedAuth = null;
        string? capturedOrg = null;

        const string payload = """
            {
              "data": [
                {
                  "id": "gpt-4o",
                  "display_name": "GPT-4o",
                  "context_length": 128000,
                  "pricing": { "prompt": "0.0000025", "completion": "0.00001" }
                },
                {
                  "id": "deepseek-chat",
                  "context_window": 65536
                }
              ]
            }
            """;

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            capturedUri = req.RequestUri;
            capturedAuth = req.Headers.Authorization?.ToString();
            capturedOrg = req.Headers.TryGetValues("OpenAI-Organization", out var vals) ? vals.FirstOrDefault() : null;
            return Task.FromResult(TestArchiveHelper.JsonResponse(payload));
        }));

        var sut = new OpenAiProviderConfigurationService(
            httpClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var result = await sut.DiscoverModelsAsync(
            "https://api.openai.com/v1",
            apiKeyOverride: "sk-live-test",
            organizationId: "org-123");

        Assert.True(result.IsSuccess);
        Assert.True(result.IsDiscoveryImplemented);
        Assert.Equal("https://api.openai.com/v1/models", capturedUri?.ToString());
        Assert.Equal("Bearer sk-live-test", capturedAuth);
        Assert.Equal("org-123", capturedOrg);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("deepseek-chat", result.Models[0].ModelId);
        Assert.Equal(65536, result.Models[0].ContextWindowTokens);
        Assert.Equal("gpt-4o", result.Models[1].ModelId);
        Assert.Equal("GPT-4o", result.Models[1].DisplayName);
        Assert.Equal(128000, result.Models[1].ContextWindowTokens);
        Assert.Equal(2.5m, result.Models[1].PromptCostPerMillion);
        Assert.Equal(10m, result.Models[1].CompletionCostPerMillion);
    }

    [Fact]
    public async Task DiscoverModelsAsync_Handles401And404FallbackToManualModels()
    {
        using var unauthClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var unauthSut = new OpenAiProviderConfigurationService(
            unauthClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var unauthResult = await unauthSut.DiscoverModelsAsync("https://openrouter.ai/api/v1", "sk-bad", null);
        Assert.False(unauthResult.IsSuccess);
        Assert.Contains("401", unauthResult.ErrorMessage, StringComparison.Ordinal);

        using var notFoundClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        var notFoundSut = new OpenAiProviderConfigurationService(
            notFoundClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var notFoundResult = await notFoundSut.DiscoverModelsAsync("http://localhost:1234", null, null);
        Assert.True(notFoundResult.IsSuccess);
        Assert.False(notFoundResult.IsDiscoveryImplemented);
        Assert.Empty(notFoundResult.Models);
    }

    [Fact]
    public async Task SaveConfigurationAsync_StoresKeyInKeyringWritesConfigJsonPreservingSectionsAndReadsBack()
    {
        var installDir = _workspace.CreateSubdirectory("ncf");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");

        await File.WriteAllTextAsync(configPath, """
            {
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "LlmProvider": {
                "ProviderType": "OllamaRouter",
                "ModelId": "legacy-model",
                "OpenAiCompatible": {
                  "ApiKey": "plaintext-legacy-should-be-removed",
                  "AllowedModels": [{ "ModelId": "stale-model" }]
                }
              }
            }
            """);

        var secretStore = new Mock<IInstallerSecretStore>();
        secretStore.Setup(s => s.IsKeyringAvailable()).Returns(true);
        secretStore.Setup(s => s.HasOpenAiApiKey("https://openrouter.ai/api/v1")).Returns(true);

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("{}"))));
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            new InstalledComponentInfo(false, "router", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []));

        var sut = new OpenAiProviderConfigurationService(httpClient, detector.Object, secretStore.Object);

        var summary = await sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            InstallDirectory: installDir,
            ProviderName: "OpenRouter",
            Endpoint: "https://openrouter.ai/api/v1/",
            ApiKeyInput: "sk-or-secret",
            OrganizationId: "org-test",
            DefaultModelId: "anthropic/claude-sonnet-4",
            ArchitectModelId: "openai/o3",
            CoderModelId: "deepseek/deepseek-chat",
            ContextWindowTokens: 200000,
            AllowedModelsMode: OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery));

        secretStore.Verify(s => s.SetOpenAiApiKey("https://openrouter.ai/api/v1", "sk-or-secret"), Times.Once);
        Assert.True(summary.ApiKeyStoredInKeyring);
        Assert.Equal("https://openrouter.ai/api/v1", summary.Endpoint);

        var rawJson = await File.ReadAllTextAsync(configPath);
        Assert.DoesNotContain("sk-or-secret", rawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-legacy-should-be-removed", rawJson, StringComparison.Ordinal);

        var root = JsonNode.Parse(rawJson)!.AsObject();
        Assert.NotNull(root["Logging"]);
        var llm = root["LlmProvider"]!.AsObject();
        Assert.Equal(InstallerConstants.OpenAiCompatibleProviderTypeName, llm["ProviderType"]?.GetValue<string>());
        Assert.False(llm.ContainsKey("ModelId"));
        Assert.Equal("anthropic/claude-sonnet-4", llm["DefaultModelId"]?.GetValue<string>());
        Assert.Equal("openai/o3", llm["ArchitectModelId"]?.GetValue<string>());
        Assert.Equal("deepseek/deepseek-chat", llm["CoderModelId"]?.GetValue<string>());
        Assert.Equal(200000, llm["ContextWindowTokens"]?.GetValue<int>());

        var openAi = llm["OpenAiCompatible"]!.AsObject();
        Assert.False(openAi.ContainsKey("ApiKey"));
        Assert.False(openAi.ContainsKey("AllowedModels"));
        Assert.Equal("https://openrouter.ai/api/v1", openAi["Endpoint"]?.GetValue<string>());
        Assert.Equal("org-test", openAi["OrganizationId"]?.GetValue<string>());

        // Re-reading extracts saved settings cleanly
        var existing = sut.TryReadExistingConfiguration(installDir);
        Assert.NotNull(existing);
        Assert.Equal("https://openrouter.ai/api/v1", existing.Endpoint);
        Assert.Equal("org-test", existing.OrganizationId);
        Assert.Equal("anthropic/claude-sonnet-4", existing.DefaultModelId);
        Assert.Equal("openai/o3", existing.ArchitectModelId);
        Assert.Equal("deepseek/deepseek-chat", existing.CoderModelId);
        Assert.Equal(200000, existing.ContextWindowTokens);
        Assert.False(existing.HasManualAllowedModels);
        Assert.Equal(configPath, existing.ConfigFilePath);

        // WriteManualModels persists AllowedModels array cleanly
        await sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            InstallDirectory: installDir,
            ProviderName: "Custom",
            Endpoint: "http://localhost:8000/v1",
            ApiKeyInput: null,
            OrganizationId: null,
            DefaultModelId: "local-coder",
            ArchitectModelId: "local-arch",
            CoderModelId: "local-coder",
            ContextWindowTokens: 131072,
            AllowedModelsMode: OpenAiAllowedModelsPersistenceMode.WriteManualModels));

        var updatedRoot = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        var allowed = updatedRoot["LlmProvider"]!["OpenAiCompatible"]!["AllowedModels"]!.AsArray();
        Assert.Equal(2, allowed.Count);
        Assert.Equal("local-coder", allowed[0]!["ModelId"]?.GetValue<string>());
        Assert.Equal("local-arch", allowed[1]!["ModelId"]?.GetValue<string>());

        var manualExisting = sut.TryReadExistingConfiguration(installDir);
        Assert.NotNull(manualExisting);
        Assert.True(manualExisting.HasManualAllowedModels);
    }

    [Fact]
    public async Task SaveConfigurationAsync_ValidatesEndpointAndDefaultModel()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("{}"))));
        var sut = new OpenAiProviderConfigurationService(
            httpClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        await Assert.ThrowsAsync<OpenAiConfigurationException>(() => sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            _workspace.RootPath, "OpenAI", "not-a-url", null, null, "gpt-4o", null, null, 131072, OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery)));

        await Assert.ThrowsAsync<OpenAiConfigurationException>(() => sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            _workspace.RootPath, "OpenAI", "https://api.openai.com/v1", null, null, "   ", null, null, 131072, OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery)));
    }

    [Fact]
    public async Task DiscoverModelsAsync_RejectsInvalidApiKeyOrOrganizationIdWithoutLeakingSecret()
    {
        var secretStore = new Mock<IInstallerSecretStore>();
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("HTTP request should not be sent for invalid header values.")));

        var sut = new OpenAiProviderConfigurationService(
            httpClient,
            Mock.Of<IInstallationDetector>(),
            secretStore.Object);

        const string secretWithNewline = "sk-secret-do-not-leak\ninjected-header";
        var keyResult = await sut.DiscoverModelsAsync("https://api.openai.com/v1", secretWithNewline, null);
        Assert.False(keyResult.IsSuccess);
        Assert.NotNull(keyResult.ErrorMessage);
        Assert.DoesNotContain("sk-secret-do-not-leak", keyResult.ErrorMessage, StringComparison.Ordinal);

        var orgResult = await sut.DiscoverModelsAsync("https://api.openai.com/v1", "sk-valid", "org\rinjected");
        Assert.False(orgResult.IsSuccess);
        Assert.Contains("Organization ID", orgResult.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverModelsAsync_RejectsHtmlAndNonModelsJsonOnHttp200_WhileAcceptingEmptyModelsArray()
    {
        using var htmlClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Sign in</body></html>")
            })));
        var htmlSut = new OpenAiProviderConfigurationService(
            htmlClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var htmlResult = await htmlSut.DiscoverModelsAsync("https://api.openai.com/v1", null, null);
        Assert.False(htmlResult.IsSuccess);
        Assert.Contains("valid OpenAI-compatible JSON", htmlResult.ErrorMessage, StringComparison.Ordinal);

        using var genericJsonClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("""{"status":"ok"}"""))));
        var genericSut = new OpenAiProviderConfigurationService(
            genericJsonClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var genericResult = await genericSut.DiscoverModelsAsync("https://api.openai.com/v1", null, null);
        Assert.False(genericResult.IsSuccess);

        using var emptyArrayClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("""{"data":[]}"""))));
        var emptySut = new OpenAiProviderConfigurationService(
            emptyArrayClient,
            Mock.Of<IInstallationDetector>(),
            Mock.Of<IInstallerSecretStore>());

        var emptyResult = await emptySut.DiscoverModelsAsync("https://api.openai.com/v1", null, null);
        Assert.True(emptyResult.IsSuccess);
        Assert.True(emptyResult.IsDiscoveryImplemented);
        Assert.Empty(emptyResult.Models);
    }

    [Fact]
    public async Task SaveConfigurationAsync_WhenInstallDirectoryInvalid_ThrowsBeforeTouchingKeyring()
    {
        var secretStore = new Mock<IInstallerSecretStore>();
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(new InstallationDiscoveryResult(
            new InstalledComponentInfo(false, string.Empty, null, false, false, false, false, null),
            new InstalledComponentInfo(false, string.Empty, null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []));

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("{}"))));
        var sut = new OpenAiProviderConfigurationService(httpClient, detector.Object, secretStore.Object);

        await Assert.ThrowsAsync<OpenAiConfigurationException>(() => sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            InstallDirectory: null,
            ProviderName: "OpenAI",
            Endpoint: "https://api.openai.com/v1",
            ApiKeyInput: "sk-should-not-write",
            OrganizationId: null,
            DefaultModelId: "gpt-4o",
            ArchitectModelId: null,
            CoderModelId: null,
            ContextWindowTokens: 131072,
            AllowedModelsMode: OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery)));

        await Assert.ThrowsAsync<OpenAiConfigurationException>(() => sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            InstallDirectory: "invalid\0path",
            ProviderName: "OpenAI",
            Endpoint: "https://api.openai.com/v1",
            ApiKeyInput: "sk-should-not-write",
            OrganizationId: null,
            DefaultModelId: "gpt-4o",
            ArchitectModelId: null,
            CoderModelId: null,
            ContextWindowTokens: 131072,
            AllowedModelsMode: OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery)));

        secretStore.Verify(s => s.SetOpenAiApiKey(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveConfigurationAsync_PreserveExistingAllowedModels_KeepsRichEntriesAndReadsMetadata()
    {
        var installDir = _workspace.CreateSubdirectory("ncf-preserve");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, "config")).FullName;
        var configPath = Path.Combine(configDir, "config.json");

        await File.WriteAllTextAsync(configPath, """
            {
              "LlmProvider": {
                "ProviderType": "OpenAiCompatible",
                "DefaultModelId": "custom-vision",
                "ContextWindowTokens": 200000,
                "OpenAiCompatible": {
                  "Endpoint": "https://api.openai.com/v1",
                  "AllowedModels": [
                    {
                      "ModelId": "custom-vision",
                      "DisplayName": "Custom Vision",
                      "ContextWindowTokens": 200000,
                      "SupportsVision": true,
                      "PromptCostPerMillion": 1.25
                    }
                  ]
                }
              }
            }
            """);

        var secretStore = new Mock<IInstallerSecretStore>();
        secretStore.Setup(s => s.IsKeyringAvailable()).Returns(true);
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("{}"))));

        var sut = new OpenAiProviderConfigurationService(httpClient, Mock.Of<IInstallationDetector>(), secretStore.Object);

        var existing = sut.TryReadExistingConfiguration(installDir);
        Assert.NotNull(existing);
        Assert.Equal(200000, existing.ContextWindowTokens);
        Assert.True(existing.HasManualAllowedModels);
        Assert.Equal(configPath, existing.ConfigFilePath);

        await sut.SaveConfigurationAsync(new OpenAiProviderSaveRequest(
            InstallDirectory: installDir,
            ProviderName: "OpenAI",
            Endpoint: "https://api.openai.com/v1",
            ApiKeyInput: "sk-new-key",
            OrganizationId: null,
            DefaultModelId: "custom-vision",
            ArchitectModelId: null,
            CoderModelId: null,
            ContextWindowTokens: 200000,
            AllowedModelsMode: OpenAiAllowedModelsPersistenceMode.PreserveExisting));

        var savedRoot = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        var allowed = savedRoot["LlmProvider"]!["OpenAiCompatible"]!["AllowedModels"]!.AsArray();
        var entry = Assert.Single(allowed)!.AsObject();
        Assert.Equal("custom-vision", entry["ModelId"]?.GetValue<string>());
        Assert.True(entry["SupportsVision"]?.GetValue<bool>());
        Assert.Equal(1.25m, entry["PromptCostPerMillion"]?.GetValue<decimal>());
    }

    [Fact]
    public void ParseModelsJson_HandlesOutOfRangeFloatPricingWithoutOverflow()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "extreme-cost-model",
                  "pricing": { "prompt": 1e308, "completion": "1e308" }
                }
              ]
            }
            """;

        Assert.True(OpenAiProviderConfigurationService.TryParseModelsJson(json, out var models));
        var model = Assert.Single(models);
        Assert.Equal("extreme-cost-model", model.ModelId);
        Assert.Null(model.PromptCostPerMillion);
        Assert.Null(model.CompletionCostPerMillion);
    }
}
