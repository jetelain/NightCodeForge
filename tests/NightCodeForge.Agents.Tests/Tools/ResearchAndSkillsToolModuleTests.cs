using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public sealed class ResearchAndSkillsToolModuleTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly ISandboxPathResolver _pathResolver;

    public ResearchAndSkillsToolModuleTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"ncf-researchmodule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);

        var scope = new CanonicalToolScope(_workspaceRoot, WorkspaceId: 1);
        _pathResolver = new WorkspacePathResolverSandboxAdapter(scope, TestPathResolver.Create(), NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WithNullDependencies_ThrowsArgumentNullException()
    {
        var webSearch = new Mock<IWebSearchService>().Object;
        var skills = new Mock<IWorkspaceSkillsService>().Object;

        Assert.Throws<ArgumentNullException>(() => new ResearchAndSkillsToolModule(null!, webSearch, skills));
        Assert.Throws<ArgumentNullException>(() => new ResearchAndSkillsToolModule(_pathResolver, (IWebSearchService)null!, skills));
        Assert.Throws<ArgumentNullException>(() => new ResearchAndSkillsToolModule(_pathResolver, (IServiceScopeFactory)null!, skills));
        Assert.Throws<ArgumentNullException>(() => new ResearchAndSkillsToolModule(_pathResolver, webSearch, null!));
    }

    [Fact]
    public async Task CreateFunctions_WhenSchemaOnly_ThrowsMissingModuleInstanceMessage()
    {
        var functions = ResearchAndSkillsToolModule.CreateFunctions(null);
        Assert.Equal(2, functions.Count);

        var loadSkillFn = functions.Single(f => f.Name == CanonicalToolNames.LoadSkill);
        var webSearchFn = functions.Single(f => f.Name == CanonicalToolNames.WebSearch);

        var skillEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            loadSkillFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["skill_name"] = "csharp-nav"
            })).AsTask());
        Assert.Equal(ResearchAndSkillsToolModule.MissingModuleInstanceMessage, skillEx.Message);

        var searchEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            webSearchFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["query"] = "dotnet 10"
            })).AsTask());
        Assert.Equal(ResearchAndSkillsToolModule.MissingModuleInstanceMessage, searchEx.Message);
    }

    [Fact]
    public async Task LoadSkillAsync_ValidatesEmptyName_AndDelegatesToWorkspaceSkillsService()
    {
        var webSearch = new Mock<IWebSearchService>(MockBehavior.Strict);
        var skills = new Mock<IWorkspaceSkillsService>(MockBehavior.Strict);
        skills
            .Setup(s => s.LoadSkillAsync("my-skill", It.IsAny<CancellationToken>()))
            .ReturnsAsync("# My Skill Content");

        var module = new ResearchAndSkillsToolModule(_pathResolver, webSearch.Object, skills.Object);

        Assert.Equal("Error: skill_name parameter is required.", await module.LoadSkillAsync(""));
        Assert.Equal("Error: skill_name parameter is required.", await module.LoadSkillAsync("   "));

        var loadFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.LoadSkill);
        var result = await loadFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["skill_name"] = "my-skill"
        }));

        Assert.Equal("# My Skill Content", result?.ToString());
    }

    [Fact]
    public async Task WebSearchAsync_HandlesAirGapped_Unconfigured_Exception_AndEmptyResults()
    {
        var skills = new Mock<IWorkspaceSkillsService>(MockBehavior.Strict).Object;

        // 1. Disabled due to Air-Gapped mode
        var airGappedService = new Mock<IWebSearchService>(MockBehavior.Strict);
        airGappedService.SetupGet(s => s.IsEnabled).Returns(false);
        airGappedService.SetupGet(s => s.IsConfigured).Returns(true);
        var airGappedModule = new ResearchAndSkillsToolModule(_pathResolver, airGappedService.Object, skills);
        Assert.False(airGappedModule.IsWebSearchEnabled);
        Assert.True(airGappedModule.IsWebSearchConfigured);
        Assert.Equal(WebSearchConstants.AirGappedToolErrorMessage, await airGappedModule.WebSearchAsync("query"));

        // 2. Unconfigured
        var unconfiguredService = new Mock<IWebSearchService>(MockBehavior.Strict);
        unconfiguredService.SetupGet(s => s.IsEnabled).Returns(false);
        unconfiguredService.SetupGet(s => s.IsConfigured).Returns(false);
        unconfiguredService.SetupGet(s => s.DisabledReason).Returns("Missing API key");
        var unconfiguredModule = new ResearchAndSkillsToolModule(_pathResolver, unconfiguredService.Object, skills);
        Assert.Equal(WebSearchConstants.UnconfiguredToolErrorMessage, await unconfiguredModule.WebSearchAsync("query"));

        // 3. Throws AirGappedOperationException during SearchAsync
        var throwingService = new Mock<IWebSearchService>(MockBehavior.Strict);
        throwingService.SetupGet(s => s.IsEnabled).Returns(true);
        throwingService.SetupGet(s => s.IsConfigured).Returns(true);
        throwingService
            .Setup(s => s.SearchAsync("blocked", WebSearchSettings.DefaultMaxResultsCount, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AirGappedOperationException("Air-gapped"));
        var throwingModule = new ResearchAndSkillsToolModule(_pathResolver, throwingService.Object, skills);
        Assert.Equal(WebSearchConstants.AirGappedToolErrorMessage, await throwingModule.WebSearchAsync("blocked"));

        // 4. Empty results
        var emptyService = new Mock<IWebSearchService>(MockBehavior.Strict);
        emptyService.SetupGet(s => s.IsEnabled).Returns(true);
        emptyService.SetupGet(s => s.IsConfigured).Returns(true);
        emptyService
            .Setup(s => s.SearchAsync("nothing", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebSearchResponse([]));
        var emptyModule = new ResearchAndSkillsToolModule(_pathResolver, emptyService.Object, skills);
        Assert.Equal("No web search results found for: 'nothing'.", await emptyModule.WebSearchAsync("nothing", maxResults: 3));
    }

    [Fact]
    public async Task WebSearchAsync_ReturnsCompactResultsInline_AndOffloadsLargeResultsToTmp()
    {
        var skills = new Mock<IWorkspaceSkillsService>(MockBehavior.Strict).Object;
        var webSearch = new Mock<IWebSearchService>(MockBehavior.Strict);
        webSearch.SetupGet(s => s.IsEnabled).Returns(true);
        webSearch.SetupGet(s => s.IsConfigured).Returns(true);

        webSearch
            .Setup(s => s.SearchAsync("small query", WebSearchSettings.DefaultMaxResultsCount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebSearchResponse([
                new WebSearchResultItem("Doc 1", "https://example.com/1", "Short snippet")
            ]));

        var largeContent = new string('X', WorkflowDefaults.MaxWebSearchResultInlineChars + 800);
        webSearch
            .Setup(s => s.SearchAsync("large query", WebSearchSettings.DefaultMaxResultsCount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebSearchResponse([
                new WebSearchResultItem("Large Doc", "https://example.com/large", largeContent)
            ]));

        var module = new ResearchAndSkillsToolModule(_pathResolver, webSearch.Object, skills);

        var inlineResult = await module.WebSearchAsync("small query");
        Assert.Contains("[1] Doc 1 (https://example.com/1)", inlineResult);
        Assert.Contains("Short snippet", inlineResult);
        Assert.DoesNotContain("/tmp/web-search-", inlineResult);

        var offloadedResult = await module.WebSearchAsync("large query");
        Assert.Contains("... [truncated inline]", offloadedResult);
        var match = Regex.Match(offloadedResult, @"saved to '(/tmp/web-search-[^']+\.md)'");
        Assert.True(match.Success, $"Expected /tmp offload path in: {offloadedResult}");

        var hostPath = _pathResolver.ResolveHostPath(match.Groups[1].Value, forWrite: false);
        Assert.True(File.Exists(hostPath));
        var savedMarkdown = await File.ReadAllTextAsync(hostPath);
        Assert.Contains(largeContent, savedMarkdown);
    }

    [Fact]
    public async Task WebSearchAsync_WhenTmpMountIsUnavailable_OmitsNonExistentTmpPathFromItemHeadersAndFooter()
    {
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: _workspaceRoot,
            wslNcfLogsPath: "/mnt/c/ncf-logs",
            hostSkillsPath: _workspaceRoot,
            wslSkillsPath: "/mnt/c/skills");
        var nwResolver = new SandboxPathResolver(nwScope);

        var largeContent = new string('Z', WorkflowDefaults.MaxWebSearchResultInlineChars + 600);
        var webSearch = new Mock<IWebSearchService>(MockBehavior.Strict);
        webSearch.SetupGet(s => s.IsEnabled).Returns(true);
        webSearch.SetupGet(s => s.IsConfigured).Returns(true);
        webSearch
            .Setup(s => s.SearchAsync("nightwatch docs", WebSearchSettings.DefaultMaxResultsCount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebSearchResponse([
                new WebSearchResultItem("Nightwatch Guide", "https://example.com/nw", largeContent)
            ]));

        var module = new ResearchAndSkillsToolModule(nwResolver, webSearch.Object, new Mock<IWorkspaceSkillsService>(MockBehavior.Strict).Object);

        var result = await module.WebSearchAsync("nightwatch docs");

        Assert.Contains($"[1] Nightwatch Guide (https://example.com/nw) [{largeContent.Length:N0} chars]", result);
        Assert.Contains("... [truncated inline]", result);
        Assert.Contains("were truncated inline to fit context budget.", result);
        Assert.DoesNotContain("/tmp/web-search-", result);
        Assert.DoesNotContain("full content:", result);
    }

    private sealed class TrackingWebSearchService : IWebSearchService, IDisposable
    {
        private readonly Action<TrackingWebSearchService> _onDispose;

        public TrackingWebSearchService(Action<TrackingWebSearchService> onDispose, bool isConfigured = true, bool isEnabled = true)
        {
            _onDispose = onDispose;
            IsConfigured = isConfigured;
            IsEnabled = isEnabled;
        }

        public int SearchCalls { get; private set; }
        public bool IsDisposed { get; private set; }
        public string ProviderName => "TestWebSearch";
        public bool IsConfigured { get; }
        public bool IsEnabled { get; }
        public string? DisabledReason => IsConfigured ? null : "Disabled in test";

        public Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            SearchCalls++;
            return Task.FromResult(new WebSearchResponse([
                new WebSearchResultItem("Result for " + query, "https://example.com/" + SearchCalls, "Short snippet")
            ]));
        }

        public void Dispose()
        {
            IsDisposed = true;
            _onDispose(this);
        }
    }

    [Fact]
    public async Task WebSearchAsync_WithScopeFactory_ResolvesFreshScopedServicePerCallAndDisposesScopeWithoutRetainingInstance()
    {
        var createdServices = new List<TrackingWebSearchService>();
        var disposedServices = new List<TrackingWebSearchService>();

        var services = new ServiceCollection();
        services.AddScoped<IWebSearchService>(_ =>
        {
            var svc = new TrackingWebSearchService(s => disposedServices.Add(s));
            createdServices.Add(svc);
            return svc;
        });

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var skillsService = new Mock<IWorkspaceSkillsService>().Object;
        var module = new ResearchAndSkillsToolModule(_pathResolver, scopeFactory, skillsService);

        Assert.Null(module.WebSearchService);
        Assert.Same(scopeFactory, module.ScopeFactory);

        var res1 = await module.WebSearchAsync("first query", 3);
        var res2 = await module.WebSearchAsync("second query", 3);

        Assert.Contains("Result for first query", res1);
        Assert.Contains("Result for second query", res2);
        Assert.Equal(2, createdServices.Count);
        Assert.NotSame(createdServices[0], createdServices[1]);
        Assert.Equal(2, disposedServices.Count);
        Assert.All(createdServices, svc =>
        {
            Assert.True(svc.IsDisposed);
            Assert.Equal(1, svc.SearchCalls);
        });
        Assert.Null(module.WebSearchService);
    }

    [Fact]
    public async Task IsWebSearchConfiguredAndEnabled_WithScopeFactory_ProbesFreshScopeWithoutRetainingCaptiveService()
    {
        var createdServices = new List<TrackingWebSearchService>();
        var services = new ServiceCollection();
        services.AddScoped<IWebSearchService>(_ =>
        {
            var svc = new TrackingWebSearchService(_ => { }, isConfigured: true, isEnabled: false);
            createdServices.Add(svc);
            return svc;
        });

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var initialDefault = new TrackingWebSearchService(_ => { }, isConfigured: true, isEnabled: true);
        var module = new ResearchAndSkillsToolModule(
            _pathResolver,
            initialDefault,
            new Mock<IWorkspaceSkillsService>().Object);

        module.ConfigureWebSearch(defaultWebSearchService: null, scopeFactory);

        Assert.Null(module.WebSearchService);
        Assert.True(module.IsWebSearchConfigured);
        Assert.False(module.IsWebSearchEnabled);
        Assert.Equal(2, createdServices.Count);
        Assert.All(createdServices, s => Assert.True(s.IsDisposed));
    }

    private sealed class AsyncOnlyDisposableWebSearchService : IWebSearchService, IAsyncDisposable
    {
        public bool IsAsyncDisposed { get; private set; }
        public string ProviderName => "AsyncOnly";
        public bool IsConfigured => true;
        public bool IsEnabled => true;
        public string? DisabledReason => null;

        public Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebSearchResponse(Array.Empty<WebSearchResultItem>()));

        public ValueTask DisposeAsync()
        {
            IsAsyncDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task IsWebSearchConfiguredAndEnabled_WhenServiceImplementsOnlyIAsyncDisposable_DisposesScopeCleanlyWithoutThrowing()
    {
        var created = new List<AsyncOnlyDisposableWebSearchService>();
        var services = new ServiceCollection();
        services.AddScoped<IWebSearchService>(_ =>
        {
            var svc = new AsyncOnlyDisposableWebSearchService();
            created.Add(svc);
            return svc;
        });

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var module = new ResearchAndSkillsToolModule(
            _pathResolver,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IWorkspaceSkillsService>().Object);

        Assert.True(module.IsWebSearchConfigured);
        Assert.True(module.IsWebSearchEnabled);
        Assert.Equal(2, created.Count);
        Assert.All(created, svc => Assert.True(svc.IsAsyncDisposed));
    }

    private sealed class AsyncYieldingOnlyDisposableWebSearchService : IWebSearchService, IAsyncDisposable
    {
        public bool IsAsyncDisposed { get; private set; }
        public string ProviderName => "AsyncYieldingOnly";
        public bool IsConfigured => true;
        public bool IsEnabled => true;
        public string? DisabledReason => null;

        public Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebSearchResponse(Array.Empty<WebSearchResultItem>()));

        public async ValueTask DisposeAsync()
        {
            await Task.Delay(15);
            IsAsyncDisposed = true;
        }
    }

    private sealed class SingleThreadSyncContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Synchronous Send not expected.");

        public void RunOnDedicatedThread(Action action, TimeSpan timeout)
        {
            Exception? captured = null;
            using var done = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                Post(_ =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        captured = ex;
                    }
                    finally
                    {
                        _queue.CompleteAdding();
                    }
                }, null);

                foreach (var (cb, state) in _queue.GetConsumingEnumerable())
                {
                    cb(state);
                }

                done.Set();
            })
            {
                IsBackground = true
            };
            thread.Start();

            if (!done.Wait(timeout))
            {
                _queue.CompleteAdding();
                Assert.Fail("Deadlock detected when disposing IAsyncDisposable scoped service under single-threaded SynchronizationContext.");
            }

            thread.Join(timeout);
            if (captured != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
            }
        }

        public void Dispose() => _queue.Dispose();
    }

    [Fact]
    public async Task IsWebSearchConfiguredAndEnabled_FromSingleThreadedSynchronizationContext_DoesNotDeadlock()
    {
        var created = new List<AsyncYieldingOnlyDisposableWebSearchService>();
        var services = new ServiceCollection();
        services.AddScoped<IWebSearchService>(_ =>
        {
            var svc = new AsyncYieldingOnlyDisposableWebSearchService();
            created.Add(svc);
            return svc;
        });

        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        var module = new ResearchAndSkillsToolModule(
            _pathResolver,
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IWorkspaceSkillsService>().Object);

        using var syncCtx = new SingleThreadSyncContext();
        bool configured = false;
        bool enabled = false;
        syncCtx.RunOnDedicatedThread(() =>
        {
            configured = module.IsWebSearchConfigured;
            enabled = module.IsWebSearchEnabled;
        }, TimeSpan.FromSeconds(5));

        Assert.True(configured);
        Assert.True(enabled);
        Assert.Equal(2, created.Count);
        Assert.All(created, svc => Assert.True(svc.IsAsyncDisposed));
    }
}
