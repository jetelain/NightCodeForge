using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tools;

public record EditChunk(string OldText, string NewText, bool ReplaceAll = false);

/// <summary>
/// Handler that presents batched questions to a live human and returns the numbered answers without ending the agent turn.
/// </summary>
public delegate Task<string> AskHandler(IReadOnlyList<QuestionItem> questions, CancellationToken cancellationToken);

/// <summary>
/// Composition facade over the five modular <see cref="IAgentToolModule"/> implementations:
/// <see cref="WorkspaceFileToolModule"/>, <see cref="SandboxBashToolModule"/>,
/// <see cref="WorkflowInteractionToolModule"/>, <see cref="BacklogManagementToolModule"/>,
/// and <see cref="ResearchAndSkillsToolModule"/>.
/// </summary>
public partial class CanonicalTools
{
    public static HashSet<string> IndentationSignificantExtensions =>
        WorkspaceFileToolModule.IndentationSignificantExtensions;

    private ISandboxPathResolver _sandboxPathResolver;
    private IToolTurnInterruptionController? _interruptionController;

    public WorkspaceFileToolModule FileModule { get; }
    public SandboxBashToolModule BashModule { get; }
    public WorkflowInteractionToolModule InteractionModule { get; }
    public BacklogManagementToolModule BacklogModule { get; }
    public ResearchAndSkillsToolModule ResearchAndSkillsModule { get; }
    public IReadOnlyList<IAgentToolModule> Modules { get; }

    public ISandboxPathResolver SandboxPathResolver
    {
        get => _sandboxPathResolver;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _sandboxPathResolver = value;
            FileModule.SandboxPathResolver = value;
            BashModule.SandboxPathResolver = value;
            ResearchAndSkillsModule.SandboxPathResolver = value;
        }
    }

    public AskHandler? AskHandler
    {
        get => InteractionModule.AskHandler;
        set => InteractionModule.AskHandler = value;
    }

    public Action<string, string?>? RequestChangesHandler
    {
        get => InteractionModule.RequestChangesHandler;
        set => InteractionModule.RequestChangesHandler = value;
    }

    public IToolTurnInterruptionController? InterruptionController
    {
        get => _interruptionController;
        set
        {
            _interruptionController = value;
            foreach (var module in Modules)
            {
                module.InterruptionController = value;
            }
        }
    }

    public ITaskMarkdownValidator? TaskMarkdownValidator
    {
        get => FileModule.TaskMarkdownValidator;
        set => FileModule.TaskMarkdownValidator = value;
    }

    public IWebSearchService WebSearchService => ResearchAndSkillsModule.WebSearchService;
    public bool IsWebSearchConfigured => ResearchAndSkillsModule.IsWebSearchConfigured;
    public bool IsWebSearchEnabled => ResearchAndSkillsModule.IsWebSearchEnabled;
    public IWorkspaceSkillsService SkillsService => ResearchAndSkillsModule.SkillsService;
    public int MaxReadFileBytes => FileModule.MaxReadFileBytes;
    public List<TaskProposal> PendingProposals => BacklogModule.PendingProposals;

    public CanonicalTools(
        CanonicalToolScope scope,
        IWorkspacePathResolver pathResolver,
        IExecutionEnvironment executionEnv,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWebSearchService webSearchService,
        IWorkspaceSkillsService skillsService,
        ILogger<CanonicalTools> logger)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.WorkspaceRoot);
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentNullException.ThrowIfNull(executionEnv);
        ArgumentNullException.ThrowIfNull(editorConfigService);
        ArgumentNullException.ThrowIfNull(editorConfigFileApplier);
        ArgumentNullException.ThrowIfNull(webSearchService);
        ArgumentNullException.ThrowIfNull(skillsService);
        ArgumentNullException.ThrowIfNull(logger);

        _sandboxPathResolver = scope.SandboxPathResolver
            ?? (scope.SandboxScope != null
                ? new SandboxPathResolver(scope.SandboxScope)
                : new WorkspacePathResolverSandboxAdapter(scope, pathResolver, logger));

        FileModule = new WorkspaceFileToolModule(
            scope,
            _sandboxPathResolver,
            editorConfigService,
            editorConfigFileApplier,
            logger);

        BashModule = new SandboxBashToolModule(
            scope,
            _sandboxPathResolver,
            executionEnv,
            logger);

        InteractionModule = new WorkflowInteractionToolModule();

        BacklogModule = new BacklogManagementToolModule(scope);

        ResearchAndSkillsModule = new ResearchAndSkillsToolModule(
            _sandboxPathResolver,
            webSearchService,
            skillsService);

        Modules =
        [
            FileModule,
            BashModule,
            InteractionModule,
            BacklogModule,
            ResearchAndSkillsModule
        ];
    }

    public string ResolveFullPath(string path) => FileModule.ResolveFullPath(path, forWrite: false);

    public string ResolveFullPath(string path, bool forWrite) => FileModule.ResolveFullPath(path, forWrite);

    public Task<string> ReadAsync(string path, int? offset = null, int? limit = null, CancellationToken cancellationToken = default) =>
        FileModule.ReadAsync(path, offset, limit, cancellationToken);

    public Task<string> WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
        FileModule.WriteAsync(path, content, cancellationToken);

    public Task<string> EditAsync(string path, IReadOnlyList<EditChunk> edits, CancellationToken cancellationToken = default) =>
        FileModule.EditAsync(path, edits, cancellationToken);

    public Task<string> EditAsync(string path, string oldText, string newText, bool replaceAll = false, CancellationToken cancellationToken = default) =>
        FileModule.EditAsync(path, oldText, newText, replaceAll, cancellationToken);

    public Task<string> BashAsync(string command, int? timeout = null, string? workingDirectory = null, CancellationToken cancellationToken = default) =>
        BashModule.BashAsync(command, timeout, workingDirectory, cancellationToken);

    public Task<string> AskAsync(IReadOnlyList<QuestionItem>? questions, CancellationToken cancellationToken = default) =>
        InteractionModule.AskAsync(questions, cancellationToken);

    public string RequestChanges(string feedback, string? reason = null) =>
        InteractionModule.RequestChanges(feedback, reason);

    public Task<string> WebSearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default) =>
        ResearchAndSkillsModule.WebSearchAsync(query, maxResults, cancellationToken);

    public Task<string> LoadSkillAsync(string skillName, CancellationToken cancellationToken = default) =>
        ResearchAndSkillsModule.LoadSkillAsync(skillName, cancellationToken);
}
