using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Streaming;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Interactive Ideation and Discovery Agent conversing with developers, exploring codebase structure,
/// and proposing backlog tasks with cross-dependencies.
/// Relies on an AgentProfile (not scheduled as a background pipeline workload).
/// Provides live feedback via IAgentActivityStreamer and streaming updates.
/// </summary>
public partial class IdeationService : IIdeationService
{
    /// <summary>
    /// Default maximum turns for ideation agent exploration and tool loop.
    /// </summary>
    public const int DefaultMaxTurns = AgentProfile.DefaultMaxTurns;

    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex TaskJsonArrayRegex();

    private readonly IOllamaChatClientFactory _chatClientFactory;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IWebSearchService _webSearchService;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly SessionTranscriptService _transcriptService;
    private readonly IGlobalOptimizationSettingsService _globalOptimizationSettings;
    private readonly ILogger<IdeationService> _logger;
    private readonly ILogger<CanonicalTools> _canonicalToolsLogger;
    private readonly LlmProviderOptions _llmOptions;
    private readonly SkillsService _skillsService;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly TimeProvider _timeProvider;

    public IdeationService(
        IWorkspacePathResolver pathResolver,
        IOllamaChatClientFactory chatClientFactory,
        IExecutionEnvironmentProvider envProvider,
        IWebSearchService webSearchService,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        AgentsMdService agentsMdService,
        IAgentActivityStreamer activityStreamer,
        SessionTranscriptService transcriptService,
        IGlobalOptimizationSettingsService globalOptimizationSettings,
        SkillsService skillsService,
        IWorkspaceRepository workspaceRepository,
        ITaskMarkdownSyncService markdownSyncService,
        Microsoft.Extensions.Options.IOptions<LlmProviderOptions> llmOptions,
        ILogger<IdeationService> logger,
        ILogger<CanonicalTools> canonicalToolsLogger,
        TimeProvider timeProvider)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _webSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _agentsMdService = agentsMdService ?? throw new ArgumentNullException(nameof(agentsMdService));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _globalOptimizationSettings = globalOptimizationSettings ?? throw new ArgumentNullException(nameof(globalOptimizationSettings));
        _skillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        ArgumentNullException.ThrowIfNull(llmOptions);
        _llmOptions = llmOptions.Value ?? throw new ArgumentNullException(nameof(llmOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canonicalToolsLogger = canonicalToolsLogger ?? throw new ArgumentNullException(nameof(canonicalToolsLogger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Resolves the effective AgentProfile for ideation, prioritizing explicit profile,
    /// then workspace profile named/id 'ideation', or throws AgentProfileNotFoundException if missing.
    /// </summary>
    public static AgentProfile ResolveIdeationProfile(Workspace? workspace, AgentProfile? explicitProfile = null)
    {
        if (explicitProfile != null) return explicitProfile;

        var profileFromWorkspace = workspace?.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains(StandardStageIds.Ideation, StringComparison.OrdinalIgnoreCase));

        if (profileFromWorkspace != null)
        {
            return profileFromWorkspace;
        }

        throw new AgentProfileNotFoundException(StandardStageIds.Ideation, workspace?.Id);
    }

    public Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        IdeationResult? finalResult = null;
        await foreach (var update in ConverseStreamingAsync(
            workspace,
            userPrompt,
            conversationHistory,
            agentProfile,
            customSystemPrompt,
            cancellationToken).ConfigureAwait(false))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed && update.FinalResult != null)
            {
                finalResult = update.FinalResult;
            }
        }

        return finalResult ?? new IdeationResult(string.Empty, Array.Empty<IdeationProposal>());
    }

    public IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseStreamingAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var profile = agentProfile;
        if (profile == null)
        {
            if (_workspaceRepository != null)
            {
                profile = await _workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.Ideation, cancellationToken);
            }
            if (profile == null)
            {
                profile = ResolveIdeationProfile(workspace, agentProfile);
            }
        }

        // Resolve client dynamically for the agent profile
        var client = _chatClientFactory.CreateClient(profile);

        var sessionId = $"ideation-{workspace.Id}-{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace.Id,
            ["agentProfile"] = profile.Id,
            ["model"] = profile.ModelName
        };

        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.StageStarted, "Starting ideation discovery", cancellationToken, metadata);

        var repoPath = !string.IsNullOrEmpty(workspace.RepositoryPath) ? workspace.RepositoryPath : Directory.GetCurrentDirectory();

        // Pre-sync existing backlog tasks into clone/docs/tasks so the ideation agent has full visibility of existing tasks
        string? cloneDir = null;
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            var customClone = _pathResolver.ToHostPath(workspace.Ext4MirrorPath);
            if (Directory.Exists(customClone)) cloneDir = customClone;
        }
        if (cloneDir == null && workspace.Id > 0)
        {
            try
            {
                var canonicalClone = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                if (!string.IsNullOrWhiteSpace(canonicalClone))
                {
                    var hostClone = _pathResolver.ToHostPath(canonicalClone);
                    if (Directory.Exists(hostClone)) cloneDir = hostClone;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve working clone directory for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(repoPath) && Directory.Exists(repoPath))
        {
            try
            {
                await _markdownSyncService.SyncAllTasksToDiskAsync(workspace, repoPath, repoPath, cloneDirectory: cloneDir, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pre-sync existing backlog tasks to clone/docs/tasks for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        var agentsRules = await _agentsMdService.IngestRulesAsync(repoPath, cancellationToken);
        var projectConfig = CodeStackPresets.ResolveConfig(repoPath, null);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules, projectConfig, workspace);

        // Determine agent's effective working directory based on execution mode and workspace paths
        string agentWorkingDir;
        if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox)
        {
            bool hasClone = !string.IsNullOrEmpty(workspace.Ext4MirrorPath) ||
                            Directory.Exists(_pathResolver.ResolveCruciblePath(workspace.Id, string.Empty));
            agentWorkingDir = hasClone ? "/workspace/clone" : "/workspace";
        }
        else if (workspace.ExecutionMode == ExecutionMode.WslDirect)
        {
            agentWorkingDir = !string.IsNullOrEmpty(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : _pathResolver.GetAgentWorkingClonePath(workspace.Id);
        }
        else
        {
            agentWorkingDir = repoPath;
        }

        string? topLevelSummary = null;
        try
        {
            var hostDir = _pathResolver.ResolveCruciblePath(workspace.Id, string.Empty);
            if (string.IsNullOrEmpty(hostDir) || !Directory.Exists(hostDir))
            {
                hostDir = repoPath;
            }
            if (!string.IsNullOrEmpty(hostDir) && Directory.Exists(hostDir))
            {
                var entries = Directory.GetFileSystemEntries(hostDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n) &&
                                !n.StartsWith(".git") &&
                                !n.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                                !n.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                                !n.Equals(".vs", StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList();
                if (entries.Count > 0)
                {
                    topLevelSummary = string.Join(", ", entries);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not discover top-level entries for workspace {WorkspaceId}", workspace.Id);
        }

        var environmentSection = new StringBuilder();
        environmentSection.AppendLine("<environment>");
        environmentSection.AppendLine($"- Current Working Directory (PWD): {agentWorkingDir}");
        environmentSection.AppendLine($"- Repository Root: {agentWorkingDir}");
        environmentSection.AppendLine($"- Execution Mode: {workspace.ExecutionMode}");
        environmentSection.AppendLine($"- All relative paths, file operations (read, edit, write), and bash commands execute in this working directory ({agentWorkingDir}).");
        if (!string.IsNullOrEmpty(topLevelSummary))
        {
            environmentSection.AppendLine($"- Top-level files and directories in repository root: {topLevelSummary}");
        }
        environmentSection.AppendLine($"- When exploring the codebase, begin by inspecting the directory structure or key files in '{agentWorkingDir}'.");
        environmentSection.AppendLine("</environment>");

        var basePersona = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : (!string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt
                : @"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.
Each proposed task represents a full Pull Request / Merge Request unit of work with autonomous build, test, and code review gates.
Avoid over-decomposing into micro-tasks; structure cohesive PR-sized tasks with sub-tasks and open questions to be answered in the task review process.");

        var permittedTools = profile.PermittedTools?.Count > 0
            ? profile.PermittedTools
            : DefaultAgentTools.GetTools(profile.Mode);

        var taskGuidance = @"<task_planning_guidelines>
When proposing implementation tasks to the developer, adhere to the following rules:
1. PULL-REQUEST / MERGE-REQUEST SIZING:
   - Each task in NightCodeForge represents a full, independent 'pull-request' / 'merge-request' (PR/MR).
   - Each task automatically passes through rigorous pipeline gates: Stage 1 (ForgeTask Review & ambiguity clarification), Pre-Implementation Baseline build & test gates, Stage 2 (Implementation), Post-Implementation Ratchet build & test gates, Stage 3 (Code Review & automated fix gate), and a Merge Gate.
   - Each task will have its own tests (unit, integration, or functional tests) verifying the implementation.
   - DO NOT over-decompose into excessively small or trivial micro-tasks (e.g., do NOT create separate tasks for creating a single file, adding a single method, updating a configuration line, or adding a single dependency). Over-decomposition creates unnecessary pipeline, build, and review overhead.

2. SUB-TASKS WITHIN A TASK:
   - A task may have sub-tasks! When a feature, refactoring, or fix consists of multiple logical steps or components (e.g., database schema changes, backend service implementation, API endpoints, UI updates), group them together into a single cohesive task.
   - Detail these sub-tasks as an implementation checklist or breakdown inside the task's 'description'.
   - The downstream Implementation Agent will execute all sub-tasks together within the single PR/task lifecycle.

3. OPEN QUESTIONS RESOLVED IN TASK REVIEW:
   - A task may have open questions! You do not need every minor detail, design alternative, or edge case 100% resolved during ideation.
   - If there are unresolved architectural questions, alternative approaches, or decisions that require deeper inspection or user input, list them explicitly under an 'Open Questions' section in the task's 'description'.
   - These open questions will be answered in the task review process (Stage 1: ForgeTask Review), where the review agent inspects the code and can proactively prompt the developer ('ask') before coding begins.

4. TASK PROPOSAL INVOCATION:
   - When you recommend one or more implementation tasks, do NOT output markdown JSON codeblocks in your response.
   - Instead, invoke the 'propose_tasks' tool with your proposed tasks (each having title, description, and optional priority).
   - Structure each task's description with clear Markdown sections (## Summary, ## Sub-tasks / Scope, ## Testing Plan, ## Open Questions if applicable).
   - If multiple tasks are proposed, they will be scheduled sequentially with dependencies established automatically (or via dependsOnTitles).
   - After proposing tasks, explain your proposal and architectural rationale clearly to the developer.

5. EXISTING BACKLOG & ISSUE AWARENESS:
   - Existing backlog tasks and issues are maintained as Markdown specifications in 'docs/tasks/'.
   - Before proposing new tasks, inspect 'docs/tasks/' (using 'read' or 'bash' e.g. ls docs/tasks) to understand currently planned, in-progress, or completed work.
   - Do NOT propose duplicate tasks for issues already present in the backlog.
   - You may reference existing task IDs or link dependencies (via dependsOnTitles) to existing backlog tasks when planning follow-ups or prerequisite work.
</task_planning_guidelines>";

        // Skills catalog (lean ~25 tokens/skill) is only injected when the agent can call load_skill.
        IReadOnlyList<SkillDescriptor> discoveredSkills = Array.Empty<SkillDescriptor>();
        if (permittedTools.Contains(CanonicalToolNames.LoadSkill, StringComparer.OrdinalIgnoreCase))
        {
            discoveredSkills = await _skillsService.DiscoverSkillsAsync(repoPath, cancellationToken: cancellationToken);
        }
        var activeSkills = discoveredSkills.Select(s => s.Name).ToList();
        var skillsCatalog = _skillsService.FormatSkillsCatalogPrompt(discoveredSkills);
        var skillsSection = string.IsNullOrEmpty(skillsCatalog) ? string.Empty : "\n\n" + skillsCatalog;

        var systemPrompt = $@"{basePersona}
{environmentSection.ToString().TrimEnd()}
{projectContext}{skillsSection}

{taskGuidance}";

        // The system prompt is supplied to HarnessAgent as HarnessInstructions (ChatOptions.Instructions);
        // adding it as a System message too would send it to the model twice.
        var messages = new List<ChatMessage>();

        if (conversationHistory != null)
        {
            foreach (var (role, content) in conversationHistory)
            {
                var chatRole = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant;
                messages.Add(new ChatMessage(chatRole, content));
            }
        }

        var currentDate = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        var effectiveUserPrompt = userPrompt;
        var hasDateInHistory = conversationHistory != null && conversationHistory.Any(m => m.Content.Contains("Current Date:", StringComparison.OrdinalIgnoreCase));
        if (!hasDateInHistory && !effectiveUserPrompt.Contains("Current Date:", StringComparison.OrdinalIgnoreCase))
        {
            if (effectiveUserPrompt.Contains("[Context]:", StringComparison.OrdinalIgnoreCase))
            {
                var contextIdx = effectiveUserPrompt.IndexOf("[Context]:", StringComparison.OrdinalIgnoreCase);
                effectiveUserPrompt = effectiveUserPrompt.Insert(contextIdx + "[Context]:".Length, $"\n- Current Date: {currentDate}");
            }
            else
            {
                effectiveUserPrompt = $"{effectiveUserPrompt}\n\n[Context]:\n- Current Date: {currentDate}".Trim();
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, effectiveUserPrompt));

        var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var transcriptEntries = new List<TranscriptEntry>
        {
            new(_timeProvider.GetUtcNow().UtcDateTime, "USER", effectiveUserPrompt)
        };

        var transcriptBuffer = new TranscriptStreamBuffer(transcriptEntries);

        var effectiveEnv = _envProvider.GetEnvironment(workspace, profile.SecurityTier)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var canonicalTools = new CanonicalTools(
            repoPath,
            _pathResolver,
            effectiveEnv,
            _editorConfigService,
            _editorConfigFileApplier,
            _webSearchService,
            _canonicalToolsLogger,
            buildTimeoutSeconds: null,
            testTimeoutSeconds: null,
            workspaceId: workspace.Id,
            taskId: null,
            isReadOnly: profile.Mode == AgentMode.ReadOnly || profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly);

        var sessionProposals = new List<IdeationProposal>();

        // In-turn ask: the tool awaits the user's answers while the stream surfaces questions to the UI.
        var questionChannel = System.Threading.Channels.Channel.CreateUnbounded<IdeationQuestion>();
        canonicalTools.AskHandler = async (questions, ct) =>
        {
            var registrations = new List<CancellationTokenRegistration>();
            try
            {
                var tasks = new List<Task<string>>();
                var questionList = new List<IdeationQuestion>();
                foreach (var q in questions)
                {
                    var question = new IdeationQuestion(q.Prompt, q.Rationale, q.Options);
                    questionList.Add(question);
                    registrations.Add(ct.Register(() => question.Cancel()));
                    tasks.Add(question.AnswerTask);
                    questionChannel.Writer.TryWrite(question);
                }

                var answers = await Task.WhenAll(tasks).ConfigureAwait(false);

                var sb = new StringBuilder();
                for (int i = 0; i < questionList.Count; i++)
                {
                    if (i > 0) sb.AppendLine();
                    sb.AppendLine($"{i + 1}. Question: {questionList[i].Prompt}");
                    sb.AppendLine($"   Answer: {answers[i]}");
                }
                return sb.ToString().TrimEnd();
            }
            finally
            {
                foreach (var reg in registrations)
                {
                    reg.Dispose();
                }
            }
        };
        HumanInterruptionException? interruption = null;

        var aiTools = BuildAIFunctionList(canonicalTools, permittedTools);

        var effectiveModelId = !string.IsNullOrWhiteSpace(profile.ModelName)
            ? profile.ModelName
            : (client.GetService<ChatClientMetadata>()?.DefaultModelId ?? _llmOptions?.ModelId);

        var chatOptions = new ChatOptions
        {
            ModelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : null,
            Temperature = (float?)profile.Temperature ?? 0.3f,
            Tools = aiTools,
            AdditionalProperties = ModelCapabilityResolver.BuildReasoningProperties(_llmOptions, effectiveModelId)
        };

        int maxTurns = profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0 ? profile.MaxTurns.Value : DefaultMaxTurns;

        var harnessOptions = new HarnessAgentOptions
        {
            Id = "ideation-agent",
            Name = "IdeationAgent",
            HarnessInstructions = systemPrompt,
            ChatOptions = chatOptions,
            MaximumIterationsPerRequest = maxTurns,
            CompactionStrategy = ModelCapabilityResolver.BuildCompactionStrategy(_llmOptions, effectiveModelId),
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var tracingClient = new ThinkingAndActivityTracingChatClient(client);
        var effectiveClient = new LenientToolCallChatClient(tracingClient);
        var agent = new HarnessAgent(effectiveClient, harnessOptions);
        var session = await agent.CreateSessionAsync(cancellationToken);

        var projector = new AgentStreamProjector();
        var fullTextBuilder = new StringBuilder();
        var fullReasoningBuilder = new StringBuilder();
        var allToolCalls = new List<IdeationToolCallInfo>();

        bool streamingFailed = false;
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        try
        {
            var stream = agent.RunStreamingAsync(messages, session, cancellationToken: cancellationToken);
            enumerator = stream.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            streamingFailed = true;
        }

        if (!streamingFailed && enumerator != null)
        {
            try
            {
                while (true)
                {
                    bool hasMore;
                    var moveTask = enumerator.MoveNextAsync().AsTask();
                    while (!moveTask.IsCompleted)
                    {
                        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var waitQuestion = questionChannel.Reader.WaitToReadAsync(waitCts.Token).AsTask();
                        await Task.WhenAny(moveTask, waitQuestion).ConfigureAwait(false);
                        waitCts.Cancel();
                        while (questionChannel.Reader.TryRead(out var pendingQuestion))
                        {
                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolStarted, $"ask: {pendingQuestion.Prompt}", cancellationToken, metadata);
                            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.QuestionAsked, Question: pendingQuestion);
                        }
                    }

                    try
                    {
                        hasMore = await moveTask;
                    }
                    catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                    {
                        streamingFailed = true;
                        break;
                    }
                    catch (Exception ex) when (FindInterruption(ex) != null)
                    {
                        interruption = FindInterruption(ex);
                        break;
                    }
                    catch (Exception ex)
                    {
                        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                        throw;
                    }

                    if (!hasMore) break;

                    var update = enumerator.Current;
                    foreach (var domainEvt in projector.ProjectUpdate(update))
                    {
                        if (domainEvt.Kind == IdeationStreamUpdateKind.ThinkingDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                        {
                            fullReasoningBuilder.Append(domainEvt.TextDelta);
                            transcriptBuffer.Append("THINKING", domainEvt.TextDelta);
                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ThinkingDelta, domainEvt.TextDelta, cancellationToken, metadata);
                        }
                        else if (domainEvt.Kind == IdeationStreamUpdateKind.MessageDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                        {
                            fullTextBuilder.Append(domainEvt.TextDelta);
                            transcriptBuffer.Append("ASSISTANT", domainEvt.TextDelta);
                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.MessageDelta, domainEvt.TextDelta, cancellationToken, metadata);
                        }
                        else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallStarted && domainEvt.ToolCall != null)
                        {
                            allToolCalls.Add(domainEvt.ToolCall);
                            transcriptBuffer.Flush();
                            transcriptEntries.Add(new TranscriptEntry(DateTime.UtcNow, "TOOL", string.Empty, domainEvt.ToolCall.ToolName, domainEvt.ToolCall.Arguments));
                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolStarted, $"{domainEvt.ToolCall.ToolName}: {domainEvt.ToolCall.Arguments}", cancellationToken, metadata);
                        }
                        else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallCompleted && domainEvt.ToolCall != null)
                        {
                            transcriptEntries.Add(new TranscriptEntry(DateTime.UtcNow, domainEvt.ToolCall.IsError ? "TOOL_ERROR" : "TOOL_RESULT", domainEvt.ToolCall.Result ?? string.Empty, domainEvt.ToolCall.ToolName));

                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, domainEvt.ToolCall.Result ?? string.Empty, cancellationToken, metadata);

                            if (domainEvt.ToolCall.IsError)
                            {
                                _logger.LogWarning("Ideation tool '{ToolName}' failed or returned error: {ToolResult}",
                                    domainEvt.ToolCall.ToolName, domainEvt.ToolCall.Result);
                            }

                            // Sync any newly discovered proposals from canonicalTools
                            foreach (var p in canonicalTools.PendingProposals)
                            {
                                if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var ideationProp = new IdeationProposal(p.Title, p.Description, p.Priority, p.DependsOnTitles);
                                    sessionProposals.Add(ideationProp);
                                    yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: ideationProp);
                                    await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, $"Proposed task: {ideationProp.Title}", cancellationToken, metadata);
                                }
                            }
                        }

                        yield return domainEvt;
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        if (streamingFailed)
        {
            // No live pump in the non-streaming fallback: an interactive ask would deadlock.
            canonicalTools.AskHandler = null;
            AgentResponse? response = null;
            try
            {
                response = await agent.RunAsync(messages, session, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (FindInterruption(ex) != null)
            {
                interruption = FindInterruption(ex);
            }
            catch (Exception ex)
            {
                await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                throw;
            }

            foreach (var update in response?.ToAgentResponseUpdates() ?? Enumerable.Empty<AgentResponseUpdate>())
            {
                foreach (var domainEvt in projector.ProjectUpdate(update))
                {
                    if (domainEvt.Kind == IdeationStreamUpdateKind.ThinkingDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                    {
                        fullReasoningBuilder.Append(domainEvt.TextDelta);
                        transcriptBuffer.Append("THINKING", domainEvt.TextDelta);
                        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ThinkingDelta, domainEvt.TextDelta, cancellationToken, metadata);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.MessageDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
                    {
                        fullTextBuilder.Append(domainEvt.TextDelta);
                        transcriptBuffer.Append("ASSISTANT", domainEvt.TextDelta);
                        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.MessageDelta, domainEvt.TextDelta, cancellationToken, metadata);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallStarted && domainEvt.ToolCall != null)
                    {
                        allToolCalls.Add(domainEvt.ToolCall);
                        transcriptBuffer.Flush();
                        transcriptEntries.Add(new TranscriptEntry(DateTime.UtcNow, "TOOL", string.Empty, domainEvt.ToolCall.ToolName, domainEvt.ToolCall.Arguments));
                        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolStarted, $"{domainEvt.ToolCall.ToolName}: {domainEvt.ToolCall.Arguments}", cancellationToken, metadata);
                    }
                    else if (domainEvt.Kind == IdeationStreamUpdateKind.ToolCallCompleted && domainEvt.ToolCall != null)
                    {
                        transcriptEntries.Add(new TranscriptEntry(DateTime.UtcNow, domainEvt.ToolCall.IsError ? "TOOL_ERROR" : "TOOL_RESULT", domainEvt.ToolCall.Result ?? string.Empty, domainEvt.ToolCall.ToolName));

                        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, domainEvt.ToolCall.Result ?? string.Empty, cancellationToken, metadata);

                        if (domainEvt.ToolCall.IsError)
                        {
                            _logger.LogWarning("Ideation tool '{ToolName}' failed or returned error: {ToolResult}",
                                domainEvt.ToolCall.ToolName, domainEvt.ToolCall.Result);
                        }

                        // Sync any newly discovered proposals from canonicalTools
                        foreach (var p in canonicalTools.PendingProposals)
                        {
                            if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
                            {
                                var ideationProp = new IdeationProposal(p.Title, p.Description, p.Priority, p.DependsOnTitles);
                                sessionProposals.Add(ideationProp);
                                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: ideationProp);
                                await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, $"Proposed task: {ideationProp.Title}", cancellationToken, metadata);
                            }
                        }
                    }

                    yield return domainEvt;
                }
            }
        }

        foreach (var domainEvt in projector.Flush())
        {
            if (domainEvt.Kind == IdeationStreamUpdateKind.ThinkingDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
            {
                fullReasoningBuilder.Append(domainEvt.TextDelta);
                transcriptBuffer.Append("THINKING", domainEvt.TextDelta);
                await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ThinkingDelta, domainEvt.TextDelta, cancellationToken, metadata);
            }
            else if (domainEvt.Kind == IdeationStreamUpdateKind.MessageDelta && !string.IsNullOrEmpty(domainEvt.TextDelta))
            {
                fullTextBuilder.Append(domainEvt.TextDelta);
                transcriptBuffer.Append("ASSISTANT", domainEvt.TextDelta);
                await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.MessageDelta, domainEvt.TextDelta, cancellationToken, metadata);
            }

            yield return domainEvt;
        }

        // Safety net: an ask that escaped as an exception becomes visible chat text instead of a crash.
        if (interruption != null)
        {
            var prompts = string.Join("\n- ", interruption.Questions.Select(q => q.Prompt));
            var interruptionText = $"\n\n**I need your input before continuing:**\n- {prompts}\n";
            fullTextBuilder.Append(interruptionText);
            transcriptBuffer.Append("ASSISTANT", interruptionText);
            _logger.LogWarning("Ideation ask escaped the live handler; surfaced as chat text: {Prompts}", prompts);
            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.MessageDelta, TextDelta: interruptionText);
        }

        // Final sync of any pending proposals from canonicalTools
        foreach (var p in canonicalTools.PendingProposals)
        {
            if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
            {
                var ideationProp = new IdeationProposal(p.Title, p.Description, p.Priority, p.DependsOnTitles);
                sessionProposals.Add(ideationProp);
                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: ideationProp);
                await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolCompleted, $"Proposed task: {ideationProp.Title}", cancellationToken, metadata);
            }
        }

        transcriptBuffer.Flush();
        sessionStopwatch.Stop();
        string? transcriptPath = null;
        string? fullTranscriptMarkdown = null;

        if (_transcriptService != null)
        {
            try
            {
                var targetDir = Path.Combine(repoPath, "docs", "transcripts");
                var metadataRecord = new TranscriptMetadata(
                    TaskId: workspace?.Id ?? 0,
                    StageId: StandardStageIds.Ideation,
                    AgentProfile: profile.Id,
                    ModelName: profile.ModelName ?? "default",
                    DurationMs: sessionStopwatch.ElapsedMilliseconds,
                    Metrics: new AgentRunMetrics(sessionStopwatch.ElapsedMilliseconds, 0, 0, 0),
                    Optimizations: profile.OptimizationFlags ?? new LocalOptimizationFlags(),
                    Status: AgentRunStatus.Completed,
                    KvCacheHitRate: null,
                    SystemPrompt: tracingClient.LastCapturedSystemPrompt ?? systemPrompt,
                    AvailableTools: tracingClient.LastCapturedTools.Count > 0 ? tracingClient.LastCapturedTools : permittedTools,
                    ActiveSkills: activeSkills,
                    MafConfiguration: MafConfigurationSummary.Build(harnessOptions, tracingClient.DetectedMafConfiguration),
                    ModelCalls: tracingClient.ModelCalls);

                transcriptPath = await _transcriptService.SaveTranscriptAsync(metadataRecord, transcriptEntries, targetDir, cancellationToken);
                if (File.Exists(transcriptPath))
                {
                    fullTranscriptMarkdown = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save ideation session transcript.");
            }
        }

        var fullText = fullTextBuilder.ToString();
        var fallbackProposals = ParseProposals(fullText);

        foreach (var p in fallbackProposals)
        {
            if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
            {
                sessionProposals.Add(p);
                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: p);
            }
        }

        var finalResult = new IdeationResult(fullText, sessionProposals, allToolCalls, transcriptPath, fullTranscriptMarkdown);
        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.StageCompleted, $"Ideation completed with {sessionProposals.Count} proposal(s).", cancellationToken, metadata);

        yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.Completed, FinalResult: finalResult);
    }

    private List<IdeationProposal> ParseProposals(string text)
    {
        var proposals = new List<IdeationProposal>();
        var match = TaskJsonArrayRegex().Match(text);
        if (!match.Success) return proposals;

        var rawJson = match.Value.Trim();
        if (rawJson.StartsWith("```"))
        {
            var firstLine = rawJson.IndexOf('\n');
            var lastTicks = rawJson.LastIndexOf("```");
            if (firstLine > 0 && lastTicks > firstLine)
            {
                rawJson = rawJson.Substring(firstLine + 1, lastTicks - firstLine - 1).Trim();
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<IdeationProposalDto>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed != null && parsed.Count > 0)
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    var p = parsed[i];
                    var deps = new List<string>();
                    if (p.DependsOnTitles != null && p.DependsOnTitles.Count > 0)
                    {
                        deps.AddRange(p.DependsOnTitles.Where(d => !string.IsNullOrWhiteSpace(d)));
                    }

                    // Enforce sequential implicit dependencies if multiple tasks are parsed:
                    // Task i (for i > 0) depends on Task i - 1.
                    if (parsed.Count > 1 && i > 0)
                    {
                        var prevTitle = parsed[i - 1].Title?.Trim();
                        if (!string.IsNullOrWhiteSpace(prevTitle) &&
                            !deps.Any(d => string.Equals(d, prevTitle, StringComparison.OrdinalIgnoreCase)))
                        {
                            deps.Add(prevTitle);
                        }
                    }

                    proposals.Add(new IdeationProposal(
                        p.Title ?? "Untitled Proposal",
                        p.Description ?? string.Empty,
                        p.Priority <= 0 ? TaskPriority.Normal : p.Priority,
                        deps));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse proposed tasks JSON from ideation response.");
        }

        return proposals;
    }

    private async Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
    }

    private static HumanInterruptionException? FindInterruption(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is HumanInterruptionException hie) return hie;
        }
        return null;
    }

    private static IList<AITool> BuildAIFunctionList(CanonicalTools canonicalTools, IReadOnlyList<string> permittedTools)
    {
        return CanonicalAIFunctionFactory.CreateTools(canonicalTools, permittedTools).Cast<AITool>().ToList();
    }


    public class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; } = 1;
        public List<string>? DependsOnTitles { get; set; }
    }
}


