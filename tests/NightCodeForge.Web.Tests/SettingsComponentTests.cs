using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Components.Workspaces;
using NightCodeForge.Web.Services;
using NightCodeForge.Web.Tailscale;
using NightCodeForge.Web.Tests.Fixtures;
using Moq;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class SettingsComponentTests : BunitContext, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly MockGlobalOptimizationSettingsService _optService;
    private readonly MockModelCatalogService _catalogService;
    private readonly MockHostSdkDiscoveryService _sdkService;
    private readonly MockOllamaRouterGatewayService _gatewayService;
    private readonly MockWslMetadataService _wslService;
    private readonly MockGitMirrorSyncService _mirrorService;
    private readonly MockWebSearchSettingsService _webSearchSettingsService;
    private readonly NightCodeForge.Agents.Tools.MockWebSearchService _webSearchService;
    private readonly MockLlmConfigurationWriter _configWriter;
    private readonly LlmProviderOptions _llmOptions;
    private readonly Mock<ITailscaleServeService> _tailscaleServeService;
    private readonly Mock<ITailscaleConfigurationWriter> _tailscaleConfigWriter;
    private readonly Mock<IOptionsMonitor<TailscaleOptions>> _tailscaleOptionsMonitor;
    private readonly MockAcpHarnessCatalogService _acpCatalogService;
    private readonly FakeApiKeySecretStore _secretStore;
    private readonly Dictionary<string, string?> _fakeEnvVars = new(StringComparer.Ordinal);
    private readonly ApiKeySettingsUiService _apiKeyUiService;

    public SettingsComponentTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var initDb = new AppDbContext(_dbOptions))
        {
            initDb.Database.EnsureCreated();
            initDb.Workspaces.Add(new Workspace { Id = 42, Name = "TestProject", RepositoryPath = "/repo" });
            initDb.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b").GetAwaiter().GetResult();
        }

        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _optService = new MockGlobalOptimizationSettingsService();
        _catalogService = new MockModelCatalogService();
        _sdkService = new MockHostSdkDiscoveryService();
        _gatewayService = new MockOllamaRouterGatewayService();
        _wslService = new MockWslMetadataService();
        _mirrorService = new MockGitMirrorSyncService();
        _webSearchSettingsService = new MockWebSearchSettingsService();
        _webSearchService = new NightCodeForge.Agents.Tools.MockWebSearchService();
        _configWriter = new MockLlmConfigurationWriter();
        _llmOptions = new LlmProviderOptions();
        _tailscaleServeService = new Mock<ITailscaleServeService>();
        _tailscaleConfigWriter = new Mock<ITailscaleConfigurationWriter>();
        _tailscaleOptionsMonitor = new Mock<IOptionsMonitor<TailscaleOptions>>();
        _acpCatalogService = new MockAcpHarnessCatalogService();
        _secretStore = new FakeApiKeySecretStore();
        _apiKeyUiService = new ApiKeySettingsUiService(
            _secretStore,
            name => _fakeEnvVars.TryGetValue(name, out var val) ? val : null);

        _tailscaleOptionsMonitor.SetupGet(m => m.CurrentValue).Returns(new TailscaleOptions());
        _tailscaleServeService.Setup(s => s.GetStatusSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TailscaleStatusSnapshot(
                TailscaleDaemonState.Inactive,
                "1.76.0",
                "mybox.tail1234.ts.net",
                "tail1234.ts.net",
                "100.101.102.103",
                "127.0.0.1:5270",
                null));

        Services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(_connection));
        Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        Services.AddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        Services.AddScoped<IWorkflowRepository, EfCoreWorkflowRepository>();
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IGlobalOptimizationSettingsService>(_optService);
        Services.AddSingleton<IWebSearchSettingsService>(_webSearchSettingsService);
        Services.AddSingleton<NightCodeForge.Agents.Tools.IWebSearchService>(_webSearchService);
        Services.AddSingleton<ILlmModelCatalogService>(_catalogService);
        Services.AddSingleton<IAcpHarnessCatalogService>(_acpCatalogService);
        Services.AddSingleton<IHostSdkDiscoveryService>(_sdkService);
        Services.AddSingleton<ILlmGatewayService>(_gatewayService);
        Services.AddSingleton<IWslMetadataService>(_wslService);
        Services.AddSingleton<IGitMirrorSyncService>(_mirrorService);
        Services.AddMemoryCache();
        Services.AddSingleton<ILlmContextWindowCache, NightCodeForge.Infrastructure.Inference.LlmContextWindowCache>();
        Services.AddSingleton<ILlmConfigurationWriter>(_configWriter);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(_llmOptions));
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Security.SecurityOptions()));
        Services.AddSingleton<ICompanionToolInstaller>(new Mock<ICompanionToolInstaller>().Object);
        Services.AddSingleton<IWorkspaceSkillsServiceFactory>(new WorkspaceSkillsServiceFactory(
            new SkillsService(),
            new BundledSkillsProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceSkillsService>.Instance));
        Services.AddSingleton<IGlobalSkillsManagerService>(new GlobalSkillsManagerService(
            new BundledSkillsProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalSkillsManagerService>.Instance));
        Services.AddSingleton<ITailscaleServeService>(_tailscaleServeService.Object);
        Services.AddSingleton<ITailscaleConfigurationWriter>(_tailscaleConfigWriter.Object);
        Services.AddSingleton<IOptionsMonitor<TailscaleOptions>>(_tailscaleOptionsMonitor.Object);
        Services.AddSingleton<IApiKeySecretStore>(_secretStore);
        Services.AddSingleton<IApiKeySettingsUiService>(_apiKeyUiService);

        var mockNotificationService = new Mock<IWebNotificationService>();
        mockNotificationService.Setup(n => n.GetPermissionStateAsync()).ReturnsAsync(NotificationConstants.PermissionStates.Default);
        mockNotificationService.Setup(n => n.GetPreferencesAsync()).ReturnsAsync(new NotificationPreferences());
        mockNotificationService.Setup(n => n.IsSupportedAsync()).ReturnsAsync(true);
        Services.AddSingleton<IWebNotificationService>(mockNotificationService.Object);
    }

    public new void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
    }

    #region SettingsPage Tests

    [Fact]
    public void SettingsPage_ScopeSwitching_TogglesBetweenGlobalAndWorkspace()
    {
        var cut = Render<Settings>();

        // Default: Global Scope
        Assert.Contains("Global System Settings", cut.Markup);
        Assert.Contains("Local LLM Optimizations", cut.Markup);
        Assert.Contains("Global Agent Profiles", cut.Markup);

        // Click Workspace Scope button (No workspace active)
        var scopeButtons = cut.FindAll(".scope-switcher button");
        scopeButtons[1].Click();

        Assert.Contains("No Active Workspace Selected", cut.Markup);
        Assert.Contains("Launch Workspace Creation Wizard", cut.Markup);

        // Set active workspace
        _workspaceState.SetActiveWorkspace(new Workspace
        {
            Id = 1,
            Name = "ForgeRepo",
            RepositoryPath = @"C:\repos\ForgeRepo",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        cut.Render();

        Assert.Contains("Confinement &amp; Sandboxing", cut.Markup);
        Assert.Contains("Workspace Agent Profiles", cut.Markup);
    }

    [Fact]
    public void SettingsPage_WhenActiveWorkspaceChanges_AutomaticallyReRendersWorkspaceSettings()
    {
        var cut = Render<Settings>();

        var scopeButtons = cut.FindAll(".scope-switcher button");
        scopeButtons[1].Click();

        Assert.Contains("No Active Workspace Selected", cut.Markup);

        // Setting active workspace triggers OnWorkspaceChanged, automatically updating the view
        _workspaceState.SetActiveWorkspace(new Workspace
        {
            Id = 1,
            Name = "ForgeRepo",
            RepositoryPath = @"C:\repos\ForgeRepo",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        cut.WaitForState(() => cut.Markup.Contains("Confinement &amp; Sandboxing"));
        Assert.Contains("Workspace Agent Profiles", cut.Markup);
    }

    [Fact]
    public void SettingsPage_GlobalScope_RendersGlobalTabs_ExcludesABTesting()
    {
        var cut = Render<Settings>();

        var globalTabs = cut.FindAll(".settings-tabs button");
        Assert.Equal(9, globalTabs.Count);
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Local LLM Optimizations"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Agent Profiles"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Workflows & Stages"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Host SDK Inspector"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("OllamaRouter Gateway"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Cloud & Web Search"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Global Skills Store"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Remote & Tailscale"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Web Notifications"));
        Assert.DoesNotContain(globalTabs, b => b.TextContent.Contains("A/B Testing"));
    }

    [Fact]
    public void SettingsPage_GlobalScope_WhenProviderIsOpenAiCompatible_RendersOpenAiGatewayTab()
    {
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<Settings>();

        var globalTabs = cut.FindAll(".settings-tabs button");
        Assert.Contains(globalTabs, b => b.TextContent.Contains("OpenAI Compatible Gateway"));
    }

    [Fact]
    public void SettingsPage_GatewayTabLabel_WhenOllamaRouter_DisplaysOllamaRouterLabel()
    {
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;

        var cut = Render<Settings>();

        var tabButtons = cut.FindAll(".settings-tabs button");
        Assert.Contains(tabButtons, b => b.TextContent.Contains("OllamaRouter Gateway & VRAM"));
    }

    [Fact]
    public void SettingsPage_GatewayTabLabel_WhenStandaloneOllama_DisplaysOllamaLabel()
    {
        _gatewayService.ProviderType = LlmProviderType.Ollama;

        var cut = Render<Settings>();

        var tabButtons = cut.FindAll(".settings-tabs button");
        Assert.Contains(tabButtons, b => b.TextContent.Contains("Standalone Ollama Gateway"));
    }

    [Fact]
    public void SettingsPage_NavigatesToWebSearchTab_AndRendersWebSearchCard()
    {
        var cut = Render<Settings>();

        // Find web search tab button
        var webSearchTab = cut.FindAll(".settings-tabs button")
            .FirstOrDefault(b => b.TextContent.Contains("Web Search"));
        Assert.NotNull(webSearchTab);

        webSearchTab.Click();

        Assert.Contains("Cloud &amp; Web Search Integration", cut.Markup);
        Assert.Contains("Search Provider", cut.Markup);
        Assert.Contains("Ollama API Key", cut.Markup);
        Assert.Contains("Live Connectivity &amp; Search Tester", cut.Markup);
    }

    [Fact]
    public void SettingsPage_NavigatesToTailscaleTab_AndRendersTailscaleCard()
    {
        var cut = Render<Settings>();

        var tailscaleTab = cut.FindAll(".settings-tabs button")
            .FirstOrDefault(b => b.TextContent.Contains("Remote & Tailscale"));
        Assert.NotNull(tailscaleTab);

        tailscaleTab.Click();

        Assert.Contains("Remote &amp; Tailscale Access", cut.Markup);
        Assert.Contains("Exposition Configuration", cut.Markup);
        Assert.Contains("Mobile Onboarding &amp; Live Mesh Status", cut.Markup);
    }

    #endregion

    #region OllamaRouterGatewayCard Tests

    [Fact]
    public void OllamaRouterGatewayCard_RendersTargetStatusAndTriggersReclaim()
    {
        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Contains("OllamaRouter Gateway &amp; VRAM Inspector", cut.Markup);
        Assert.Contains("🟢 Gateway Connected", cut.Markup);
        Assert.Contains("OllamaRouter Fleet Active", cut.Markup);
        Assert.Contains("Local GPU Target", cut.Markup);
        Assert.Contains("Remote Server Target", cut.Markup);
        Assert.Contains("Cloud Overflow Target", cut.Markup);
        Assert.Contains("qwen2.5-coder:32b", cut.Markup);

        // Click reclaim
        var reclaimBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Reclaim GPU VRAM"));
        reclaimBtn.Click();

        Assert.True(_gatewayService.ReclaimCalled);
    }

    [Fact]
    public void OllamaRouterGatewayCard_WhenProviderIsOpenAiCompatible_RendersOpenAiFieldsAndHidesVramReclaim()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.Endpoint = "https://openrouter.ai/api/v1";
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Contains("OpenAI Compatible Gateway &amp; Models", cut.Markup);
        Assert.NotNull(cut.Find("#openai-endpoint-input"));
        Assert.NotNull(cut.Find("#openai-apikey-input"));
        Assert.NotNull(cut.Find("#openai-orgid-input"));
        Assert.DoesNotContain("Reclaim GPU VRAM", cut.Markup);
        Assert.DoesNotContain("Resident Models in GPU Memory", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_StandaloneOllama_RendersHostStatusAndReclaim()
    {
        _llmOptions.ProviderType = LlmProviderType.Ollama;
        _gatewayService.ProviderType = LlmProviderType.Ollama;

        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Contains("Standalone Ollama Gateway &amp; VRAM Inspector", cut.Markup);
        Assert.Contains("Host Inference Backend &amp; Health", cut.Markup);
        Assert.Contains("Reclaim GPU VRAM", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_ToggleApiKeyVisibility_ChangesInputType()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        var keyInput = cut.Find("#openai-apikey-input");
        Assert.Equal("password", keyInput.GetAttribute("type"));

        // Click Show button
        var toggleBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Show"));
        toggleBtn.Click();

        keyInput = cut.Find("#openai-apikey-input");
        Assert.Equal("text", keyInput.GetAttribute("type"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_WhenDiscoveryImplemented_DisplaysDiscoveredCountBadge()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _catalogService.VerificationResult = new EndpointVerificationResult(true, true, 24, [new("deepseek-chat", "DeepSeek Chat", ContextWindowTokens: 65536)]);

        var cut = Render<OllamaRouterGatewayCard>();

        var badge = cut.Find("#discovery-status-badge");
        Assert.Contains("Probed /api/v1/models: 24 models discovered automatically", badge.TextContent);
        Assert.Contains("Custom Model Overrides &amp; Explicit Restrictions", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_WhenDiscoveryNotImplemented_RendersMandatoryAllowedModels()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _catalogService.VerificationResult = new EndpointVerificationResult(false, false, 0, null, ErrorMessage: "/api/v1/models not implemented (HTTP 404)");

        var cut = Render<OllamaRouterGatewayCard>();

        var badge = cut.Find("#discovery-status-badge");
        Assert.Contains("/api/v1/models not implemented", badge.TextContent);
        Assert.Contains("Allowed Models &amp; Context Sizing (Mandatory)", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_WhenProbeFails_ShowsErrorAlert()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _catalogService.VerificationResult = new EndpointVerificationResult(
            IsSuccess: false,
            IsDiscoveryImplemented: true,
            DiscoveredCount: 0,
            ErrorMessage: "Connection refused to remote host");

        var cut = Render<OllamaRouterGatewayCard>();

        var badge = cut.Find("#discovery-status-badge");
        Assert.Contains("Connection or probe failed: Connection refused to remote host", badge.TextContent);
    }

    [Fact]
    public void OllamaRouterGatewayCard_TestConnection_CallsVerifyEndpointAsync()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        var testBtn = cut.Find("#btn-test-connection");
        testBtn.Click();

        Assert.Contains("discovery-status-badge", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_RoleDifferentiation_TogglesCoderAndArchitectDropdowns()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Empty(cut.FindAll("#select-coder-model"));
        Assert.Empty(cut.FindAll("#select-architect-model"));

        cut.Find("#chk-differentiate-roles").Change(true);

        Assert.NotNull(cut.Find("#select-coder-model"));
        Assert.NotNull(cut.Find("#select-architect-model"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_SaveSettings_InvokesLlmConfigurationWriter()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";

        var cut = Render<OllamaRouterGatewayCard>();

        var saveBtn = cut.Find("#btn-save-settings");
        saveBtn.Click();

        Assert.True(_configWriter.SaveProviderCalled);
        Assert.True(_configWriter.SaveAllowedModelsCalled);
        Assert.Equal(LlmProviderType.OpenAiCompatible, _configWriter.LastSavedOptions.ProviderType);
    }

    [Fact]
    public void OllamaRouterGatewayCard_SaveSettings_PersistsToConfigurationWriterWithRolesAndAllowedModels()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.Endpoint = "https://openrouter.ai/api/v1";
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        // Toggle role differentiation
        cut.Find("#chk-differentiate-roles").Change(true);

        cut.Find("#btn-save-settings").Click();

        Assert.True(_configWriter.SaveProviderCalled);
        Assert.True(_configWriter.SaveAllowedModelsCalled);
        Assert.Equal(LlmProviderType.OpenAiCompatible, _configWriter.LastSavedOptions.ProviderType);
    }

    [Fact]
    public void OllamaRouterGatewayCard_ProviderSwitching_UpdatesUI()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;

        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Contains("OllamaRouter Gateway &amp; VRAM Inspector", cut.Markup);

        // Click OpenAI Compatible radio
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[2].Change(new ChangeEventArgs { Value = "OpenAiCompatible" });

        Assert.Contains("OpenAI Compatible Gateway &amp; Models", cut.Markup);
        Assert.Contains("Restart required after saving to activate OpenAiCompatible", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_WhenAuthenticationFails_ShowsErrorAlertNot404Warning()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        // Real-world 401 response sets IsSuccess=false and IsDiscoveryImplemented=false
        _catalogService.VerificationResult = new EndpointVerificationResult(
            IsSuccess: false,
            IsDiscoveryImplemented: false,
            DiscoveredCount: 0,
            ErrorMessage: "Authentication failed (HTTP 401 Unauthorized). Verify your API key in Settings.");

        var cut = Render<OllamaRouterGatewayCard>();

        var badge = cut.Find("#discovery-status-badge");
        Assert.Contains("Authentication failed (HTTP 401 Unauthorized)", badge.TextContent);
        Assert.DoesNotContain("/api/v1/models not implemented", badge.TextContent);
        Assert.DoesNotContain("Allowed Models &amp; Context Sizing (Mandatory)", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_ProviderSwitching_SeparatesOllamaAndOpenAiEndpoints()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.Endpoint = "http://my-ollama-router:11434";
        _llmOptions.OpenAiCompatible.Endpoint = "https://api.deepseek.com/v1";
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;

        var cut = Render<OllamaRouterGatewayCard>();

        // In Ollama mode: check endpoint input
        var ollamaInput = cut.Find("input.forge-input.font-mono");
        Assert.Equal("http://my-ollama-router:11434", ollamaInput.GetAttribute("value"));

        // Switch to OpenAI Compatible
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[2].Change(new ChangeEventArgs { Value = "OpenAiCompatible" });

        var openAiInput = cut.Find("#openai-endpoint-input");
        Assert.Equal("https://api.deepseek.com/v1", openAiInput.GetAttribute("value"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_CoderAndArchitectDropdowns_UseAllowedModelsWhenDiscoveryUnavailable()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.AllowedModels =
        [
            new OpenAiAllowedModel { ModelId = "custom-deepseek", DisplayName = "Custom DeepSeek", ContextWindowTokens = 65536 }
        ];
        _catalogService.Models = [];
        _catalogService.VerificationResult = new EndpointVerificationResult(false, false, 0, null, ErrorMessage: "/api/v1/models not implemented (HTTP 404)");
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        // Toggle role differentiation
        cut.Find("#chk-differentiate-roles").Change(true);

        var coderSelect = cut.Find("#select-coder-model");
        Assert.Contains("Inherit Default Model", coderSelect.InnerHtml);
        Assert.Contains("custom-deepseek", coderSelect.InnerHtml);

        var architectSelect = cut.Find("#select-architect-model");
        Assert.Contains("Inherit Default Model", architectSelect.InnerHtml);
        Assert.Contains("custom-deepseek", architectSelect.InnerHtml);
    }

    [Fact]
    public void OllamaRouterGatewayCard_DefaultModelDropdown_IncludesAutoSelect_WhenUsingAllowedModels()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.AllowedModels =
        [
            new OpenAiAllowedModel { ModelId = "custom-qwen", DisplayName = "Custom Qwen", ContextWindowTokens = 131072 }
        ];
        _catalogService.Models = [];
        _catalogService.VerificationResult = new EndpointVerificationResult(false, false, 0, null, ErrorMessage: "/api/v1/models not implemented (HTTP 404)");
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<OllamaRouterGatewayCard>();

        var defaultSelect = cut.Find("#select-default-model");
        Assert.Contains("Auto-select (Strategy-based: lowest prompt cost)", defaultSelect.InnerHtml);
        Assert.Contains("custom-qwen", defaultSelect.InnerHtml);
    }

    [Fact]
    public void OllamaRouterGatewayCard_WriteOnlyKeyUx_NeverEchoesStoredKey_PreservesOnBlank_ReplacesAndClears()
    {
        const string initialSecret = "sk-secret-never-rendered-in-dom";
        const string rotatedSecret = "sk-rotated-openai-key-999";

        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;
        _secretStore.SeedOpenAiKey("https://api.openai.com/v1", initialSecret);

        var cut = Render<OllamaRouterGatewayCard>();

        // Stored key MUST never appear in DOM or input value
        Assert.DoesNotContain(initialSecret, cut.Markup);
        var keyInput = cut.Find("#openai-apikey-input");
        Assert.True(string.IsNullOrEmpty(keyInput.GetAttribute("value")));

        var badge = cut.Find("#openai-apikey-status-badge");
        Assert.Contains(ApiKeySettingsUiService.StoredInKeyringBadgeText, badge.TextContent);
        Assert.Contains("Pmad.KeySharp", cut.Markup);
        Assert.Contains("config/config.json", cut.Markup);

        // Save with blank input preserves existing keyring entry
        cut.Find("#btn-save-settings").Click();
        Assert.Equal(initialSecret, _secretStore.PeekRawOpenAiKey("https://api.openai.com/v1"));
        Assert.True(_configWriter.SaveProviderCalled);

        // Save with new non-empty input trims, stores in keyring, clears input, keeps badge
        cut.Find("#openai-apikey-input").Input($"  {rotatedSecret}  ");
        cut.Find("#btn-save-settings").Click();

        Assert.Equal(rotatedSecret, _secretStore.PeekRawOpenAiKey("https://api.openai.com/v1"));
        Assert.DoesNotContain(rotatedSecret, cut.Markup);
        Assert.True(string.IsNullOrEmpty(cut.Find("#openai-apikey-input").GetAttribute("value")));
        Assert.Contains(ApiKeySettingsUiService.StoredInKeyringBadgeText, cut.Find("#openai-apikey-status-badge").TextContent);

        // Explicit Clear Key removes entry from keyring and updates badge
        cut.Find("#btn-clear-openai-apikey").Click();
        Assert.Null(_secretStore.PeekRawOpenAiKey("https://api.openai.com/v1"));
        Assert.Contains(ApiKeySettingsUiService.NotConfiguredBadgeText, cut.Find("#openai-apikey-status-badge").TextContent);
        Assert.Empty(cut.FindAll("#btn-clear-openai-apikey"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_EditingEndpointRefreshesBadgeWithoutCopying_AndOllamaModeSkipsKeyring()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";
        _secretStore.SeedOpenAiKey("https://api.openai.com/v1", "sk-openai-only");

        var cut = Render<OllamaRouterGatewayCard>();

        // In OllamaRouter mode, OS keyring MUST NOT be queried
        Assert.Equal(0, _secretStore.OpenAiHasCalls);

        // Switch to OpenAI Compatible -> queries keyring for initial endpoint
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[2].Change(new ChangeEventArgs { Value = "OpenAiCompatible" });

        Assert.Equal(1, _secretStore.OpenAiHasCalls);
        Assert.Contains(ApiKeySettingsUiService.StoredInKeyringBadgeText, cut.Find("#openai-apikey-status-badge").TextContent);

        // Edit endpoint URL -> badge updates immediately for target endpoint, never copying key
        cut.Find("#openai-endpoint-input").Input("https://openrouter.ai/api/v1");
        Assert.Contains(ApiKeySettingsUiService.NotConfiguredBadgeText, cut.Find("#openai-apikey-status-badge").TextContent);
        Assert.Null(_secretStore.PeekRawOpenAiKey("https://openrouter.ai/api/v1"));
        Assert.Equal("sk-openai-only", _secretStore.PeekRawOpenAiKey("https://api.openai.com/v1"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_DisplaysInheritedEnvAndKeyringUnavailableBadges()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;
        _fakeEnvVars[ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable] = "sk-env-inherited";

        var cut = Render<OllamaRouterGatewayCard>();
        Assert.Contains(ApiKeySettingsUiService.InheritedFromOpenAiEnvBadgeText, cut.Find("#openai-apikey-status-badge").TextContent);
        Assert.DoesNotContain("sk-env-inherited", cut.Markup);

        _fakeEnvVars.Clear();
        _secretStore.KeyringAvailable = false;
        var unavailableCut = Render<OllamaRouterGatewayCard>();
        Assert.Contains(ApiKeySettingsUiService.KeyringUnavailableBadgeText, unavailableCut.Find("#openai-apikey-status-badge").TextContent);
    }

    [Fact]
    public void OllamaRouterGatewayCard_HandlesSecretStoreExceptionGracefully_AndRejectsInvalidEndpointOnSave()
    {
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.OpenAiCompatible.Endpoint = "https://api.openai.com/v1";
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;
        _secretStore.ThrowOnRead = new SecretStoreException("Keyring access denied on read");

        var errorCut = Render<OllamaRouterGatewayCard>();
        Assert.Contains(ApiKeySettingsUiService.KeyringErrorBadgeText, errorCut.Find("#openai-apikey-status-badge").TextContent);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Keyring access denied on read"));

        // Endpoint edit while keyring read fails surfaces error badge & toast without crashing
        errorCut.Find("#openai-endpoint-input").Input("https://openrouter.ai/api/v1");
        Assert.Contains(ApiKeySettingsUiService.KeyringErrorBadgeText, errorCut.Find("#openai-apikey-status-badge").TextContent);

        // Save and Clear errors surface error toasts without persisting config.json
        _secretStore.ThrowOnRead = null;
        _secretStore.SeedOpenAiKey("https://api.openai.com/v1", "sk-existing");
        var cut = Render<OllamaRouterGatewayCard>();

        _secretStore.ThrowOnWrite = new SecretStoreException("Keyring write failure");
        cut.Find("#openai-apikey-input").Input("sk-new-secret");
        cut.Find("#btn-save-settings").Click();
        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Keyring write failure"));

        _secretStore.ThrowOnWrite = null;
        _secretStore.ThrowOnDelete = new SecretStoreException("Keyring delete failure");
        cut.Find("#btn-clear-openai-apikey").Click();
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Keyring delete failure"));

        // Invalid endpoint URL on save -> shows error toast and never invokes ConfigurationWriter
        _secretStore.ThrowOnDelete = null;
        cut.Find("#openai-endpoint-input").Input("not-a-valid-uri");
        cut.Find("#btn-save-settings").Click();
        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Title == "Error Saving Settings");
    }

    [Fact]
    public void OllamaRouterGatewayCard_RendersProtocolShortcutsWithoutBlankTarget_AndHidesRemovedMonitorLink()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;

        var cut = Render<OllamaRouterGatewayCard>();

        var openAppLink = cut.Find("#link-open-ollamarouter-app");
        Assert.Equal(OllamaRouterProtocolUris.OpenApp, openAppLink.GetAttribute("href"));
        Assert.False(openAppLink.HasAttribute("target"));
        Assert.Contains("Open OllamaRouter App", openAppLink.TextContent);

        var wizardLink = cut.Find("#link-run-ollamarouter-wizard");
        Assert.Equal(OllamaRouterProtocolUris.SetupWizard, wizardLink.GetAttribute("href"));
        Assert.False(wizardLink.HasAttribute("target"));
        Assert.Contains("Run Router Setup Wizard", wizardLink.TextContent);

        Assert.NotNull(cut.Find("#btn-sync-recommended-models"));
        Assert.DoesNotContain("/monitor", cut.Markup);
        Assert.DoesNotContain("Open Router Monitor", cut.Markup);

        // Switching to Standalone Ollama hides OllamaRouter protocol links and sync button
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[0].Change(new ChangeEventArgs { Value = "Ollama" });
        Assert.Empty(cut.FindAll("#link-open-ollamarouter-app"));
        Assert.Empty(cut.FindAll("#link-run-ollamarouter-wizard"));
        Assert.Empty(cut.FindAll("#btn-sync-recommended-models"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_PopulatesRolesAndPersistsConfig()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.Endpoint = "http://localhost:11434";
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = new RouterRecommendationsDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            Roles: new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.Basic] = new("basic", "gemma4:e4b", 131072, "Fast auxiliary", []),
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "qwen3.8:27b", 262144, "Ideation", []),
                [OllamaRouterRoleNames.SoftwareArchitect] = new("software_architect", "qwen3.8:27b", 262144, "Architecture", []),
                [OllamaRouterRoleNames.SeniorDeveloper] = new("senior_developer", "qwen3.6:35b-a3b", 131072, "Implementation", [])
            });

        var cut = Render<OllamaRouterGatewayCard>();

        cut.Find("#btn-sync-recommended-models").Click();

        Assert.True(_configWriter.SaveProviderCalled);
        Assert.Equal(LlmProviderType.OllamaRouter, _configWriter.LastSavedOptions.ProviderType);
        Assert.Equal("qwen3.8:27b", _configWriter.LastSavedOptions.DefaultModelId);
        Assert.Equal("qwen3.8:27b", _configWriter.LastSavedOptions.ArchitectModelId);
        Assert.Equal("qwen3.6:35b-a3b", _configWriter.LastSavedOptions.CoderModelId);
        Assert.Equal(262144, _configWriter.LastSavedOptions.ContextWindowTokens);

        Assert.NotNull(cut.Find("#select-coder-model"));
        Assert.NotNull(cut.Find("#select-architect-model"));
        Assert.Equal("qwen3.8:27b", cut.Find("#select-default-model").GetAttribute("value"));
        Assert.Equal("qwen3.6:35b-a3b", cut.Find("#select-coder-model").GetAttribute("value"));
        Assert.Equal("qwen3.8:27b", cut.Find("#select-architect-model").GetAttribute("value"));

        Assert.Contains(
            _toastService.Toasts,
            t => t.Level == ToastLevel.Success &&
                 t.Title == "Recommended Models Synced" &&
                 t.Message.Contains("qwen3.8:27b") &&
                 t.Message.Contains("qwen3.6:35b-a3b"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_WhenUnreachableOrEmpty_ShowsWarningWithoutPersisting()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = null;

        var cut = Render<OllamaRouterGatewayCard>();
        cut.Find("#btn-sync-recommended-models").Click();

        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning && t.Title == "OllamaRouter Unreachable");

        _gatewayService.Recommendations = new RouterRecommendationsDto(null, new Dictionary<string, RouterRoleRecommendationDto>());
        cut.Find("#btn-sync-recommended-models").Click();

        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning && t.Title == "No Recommendations Available");
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_WhenActiveProviderNotOllamaRouter_ShowsRestartRequiredToast()
    {
        _llmOptions.ProviderType = LlmProviderType.Ollama;
        _gatewayService.ProviderType = LlmProviderType.Ollama;

        var cut = Render<OllamaRouterGatewayCard>();
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[1].Change(new ChangeEventArgs { Value = "OllamaRouter" });

        cut.Find("#btn-sync-recommended-models").Click();

        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning && t.Title == "Restart Required");
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_WhenPersistenceThrows_PreservesUiStateAndShowsErrorToast()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.DefaultModelId = "existing:latest";
        _llmOptions.ArchitectModelId = null;
        _llmOptions.CoderModelId = null;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = new RouterRecommendationsDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            Roles: new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "qwen3.8:27b", 262144, "Ideation", []),
                [OllamaRouterRoleNames.SoftwareArchitect] = new("software_architect", "qwen3.8:27b", 262144, "Architecture", []),
                [OllamaRouterRoleNames.SeniorDeveloper] = new("senior_developer", "qwen3.6:35b-a3b", 131072, "Implementation", [])
            });
        _configWriter.ThrowOnSaveProvider = new IOException("Disk full");

        var cut = Render<OllamaRouterGatewayCard>();
        cut.Find("#btn-sync-recommended-models").Click();

        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Title == "Recommendation Sync Error" && t.Message.Contains("Disk full"));
        Assert.Equal("existing:latest", cut.Find("#select-default-model").GetAttribute("value"));
        Assert.Empty(cut.FindAll("#select-coder-model"));
    }

    [Fact]
    public void OllamaRouterGatewayCard_InvalidOllamaEndpoint_BlocksBothSyncAndSaveWithoutPersisting()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = new RouterRecommendationsDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            Roles: new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "qwen3.8:27b", 262144, "Ideation", [])
            });

        var cut = Render<OllamaRouterGatewayCard>();
        cut.Find("#ollama-endpoint-input").Change("not-a-url");

        cut.Find("#btn-sync-recommended-models").Click();
        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Title == "Recommendation Sync Error");

        cut.Find("#btn-save-settings-ollama").Click();
        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Title == "Error Saving Settings");
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_WhenEndpointChangedWithoutRestart_ShowsRestartRequiredToast()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.Endpoint = "http://localhost:11434";
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = new RouterRecommendationsDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            Roles: new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "qwen3.8:27b", 262144, "Ideation", [])
            });

        var cut = Render<OllamaRouterGatewayCard>();
        cut.Find("#ollama-endpoint-input").Change("http://127.0.0.1:22434");

        cut.Find("#btn-sync-recommended-models").Click();

        Assert.False(_configWriter.SaveProviderCalled);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Warning && t.Title == "Restart Required");
    }

    [Fact]
    public void OllamaRouterGatewayCard_SyncRecommendedModels_WhenEndpointMatchesModuloTrailingSlash_SyncsNormally()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _llmOptions.Endpoint = "http://localhost:11434";
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.Recommendations = new RouterRecommendationsDto(
            GeneratedAt: DateTimeOffset.UtcNow,
            Roles: new Dictionary<string, RouterRoleRecommendationDto>
            {
                [OllamaRouterRoleNames.AgenticChat] = new("agentic_chat", "qwen3.8:27b", 262144, "Ideation", [])
            });

        var cut = Render<OllamaRouterGatewayCard>();
        cut.Find("#ollama-endpoint-input").Change(" http://localhost:11434/ ");

        cut.Find("#btn-sync-recommended-models").Click();

        Assert.True(_configWriter.SaveProviderCalled);
        Assert.Equal("http://localhost:11434", _configWriter.LastSavedOptions.Endpoint);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Success && t.Title == "Recommended Models Synced");
    }

    [Fact]
    public void OllamaRouterGatewayCard_RoleDifferentiation_SavesAndClearsForOllamaRouterAndOllama()
    {
        _llmOptions.ProviderType = LlmProviderType.OllamaRouter;
        _gatewayService.ProviderType = LlmProviderType.OllamaRouter;
        _catalogService.Models =
        [
            new AvailableModelInfo("qwen3.8:27b", "qwen3.8:27b", ContextWindowTokens: 262144),
            new AvailableModelInfo("qwen3.6:35b-a3b", "qwen3.6:35b-a3b", ContextWindowTokens: 131072)
        ];

        var cut = Render<OllamaRouterGatewayCard>();

        cut.Find("#chk-differentiate-roles").Change(true);
        cut.Find("#select-default-model").Change("qwen3.8:27b");
        cut.Find("#select-coder-model").Change("qwen3.6:35b-a3b");
        cut.Find("#select-architect-model").Change("qwen3.8:27b");
        cut.Find("#btn-save-settings-ollama").Click();

        Assert.True(_configWriter.SaveProviderCalled);
        Assert.Equal(LlmProviderType.OllamaRouter, _configWriter.LastSavedOptions.ProviderType);
        Assert.Equal("qwen3.8:27b", _configWriter.LastSavedOptions.DefaultModelId);
        Assert.Equal("qwen3.6:35b-a3b", _configWriter.LastSavedOptions.CoderModelId);
        Assert.Equal("qwen3.8:27b", _configWriter.LastSavedOptions.ArchitectModelId);

        // Unchecking clears role overrides
        cut.Find("#chk-differentiate-roles").Change(false);
        cut.Find("#btn-save-settings-ollama").Click();
        Assert.Null(_configWriter.LastSavedOptions.CoderModelId);
        Assert.Null(_configWriter.LastSavedOptions.ArchitectModelId);

        // Also works for Standalone Ollama
        var radioInputs = cut.FindAll("input[name=providerOption]");
        radioInputs[0].Change(new ChangeEventArgs { Value = "Ollama" });
        cut.Find("#chk-differentiate-roles").Change(true);
        cut.Find("#select-coder-model").Change("coder-local:latest");
        cut.Find("#select-architect-model").Change("architect-local:latest");
        cut.Find("#btn-save-settings-ollama").Click();

        Assert.Equal(LlmProviderType.Ollama, _configWriter.LastSavedOptions.ProviderType);
        Assert.Equal("coder-local:latest", _configWriter.LastSavedOptions.CoderModelId);
        Assert.Equal("architect-local:latest", _configWriter.LastSavedOptions.ArchitectModelId);
    }

    [Fact]
    public void OllamaRouterGatewayUiHelper_ExposesProtocolUrisAndFormatsSummary()
    {
        Assert.Equal("ollamarouter://open", OllamaRouterGatewayUiHelper.OpenAppUri);
        Assert.Equal("ollamarouter://wizard", OllamaRouterGatewayUiHelper.SetupWizardUri);

        var summary = OllamaRouterGatewayUiHelper.FormatSyncedModelsSummary(
            new NightCodeForge.Core.Inference.MappedRouterRecommendations(
                DefaultModelId: "qwen3.8:27b",
                ArchitectModelId: "qwen3.8:27b",
                CoderModelId: "qwen3.6:35b-a3b",
                ContextWindowTokens: 262144,
                GeneratedAt: DateTimeOffset.UtcNow));

        Assert.Contains("Architect: qwen3.8:27b", summary);
        Assert.Contains("Coder: qwen3.6:35b-a3b", summary);
        Assert.Contains("Default: qwen3.8:27b", summary);
        Assert.Contains("262,144 tokens", summary);
        Assert.Contains("config/config.json", summary);
    }

    #endregion

    #region AgentProfilesTab Tests

    [Fact]
    public void AgentProfilesTab_GlobalScope_RendersStandardProfilesAndGenerousTurns()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        Assert.Contains("Global Agent Reasoning Profiles", cut.Markup);
        Assert.Contains("Stage 0 Untrusted Intake Triage Agent", cut.Markup);
        Assert.Contains("Implementation Agent", cut.Markup);
        Assert.Contains("Code Review &amp; Targeted Fix Agent", cut.Markup);

        // Verify model options from catalog
        Assert.Contains("qwen2.5-coder:32b", cut.Markup);

        // Verify OllamaRouter target options
        Assert.Contains("Host GPU", cut.Markup);
        Assert.Contains("LAN GPU Server", cut.Markup);
        Assert.Contains("Cloud Overflow", cut.Markup);

        // Verify Temperature is empty by default with Model default placeholder
        var tempInputs = cut.FindAll("input[type=number]")
            .Where(i => i.GetAttribute("placeholder") == "Model default")
            .ToList();
        Assert.NotEmpty(tempInputs);
        Assert.All(tempInputs, input => Assert.True(string.IsNullOrEmpty(input.GetAttribute("value"))));
    }

    [Fact]
    public async Task AgentProfilesTab_WorkspaceScope_CreatesAndRevertsDedicatedProfiles()
    {
        const int workspaceId = 42;
        var cut = Render<AgentProfilesTab>(p => p
            .Add(x => x.Scope, AgentProfilesTab.ProfileScope.Workspace)
            .Add(x => x.WorkspaceId, workspaceId)
            .Add(x => x.WorkspaceName, "TestProject"));

        // Initial state: Inheriting global
        Assert.Contains("Global System Profiles", cut.Markup);
        var customizeBtn = cut.Find("button.forge-btn-primary");
        await cut.InvokeAsync(() => customizeBtn.Click());

        // After creation: dedicated profiles created
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var db = new AppDbContext(_dbOptions);
            var dedicated = await db.AgentProfiles.Where(x => x.WorkspaceId == workspaceId).ToListAsync();
            Assert.Equal(5, dedicated.Count);
            Assert.Contains(dedicated, d => d.Id.StartsWith($"ws-{workspaceId}:"));
        });

        cut.WaitForAssertion(() => Assert.Contains("Revert to Global Profiles", cut.Markup));

        // Click revert
        var revertBtn = cut.Find("button.forge-btn-outline");
        await cut.InvokeAsync(() => revertBtn.Click());

        // After revert: dedicated profiles deleted
        await cut.WaitForAssertionAsync(async () =>
        {
            await using var db = new AppDbContext(_dbOptions);
            var dedicated = await db.AgentProfiles.Where(x => x.WorkspaceId == workspaceId).ToListAsync();
            Assert.Empty(dedicated);
        });
    }

    [Fact]
    public void AgentProfilesTab_OverridesOptimizationFlags_ExposesCompleteParameters()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        // Click Override on the first profile
        var overrideBtn = cut.FindAll("button.forge-btn-outline").First(b => b.TextContent.Contains("Override"));
        overrideBtn.Click();

        // Verify boolean switches appear
        Assert.Contains("Complete Profile Optimization Overrides", cut.Markup);
        Assert.Contains("Guided Grammar Decoding", cut.Markup);
        Assert.Contains("Deterministic Symbol Map", cut.Markup);
        Assert.Contains("Smart CLI Output Filtering", cut.Markup);
        Assert.Contains("JIT Model Preload", cut.Markup);
        Assert.Contains("Synthetic Conversation Priming", cut.Markup);
    }

    [Fact]
    public void AgentProfilesTab_GlobalScope_SaveProfilesButtonIsEnabledAndSavesChanges()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        // Find "Save Profiles" button
        var saveBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Save Profiles"));
        Assert.NotNull(saveBtn);
        Assert.False(saveBtn.HasAttribute("disabled"), "Save Profiles button must be enabled in Global scope");

        // Find a MaxTurns input
        var maxTurnsInput = cut.Find("input[type='number']");
        Assert.NotNull(maxTurnsInput);
        maxTurnsInput.Change(750);

        // Click Save Profiles
        saveBtn.Click();

        // Verify in DB that MaxTurns was persisted
        using var db = new AppDbContext(_dbOptions);
        var updatedProfile = db.AgentProfiles.FirstOrDefault(p => p.WorkspaceId == null);
        Assert.NotNull(updatedProfile);
        Assert.Equal(750, updatedProfile.MaxTurns);
    }

    [Fact]
    public void AgentProfilesTab_CustomModelToggle_SwitchesBetweenDropdownAndInput()
    {
        // Add a mock model to catalog
        _catalogService.Models = new List<AvailableModelInfo>
        {
            new("llama3:8b", "llama3:8b", SizeBytes: 4_000_000_000)
        };

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        // Find "Enter custom name" button
        var customBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Enter custom name"));
        Assert.NotNull(customBtn);

        // Click it to switch to manual input
        customBtn.Click();

        // Verify button text changed to "Select from list"
        Assert.Contains("Select from list", cut.Markup);
    }

    [Fact]
    public void AgentProfilesTab_GlobalScope_ChangingProfileModel_PersistsToDatabase()
    {
        _catalogService.Models = new List<AvailableModelInfo>
        {
            new("model-a:latest", "model-a:latest", SizeBytes: 1024),
            new("model-b:latest", "model-b:latest", SizeBytes: 2048)
        };

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var modelSelect = cut.Find("select.font-mono");
        modelSelect.Change("model-b:latest");
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles"));
        saveBtn.Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new AppDbContext(_dbOptions);
            var updatedProfile = db.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == "code-review-fix");
            Assert.Equal("model-b:latest", updatedProfile.ModelName);

            var implProfile = db.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == "implementation");
            Assert.Equal("qwen2.5-coder:32b", implProfile.ModelName);
        });
    }

    [Fact]
    public void AgentProfilesTab_GlobalScope_CustomBudget_PreservedOnSave()
    {
        using (var dbInit = new AppDbContext(_dbOptions))
        {
            var impl = dbInit.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == StandardStageIds.Implementation);
            impl.ThinkingEffort = ThinkingEffort.CustomBudget;
            impl.ThinkingTokenBudget = 9999;
            dbInit.SaveChanges();
        }

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles"));
        saveBtn.Click();

        using var db = new AppDbContext(_dbOptions);
        var implAfter = db.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == StandardStageIds.Implementation);
        Assert.Equal(ThinkingEffort.CustomBudget, implAfter.ThinkingEffort);
        Assert.Equal(9999, implAfter.ThinkingTokenBudget);
    }

    [Fact]
    public void AgentProfilesTab_WhenOpenAiCompatible_DisplaysOpenAiInConnectionStrip()
    {
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;
        _llmOptions.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        Assert.Contains("OpenAI-Compatible Gateway connected", cut.Markup);
        Assert.DoesNotContain("OllamaRouter Target", cut.Markup);
    }

    [Fact]
    public void AgentProfilesTab_WhenOpenAiCompatible_HidesOllamaRouterTargetPinning()
    {
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        Assert.Empty(cut.FindAll("select option[value='Local']"));
        Assert.Empty(cut.FindAll("select option[value='Remote']"));
        Assert.Empty(cut.FindAll("select option[value='Cloud']"));
    }

    [Fact]
    public async Task AgentProfilesTab_WhenOpenAiCompatible_SaveProfiles_ResetsRoutingTargetToAuto()
    {
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles"));
        await cut.InvokeAsync(() => saveBtn.Click());

        await using var db = new AppDbContext(_dbOptions);
        var profiles = await db.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.All(profiles, p => Assert.Equal(OllamaRoutingTarget.Auto, p.RoutingTarget));
    }

    [Fact]
    public async Task AgentProfilesTab_DetectProvider_WhenMatchesConfiguredOpenAi_DisplaysVerifiedToast()
    {
        _gatewayService.ProviderType = LlmProviderType.OpenAiCompatible;

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var detectBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Detect"));
        await cut.InvokeAsync(() => detectBtn.Click());

        Assert.Contains("The endpoint matches the configured provider", _toastService.Toasts.LastOrDefault()?.Message ?? "");
    }

    [Fact]
    public void AgentProfilesTab_DisplaysThinkingCapabilityBadge_WhenModelSupportsReasoning()
    {
        _catalogService.Models =
        [
            new(
                ModelId: "qwen2.5-coder:32b",
                DisplayName: "qwen2.5-coder:32b",
                ContextWindowTokens: 131072,
                SupportsReasoning: true,
                ThinkingCapability: new ModelThinkingCapability(true, ["low", "medium", "xhigh"], false, "medium"))
        ];

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        Assert.Contains("🧠 Reasoning: low, medium, xhigh", cut.Markup);
    }

    [Fact]
    public void AgentProfilesTab_DisplaysAntigravityAcpStatus_AndOpensLoginModal()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var badge = cut.Find("#acp-status-badge");
        Assert.Contains("Antigravity ACP v1.5.3 Ready", badge.TextContent);
        Assert.Contains("Google AI Ultra", badge.TextContent);

        cut.Find("#btn-acp-login-hints").Click();
        var modal = cut.Find("#acp-login-modal");
        Assert.Contains("wsl -e ~/.local/bin/agy login", modal.TextContent);

        _acpCatalogService.Status = new AcpCatalogProviderStatus(
            KnownAcpCatalogIds.Antigravity,
            AcpHarnessCatalogService.AntigravityDisplayName,
            IsInstalled: true,
            IsAuthenticated: false,
            DetectedExecutablePath: "~/.local/bin/agy",
            ExecutionTargetDescription: "WSL2",
            ServerVersion: "1.5.3",
            AccountTierName: null,
            DiagnosticMessage: "Sign in required",
            AvailableModels: AcpHarnessCatalogService.DefaultAntigravityModels);

        cut.Find("#btn-refresh-acp").Click();
        Assert.Contains("Authentication Required", cut.Find("#acp-status-badge").TextContent);
    }

    [Fact]
    public void AgentProfilesTab_PipelineStagesAllowSwitchingToAcp_WhileTriageAndIdeationRemainPinnedToCrucible()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var triageCard = cut.Find("[data-profile-id='triage']");
        Assert.Contains("Harness: crucible (Pinned)", triageCard.TextContent);
        Assert.Empty(triageCard.QuerySelectorAll("select.harness-select"));

        var ideationCard = cut.Find("[data-profile-id='ideation']");
        Assert.Contains("Harness: crucible (Pinned)", ideationCard.TextContent);
        Assert.Empty(ideationCard.QuerySelectorAll("select.harness-select"));

        var implCard = cut.Find("[data-profile-id='implementation']");
        var harnessSelect = implCard.QuerySelector("select.harness-select");
        Assert.NotNull(harnessSelect);

        harnessSelect!.Change(StandardHarnessIds.Acp);

        var updatedImplCard = cut.Find("[data-profile-id='implementation']");
        Assert.Contains("External Antigravity ACP", updatedImplCard.TextContent);
        Assert.Contains("gemini-4-argon", updatedImplCard.InnerHtml);
        Assert.DoesNotContain("OllamaRouter Target", updatedImplCard.TextContent);

        var thinkingSelect = updatedImplCard.QuerySelectorAll("select")
            .First(s => s.InnerHtml.Contains("thought_level: high"));
        thinkingSelect.Change(nameof(ThinkingEffort.High));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles"));
        saveBtn.Click();

        using var db = new AppDbContext(_dbOptions);
        var implAfter = db.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == StandardStageIds.Implementation);
        Assert.Equal(StandardHarnessIds.Acp, implAfter.HarnessId);
        Assert.Equal("gemini-4-argon", implAfter.ModelName);
        Assert.Equal(ThinkingEffort.High, implAfter.ThinkingEffort);
    }

    [Fact]
    public void AgentProfileAcpPresentationHelper_EnforcesPinnedStages_FormatsBadges_AndSynchronizesHarnessFields()
    {
        Assert.False(AgentProfileAcpPresentationHelper.IsStageHarnessSelectable(StandardStageIds.Triage));
        Assert.False(AgentProfileAcpPresentationHelper.IsStageHarnessSelectable("ws-42:ideation"));
        Assert.True(AgentProfileAcpPresentationHelper.IsStageHarnessSelectable(StandardStageIds.Implementation));
        Assert.True(AgentProfileAcpPresentationHelper.IsStageHarnessSelectable("ws-42:task-review"));

        Assert.Contains("Not Detected", AgentProfileAcpPresentationHelper.FormatAntigravityBadge(null, false).Text);
        Assert.Contains("Probing", AgentProfileAcpPresentationHelper.FormatAntigravityBadge(null, true).Text);

        var globalImpl = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            Name = "Impl",
            HarnessId = StandardHarnessIds.Acp,
            HarnessConfigurationJson = "{\"executablePath\":\"custom-agy\"}",
            ModelName = "gemini-4-argon"
        };

        var cloned = AgentProfileAcpPresentationHelper.CloneToDedicatedProfile(globalImpl, 9, "MyRepo");
        Assert.Equal(StandardHarnessIds.Acp, cloned.HarnessId);
        Assert.Equal("{\"executablePath\":\"custom-agy\"}", cloned.HarnessConfigurationJson);
    }

    [Fact]
    public void AgentProfilesTab_SelectingAcpModelWithRestrictedThoughtLevels_AndSecurityTierNormalization()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var taskReviewCard = cut.Find("[data-profile-id='task-review']");
        var harnessSelect = taskReviewCard.QuerySelector("select.harness-select");
        Assert.NotNull(harnessSelect);

        harnessSelect!.Change(StandardHarnessIds.Acp);

        var updatedCard = cut.Find("[data-profile-id='task-review']");
        var modelSelect = updatedCard.QuerySelector("select.font-mono");
        Assert.NotNull(modelSelect);
        modelSelect!.Change("gpt-oss-120b-medium");

        var refreshedCard = cut.Find("[data-profile-id='task-review']");
        var thinkingSelect = refreshedCard.QuerySelectorAll("select")
            .First(s => s.InnerHtml.Contains("thought_level:"));
        var thinkingOptions = thinkingSelect.QuerySelectorAll("option");
        Assert.Single(thinkingOptions);
        Assert.Equal(nameof(ThinkingEffort.Medium), thinkingOptions[0].GetAttribute("value"));

        var securitySelect = refreshedCard.QuerySelectorAll("select")
            .First(s => s.InnerHtml.Contains("Standard (Full Sandbox)"));
        Assert.True(securitySelect.HasAttribute("disabled"));
        Assert.Empty(securitySelect.QuerySelectorAll("option[value='RestrictedReadOnly']"));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles"));
        saveBtn.Click();

        using var db = new AppDbContext(_dbOptions);
        var saved = db.AgentProfiles.First(p => p.WorkspaceId == null && p.Id == StandardStageIds.TaskReview);
        Assert.Equal(StandardHarnessIds.Acp, saved.HarnessId);
        Assert.Equal("gpt-oss-120b-medium", saved.ModelName);
        Assert.Equal(ThinkingEffort.Medium, saved.ThinkingEffort);
        Assert.Equal(SandboxSecurityTier.Standard, saved.SecurityTier);
    }

    [Fact]
    public async Task AppDbContext_EnsureDefaultGlobalAgentProfilesAsync_NormalizesLegacyPiRpcProfilesToCrucible()
    {
        await using (var db = new AppDbContext(_dbOptions))
        {
            var impl = await db.AgentProfiles.FirstAsync(p => p.Id == StandardStageIds.Implementation);
            impl.HarnessId = "PiRpc";
            await db.SaveChangesAsync();

            await db.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b");
        }

        await using (var verifyDb = new AppDbContext(_dbOptions))
        {
            var impl = await verifyDb.AgentProfiles.FirstAsync(p => p.Id == StandardStageIds.Implementation);
            Assert.Equal(StandardHarnessIds.Crucible, impl.HarnessId);
        }
    }

    #endregion

    #region GlobalOptimizationSettingsTab Tests

    [Fact]
    public void GlobalOptimizationSettingsTab_RendersSyntheticConversationPrimingSwitch()
    {
        var cut = Render<GlobalOptimizationSettingsTab>();
        Assert.Contains("Synthetic Conversation Priming", cut.Markup);
        Assert.Contains("chkSyntheticPriming", cut.Markup);
    }

    #endregion

    #region WorkspaceSettingsTab Tests

    [Fact]
    public void WorkspaceSettingsTab_RendersAndSavesWorkspaceParameters()
    {
        var workspace = new Workspace
        {
            Id = 10,
            Name = "AlphaRepo",
            RepositoryPath = @"C:\repos\AlphaRepo",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost,
            Ext4MirrorPath = @"/home/user/mirrors/alpha"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<WorkspaceSettingsTab>();

        Assert.Contains("Repository &amp; Execution Confinement", cut.Markup);
        Assert.Contains("AlphaRepo", cut.Markup);
        Assert.Contains(@"C:\repos\AlphaRepo", cut.Markup);
        Assert.Contains("WSL2 Native Environment", cut.Markup);
        Assert.Contains("Git Ext4 Mirror Synchronization", cut.Markup);

        // Save
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Workspace Settings"));
        saveBtn.Click();

        // Sync mirror
        var syncBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Sync Ext4 Mirror Now"));
        syncBtn.Click();

        Assert.True(_mirrorService.FetchCalled);
    }

    #endregion

    #region WebSearchSettingsTab Tests

    [Fact]
    public void WebSearchSettingsTab_PastingApiKey_EnablesTestButtonAndAllowsSaving()
    {
        _fakeEnvVars.Clear();
        _secretStore.SeedOllamaKey(null);
        _webSearchSettingsService.CurrentSettings = new WebSearchSettings();

        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        // Find the API key input (password input) — must start empty
        var apiKeyInput = cut.Find("#websearch-apikey-input");
        Assert.Equal("password", apiKeyInput.GetAttribute("type"));
        Assert.True(string.IsNullOrEmpty(apiKeyInput.GetAttribute("value")));
        Assert.Contains(ApiKeySettingsUiService.NotConfiguredBadgeText, cut.Find("#websearch-apikey-status-badge").TextContent);
        Assert.Contains("Pmad.KeySharp", cut.Markup);
        Assert.Contains("SQLite", cut.Markup);

        // Save button in header should be enabled (not disabled)
        var saveBtn = cut.Find("button.forge-btn-primary");
        Assert.False(saveBtn.HasAttribute("disabled"));

        // Test button should initially be disabled because no API key is configured
        var testBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Test Search Connection"));
        Assert.NotNull(testBtn);
        Assert.True(testBtn.HasAttribute("disabled"));

        // Simulate pasting / typing an API key
        apiKeyInput.Input("  ollama_pasted_key_12345  ");

        // Now the test button should be enabled!
        testBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Test Search Connection"));
        Assert.NotNull(testBtn);
        Assert.False(testBtn.HasAttribute("disabled"));

        // Click save button -> stores trimmed key in OS keyring, clears input, updates badge
        saveBtn.Click();

        Assert.Equal("ollama_pasted_key_12345", _secretStore.PeekRawOllamaKey());
        Assert.DoesNotContain("ollama_pasted_key_12345", cut.Markup);
        Assert.True(string.IsNullOrEmpty(cut.Find("#websearch-apikey-input").GetAttribute("value")));
        Assert.Contains(ApiKeySettingsUiService.StoredInKeyringBadgeText, cut.Find("#websearch-apikey-status-badge").TextContent);
    }

    [Fact]
    public void WebSearchSettingsTab_WriteOnlyPreserveOnBlank_ExplicitClear_AndInheritedEnvTestEnablement()
    {
        const string storedKey = "ollama_stored_keyring_secret_777";
        _secretStore.SeedOllamaKey(storedKey);

        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        // Stored key must never be echoed into DOM
        Assert.DoesNotContain(storedKey, cut.Markup);
        Assert.True(string.IsNullOrEmpty(cut.Find("#websearch-apikey-input").GetAttribute("value")));
        Assert.Contains(ApiKeySettingsUiService.StoredInKeyringBadgeText, cut.Find("#websearch-apikey-status-badge").TextContent);

        // Test Search Connection enabled when keyring key exists
        var testBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Test Search Connection"));
        Assert.False(testBtn.HasAttribute("disabled"));

        // Save with blank input preserves stored keyring entry
        cut.Find("button.forge-btn-primary").Click();
        Assert.Equal(storedKey, _secretStore.PeekRawOllamaKey());

        // Clear Key deletes entry from keyring and disables Test button
        cut.Find("#btn-clear-websearch-apikey").Click();
        Assert.Null(_secretStore.PeekRawOllamaKey());
        Assert.Contains(ApiKeySettingsUiService.NotConfiguredBadgeText, cut.Find("#websearch-apikey-status-badge").TextContent);
        testBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Test Search Connection"));
        Assert.True(testBtn.HasAttribute("disabled"));

        // Inherited from OLLAMA_API_KEY env var shows badge without echoing key and enables Test button
        _fakeEnvVars[ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable] = "ollama_env_secret_888";
        var envCut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();
        Assert.Contains(ApiKeySettingsUiService.InheritedFromOllamaEnvBadgeText, envCut.Find("#websearch-apikey-status-badge").TextContent);
        Assert.DoesNotContain("ollama_env_secret_888", envCut.Markup);
        var envTestBtn = envCut.FindAll("button").First(b => b.TextContent.Contains("Test Search Connection"));
        Assert.False(envTestBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void WebSearchSettingsTab_AirGappedMode_RendersWarningBannerAndDisablesControls()
    {
        var secDescriptor = Services.First(s => s.ServiceType == typeof(Microsoft.Extensions.Options.IOptions<NightCodeForge.Core.Security.SecurityOptions>));
        Services.Remove(secDescriptor);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Security.SecurityOptions { AirGappedMode = true }));

        _secretStore.SeedOllamaKey("ollama_key_123");

        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        // Verify Warning Banner is displayed with exact text
        Assert.Contains("🛡️ Air-Gapped Mode Active: External web search via ollama.com is currently deactivated to prevent external network traffic.", cut.Markup);

        // Header status badge indicates Air-Gapped
        Assert.Contains("🛡️ Search Deactivated (Air-Gapped)", cut.Markup);
        Assert.DoesNotContain("Reset to Default", cut.Markup);

        // Save & Reset buttons should be disabled and have explanatory tooltips
        var buttons = cut.FindAll("button");
        var saveBtn = buttons.First(b => b.TextContent.Contains("Save Search Settings"));
        var resetBtn = buttons.First(b => b.TextContent.Contains("Reset Defaults"));
        Assert.True(saveBtn.HasAttribute("disabled"));
        Assert.True(resetBtn.HasAttribute("disabled"));
        Assert.Equal("Configuration cannot be saved in Air-Gapped mode.", saveBtn.GetAttribute("title"));
        Assert.Equal("Configuration cannot be reset in Air-Gapped mode.", resetBtn.GetAttribute("title"));

        // Show/Hide and Clear API Key buttons should also be disabled with tooltips
        var showBtn = buttons.First(b => b.TextContent.Contains("Show") || b.TextContent.Contains("Hide"));
        var clearBtn = cut.Find("#btn-clear-websearch-apikey");
        Assert.True(showBtn.HasAttribute("disabled"));
        Assert.True(clearBtn.HasAttribute("disabled"));
        Assert.Equal("API key configuration is disabled in Air-Gapped mode.", showBtn.GetAttribute("title"));
        Assert.Equal("API key configuration is disabled in Air-Gapped mode.", clearBtn.GetAttribute("title"));

        // All inputs should be disabled
        var inputs = cut.FindAll("input");
        foreach (var input in inputs)
        {
            Assert.True(input.HasAttribute("disabled"), $"Input element should be disabled in air-gapped mode: {input.OuterHtml}");
        }

        // Test connection button should be disabled with tooltip
        var testBtn = buttons.First(b => b.TextContent.Contains("Test Search Connection"));
        Assert.True(testBtn.HasAttribute("disabled"));
        Assert.Equal("Search testing is disabled in Air-Gapped mode.", testBtn.GetAttribute("title"));
    }

    [Fact]
    public void WebSearchSettingsTab_AirGappedViaWebSearchService_RendersWarningBannerAndDisablesControls()
    {
        var searchDescriptor = Services.First(s => s.ServiceType == typeof(NightCodeForge.Agents.Tools.IWebSearchService));
        Services.Remove(searchDescriptor);
        Services.AddSingleton<NightCodeForge.Agents.Tools.IWebSearchService>(
            new NightCodeForge.Agents.Tools.MockWebSearchService(
                isConfigured: true,
                isEnabled: false,
                disabledReason: "Web search is disabled in Air-Gapped mode (external cloud search to ollama.com is prohibited)."));

        _secretStore.SeedOllamaKey("ollama_key_123");

        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        Assert.Contains("🛡️ Air-Gapped Mode Active: External web search via ollama.com is currently deactivated to prevent external network traffic.", cut.Markup);
        Assert.Contains("🛡️ Search Deactivated (Air-Gapped)", cut.Markup);

        var buttons = cut.FindAll("button");
        var saveBtn = buttons.First(b => b.TextContent.Contains("Save Search Settings"));
        Assert.True(saveBtn.HasAttribute("disabled"));
    }

    [Fact]
    public void WebSearchSettingsTab_HandlesSecretStoreExceptionGracefully_OnRenderSaveAndClear()
    {
        _secretStore.ThrowOnRead = new SecretStoreException("Ollama keyring read denied");
        var errorCut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        Assert.Contains(ApiKeySettingsUiService.KeyringErrorBadgeText, errorCut.Find("#websearch-apikey-status-badge").TextContent);
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Ollama keyring read denied"));

        _secretStore.ThrowOnRead = null;
        _secretStore.SeedOllamaKey("ollama-existing");
        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        _secretStore.ThrowOnWrite = new SecretStoreException("Ollama keyring write failed");
        cut.Find("#websearch-apikey-input").Input("ollama-new-key");
        cut.Find("button.forge-btn-primary").Click();
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Ollama keyring write failed"));

        _secretStore.ThrowOnWrite = null;
        _secretStore.ThrowOnDelete = new SecretStoreException("Ollama keyring delete failed");
        cut.Find("#btn-clear-websearch-apikey").Click();
        Assert.Contains(_toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Ollama keyring delete failed"));
    }

    #endregion

    [Fact]
    public void AgentProfilesTab_DisplaysVisionCapabilityBadgeForVisionModels()
    {
        _catalogService.Models =
        [
            new AvailableModelInfo("qwen3-vl:8b", "qwen3-vl:8b", ContextWindowTokens: 131072, SupportsVision: true),
            new AvailableModelInfo("qwen2.5-coder:32b", "qwen2.5-coder:32b", ContextWindowTokens: 65536, SupportsVision: false)
        ];

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        Assert.Contains(VisionConstants.VisionBadgeLabel, cut.Markup);
        var modelSelect = cut.Find("[data-profile-id='ideation'] select");
        modelSelect.Change("qwen3-vl:8b");
        Assert.NotEmpty(cut.FindAll(".vision-capability-badge"));
    }

    #region Test Mocks

    private class MockGlobalOptimizationSettingsService : IGlobalOptimizationSettingsService
    {
        private LocalOptimizationFlags _flags = new();
        public LocalOptimizationFlags GetFlags() => _flags;
        public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_flags);
        public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
        {
            _flags = flags;
            OnFlagsChanged?.Invoke(flags);
            return Task.CompletedTask;
        }
        public event Action<LocalOptimizationFlags>? OnFlagsChanged;
    }

    private class MockModelCatalogService : ILlmModelCatalogService
    {
        public List<AvailableModelInfo> Models { get; set; } = new()
        {
            new("qwen2.5-coder:32b", "qwen2.5-coder:32b", SizeBytes: 19800000000)
        };

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public EndpointVerificationResult VerificationResult { get; set; } = new(true, true, 1, [new("qwen2.5-coder:32b", "qwen2.5-coder:32b", ContextWindowTokens: 131072)]);

        public Task<AvailableModelInfo?> GetDefaultModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Models.FirstOrDefault());

        public Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(VerificationResult);

        public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AvailableModelInfo>>(Models);
        }
    }

    private class MockLlmConfigurationWriter : ILlmConfigurationWriter
    {
        public bool SaveProviderCalled { get; private set; }
        public bool SaveAllowedModelsCalled { get; private set; }
        public Exception? ThrowOnSaveProvider { get; set; }
        public LlmProviderOptions LastSavedOptions { get; private set; } = new();
        public List<OpenAiAllowedModel> LastSavedAllowedModels { get; private set; } = [];

        public Task SaveProviderConfigurationAsync(Action<LlmProviderOptions> updateAction, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSaveProvider is not null)
            {
                throw ThrowOnSaveProvider;
            }

            SaveProviderCalled = true;
            updateAction(LastSavedOptions);
            return Task.CompletedTask;
        }

        public Task SaveAllowedModelsAsync(IEnumerable<OpenAiAllowedModel> allowedModels, CancellationToken cancellationToken = default)
        {
            SaveAllowedModelsCalled = true;
            LastSavedAllowedModels = allowedModels.ToList();
            return Task.CompletedTask;
        }
    }

    private class MockHostSdkDiscoveryService : IHostSdkDiscoveryService
    {
        public IReadOnlyList<string> DiscoverSdkMountPaths(string? distribution = null) => Array.Empty<string>();
        public HostSdkDiscoveryDetails DiscoverSdkDetails(string? distribution = null) => new(Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        public Task<IReadOnlyList<string>> DiscoverSdkMountPathsAsync(string? distribution = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<HostSdkDiscoveryDetails> DiscoverSdkDetailsAsync(string? distribution = null, CancellationToken cancellationToken = default) => Task.FromResult(new HostSdkDiscoveryDetails(Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>()));
        public void ClearCache() { }
    }

    private class MockOllamaRouterGatewayService : ILlmGatewayService
    {
        public bool ReclaimCalled { get; private set; }

        public LlmProviderType ProviderType { get; set; } = LlmProviderType.OllamaRouter;

        public RouterRecommendationsDto? Recommendations { get; set; }

        public Task<LlmProviderType?> DetectProviderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmProviderType?>(ProviderType);

        public Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
        {
            var targets = new List<FleetTargetStatus>
            {
                new("Local GPU Target", "http://localhost:11434", true, FleetLiveness.Online, 15, new FleetTargetHardware("Desktop", "Nvidia", ["GPU"], 16384, 2), new FleetTargetExecution(0, null, null)),
                new("Remote Server Target", null, true, FleetLiveness.Probing, 20, new FleetTargetHardware("Server", "Nvidia", ["GPU"], 98304, 4), new FleetTargetExecution(0, null, null)),
                new("Cloud Overflow Target", null, true, FleetLiveness.Online, 40, new FleetTargetHardware(null, FleetTargetHardware.UnknownGpuVendor, [], null, 8), new FleetTargetExecution(0, null, null))
            };
            return Task.FromResult(new GatewayStatusReport(true, LlmProviderType.OllamaRouter, "http://localhost:11434", "1.0.0", targets));
        }

        public Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ResidentModelInfo> list = new List<ResidentModelInfo>
            {
                new("qwen2.5-coder:32b", "qwen2.5-coder:32b", 19800000000, 14200000000, DateTimeOffset.UtcNow.AddMinutes(15))
            };
            return Task.FromResult(list);
        }

        public Task<int> ResolveContextWindowAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(131072);

        public Task<ModelThinkingCapability?> ResolveThinkingCapabilityAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelThinkingCapability?>(null);

        public Task<bool> ResolveVisionCapabilityAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default) =>
            ReclaimVramAsync(null, cancellationToken);

        public Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default)
        {
            ReclaimCalled = true;
            return Task.FromResult(true);
        }

        public Task<RouterSessionUsage?> GetSessionUsageAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RouterSessionUsage?>(null);

        public Task<bool> ReleaseSessionAffinityAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<RouterRecommendationsDto?> GetRecommendationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Recommendations);
    }

    private class MockWslMetadataService : IWslMetadataService
    {
        public string GetDefaultDistributionName() => "Ubuntu";
        public string GetDefaultUserName() => "julien";
        public void ClearCache() { }
    }

    private class MockGitMirrorSyncService : IGitMirrorSyncService
    {
        public bool FetchCalled { get; private set; }
        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult("/mirrors/test");
        public Task PullFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            return Task.CompletedTask;
        }
        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, "main", null));
        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, "main", null));
    }

    private class MockWebSearchSettingsService : IWebSearchSettingsService
    {
        public WebSearchSettings CurrentSettings { get; set; } = new();
        public event Action<WebSearchSettings>? OnSettingsChanged;

        public WebSearchSettings GetSettings() => CurrentSettings.Clone();
        public Task<WebSearchSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(GetSettings());
        public Task UpdateSettingsAsync(WebSearchSettings settings, CancellationToken cancellationToken = default)
        {
            CurrentSettings = settings.Clone();
            OnSettingsChanged?.Invoke(CurrentSettings);
            return Task.CompletedTask;
        }
    }

    private class MockAcpHarnessCatalogService : IAcpHarnessCatalogService
    {
        public AcpCatalogProviderStatus Status { get; set; } = new(
            KnownAcpCatalogIds.Antigravity,
            AcpHarnessCatalogService.AntigravityDisplayName,
            IsInstalled: true,
            IsAuthenticated: true,
            DetectedExecutablePath: "~/.local/bin/agy",
            ExecutionTargetDescription: "WSL2 + Container",
            ServerVersion: "1.5.3",
            AccountTierName: "Google AI Ultra",
            DiagnosticMessage: null,
            AvailableModels: AcpHarnessCatalogService.DefaultAntigravityModels);

        public Task<IReadOnlyList<AcpCatalogProviderStatus>> GetCatalogStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AcpCatalogProviderStatus>>([Status]);

        public Task<AcpCatalogProviderStatus> ProbeProviderAsync(string catalogId, bool forceRefresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Status);
    }

    #endregion
}
