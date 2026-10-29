using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Components.Workspaces;
using NightCodeForge.Web.Services;

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

        Services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        Services.AddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        Services.AddScoped<IWorkflowRepository, EfCoreWorkflowRepository>();
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IGlobalOptimizationSettingsService>(_optService);
        Services.AddSingleton<IWebSearchSettingsService>(_webSearchSettingsService);
        Services.AddSingleton<NightCodeForge.Agents.Tools.IWebSearchService>(_webSearchService);
        Services.AddSingleton<ILlmModelCatalogService>(_catalogService);
        Services.AddSingleton<IHostSdkDiscoveryService>(_sdkService);
        Services.AddSingleton<IOllamaRouterGatewayService>(_gatewayService);
        Services.AddSingleton<IWslMetadataService>(_wslService);
        Services.AddSingleton<IGitMirrorSyncService>(_mirrorService);
        Services.AddMemoryCache();
        Services.AddSingleton<ILlmContextWindowCache, NightCodeForge.Infrastructure.Inference.LlmContextWindowCache>();
    }

    public new void Dispose()
    {
        _connection.Dispose();
        base.Dispose();
    }

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
    public void SettingsPage_GlobalScope_RendersSixGlobalTabs_ExcludesABTesting()
    {
        var cut = Render<Settings>();

        var globalTabs = cut.FindAll(".settings-tabs button");
        Assert.Equal(6, globalTabs.Count);
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Local LLM Optimizations"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Agent Profiles"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Workflows & Stages"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Host SDK Inspector"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("OllamaRouter Gateway"));
        Assert.Contains(globalTabs, b => b.TextContent.Contains("Cloud & Web Search"));
        Assert.DoesNotContain(globalTabs, b => b.TextContent.Contains("A/B Testing"));
    }

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
        Assert.Contains("Structured Diagnostics", cut.Markup);
        Assert.Contains("JIT Model Preload", cut.Markup);
        Assert.Contains("Synthetic Conversation Priming", cut.Markup);
    }

    [Fact]
    public void GlobalOptimizationSettingsTab_RendersSyntheticConversationPrimingSwitch()
    {
        var cut = Render<GlobalOptimizationSettingsTab>();
        Assert.Contains("Synthetic Conversation Priming", cut.Markup);
        Assert.Contains("chkSyntheticPriming", cut.Markup);
    }

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

        public Task<AvailableModelInfo?> GetDefaultModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Models.FirstOrDefault());

        public Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new EndpointVerificationResult(true, true, Models.Count, Models));

        public Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AvailableModelInfo>>(Models);
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

    private class MockOllamaRouterGatewayService : IOllamaRouterGatewayService
    {
        public bool ReclaimCalled { get; private set; }

        public LlmProviderType ProviderType => LlmProviderType.OllamaRouter;

        public Task<LlmProviderType?> DetectProviderAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmProviderType?>(LlmProviderType.OllamaRouter);

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

        public Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default) =>
            ReclaimVramAsync(null, cancellationToken);

        public Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default)
        {
            ReclaimCalled = true;
            return Task.FromResult(true);
        }
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
    public void WebSearchSettingsTab_PastingApiKey_EnablesTestButtonAndAllowsSaving()
    {
        // Ensure no env key or settings key
        Environment.SetEnvironmentVariable("OLLAMA_API_KEY", null);
        _webSearchSettingsService.CurrentSettings = new WebSearchSettings { ApiKey = "" };

        var cut = Render<NightCodeForge.Web.Components.Settings.WebSearchSettingsTab>();

        // Find the API key input (password input)
        var apiKeyInput = cut.Find("input[type='password']");
        Assert.NotNull(apiKeyInput);

        // Save button in header should be enabled (not disabled)
        var saveBtn = cut.Find("button.forge-btn-primary");
        Assert.False(saveBtn.HasAttribute("disabled"));

        // Test button should initially be disabled because no API key is configured
        var testBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Test Search Connection"));
        Assert.NotNull(testBtn);
        Assert.True(testBtn.HasAttribute("disabled"));

        // Simulate pasting / typing an API key
        apiKeyInput.Input("ollama_pasted_key_12345");

        // Now the test button should be enabled!
        testBtn = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Test Search Connection"));
        Assert.NotNull(testBtn);
        Assert.False(testBtn.HasAttribute("disabled"));

        // Click save button
        saveBtn.Click();

        // Verify settings were persisted
        Assert.Equal("ollama_pasted_key_12345", _webSearchSettingsService.CurrentSettings.ApiKey);
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

    #endregion
}
