using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Tools;

/// <summary>Immutable per-session scope values of a <see cref="CanonicalTools"/> instance.</summary>
public sealed record CanonicalToolScope(
    string WorkspaceRoot,
    int? WorkspaceId = null,
    int? TaskId = null,
    int? BuildTimeoutSeconds = null,
    int? TestTimeoutSeconds = null,
    bool IsReadOnly = false,
    int? MaxReadFileBytes = null,
    SandboxScopeDescriptor? SandboxScope = null,
    ISandboxPathResolver? SandboxPathResolver = null);

/// <summary>Everything the toolset factory needs to assemble the tools of one agent session.</summary>
public sealed record CanonicalToolsetRequest(
    Workspace? Workspace,
    AgentProfile Profile,
    CanonicalToolScope Scope,
    IReadOnlyList<string> PermittedTools,
    IReadOnlyList<CustomToolConfig> CustomTools,
    IReadOnlyDictionary<string, string> ToolDirectives,
    bool AttachBacklogMutationService,
    SandboxScopeDescriptor? SandboxScope = null);

/// <summary>The configured tools of one agent session.</summary>
public sealed record CanonicalToolsetSession(
    CanonicalTools Tools,
    LenientToolDispatcher Dispatcher,
    IWorkspaceSkillsService SkillsService,
    IList<AITool> AITools);

/// <summary>Builds the <see cref="CanonicalTools"/>, dispatcher, skills service and AI tool list of an agent session.</summary>
public interface ICanonicalToolsetFactory
{
    Task<CanonicalToolsetSession> CreateAsync(CanonicalToolsetRequest request, CancellationToken cancellationToken);

    /// <summary>Removes the per-task sandbox container of a task (no-op outside container sandboxes).</summary>
    Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class CanonicalToolsetFactory : ICanonicalToolsetFactory
{
    public const string CustomToolNotExecutableMessageFormat = "Error: custom tool '{0}' has no executable handler configured; the call was not executed.";
    public const string McpToolNotSupportedMessageFormat = "Error: MCP server '{0}' is configured but MCP tool calls are not supported yet; the call was not executed.";
    public const string McpToolNamePrefix = "mcp_";
    private const string FreeFormObjectSchemaJson = """{"type":"object","additionalProperties":true}""";

    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;
    private readonly IWorkspaceSkillsServiceFactory _skillsServiceFactory;
    private readonly IMcpClientManager _mcpManager;
    private readonly IBacklogPlanningContext _backlogContext;

    public CanonicalToolsetFactory(
        IWorkspacePathResolver pathResolver,
        IExecutionEnvironmentProvider envProvider,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWebSearchService webSearchService,
        IWorkspaceSkillsServiceFactory skillsServiceFactory,
        IMcpClientManager mcpManager,
        IBacklogPlanningContext backlogContext)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _webSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        _skillsServiceFactory = skillsServiceFactory ?? throw new ArgumentNullException(nameof(skillsServiceFactory));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _backlogContext = backlogContext ?? throw new ArgumentNullException(nameof(backlogContext));
    }

    public async Task<CanonicalToolsetSession> CreateAsync(CanonicalToolsetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workspace = request.Workspace;
        var scope = request.Scope;
        var taskId = scope.TaskId is > 0 ? scope.TaskId : null;

        var sandboxScope = request.SandboxScope ?? scope.SandboxScope;
        var effectiveScope = sandboxScope != null && scope.SandboxScope == null
            ? scope with { SandboxScope = sandboxScope }
            : scope;

        IExecutionEnvironment effectiveEnv;
        if (sandboxScope != null)
        {
            effectiveEnv = await _envProvider.GetEnvironmentAsync(sandboxScope, cancellationToken)
                ?? throw new InvalidOperationException($"No execution environment could be resolved for scope '{sandboxScope.ScopeCacheKey}'.");
        }
        else
        {
            var requiredWorkspace = workspace
                ?? throw new InvalidOperationException("Either Workspace or SandboxScope is required to create a canonical toolset.");
            effectiveEnv = await _envProvider.GetEnvironmentAsync(requiredWorkspace, request.Profile.SecurityTier, taskId, cancellationToken)
                ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{requiredWorkspace.Id}.");
        }

        var skillsService = _skillsServiceFactory.Create(workspace, effectiveScope.WorkspaceRoot, taskId);

        var tools = new CanonicalTools(
            effectiveScope,
            _pathResolver,
            effectiveEnv,
            _editorConfigService,
            _editorConfigFileApplier,
            _webSearchService,
            skillsService,
            _backlogContext.CanonicalToolsLogger)
        {
            TaskMarkdownValidator = _backlogContext.TaskMarkdownValidator
        };

        if (request.AttachBacklogMutationService)
        {
            tools.BacklogMutationService = _backlogContext.BacklogMutationService;
        }

        var dispatcher = NightCodeForgeToolDispatcher.Create(
            tools,
            request.CustomTools,
            _backlogContext.CanonicalToolsLogger);

        RegisterCustomTools(dispatcher, request.CustomTools);
        await RegisterMcpToolsAsync(dispatcher, scope.WorkspaceRoot, cancellationToken);

        var aiTools = BuildAIFunctionList(tools, request.PermittedTools, dispatcher, request.CustomTools, request.ToolDirectives);
        return new CanonicalToolsetSession(tools, dispatcher, skillsService, aiTools);
    }

    public Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken) =>
        _envProvider.CleanupTaskContainerAsync(workspace, taskId, cancellationToken);

    private static void RegisterCustomTools(LenientToolDispatcher dispatcher, IReadOnlyList<CustomToolConfig> customTools)
    {
        foreach (var customTool in customTools)
        {
            if (string.IsNullOrWhiteSpace(customTool.Name))
            {
                continue;
            }

            var toolName = customTool.Name;
            // Database-defined custom tools carry no executable definition: report that honestly
            // instead of pretending the call succeeded.
            dispatcher.RegisterTool(new FreeFormStubAIFunction(
                toolName,
                customTool.Description,
                string.Format(CustomToolNotExecutableMessageFormat, toolName)));
        }
    }

    private async Task RegisterMcpToolsAsync(LenientToolDispatcher dispatcher, string workspaceRoot, CancellationToken cancellationToken)
    {
        var mcpServers = await _mcpManager.GetConfiguredServersAsync(workspaceRoot, cancellationToken);
        if (mcpServers == null)
        {
            return;
        }

        foreach (var (serverName, def) in mcpServers)
        {
            var name = serverName;
            // There is no MCP protocol client yet. Launching the server command once and returning its
            // stdout is not a tool call (the arguments were ignored and a stdio server never exits),
            // so report clearly that the call was not executed.
            dispatcher.RegisterTool(new FreeFormStubAIFunction(
                $"{McpToolNamePrefix}{serverName}",
                $"External MCP server tool '{serverName}'. Command: {def.Command} {string.Join(" ", def.Args)}",
                string.Format(McpToolNotSupportedMessageFormat, name)));
        }
    }

    public static IList<AITool> BuildAIFunctionList(
        CanonicalTools tools,
        IReadOnlySet<string> permittedTools,
        LenientToolDispatcher dispatcher,
        IReadOnlyList<CustomToolConfig>? customTools = null,
        IReadOnlyDictionary<string, string>? toolDirectives = null) =>
        BuildAIFunctionList(tools, (IReadOnlyList<string>)permittedTools.ToList(), dispatcher, customTools, toolDirectives);

    public static IList<AITool> BuildAIFunctionList(
        CanonicalTools tools,
        IReadOnlyList<string> permittedTools,
        LenientToolDispatcher dispatcher,
        IReadOnlyList<CustomToolConfig>? customTools = null,
        IReadOnlyDictionary<string, string>? toolDirectives = null)
    {
        var permittedSet = new HashSet<string>(permittedTools, StringComparer.OrdinalIgnoreCase);
        var aiFunctions = CanonicalAIFunctionFactory.CreateTools(tools, permittedSet, dispatcher).Cast<AIFunction>().ToList();
        var knownNames = new HashSet<string>(aiFunctions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tool in permittedTools)
        {
            // Only expose non-canonical tools that are actually registered (custom DB tools, MCP servers);
            // canonical tools are already handled by CanonicalAIFunctionFactory above.
            if (CanonicalToolNames.All.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!knownNames.Contains(tool) && dispatcher.IsToolRegistered(tool))
            {
                var custom = customTools?.FirstOrDefault(c => string.Equals(c.Name, tool, StringComparison.OrdinalIgnoreCase));
                var desc = custom?.Description ?? $"Execute tool '{tool}'";
                var responseMessage = tool.StartsWith(McpToolNamePrefix, StringComparison.OrdinalIgnoreCase)
                    ? string.Format(McpToolNotSupportedMessageFormat, tool[McpToolNamePrefix.Length..])
                    : string.Format(CustomToolNotExecutableMessageFormat, tool);
                aiFunctions.Add(new FreeFormStubAIFunction(tool, desc, responseMessage));
                knownNames.Add(tool);
            }
        }

        if (toolDirectives is { Count: > 0 })
        {
            for (int i = 0; i < aiFunctions.Count; i++)
            {
                if (toolDirectives.TryGetValue(aiFunctions[i].Name, out var directive) && !string.IsNullOrWhiteSpace(directive))
                {
                    aiFunctions[i] = new DescriptionOverrideAIFunction(aiFunctions[i], directive);
                }
            }
        }

        return aiFunctions.Cast<AITool>().ToList();
    }

    private sealed class FreeFormStubAIFunction(string name, string description, string responseMessage) : AIFunction
    {
        private static readonly JsonElement FreeFormObjectSchema =
            JsonDocument.Parse(FreeFormObjectSchemaJson).RootElement.Clone();

        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => FreeFormObjectSchema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(responseMessage);
    }

    /// <summary>Overrides the description exposed to the model (database-configured tool directives).</summary>
    private sealed class DescriptionOverrideAIFunction(AIFunction inner, string description) : DelegatingAIFunction(inner)
    {
        public override string Description => description;
    }
}
