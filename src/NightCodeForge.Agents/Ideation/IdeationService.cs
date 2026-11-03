using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
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

    private const string IdeationAgentId = "ideation-agent";
    private const string IdeationAgentName = "IdeationAgent";
    private const float DefaultTemperature = 0.3f;

    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex TaskJsonArrayRegex();

    private readonly ICanonicalToolsetFactory _toolsetFactory;
    private readonly IAgentWorkspaceContextService _workspaceContext;
    private readonly IHarnessAgentFactory _agentFactory;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly SessionTranscriptService _transcriptService;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ILogger<IdeationService> _logger;
    private readonly TimeProvider _timeProvider;

    public IdeationService(
        ICanonicalToolsetFactory toolsetFactory,
        IAgentWorkspaceContextService workspaceContext,
        IHarnessAgentFactory agentFactory,
        IAgentActivityStreamer activityStreamer,
        SessionTranscriptService transcriptService,
        IWorkspaceRepository workspaceRepository,
        ILogger<IdeationService> logger,
        TimeProvider timeProvider)
    {
        _toolsetFactory = toolsetFactory ?? throw new ArgumentNullException(nameof(toolsetFactory));
        _workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var sessionId = $"ideation-{workspace.Id}-{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace.Id,
            ["agentProfile"] = profile.Id,
            ["model"] = profile.ModelName
        };

        await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.StageStarted, "Starting ideation discovery", cancellationToken, metadata);

        // Pre-sync existing backlog tasks into clone/docs/tasks so the ideation agent has full visibility of existing tasks
        var locations = _workspaceContext.ResolveLocations(workspace, taskId: null, workspacePathOverride: null);
        var repoPath = locations.RepoPath;
        await _workspaceContext.SyncBacklogToDiskAsync(workspace, locations, cancellationToken);

        var agentWorkingDir = locations.AgentWorkingDir;
        var topLevelSummary = _workspaceContext.DescribeTopLevelEntries(workspace, repoPath);
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
                : AgentProfile.DefaultIdeationSystemPrompt);

        var permittedTools = profile.PermittedTools?.Count > 0
            ? profile.PermittedTools
            : DefaultAgentTools.Ideation;

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

3. MANDATORY UPFRONT FUNCTIONAL & BUSINESS RESOLUTION (ZERO OPEN BUSINESS QUESTIONS):
   - Tasks must enter the execution queue fully determined from a functional and business standpoint (""zero-surprise scheduling"").
   - Before invoking 'propose_tasks' or 'edit_task', conduct an interactive interview with the maintainer (using 'ask') to resolve the user problem, expected behaviors, business rules, edge cases, and acceptance criteria.
   - Do NOT leave 'Open Questions', 'TBD', or similar placeholder sections about functional or business matters in a task description. If a functional question remains, ask it now.
   - You are NOT required to identify all technical impacts (such as exhaustive affected-file lists or symbol mapping). Detailed technical scoping is handled downstream by Stage 1 (ForgeTask Review), which can still ask purely technical questions after inspecting the code.

4. TASK PROPOSAL INVOCATION:
   - When you recommend one or more implementation tasks, do NOT output markdown JSON codeblocks in your response.
   - Instead, invoke the 'propose_tasks' tool with your proposed tasks (each having title, description, and optional priority).
   - Structure each task's description with clear Markdown sections (## Summary, ## Functional Scope / Sub-tasks, ## Acceptance Criteria, ## Testing Plan).
   - If multiple tasks are proposed, they will be scheduled sequentially with dependencies established automatically (or via dependsOnTitles).
   - After proposing tasks, explain your proposal and architectural rationale clearly to the developer.

5. EXISTING BACKLOG & ISSUE AWARENESS:
   - Existing backlog tasks and issues are maintained as Markdown specifications in 'docs/tasks/'.
   - Before proposing new tasks, inspect 'docs/tasks/' (using 'read' or 'bash' e.g. ls docs/tasks) to understand currently planned, in-progress, or completed work.
   - Do NOT propose duplicate tasks for issues already present in the backlog.
   - You may reference existing task IDs or link dependencies (via dependsOnTitles) to existing backlog tasks when planning follow-ups or prerequisite work.

6. DEPENDENCY IDENTIFICATION:
<dependency_identification_rules>
When planning or proposing tasks:
1. Cross-reference your ideas against the `<existing_workspace_tasks>` list above (which contains top active backlog tasks ordered by priority).
2. DO NOT propose duplicate tasks for capabilities already captured in existing tasks or already merged in the codebase.
3. If your proposed task builds on, calls, or extends interfaces or data models introduced in an existing open/suggested task, you MUST set 'dependsOnTitles' in your 'propose_tasks' call with the title or numeric ID (e.g., '#101' or '101') of the prerequisite task.
4. Explain to the developer why this dependency exists and the recommended order of execution.
5. AVOID CIRCULAR DEPENDENCIES: Ensure your dependencies form a strict acyclic graph. Circular dependencies will be rejected with an error.
</dependency_identification_rules>
</task_planning_guidelines>

<backlog_management_guidelines>
You cannot edit repository files or 'docs/tasks/*.md' directly. Existing backlog tasks are managed exclusively through these host-level tools, which are audited and keep the markdown specifications in sync:
- 'get_task' (task_id): fetch the full details (description, status, priority, merge mode, base branch, prerequisites, dependents) of a task listed in <existing_workspace_tasks>. Call it before refining a task, instead of guessing its content.
- 'edit_task' (task_id, title?, description?, priority?, depends_on_ids?, merge_mode?, base_branch?): refine an existing task. Omitted arguments are unchanged; 'description' and 'depends_on_ids' replace the existing value, so start from the 'get_task' output. Resolve functional questions with the maintainer ('ask') BEFORE calling it.
- 'set_task_priority' (task_id, priority) and 'rework_priorities' (updates: [{ task_id, priority }]): change priority tiers (1 Critical to 5 Trivial) of one or several tasks; prefer 'rework_priorities' when reordering several tasks.
- 'remove_task' (task_id, reason): cancel a task that is obsolete or duplicated. It is refused while active tasks depend on it; re-link or remove those dependents first.
Only tasks that are Suggested or Open, never executed, and not currently running can be changed. Tasks that already entered the pipeline (or are Reviewed, Implemented, Ready, Closed, Rejected, Cancelled) are rejected with an error: do not retry, create a follow-up task via 'propose_tasks' instead. Never mutate the backlog without the maintainer's agreement; summarize every change you made afterwards.
</backlog_management_guidelines>";

        var toolset = await _toolsetFactory.CreateAsync(
            new CanonicalToolsetRequest(
                workspace,
                profile,
                new CanonicalToolScope(
                    repoPath,
                    WorkspaceId: workspace.Id,
                    IsReadOnly: profile.Mode == AgentMode.ReadOnly || profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly),
                permittedTools,
                Array.Empty<CustomToolConfig>(),
                new Dictionary<string, string>(),
                AttachBacklogMutationService: true),
            cancellationToken);
        var canonicalTools = toolset.Tools;

        // Skills catalog (lean ~25 tokens/skill) is only injected when the agent can call load_skill.
        var promptContext = await _workspaceContext.BuildPromptContextAsync(workspace, repoPath, permittedTools, toolset.SkillsService, cancellationToken);
        var activeSkills = promptContext.ActiveSkills;
        var skillsSection = string.IsNullOrEmpty(promptContext.SkillsCatalog) ? string.Empty : "\n\n" + promptContext.SkillsCatalog;

        var backlogDigest = await _workspaceContext.BuildBacklogDigestAsync(workspace.Id, null, cancellationToken);
        var backlogSection = string.IsNullOrWhiteSpace(backlogDigest) ? string.Empty : backlogDigest + "\n\n";
        var systemPrompt = $@"{basePersona}
{environmentSection.ToString().TrimEnd()}
{promptContext.ProjectContext}{skillsSection}

{backlogSection}{taskGuidance}";

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

        var sessionProposals = new List<IdeationProposal>();

        // In-turn ask: the tool awaits the user's answers while the stream surfaces questions to the UI.
        var questionChannel = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<IdeationQuestion>>();
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
                }

                questionChannel.Writer.TryWrite(questionList);

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

        int maxTurns = profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0 ? profile.MaxTurns.Value : DefaultMaxTurns;

        var setup = await _agentFactory.CreateAsync(
            new HarnessAgentRequest(
                profile,
                sessionId,
                AgentId: IdeationAgentId,
                AgentName: IdeationAgentName,
                Instructions: systemPrompt,
                Tools: toolset.AITools,
                MaxTurns: maxTurns,
                CreateTracingClient: baseClient => new ThinkingAndActivityTracingChatClient(baseClient),
                DefaultTemperature: DefaultTemperature),
            cancellationToken);

        var tracingClient = setup.TracingClient;
        var harnessOptions = setup.Options;
        var agent = setup.Agent;
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
                        while (questionChannel.Reader.TryRead(out var pendingQuestionBatch))
                        {
                            var firstQ = pendingQuestionBatch.FirstOrDefault();
                            var promptSummary = pendingQuestionBatch.Count > 1
                                ? $"Batch ({pendingQuestionBatch.Count} questions): {firstQ?.Prompt}"
                                : (firstQ?.Prompt ?? "Clarification requested");
                            await BroadcastAsync(sessionId, 0, StandardStageIds.Ideation, AgentActivityKind.ToolStarted, $"ask: {promptSummary}", cancellationToken, metadata);
                            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.QuestionAsked, Question: firstQ, Questions: pendingQuestionBatch);
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

    public class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; } = 1;
        public List<string>? DependsOnTitles { get; set; }
    }
}


