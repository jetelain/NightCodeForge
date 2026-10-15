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
            initDb.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b").GetAwaiter().GetResult();
        }

        _workspaceState = new WorkspaceStateService();
        _toastService = new ToastService();
        _optService = new MockGlobalOptimizationSettingsService();
        _catalogService = new MockModelCatalogService();
        _sdkService = new MockHostSdkDiscoveryService();
        _gatewayService = new MockOllamaRouterGatewayService();
        _wslService = new MockWslMetadataService();
        _mirrorService = new MockGitMirrorSyncService();

        Services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        Services.AddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IGlobalOptimizationSettingsService>(_optService);
        Services.AddSingleton<IOllamaModelCatalogService>(_catalogService);
        Services.AddSingleton<IHostSdkDiscoveryService>(_sdkService);
        Services.AddSingleton<IOllamaRouterGatewayService>(_gatewayService);
        Services.AddSingleton<IWslMetadataService>(_wslService);
        Services.AddSingleton<IGitMirrorSyncService>(_mirrorService);
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
        Assert.Contains("Local GPU Only", cut.Markup);
        Assert.Contains("Remote Server", cut.Markup);
        Assert.Contains("Cloud Overflow", cut.Markup);
    }

    [Fact]
    public async Task AgentProfilesTab_WorkspaceScope_CreatesAndRevertsDedicatedProfiles()
    {
        const int workspaceId = 42;
        await using (var db = new AppDbContext(_dbOptions))
        {
            db.Workspaces.Add(new Workspace
            {
                Id = workspaceId,
                Name = "TestProject",
                RepositoryPath = @"C:\repos\TestProject",
                DefaultBaseBranch = "main"
            });
            await db.SaveChangesAsync();
        }

        var cut = Render<AgentProfilesTab>(p => p
            .Add(x => x.Scope, AgentProfilesTab.ProfileScope.Workspace)
            .Add(x => x.WorkspaceId, workspaceId)
            .Add(x => x.WorkspaceName, "TestProject"));

        // Initial state: Inheriting global
        Assert.Contains("inherits the <strong>Global System Profiles</strong>", cut.Markup);
        var customizeBtn = cut.FindAll("button.forge-btn-primary").First(b => b.TextContent.Contains("Dedicated") || b.TextContent.Contains("Customize"));
        customizeBtn.Click();

        // After creation: dedicated profiles created
        await using (var db = new AppDbContext(_dbOptions))
        {
            var dedicated = await db.AgentProfiles.Where(x => x.WorkspaceId == workspaceId).ToListAsync();
            Assert.Equal(5, dedicated.Count);
            Assert.Contains(dedicated, d => d.Id.StartsWith($"ws-{workspaceId}:"));
        }

        cut.Render();
        Assert.Contains("Revert to Global Profiles", cut.Markup);

        // Click revert
        var revertBtn = cut.Find("button.forge-btn-outline");
        revertBtn.Click();

        // After revert: dedicated profiles deleted
        await using (var db = new AppDbContext(_dbOptions))
        {
            var dedicated = await db.AgentProfiles.Where(x => x.WorkspaceId == workspaceId).ToListAsync();
            Assert.Empty(dedicated);
        }
    }

    [Fact]
    public void AgentProfilesTab_OverridesOptimizationFlags_ExposesComplete12Parameters()
    {
        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        // Click Override on the first profile
        var overrideBtn = cut.FindAll("button.forge-btn-outline").First(b => b.TextContent.Contains("Override"));
        overrideBtn.Click();

        // Verify all 8 boolean switches and 4 numeric fields appear
        Assert.Contains("Complete Profile Optimization Overrides", cut.Markup);
        Assert.Contains("Thinking Trace Compaction", cut.Markup);
        Assert.Contains("Tool Result Compaction", cut.Markup);
        Assert.Contains("Loop Circuit Breaker", cut.Markup);
        Assert.Contains("Guided Grammar Decoding", cut.Markup);
        Assert.Contains("Deterministic Symbol Map", cut.Markup);
        Assert.Contains("Structured Diagnostics", cut.Markup);
        Assert.Contains("Prefix Stabilization", cut.Markup);
        Assert.Contains("JIT Model Preload", cut.Markup);
        Assert.Contains("Verbatim Thought Turns", cut.Markup);
        Assert.Contains("Verbatim Tool Results", cut.Markup);
        Assert.Contains("Tool Max Char Length", cut.Markup);
        Assert.Contains("Tool Truncate Char Length", cut.Markup);
    }

    [Fact]
    public void OllamaRouterGatewayCard_RendersTargetStatusAndTriggersReclaim()
    {
        var cut = Render<OllamaRouterGatewayCard>();

        Assert.Contains("OllamaRouter Gateway &amp; VRAM Inspector", cut.Markup);
        Assert.Contains("🟢 Gateway Connected", cut.Markup);
        Assert.Contains("OllamaRouter Active", cut.Markup);
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
        public event Action<LocalOptimizationFlags>? OnFlagsChanged;

        public LocalOptimizationFlags GetFlags() => _flags;
        public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_flags);
        public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
        {
            _flags = flags;
            OnFlagsChanged?.Invoke(_flags);
            return Task.CompletedTask;
        }
    }

    private class MockModelCatalogService : IOllamaModelCatalogService
    {
        public Task<IReadOnlyList<OllamaModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<OllamaModelInfo> list = new List<OllamaModelInfo>
            {
                new("qwen2.5-coder:32b", "qwen2.5-coder:32b", 19800000000, DateTime.UtcNow, "sha256:abc")
            };
            return Task.FromResult(list);
        }
    }

    private class MockHostSdkDiscoveryService : IHostSdkDiscoveryService
    {
        public IReadOnlyList<string> DiscoverSdkMountPaths(string? distribution = null) => Array.Empty<string>();
        public HostSdkDiscoveryDetails DiscoverSdkDetails(string? distribution = null) =>
            new(Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        public Task<IReadOnlyList<string>> DiscoverSdkMountPathsAsync(string? distribution = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<HostSdkDiscoveryDetails> DiscoverSdkDetailsAsync(string? distribution = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new HostSdkDiscoveryDetails(Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>()));
        public void ClearCache() { }
    }

    private class MockOllamaRouterGatewayService : IOllamaRouterGatewayService
    {
        public bool ReclaimCalled { get; private set; }

        public Task<OllamaRouterStatus> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
        {
            var targets = new List<TargetBackendStatus>
            {
                new("Local GPU Target", TargetBackendState.Active, "Local"),
                new("Remote Server Target", TargetBackendState.Available, "Remote"),
                new("Cloud Overflow Target", TargetBackendState.Active, "Cloud")
            };
            return Task.FromResult(new OllamaRouterStatus(true, true, "http://localhost:11434", targets));
        }

        public Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ResidentModelInfo> list = new List<ResidentModelInfo>
            {
                new("qwen2.5-coder:32b", "qwen2.5-coder:32b", 19800000000, 14200000000, DateTimeOffset.UtcNow.AddMinutes(15))
            };
            return Task.FromResult(list);
        }

        public Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default)
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
        public Task FetchFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
        {
            FetchCalled = true;
            return Task.CompletedTask;
        }
        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, "main", null));
        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, "main", null));
    }

    #endregion
}
