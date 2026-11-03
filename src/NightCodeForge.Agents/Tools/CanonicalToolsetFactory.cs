using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tools;

/// <summary>Immutable per-session scope values of a <see cref="CanonicalTools"/> instance.</summary>
public sealed record CanonicalToolScope(
    string WorkspaceRoot,
    int? WorkspaceId = null,
    int? TaskId = null,
    int? BuildTimeoutSeconds = null,
    int? TestTimeoutSeconds = null,
    bool IsReadOnly = false);

/// <summary>Everything the toolset factory needs to assemble the tools of one agent session.</summary>
public sealed record CanonicalToolsetRequest(
    Workspace Workspace,
    AgentProfile Profile,
    CanonicalToolScope Scope,
    IReadOnlyList<string> PermittedTools,
    IReadOnlyList<CustomToolConfig> CustomTools,
    IReadOnlyDictionary<string, string> ToolDirectives,
    bool AttachBacklogMutationService);

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

        var effectiveEnv = _envProvider.GetEnvironment(workspace, request.Profile.SecurityTier, taskId)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var skillsService = _skillsServiceFactory.Create(workspace, scope.WorkspaceRoot, taskId);

        var tools = new CanonicalTools(
            scope,
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

        var dispatcher = new LenientToolDispatcher(tools);
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
            var toolName = customTool.Name;
            dispatcher.RegisterTool(new ToolDefinition
            {
                Name = customTool.Name,
                Description = customTool.Description,
                Aliases = customTool.Aliases,
                ParameterAliases = new Dictionary<string, string>(customTool.ParameterAliases, StringComparer.OrdinalIgnoreCase),
                // Database-defined custom tools carry no executable definition: report that honestly
                // instead of pretending the call succeeded.
                Handler = (args, ct) => Task.FromResult(string.Format(CustomToolNotExecutableMessageFormat, toolName))
            });
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
            dispatcher.RegisterTool(new ToolDefinition
            {
                Name = $"{McpToolNamePrefix}{serverName}",
                Description = $"External MCP server tool '{serverName}'. Command: {def.Command} {string.Join(" ", def.Args)}",
                // There is no MCP protocol client yet. Launching the server command once and returning its
                // stdout is not a tool call (the arguments were ignored and a stdio server never exits),
                // so report clearly that the call was not executed.
                Handler = (args, ct) => Task.FromResult(string.Format(McpToolNotSupportedMessageFormat, name))
            });
        }
    }

    public static IList<AITool> BuildAIFunctionList(
        CanonicalTools tools,
        IReadOnlyList<string> permittedTools,
        LenientToolDispatcher dispatcher,
        IReadOnlyList<CustomToolConfig>? customTools = null,
        IReadOnlyDictionary<string, string>? toolDirectives = null)
    {
        var aiFunctions = CanonicalAIFunctionFactory.CreateTools(tools, permittedTools, dispatcher).Cast<AIFunction>().ToList();
        var knownNames = new HashSet<string>(aiFunctions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tool in permittedTools)
        {
            // Only expose non-canonical tools that are actually registered (custom DB tools, MCP servers);
            // canonical tools are already handled by CanonicalAIFunctionFactory above.
            if (CanonicalToolNames.All.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!knownNames.Contains(tool) && dispatcher.TryGetTool(tool, out _))
            {
                var custom = customTools?.FirstOrDefault(c => string.Equals(c.Name, tool, StringComparison.OrdinalIgnoreCase));
                var desc = custom?.Description ?? $"Execute tool '{tool}'";
                aiFunctions.Add(new DispatcherAIFunction(tool, desc, dispatcher));
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

    /// <summary>
    /// Exposes a dispatcher-registered tool (custom DB tool, MCP server) with a free-form object schema so the
    /// model's arguments reach the dispatcher as-is, instead of being wrapped under a synthetic parameter.
    /// </summary>
    private sealed class DispatcherAIFunction(string name, string description, LenientToolDispatcher dispatcher) : AIFunction
    {
        private static readonly JsonElement FreeFormObjectSchema =
            JsonDocument.Parse(FreeFormObjectSchemaJson).RootElement.Clone();

        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => FreeFormObjectSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try
            {
                var argsJson = JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
                return await dispatcher.DispatchAsync(name, argsJson, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NightCodeForge.Core.Workflow.HumanInterruptionException)
            {
                throw;
            }
            catch (NightCodeForge.Core.Workflow.RequestChangesException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;
                return $"Error: {msg}";
            }
        }
    }

    /// <summary>Overrides the description exposed to the model (database-configured tool directives).</summary>
    private sealed class DescriptionOverrideAIFunction(AIFunction inner, string description) : DelegatingAIFunction(inner)
    {
        public override string Description => description;
    }
}

