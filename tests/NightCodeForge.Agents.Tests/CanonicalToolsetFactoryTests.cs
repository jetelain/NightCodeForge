using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class CanonicalToolsetFactoryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ncf-toolset-" + Guid.NewGuid().ToString("N"));

    public CanonicalToolsetFactoryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static (CanonicalToolsetFactory Factory, Mock<IExecutionEnvironmentProvider> Env, IBacklogPlanningContext Backlog) CreateToolsetFactory(IMcpClientManager? mcp = null)
    {
        var env = new Mock<IExecutionEnvironmentProvider>();
        env.Setup(e => e.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IExecutionEnvironment>().Object);
        var mcpManager = mcp ?? new Mock<IMcpClientManager>().Object;
        var backlog = TestBacklogContext.CreateDefault();
        var factory = new CanonicalToolsetFactory(
            TestPathResolver.Create(),
            env.Object,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            mcpManager,
            backlog);
        return (factory, env, backlog);
    }

    private CanonicalToolsetRequest ToolsetRequest(bool attachBacklog, IReadOnlyList<CustomToolConfig>? custom = null, IReadOnlyList<string>? permitted = null) =>
        new(
            new Workspace { Id = 7, Name = "WS", RepositoryPath = _tempDir },
            new AgentProfile { Id = "dev", Name = "Dev" },
            new CanonicalToolScope(_tempDir, WorkspaceId: 7, TaskId: 0),
            permitted ?? new[] { CanonicalToolNames.Read, CanonicalToolNames.Bash },
            custom ?? Array.Empty<CustomToolConfig>(),
            new Dictionary<string, string>(),
            attachBacklog);

    [Fact]
    public async Task ToolsetFactory_Create_BuildsToolsForPermittedNamesAndAttachesValidator()
    {
        var (factory, env, backlog) = CreateToolsetFactory();

        var session = await factory.CreateAsync(ToolsetRequest(attachBacklog: false), CancellationToken.None);

        Assert.Equal(new[] { CanonicalToolNames.Read, CanonicalToolNames.Bash }.Order(), session.AITools.Select(t => ((AIFunction)t).Name).Order());
        Assert.Same(backlog.TaskMarkdownValidator, session.Tools.TaskMarkdownValidator);
        Assert.Null(session.Tools.BacklogMutationService);
        Assert.NotNull(session.SkillsService);
        // A non-positive task id is normalized to "no task".
        env.Verify(e => e.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ToolsetFactory_Create_AttachesBacklogMutationServiceOnlyWhenRequested()
    {
        var (factory, _, backlog) = CreateToolsetFactory();

        var session = await factory.CreateAsync(ToolsetRequest(attachBacklog: true), CancellationToken.None);

        Assert.Same(backlog.BacklogMutationService, session.Tools.BacklogMutationService);
    }

    [Fact]
    public async Task ToolsetFactory_Create_RegistersCustomAndMcpToolsOnDispatcher()
    {
        var mcp = new Mock<IMcpClientManager>();
        mcp.Setup(m => m.GetConfiguredServersAsync(_tempDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerDefinition> { ["docs"] = new McpServerDefinition { Command = "node" } });
        var (factory, _, _) = CreateToolsetFactory(mcp.Object);
        var custom = new List<CustomToolConfig> { new() { Name = "company_lookup", Description = "Looks things up" } };
        var mcpToolName = CanonicalToolsetFactory.McpToolNamePrefix + "docs";

        var session = await factory.CreateAsync(
            ToolsetRequest(false, custom, new[] { "company_lookup", mcpToolName }),
            CancellationToken.None);

        Assert.True(session.Dispatcher.IsToolRegistered("company_lookup"));
        Assert.True(session.Dispatcher.IsToolRegistered(mcpToolName));
        Assert.Equal(new[] { "company_lookup", mcpToolName }.Order(), session.AITools.Select(t => ((AIFunction)t).Name).Order());
    }

    [Fact]
    public async Task ToolsetFactory_CleanupTaskContainer_DelegatesToEnvironmentProvider()
    {
        var (factory, env, _) = CreateToolsetFactory();
        var workspace = new Workspace { Id = 3 };

        await factory.CleanupTaskContainerAsync(workspace, 12, CancellationToken.None);

        env.Verify(e => e.CleanupTaskContainerAsync(workspace, 12, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_CustomAndMcpToolHandlersReportThatCallsAreNotExecuted()
    {
        var mcp = new Mock<IMcpClientManager>();
        mcp.Setup(m => m.GetConfiguredServersAsync(_tempDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, McpServerDefinition> { ["docs"] = new McpServerDefinition { Command = "node" } });
        var (factory, _, _) = CreateToolsetFactory(mcp.Object);
        var custom = new List<CustomToolConfig> { new() { Name = "company_lookup", Description = "d" } };
        var session = await factory.CreateAsync(ToolsetRequest(false, custom), CancellationToken.None);

        var customResult = await session.Dispatcher.ExecuteAsync("company_lookup", "{}", CancellationToken.None);
        var mcpResult = await session.Dispatcher.ExecuteAsync(CanonicalToolsetFactory.McpToolNamePrefix + "docs", "{}", CancellationToken.None);

        Assert.Equal(string.Format(CanonicalToolsetFactory.CustomToolNotExecutableMessageFormat, "company_lookup"), customResult?.ToString());
        Assert.Equal(string.Format(CanonicalToolsetFactory.McpToolNotSupportedMessageFormat, "docs"), mcpResult?.ToString());
    }

    [Fact]
    public async Task BuildAIFunctionList_AppliesToolDirectiveDescriptionOverride()
    {
        var (factory, _, _) = CreateToolsetFactory();
        var session = await factory.CreateAsync(ToolsetRequest(false), CancellationToken.None);

        var list = CanonicalToolsetFactory.BuildAIFunctionList(
            session.Tools,
            new[] { CanonicalToolNames.Read },
            session.Dispatcher,
            toolDirectives: new Dictionary<string, string> { [CanonicalToolNames.Read] = "Custom directive" });

        Assert.Equal("Custom directive", Assert.Single(list).Description);
    }

    [Fact]
    public async Task BuildAIFunctionList_IgnoresUnregisteredNonCanonicalTools()
    {
        var (factory, _, _) = CreateToolsetFactory();
        var session = await factory.CreateAsync(ToolsetRequest(false), CancellationToken.None);

        var list = CanonicalToolsetFactory.BuildAIFunctionList(session.Tools, new[] { CanonicalToolNames.Read, "ghost_tool" }, session.Dispatcher);

        Assert.Equal(new[] { CanonicalToolNames.Read }, list.Select(t => ((AIFunction)t).Name));
    }

    [Fact]
    public async Task Create_WithoutExecutionEnvironment_FailsFast()
    {
        var env = new Mock<IExecutionEnvironmentProvider>();
        env.Setup(e => e.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((IExecutionEnvironment)null!);
        var factory = new CanonicalToolsetFactory(
            TestPathResolver.Create(), env.Object, new EditorConfigService(), new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            new Mock<IMcpClientManager>().Object, TestBacklogContext.CreateDefault());

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(ToolsetRequest(false), CancellationToken.None));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new CanonicalToolsetFactory(
            null!, new Mock<IExecutionEnvironmentProvider>().Object, new EditorConfigService(), new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object, new Mock<IWorkspaceSkillsServiceFactory>().Object,
            new Mock<IMcpClientManager>().Object, TestBacklogContext.CreateDefault()));
    }

    [Fact]
    public void CanonicalToolScope_DefaultsToUnscopedWritableSession()
    {
        var scope = new CanonicalToolScope("/repo");

        Assert.Null(scope.WorkspaceId);
        Assert.Null(scope.TaskId);
        Assert.Null(scope.BuildTimeoutSeconds);
        Assert.Null(scope.TestTimeoutSeconds);
        Assert.False(scope.IsReadOnly);
        Assert.Equal(scope, new CanonicalToolScope("/repo"));
    }

    [Fact]
    public async Task CreateAsync_WithSandboxScopeOnRequest_PropagatesSandboxPathResolverToCanonicalTools()
    {
        var (factory, env, _) = CreateToolsetFactory();
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: _tempDir,
            wslNcfLogsPath: "/mnt/c/logs",
            hostSkillsPath: _tempDir,
            wslSkillsPath: "/mnt/c/skills");

        env.Setup(e => e.GetEnvironmentAsync(nwScope, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IExecutionEnvironment>().Object);

        var request = new CanonicalToolsetRequest(
            Workspace: null,
            new AgentProfile { Id = "nw", Name = "Nightwatch", SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
            new CanonicalToolScope("/nightcodeforge", IsReadOnly: true),
            new[] { CanonicalToolNames.Read, CanonicalToolNames.Bash },
            Array.Empty<CustomToolConfig>(),
            new Dictionary<string, string>(),
            AttachBacklogMutationService: false,
            SandboxScope: nwScope);

        var session = await factory.CreateAsync(request, CancellationToken.None);

        Assert.NotNull(session.Tools.SandboxPathResolver);
        Assert.Same(nwScope, session.Tools.SandboxPathResolver!.Scope);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_tempDir, "app.log")),
            session.Tools.ResolveFullPath("/nightcodeforge/ncf-logs/app.log"));
        Assert.NotNull(await session.SkillsService.GetActiveSkillsAsync());

        // When both Workspace and SandboxScope are null, CreateAsync fails fast with InvalidOperationException
        var invalidRequest = request with { SandboxScope = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync(invalidRequest, CancellationToken.None));
    }
}
