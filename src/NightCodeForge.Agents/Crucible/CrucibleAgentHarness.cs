using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible.ChatClients;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Crucible;

public partial class CrucibleAgentHarness : IAgentHarness
{
    /// <summary>Maximum consecutive "resume" prompts sent after a response cut off mid-thought before the stage fails.</summary>
    public const int MaxConsecutiveTruncationNudges = 3;

    public const string TruncationContinuationPrompt =
        "[System Notice]: Your previous response was interrupted mid-thought before calling a tool. Please resume immediately and proceed with your planned action or tool call.";

    private readonly ICanonicalToolsetFactory _toolsetFactory;
    private readonly IAgentWorkspaceContextService _workspaceContext;
    private readonly IHarnessAgentFactory _agentFactory;
    private readonly ICrucibleConversationCoordinator _conversationCoordinator;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IOperationsControlService _operationsControlService;
    private readonly ILogger<CrucibleAgentHarness> _logger;
    private readonly LlmRetryPolicyOptions _retryOptions;
    private readonly TimeProvider _timeProvider;

    private static readonly TimeSpan PauseAbortTimeout = TimeSpan.FromSeconds(5);

    public string HarnessId => StandardHarnessIds.Crucible;

    public CrucibleAgentHarness(
        ICanonicalToolsetFactory toolsetFactory,
        IAgentWorkspaceContextService workspaceContext,
        IHarnessAgentFactory agentFactory,
        ICrucibleConversationCoordinator conversationCoordinator,
        IAgentActivityStreamer activityStreamer,
        ITaskCancellationManager cancellationManager,
        IOperationsControlService operationsControlService,
        Microsoft.Extensions.Options.IOptions<LlmRetryPolicyOptions> retryOptions,
        ILogger<CrucibleAgentHarness> logger,
        TimeProvider timeProvider)
    {
        _toolsetFactory = toolsetFactory ?? throw new ArgumentNullException(nameof(toolsetFactory));
        _workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _conversationCoordinator = conversationCoordinator ?? throw new ArgumentNullException(nameof(conversationCoordinator));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        ArgumentNullException.ThrowIfNull(retryOptions);
        _retryOptions = retryOptions.Value ?? throw new ArgumentNullException(nameof(retryOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        context.OptimizationFlags = _agentFactory.ResolveOptimizationFlags(context.Profile, context.OptimizationFlags);
        var stopwatch = Stopwatch.StartNew();
        long promptTokens = 0;
        long completionTokens = 0;
        var sessionId = Guid.NewGuid().ToString("N");

        // Resolves/starts the database conversation, restores the history, applies priming and the human answer.
        var conversationState = await _conversationCoordinator.BeginAsync(context, HarnessId, cancellationToken);
        var conversation = conversationState.Conversation;
        var chatHistory = conversationState.ChatHistory;

        // Announced once the conversation record exists so a live UI can find and follow it.
        var stageMetadata = new Dictionary<string, object?>
        {
            [AgentActivityMetadataKeys.TaskTitle] = context.ForgeTask.Title,
            [AgentActivityMetadataKeys.Priority] = context.ForgeTask.Priority,
            [AgentActivityMetadataKeys.QueueType] = "llm-inference",
            [AgentActivityMetadataKeys.StageKind] = "Agentic"
        };
        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken, stageMetadata);

        // 1. Parse database-driven HarnessConfigurationJson if provided
        var harnessConfig = ParseHarnessConfig(context.Profile.HarnessConfigurationJson);

        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required for agent stage execution.");
        var taskId = context.ForgeTask.Id > 0 ? context.ForgeTask.Id : (int?)null;
        var locations = _workspaceContext.ResolveLocations(workspace, taskId, context.WorkspacePath);
        var workspacePath = locations.RepoPath;

        var projectConfig = CodeStackPresets.ResolveConfig(workspacePath, null);
        var buildTimeout = workspace.BuildTimeoutSeconds ?? projectConfig.BuildTimeoutSeconds;
        var testTimeout = workspace.TestTimeoutSeconds ?? projectConfig.TestTimeoutSeconds;

        // 2. Resolve Active Stage Permitted Tools (Database/Profile Configurable) and initialize Tools & Lenient Dispatcher
        var permittedTools = ResolvePermittedTools(harnessConfig, context.Profile, context.RequiredMode);

        var toolset = await _toolsetFactory.CreateAsync(
            new CanonicalToolsetRequest(
                workspace,
                context.Profile,
                new CanonicalToolScope(
                    workspacePath,
                    WorkspaceId: workspace.Id,
                    TaskId: taskId,
                    BuildTimeoutSeconds: buildTimeout,
                    TestTimeoutSeconds: testTimeout,
                    IsReadOnly: context.Profile.Mode == AgentMode.ReadOnly || context.Profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly),
                permittedTools,
                harnessConfig.CustomTools,
                harnessConfig.ToolDirectives,
                AttachBacklogMutationService: false),
            cancellationToken);
        var canonicalTools = toolset.Tools;
        var dispatcher = toolset.Dispatcher;

        using var interruptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NightCodeForge.Core.Workflow.HumanInterruptionException? capturedInterruption = null;
        canonicalTools.AskHandler = (questions, ct) =>
        {
            capturedInterruption = new NightCodeForge.Core.Workflow.HumanInterruptionException(questions);
            interruptionCts.Cancel();
            throw new OperationCanceledException(interruptionCts.Token);
        };

        NightCodeForge.Core.Workflow.RequestChangesException? capturedRequestChanges = null;
        canonicalTools.RequestChangesHandler = (feedback, reason) =>
        {
            capturedRequestChanges = new NightCodeForge.Core.Workflow.RequestChangesException(feedback, reason);
            interruptionCts.Cancel();
            throw new OperationCanceledException(interruptionCts.Token);
        };

        // Rehydrate any pending proposals previously recorded in DataBag
        var existingProposals = context.PipelineContext?.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals);
        if (existingProposals is { Count: > 0 })
        {
            canonicalTools.PendingProposals.AddRange(existingProposals);
        }

        // 3. Discover context: AGENTS.md, Skills (Microsoft Agent Framework), Symbols
        var promptContext = await _workspaceContext.BuildPromptContextAsync(workspace, workspacePath, permittedTools, toolset.SkillsService, cancellationToken);

        string discoveredSymbols = string.Empty;
        if (context.OptimizationFlags.DeterministicSymbolMap)
        {
            var taskText = $"{context.ForgeTask.Title} {context.ForgeTask.Description}";
            discoveredSymbols = await _workspaceContext.GetSymbolMapPromptAsync(workspacePath, taskText, context.OptimizationFlags, cancellationToken);
        }

        var activeSkills = promptContext.ActiveSkills;

        // 4. Tool schemas are provided natively via typed AIFunctions (see CanonicalToolsetFactory.BuildAIFunctionList);
        //    no hand-written <tools> prompt is injected.

        // 5. Build 4-Layer Stabilized Prompt Prefix
        var prefixStabilizer = new PromptPrefixStabilizer();
        var stageDirective = !string.IsNullOrWhiteSpace(context.StageDirective)
            ? context.StageDirective
            : WorkspacePromptResolver.ResolveStageDirective(workspacePath, context.StageId, GetStageDirective(harnessConfig, context.StageId));
        var taskDocPath = context.TaskMarkdownPath ?? context.PipelineContext?.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
        var taskDate = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        var taskScope = BuildTaskScope(context.ForgeTask, taskDocPath, taskDate);
        var workspaceSystemPrompt = WorkspacePromptResolver.ResolveSystemPrompt(workspacePath);
        var customPersona = !string.IsNullOrWhiteSpace(workspaceSystemPrompt)
            ? workspaceSystemPrompt
            : (!string.IsNullOrWhiteSpace(context.Profile.SystemPrompt)
                ? context.Profile.SystemPrompt
                : harnessConfig.CustomPersona);

        // The backlog digest only serves the Stage 1 dependency audit, and stays in Layer 3 (Task Scope).
        string? backlogDigest = null;
        if (context.StageId is StandardStageIds.TaskReview or "stage-1")
        {
            backlogDigest = await _workspaceContext.BuildBacklogDigestAsync(workspace.Id, taskId, cancellationToken);
        }

        var stabilizedPrefix = prefixStabilizer.BuildStabilizedPrefix(
            stageDirective,
            taskScope,
            promptContext.ProjectContext,
            promptContext.SkillsCatalog,
            discoveredSymbols,
            context.OptimizationFlags,
            customRulesPrompt: harnessConfig.CustomSystemRules,
            customRolePersona: customPersona,
            workingDirectory: locations.AgentWorkingDir,
            backlogDigest: backlogDigest);

        // 6. Assemble the agent and its middlewares.
        string latestSerializedState = SerializeSession(chatHistory);
        int maxTurns = ResolveMaxTurns(harnessConfig, context.Profile);

        var setup = await _agentFactory.CreateAsync(
            new HarnessAgentRequest(
                context.Profile,
                LlmSessionIds.ForStage(context.ForgeTask.Id, context.StageId),
                AgentId: context.StageId,
                AgentName: context.Profile.Name ?? context.Profile.Id,
                Instructions: stabilizedPrefix,
                Tools: toolset.AITools,
                MaxTurns: maxTurns,
                CreateTracingClient: baseClient => new ThinkingAndActivityTracingChatClient(
                    baseClient,
                    onEntry: entry => RecordTracedEntry(conversationState, entry),
                    activityStreamer: _activityStreamer,
                    sessionId: sessionId,
                    taskId: context.ForgeTask.Id,
                    stageId: context.StageId),
                DecorateTracingClient: tracing => new PerServiceCallChatHistoryPersistingChatClient(
                    tracing,
                    history =>
                    {
                        chatHistory.Clear();
                        chatHistory.AddRange(history);
                        latestSerializedState = SerializeSession(chatHistory);
                        return Task.CompletedTask;
                    },
                    _logger),
                GrammarFlags: context.OptimizationFlags),
            cancellationToken);

        var tracingClient = setup.TracingClient;
        var activeClient = setup.ActiveClient;
        var harnessOptions = setup.Options;
        var agent = setup.Agent;
        var session = await agent.CreateSessionAsync(cancellationToken);

        string? targetRouting = null;
        var accumulatedText = new StringBuilder();
        var accumulatedReasoning = new StringBuilder();
        bool streamingFailed = false;
        bool sawToolCallInPass = false;

        AgentRunMetrics CurrentMetrics() =>
            new(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens);

        async Task<string?> SaveAsync(AgentRunStatus status, string? summary, CancellationToken ct)
        {
            return await _conversationCoordinator.SaveTranscriptAsync(
                conversationState,
                new CrucibleTranscriptRequest(
                    context,
                    workspacePath,
                    chatHistory,
                    stopwatch.ElapsedMilliseconds,
                    promptTokens,
                    completionTokens,
                    status,
                    summary,
                    tracingClient,
                    harnessOptions,
                    activeSkills),
                ct);
        }

        async Task<AgentRunResult> CompleteWithRequestChangesAsync(RequestChangesException reqChange)
        {
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);

            var summary = $"Reviewer requested changes: {reqChange.Feedback}";
            await SaveAsync(AgentRunStatus.Completed, summary, cancellationToken);
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);

            var proposals = canonicalTools.PendingProposals.Count > 0
                ? (IReadOnlyList<TaskProposal>)canonicalTools.PendingProposals.ToList()
                : null;

            return AgentRunResult.Completed(
                CurrentMetrics(),
                commitHash: null,
                updatedState: latestSerializedState,
                summary: summary,
                targetRouting: reqChange.TargetStageId,
                reviewerFeedback: reqChange.Feedback,
                proposals: proposals);
        }

        async Task<AgentRunResult> CompleteWithInterruptionAsync(HumanInterruptionException interruption)
        {
            stopwatch.Stop();

            var answeredCallIds = new HashSet<string>(
                chatHistory.SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .Where(frc => !string.IsNullOrEmpty(frc.CallId))
                    .Select(frc => frc.CallId!));

            FunctionCallContent? pendingAsk = chatHistory
                .Where(m => m.Role == ChatRole.Assistant)
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .FirstOrDefault(f => string.Equals(f.Name, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase) && !answeredCallIds.Contains(f.CallId));

            string callId;
            if (pendingAsk != null && !string.IsNullOrEmpty(pendingAsk.CallId))
            {
                callId = pendingAsk.CallId;
            }
            else
            {
                var tracerCallId = tracingClient?.CapturedEntries
                    .LastOrDefault(e => string.Equals(e.Role, "TOOL", StringComparison.OrdinalIgnoreCase) && string.Equals(e.ToolName, CanonicalToolNames.Ask, StringComparison.OrdinalIgnoreCase))?.CallId;
                var existingConvCallId = conversationState.FindLastAskCallId();

                callId = !string.IsNullOrEmpty(tracerCallId)
                    ? tracerCallId
                    : (!string.IsNullOrEmpty(existingConvCallId) ? existingConvCallId : Guid.NewGuid().ToString("N"));

                var questionItems = interruption.Questions.Select(q => new QuestionItem(
                    q.Prompt,
                    q.Rationale,
                    !string.IsNullOrWhiteSpace(q.OptionsJson)
                        ? JsonSerializer.Deserialize<List<string>>(q.OptionsJson)
                        : null
                )).ToList();

                var askArgs = new Dictionary<string, object?> { ["questions"] = questionItems };
                chatHistory.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent(callId, CanonicalToolNames.Ask, askArgs)
                }));
            }

            // Exactly one Tool turn for ask with callId must be present in DB
            if (conversationState.TryRecordToolCall(callId))
            {
                var questionItems = interruption.Questions.Select(q => new QuestionItem(
                    q.Prompt,
                    q.Rationale,
                    !string.IsNullOrWhiteSpace(q.OptionsJson)
                        ? JsonSerializer.Deserialize<List<string>>(q.OptionsJson)
                        : null
                )).ToList();
                var askArgsJson = JsonSerializer.Serialize(new { questions = questionItems });
                var metadataJson = JsonSerializer.Serialize(new { CallId = callId });

                conversationState.EnqueueTurn(
                    StageTurnRole.Tool,
                    $"Failed to record ask tool turn for conversation #{conversation.Id}",
                    toolName: CanonicalToolNames.Ask,
                    toolArguments: askArgsJson,
                    metadataJson: metadataJson);
            }

            latestSerializedState = SerializeSession(chatHistory);

            var summaryPrompt = string.Join("; ", interruption.Questions.Select(q => q.Prompt));
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, summaryPrompt, cancellationToken);
            await SaveAsync(AgentRunStatus.WaitingForHuman, summaryPrompt, cancellationToken);

            return AgentRunResult.WaitingForHuman(
                interruption.Questions,
                CurrentMetrics(),
                updatedState: latestSerializedState,
                summary: "Crucible paused awaiting human clarification");
        }

        async Task<AgentRunResult> FailAsync(string errorMsg)
        {
            stopwatch.Stop();
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, errorMsg, cancellationToken);
            await SaveAsync(AgentRunStatus.Failed, errorMsg, cancellationToken);

            return AgentRunResult.Failed(errorMsg, CurrentMetrics(), latestSerializedState);
        }

        async Task AbortOnCancellationAsync()
        {
            // The caller's token is already cancelled: close the conversation with a token that still works,
            // otherwise the stage conversation would stay "Running" forever.
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, "Stage cancelled", timeoutCts.Token);
            await SaveAsync(AgentRunStatus.Cancelled, "Stage cancelled", timeoutCts.Token);
        }

        async Task SuspendOnPauseAsync()
        {
            stopwatch.Stop();
            try
            {
                interruptionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Justification: CTS was already disposed upon stream completion
            }
            SanitizeInFlightTurns(chatHistory);
            latestSerializedState = SerializeSession(chatHistory);
            using var timeoutCts = new CancellationTokenSource(PauseAbortTimeout);
            await conversationState.DrainAsync();
            await conversationState.SanitizeTrailingInterruptedTurnsAsync(timeoutCts.Token);
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, "Operations paused (GPU VRAM reclaimed)", timeoutCts.Token);
            await SaveAsync(AgentRunStatus.Interrupted, "Operations paused (GPU VRAM reclaimed)", timeoutCts.Token);

            if (context.Workspace != null)
            {
                try
                {
                    await _toolsetFactory.CleanupTaskContainerAsync(context.Workspace, context.ForgeTask.Id, timeoutCts.Token);
                }
                catch (Exception pex)
                {
                    _logger.LogWarning(pex, "Failed to clean up container during pause suspension for task #{TaskId}", context.ForgeTask.Id);
                }
            }
        }

        async Task WaitRetryBackoffAsync(TimeSpan delay)
        {
            using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(interruptionCts.Token);
            void OnStateChanged(bool isPaused)
            {
                if (isPaused)
                {
                    try
                    {
                        pauseCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Justification: pauseCts may already be disposed if delay completed concurrently.
                    }
                }
            }

            _operationsControlService.OnOperationsStateChanged += OnStateChanged;
            try
            {
                if (_cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused)
                {
                    await SuspendOnPauseAsync();
                    throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).");
                }

                await Task.Delay(delay, _timeProvider, pauseCts.Token);
            }
            catch (OperationCanceledException)
            {
                var isPause = _cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused;
                if (isPause)
                {
                    await SuspendOnPauseAsync();
                    throw;
                }
                await AbortOnCancellationAsync();
                throw;
            }
            finally
            {
                _operationsControlService.OnOperationsStateChanged -= OnStateChanged;
            }

            if (_cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused)
            {
                await SuspendOnPauseAsync();
                throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).");
            }
        }

        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        void FlushTurnToHistory()
        {
            if (accumulatedText.Length > 0 || accumulatedReasoning.Length > 0)
            {
                var text = accumulatedText.ToString();
                var reasoning = accumulatedReasoning.ToString();

                var lastMsg = chatHistory.LastOrDefault();
                if (lastMsg == null || lastMsg.Role != ChatRole.Assistant)
                {
                    var assistantMsg = new ChatMessage(ChatRole.Assistant, text);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        assistantMsg.Contents.Add(new TextReasoningContent(reasoning));
                    }
                    chatHistory.Add(assistantMsg);
                }
                else
                {
                    if (string.IsNullOrEmpty(lastMsg.Text) && !string.IsNullOrEmpty(text))
                    {
                        lastMsg.Contents.Insert(0, new TextContent(text));
                    }
                    if (!string.IsNullOrEmpty(reasoning) && !lastMsg.Contents.OfType<TextReasoningContent>().Any())
                    {
                        lastMsg.Contents.Add(new TextReasoningContent(reasoning));
                    }
                }

                accumulatedText.Clear();
                accumulatedReasoning.Clear();
                latestSerializedState = SerializeSession(chatHistory);
            }
        }

        // Returns true when the loop circuit breaker asks to abort the current turn.
        async Task<bool> ProcessUpdateAsync(AgentResponseUpdate update, bool streaming)
        {
            // While streaming, ThinkingAndActivityTracingChatClient already broadcasts reasoning and message
            // deltas; broadcasting them again here would duplicate every delta in the activity feed.
            var broadcastDeltas = !streaming;
            var sawUsageContent = false;

            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        sawToolCallInPass = true;
                        FlushTurnToHistory();
                        var canonicalName = dispatcher.ResolveToolName(fc.Name);
                        if (!string.IsNullOrEmpty(fc.CallId))
                        {
                            toolNamesByCallId[fc.CallId] = fc.Name;
                        }
                        var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken, new Dictionary<string, object?> { [AgentActivityMetadataKeys.ToolName] = canonicalName });
                    }
                    else if (content is FunctionResultContent frc)
                    {
                        var resultStr = frc.Result?.ToString() ?? string.Empty;
                        var resultToolName = frc.CallId != null && toolNamesByCallId.TryGetValue(frc.CallId, out var calledName) ? calledName : null;
                        var resultIsError = resultStr.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                            || resultStr.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase);
                        var metadataJson = JsonSerializer.Serialize(new
                        {
                            CallId = frc.CallId,
                            ToolName = resultToolName,
                            OptimizationFlags = context.OptimizationFlags,
                            PendingProposalsCount = canonicalTools.PendingProposals.Count
                        });

                        conversationState.EnqueueTurn(
                            StageTurnRole.ToolResult,
                            $"Failed to record tool result turn for conversation #{conversation.Id}",
                            toolName: resultToolName,
                            toolResult: resultStr,
                            isError: resultIsError,
                            metadataJson: metadataJson);

                        // Sync in-flight DataBag state and task persistence across tool execution milestones
                        if (context.PipelineContext?.DataBag != null)
                        {
                            // Always mirror the proposals (an emptied list must overwrite a stale persisted one).
                            context.PipelineContext.DataBag.Set(PipelineDataBagKeys.PendingProposals, canonicalTools.PendingProposals.ToList());
                            if (context.OptimizationFlags != null)
                            {
                                context.PipelineContext.DataBag.Set(PipelineDataBagKeys.OptimizationFlags, context.OptimizationFlags);
                            }
                            if (context.DiscoveredSymbols != null)
                            {
                                context.PipelineContext.DataBag.Set(PipelineDataBagKeys.DiscoveredSymbols, context.DiscoveredSymbols);
                            }
                            // The conversation history itself is NOT mirrored here: it is already persisted turn by turn
                            // in ConversationTurns and rebuilt from there by StartupRecoveryService.

                            context.ForgeTask.PipelineDataJson = context.PipelineContext.DataBag.ToJson();
                            conversationState.EnqueueTaskUpdate(context.ForgeTask, cancellationToken);
                        }

                        await BroadcastAsync(
                            sessionId,
                            context.ForgeTask.Id,
                            context.StageId,
                            AgentActivityKind.ToolCompleted,
                            resultStr,
                            cancellationToken,
                            new Dictionary<string, object?> { [AgentActivityMetadataKeys.ToolName] = resultToolName ?? "tool" });
                    }
                    else if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        accumulatedReasoning.Append(reasoning.Text);
                        if (broadcastDeltas)
                        {
                            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, reasoning.Text, cancellationToken);
                        }
                    }
                    else if (content is UsageContent uc && uc.Details != null)
                    {
                        // Each model call reports its own usage: accumulate across the iterations of the run.
                        sawUsageContent = true;
                        if (uc.Details.InputTokenCount.HasValue) promptTokens += uc.Details.InputTokenCount.Value;
                        if (uc.Details.OutputTokenCount.HasValue) completionTokens += uc.Details.OutputTokenCount.Value;
                    }
                }
            }

            if (update.RawRepresentation is ChatResponseUpdate chatUpdate && chatUpdate.AdditionalProperties != null)
            {
                // The raw "usage" property describes the same call as UsageContent: only use it as a fallback.
                if (!sawUsageContent
                    && chatUpdate.AdditionalProperties.TryGetValue("usage", out var rawUsage)
                    && rawUsage is JsonElement je)
                {
                    if (je.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptVal)) promptTokens += ptVal;
                    if (je.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctVal)) completionTokens += ctVal;
                }

                if (chatUpdate.AdditionalProperties.TryGetValue("target_routing", out var trObj) && trObj is string strVal)
                {
                    targetRouting = strVal;
                }
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                accumulatedText.Append(update.Text);

                if (broadcastDeltas)
                {
                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, update.Text, cancellationToken);
                }

            }

            return false;
        }

        // The agent receives a copy: the persisting middleware rewrites chatHistory in place on every model call.
        int consecutiveTruncationNudges = 0;
        while (true)
        {
            sawToolCallInPass = false;
            var historySnapshot = chatHistory.ToList();
            var maxTransientRetries = _retryOptions.MaxRetries;
            int streamRetryCount = 0;
            long? streamOutageStartTimestamp = null;

            IAsyncEnumerator<AgentResponseUpdate>? enumerator = agent.RunStreamingAsync(historySnapshot.ToList(), session, cancellationToken: interruptionCts.Token)
                .GetAsyncEnumerator(interruptionCts.Token);
            try
            {
                while (true)
                {
                    bool hasMore;
                    try
                    {
                        hasMore = await enumerator.MoveNextAsync();
                    }
                    catch (RequestChangesException reqChange)
                    {
                        return await CompleteWithRequestChangesAsync(reqChange);
                    }
                    catch (HumanInterruptionException interruption)
                    {
                        return await CompleteWithInterruptionAsync(interruption);
                    }
                    catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                    {
                        streamingFailed = true;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        if (capturedInterruption != null)
                        {
                            return await CompleteWithInterruptionAsync(capturedInterruption);
                        }
                        if (capturedRequestChanges != null)
                        {
                            return await CompleteWithRequestChangesAsync(capturedRequestChanges);
                        }
                        var isPause = _cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused;
                        if (isPause)
                        {
                            await SuspendOnPauseAsync();
                            throw;
                        }
                        await AbortOnCancellationAsync();
                        throw;
                    }
                    catch (Exception ex) when (HttpErrorDetailHelper.FindException<NightCodeForge.Core.Exceptions.OperationsPausedException>(ex) is { } opex)
                    {
                        _logger.LogInformation(opex, "Crucible streaming execution paused by OperationsPausedException for task #{TaskId}", context.ForgeTask.Id);
                        await SuspendOnPauseAsync();
                        throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).", opex);
                    }
                    catch (Exception ex) when (TransientErrorDetector.IsTransient(ex))
                    {
                        if (_cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused)
                        {
                            await SuspendOnPauseAsync();
                            throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).", ex);
                        }

                        streamOutageStartTimestamp ??= _timeProvider.GetTimestamp();
                        var elapsed = _timeProvider.GetElapsedTime(streamOutageStartTimestamp.Value);
                        if (streamRetryCount >= maxTransientRetries || elapsed >= _retryOptions.MaxRetryDuration)
                        {
                            var failMsg = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                            _logger.LogError(ex, "Crucible streaming LLM invocation exhausted retry policy ({Attempt}/{MaxRetries}, elapsed {Elapsed}): {ErrorMessage}",
                                streamRetryCount, maxTransientRetries, elapsed, failMsg);
                            return await FailAsync(failMsg);
                        }

                        streamRetryCount++;
                        var delay = ResilienceJitterHelper.CalculateJitteredDelay(streamRetryCount, _retryOptions.BaseDelay, _retryOptions.MaxDelay);
                        var remaining = _retryOptions.MaxRetryDuration - elapsed;
                        if (remaining > TimeSpan.Zero && delay > remaining)
                        {
                            delay = remaining;
                        }
                        var errorMsg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;

                        _logger.LogWarning(
                            ex,
                            "Crucible streaming LLM invocation encountered transient network error on attempt {Attempt}/{MaxRetries}: {ErrorMessage}. Retrying after {DelayMs}ms delay...",
                            streamRetryCount, maxTransientRetries, errorMsg, delay.TotalMilliseconds);

                        await BroadcastAsync(
                            sessionId,
                            context.ForgeTask.Id,
                            context.StageId,
                            AgentActivityKind.Notice,
                            $"Transient network error: {errorMsg}. Retrying in {Math.Round(delay.TotalSeconds, 1)}s (attempt {streamRetryCount}/{maxTransientRetries}, elapsed {Math.Round(elapsed.TotalSeconds, 0)}s / {Math.Round(_retryOptions.MaxRetryDuration.TotalSeconds, 0)}s)...",
                            cancellationToken);

                        accumulatedText.Clear();
                        accumulatedReasoning.Clear();

                        await enumerator.DisposeAsync();
                        enumerator = null;
                        await WaitRetryBackoffAsync(delay);

                        session = await agent.CreateSessionAsync(cancellationToken);
                        enumerator = agent.RunStreamingAsync(chatHistory.ToList(), session, cancellationToken: interruptionCts.Token)
                            .GetAsyncEnumerator(interruptionCts.Token);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                        _logger.LogError(ex, "Error during Crucible streaming LLM invocation: {ErrorMessage}", errorMsg);
                        return await FailAsync(errorMsg);
                    }

                    if (!hasMore) break;

                    // Reset streamRetryCount and outage timer upon successful chunk receipt so subsequent turns have their full retry budget
                    streamRetryCount = 0;
                    streamOutageStartTimestamp = null;

                    if (await ProcessUpdateAsync(enumerator.Current, streaming: true))
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync();
                }
            }

            if (capturedInterruption != null)
            {
                return await CompleteWithInterruptionAsync(capturedInterruption);
            }
            if (capturedRequestChanges != null)
            {
                return await CompleteWithRequestChangesAsync(capturedRequestChanges);
            }

            if (streamingFailed)
            {
                // Discard what a partially consumed stream produced so the non-streaming retry does not duplicate it.
                accumulatedText.Clear();
                accumulatedReasoning.Clear();
                chatHistory.Clear();
                chatHistory.AddRange(historySnapshot);

                int nonStreamingRetries = 0;
                long? nonStreamingOutageStartTimestamp = null;
                while (true)
                {
                    try
                    {
                        var response = await agent.RunAsync(chatHistory.ToList(), session, cancellationToken: interruptionCts.Token);
                        foreach (var update in response.ToAgentResponseUpdates())
                        {
                            await ProcessUpdateAsync(update, streaming: false);
                        }
                        break;
                    }
                    catch (RequestChangesException reqChange)
                    {
                        return await CompleteWithRequestChangesAsync(reqChange);
                    }
                    catch (HumanInterruptionException interruption)
                    {
                        return await CompleteWithInterruptionAsync(interruption);
                    }
                    catch (OperationCanceledException)
                    {
                        if (capturedInterruption != null)
                        {
                            return await CompleteWithInterruptionAsync(capturedInterruption);
                        }
                        if (capturedRequestChanges != null)
                        {
                            return await CompleteWithRequestChangesAsync(capturedRequestChanges);
                        }
                        var isPause = _cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused;
                        if (isPause)
                        {
                            await SuspendOnPauseAsync();
                            throw;
                        }
                        await AbortOnCancellationAsync();
                        throw;
                    }
                    catch (Exception ex) when (HttpErrorDetailHelper.FindException<NightCodeForge.Core.Exceptions.OperationsPausedException>(ex) is { } opex)
                    {
                        _logger.LogInformation(opex, "Crucible non-streaming execution paused by OperationsPausedException for task #{TaskId}", context.ForgeTask.Id);
                        await SuspendOnPauseAsync();
                        throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).", opex);
                    }
                    catch (Exception ex) when (TransientErrorDetector.IsTransient(ex))
                    {
                        if (_cancellationManager.IsPauseRequested(context.ForgeTask.Id) || _operationsControlService.IsPaused)
                        {
                            await SuspendOnPauseAsync();
                            throw new OperationCanceledException("Operations paused by user (GPU VRAM reclaimed).", ex);
                        }

                        nonStreamingOutageStartTimestamp ??= _timeProvider.GetTimestamp();
                        var elapsed = _timeProvider.GetElapsedTime(nonStreamingOutageStartTimestamp.Value);
                        if (nonStreamingRetries >= maxTransientRetries || elapsed >= _retryOptions.MaxRetryDuration)
                        {
                            var failMsg = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                            _logger.LogError(ex, "Crucible non-streaming LLM invocation exhausted retry policy ({Attempt}/{MaxRetries}, elapsed {Elapsed}): {ErrorMessage}",
                                nonStreamingRetries, maxTransientRetries, elapsed, failMsg);
                            return await FailAsync(failMsg);
                        }

                        nonStreamingRetries++;
                        var delay = ResilienceJitterHelper.CalculateJitteredDelay(nonStreamingRetries, _retryOptions.BaseDelay, _retryOptions.MaxDelay);
                        var remaining = _retryOptions.MaxRetryDuration - elapsed;
                        if (remaining > TimeSpan.Zero && delay > remaining)
                        {
                            delay = remaining;
                        }
                        var errorMsg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;

                        _logger.LogWarning(
                            ex,
                            "Crucible non-streaming LLM invocation encountered transient network error on attempt {Attempt}/{MaxRetries}: {ErrorMessage}. Retrying after {DelayMs}ms delay...",
                            nonStreamingRetries, maxTransientRetries, errorMsg, delay.TotalMilliseconds);

                        await BroadcastAsync(
                            sessionId,
                            context.ForgeTask.Id,
                            context.StageId,
                            AgentActivityKind.Notice,
                            $"Transient network error: {errorMsg}. Retrying in {Math.Round(delay.TotalSeconds, 1)}s (attempt {nonStreamingRetries}/{maxTransientRetries}, elapsed {Math.Round(elapsed.TotalSeconds, 0)}s / {Math.Round(_retryOptions.MaxRetryDuration.TotalSeconds, 0)}s)...",
                            cancellationToken);

                        accumulatedText.Clear();
                        accumulatedReasoning.Clear();

                        await WaitRetryBackoffAsync(delay);

                        session = await agent.CreateSessionAsync(cancellationToken);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = HttpErrorDetailHelper.FormatLlmInvocationError(ex);
                        _logger.LogError(ex, "Error during Crucible non-streaming LLM invocation: {ErrorMessage}", errorMsg);
                        return await FailAsync(errorMsg);
                    }
                }

                if (capturedInterruption != null)
                {
                    return await CompleteWithInterruptionAsync(capturedInterruption);
                }
                if (capturedRequestChanges != null)
                {
                    return await CompleteWithRequestChangesAsync(capturedRequestChanges);
                }
            }

            FlushTurnToHistory();

            var lastAssistantMsg = chatHistory.LastOrDefault(m => m.Role == ChatRole.Assistant);
            if (sawToolCallInPass)
            {
                consecutiveTruncationNudges = 0;
            }

            if (!InterruptedGenerationDetector.IsTruncated(lastAssistantMsg))
            {
                break;
            }

            consecutiveTruncationNudges++;
            var modelCallsUsed = tracingClient.TotalModelCalls;
            if (consecutiveTruncationNudges > MaxConsecutiveTruncationNudges || modelCallsUsed >= maxTurns)
            {
                var truncationFailure = consecutiveTruncationNudges > MaxConsecutiveTruncationNudges
                    ? $"Model repeatedly emitted truncated responses ({MaxConsecutiveTruncationNudges} consecutive continuation prompts did not recover the turn)."
                    : $"Model response was cut off mid-thought and the turn budget ({maxTurns}) is exhausted.";
                _logger.LogError("Crucible stage for task #{TaskId} failed: {Reason}", context.ForgeTask.Id, truncationFailure);
                return await FailAsync(truncationFailure);
            }

            _logger.LogWarning(
                "Assistant response in conversation #{ConversationId} appears cut off mid-thought. Injecting continuation prompt.",
                conversation.Id);

            chatHistory.Add(new ChatMessage(ChatRole.User, TruncationContinuationPrompt));
            latestSerializedState = SerializeSession(chatHistory);
            conversationState.EnqueueTurn(
                StageTurnRole.User,
                $"Failed to record continuation turn for conversation #{conversation.Id}",
                content: TruncationContinuationPrompt);

            await BroadcastAsync(
                sessionId,
                context.ForgeTask.Id,
                context.StageId,
                AgentActivityKind.Notice,
                $"Response was interrupted mid-thought; asking the model to resume (attempt {consecutiveTruncationNudges}/{MaxConsecutiveTruncationNudges}).",
                cancellationToken);

            harnessOptions.MaximumIterationsPerRequest = Math.Max(1, maxTurns - modelCallsUsed);
            agent = new HarnessAgent(activeClient, harnessOptions);
            session = await agent.CreateSessionAsync(cancellationToken);
        }

        stopwatch.Stop();
        var concludingText = StageSummaryExtractor.GetConcludingText(chatHistory);
        var extraction = StageSummaryExtractor.Extract(concludingText, fallbackSummary: "Stage completed successfully.", explicitTargetRouting: targetRouting);

        var proposals = canonicalTools.PendingProposals.Count > 0
            ? (IReadOnlyList<TaskProposal>)canonicalTools.PendingProposals.ToList()
            : null;

        await SaveAsync(AgentRunStatus.Completed, extraction.Summary, cancellationToken);

        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, extraction.Summary, cancellationToken);

        return AgentRunResult.Completed(
            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
            commitHash: null,
            updatedState: latestSerializedState,
            summary: extraction.Summary,
            targetRouting: extraction.TargetRouting,
            reviewerFeedback: extraction.ReviewerFeedback,
            proposals: proposals);
    }

    private async Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken ct, Dictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
            try
            {
                await _activityStreamer.BroadcastAsync(evt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing live feed must never abort the stage or prevent its transcript from being saved.
                _logger.LogWarning(ex, "Failed to broadcast {Kind} activity for task #{TaskId}", kind, taskId);
            }
        }
    }

    /// <summary>
    /// Builds the &lt;task&gt; scope block of the prompt, neutralising characters that would break out of the
    /// attribute values or close the block early.
    /// </summary>
    public static string BuildTaskScope(ForgeTask task, string? taskDocPath, string? date = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        static string EscapeAttribute(string value) =>
            value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

        var docAttr = !string.IsNullOrWhiteSpace(taskDocPath) ? $" path=\"{EscapeAttribute(taskDocPath)}\"" : string.Empty;
        var dateAttr = !string.IsNullOrWhiteSpace(date) ? $" date=\"{EscapeAttribute(date)}\"" : string.Empty;
        var description = (task.Description ?? string.Empty)
            .Replace("</task>", "&lt;/task&gt;", StringComparison.OrdinalIgnoreCase);

        return $"<task id=\"{task.Id}\" title=\"{EscapeAttribute(task.Title ?? string.Empty)}\"{docAttr}{dateAttr}>\n{description}\n</task>";
    }

    private void RecordTracedEntry(CrucibleConversationState conversationState, TranscriptEntry entry)
    {
        try
        {
            var role = entry.Role switch
            {
                "THINKING" => StageTurnRole.Thinking,
                "TOOL" => StageTurnRole.Tool,
                "TOOL_RESULT" or "TOOL_ERROR" => StageTurnRole.ToolResult,
                "OPTIMIZATION" => StageTurnRole.Optimization,
                "USER" or "User" => StageTurnRole.User,
                _ => StageTurnRole.Assistant
            };

            if (role == StageTurnRole.Tool && !string.IsNullOrEmpty(entry.CallId) && !conversationState.TryRecordToolCall(entry.CallId))
            {
                return;
            }

            var conversationId = conversationState.Conversation.Id;
            var metadataJson = !string.IsNullOrEmpty(entry.CallId)
                ? JsonSerializer.Serialize(new { CallId = entry.CallId })
                : null;

            conversationState.EnqueueTurn(
                role,
                $"Failed to record turn for conversation #{conversationId}",
                content: entry.Content,
                thinkingTrace: entry.Role == "THINKING" ? entry.Content : null,
                toolName: entry.ToolName,
                toolArguments: entry.ToolArguments,
                toolResult: entry.ToolResult,
                isError: string.Equals(entry.Role, "TOOL_ERROR", StringComparison.OrdinalIgnoreCase),
                metadataJson: metadataJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record turn for conversation #{ConversationId}", conversationState.Conversation.Id);
        }
    }
    private CrucibleHarnessConfig ParseHarnessConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CrucibleHarnessConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CrucibleHarnessConfig>(json) ?? new CrucibleHarnessConfig();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid HarnessConfigurationJson; using default Crucible configuration");
            return new CrucibleHarnessConfig();
        }
    }

    private static IReadOnlyList<string> ResolvePermittedTools(CrucibleHarnessConfig config, AgentProfile profile, AgentMode requiredMode)
    {
        // 1. Config override from database HarnessConfigurationJson
        if (config.AllowedTools != null && config.AllowedTools.Count > 0)
        {
            return config.AllowedTools;
        }

        // 2. Profile whitelist
        if (profile.PermittedTools != null && profile.PermittedTools.Count > 0)
        {
            return profile.PermittedTools;
        }

        // 3. Modes: ReadOnly, Review, Full
        return DefaultAgentTools.GetTools(profile.Mode == AgentMode.ReadOnly ? AgentMode.ReadOnly : requiredMode);
    }

    private static string GetStageDirective(CrucibleHarnessConfig config, string stageId)
    {
        if (config.StageDirectives.TryGetValue(stageId, out var customDirective) && !string.IsNullOrWhiteSpace(customDirective))
        {
            return customDirective;
        }

        return stageId.ToLowerInvariant() switch
        {
            StandardStageIds.Triage or "stage-0" =>
                "Analyze the reported task against the repository base branch. Determine affected files and acceptance criteria. Call propose_tasks to recommend structured backlog items. You have read-only access and cannot modify code or access external networks.",
            StandardStageIds.TaskReview or "stage-1" =>
                BuildTaskReviewDirective(),
            StandardStageIds.Implementation or "stage-2" =>
                BuildImplementationDirective(),
            StandardStageIds.CodeReviewAndFix or "stage-3" =>
                BuildReviewDirective(),
            _ => "Execute assigned engineering tasks diligently using canonical tools."
        };
    }

    public static string BuildTaskReviewDirective() =>
        "You are a Staff Software Architect & Requirements Investigator. 1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash'. 2. BACKLOG DEPENDENCY AUDIT: Inspect the <existing_workspace_tasks> list (top active tasks by priority). Determine whether this task depends on any unmerged tasks (e.g. shared database migrations, missing models, interface definitions). If a prerequisite is found, record it in the YAML frontmatter under 'depends_on: [ ... ]' (using prerequisite task ID or title) and document the architectural rationale under '## Dependencies' in the markdown body. 3. CIRCULAR DEPENDENCY PREVENTION: Never introduce a circular dependency (e.g. A depends on B when B already depends on A). Cycles are rejected immediately by the edit tool. If an edit fails with a circular dependency error, re-check your assumptions and resolve the link. 4. WORKFLOW SUSPENSION AWARENESS: If you declare a dependency on a task that is not yet merged/closed, this task will be suspended and reset to 'Open' until the prerequisite is completed, after which the full workflow will rerun from Stage 1 with the updated code. 5. MANDATORY FILE EDIT: Edit the task specification document on disk in docs/tasks/ using 'edit' or 'write' with updated frontmatter (including 'depends_on'), approved architecture, affected files, technical plan, dependencies, and acceptance criteria. Your disk changes will be read back upon stage completion to update the task specification. 6. Gather all questions and ambiguities into a single 'ask' call using the question list, expecting all answers at once; follow-up asks are permitted only for newly discovered blockers. CRITICAL: Never output questions as conversational plain text. If you require answers from the maintainer, you MUST invoke the 'ask' tool. Plain text questions cannot be processed and will trigger a stage failure. Do not guess. 7. Do not write production code.";

    public static string BuildReviewDirective() =>
        "You are a Principal Code Reviewer and Quality Gatekeeper. Review the git diff against the base branch. Compare against the specification in docs/tasks/ and the implementation claims. Apply precision polish directly using 'edit'. Rejection is strictly reserved for major tasks or missing features: invoke 'request_changes' or conclude with <request_changes>[feedback]</request_changes>. For every defect you find, instruct the implementation agent to author a corresponding automated test verifying the fix.";

    public static string BuildImplementationDirective() =>
        "You are a Senior Software Engineer & TDD Practitioner. Implement clean, robust code according to the task specification in docs/tasks/ and the approved plan. Keep blast radius minimal. Run builds and tests incrementally with bash. Fix all compiler errors and test failures. If breaking blockers arise, call 'ask'. If editing docs/tasks/, preserve the YAML frontmatter intact without modifying the id or status fields. CRITICAL: You are an autonomous coding agent operating in a multi-step pipeline. Do NOT write monologues about what you plan to do next and stop; execute your planned actions immediately via tools until your task is 100% implemented and verified.";

    public static string BuildImplementationLoopbackNotice() =>
        "The Reviewer requested changes. Notice: You are required to implement a dedicated automated test for every bug or defect identified in the reviewer's feedback before completing your implementation.";

    public static string SerializeSession(List<ChatMessage> messages)
    {
        // Contents are stored in their original order (reasoning, text, tool calls and results) so that a
        // resumed session is identical to the one that was checkpointed.
        var simpleList = messages.Select(m => new SerializedMessageItem(
            m.Role.Value,
            Text: null,
            FunctionCalls: null,
            FunctionResults: null,
            AuthorName: m.AuthorName,
            Contents: m.Contents.Select(ToSerializedContent).OfType<SerializedContent>().ToList()
        )).ToList();

        return JsonSerializer.Serialize(simpleList, new JsonSerializerOptions { WriteIndented = false });
    }

    private static SerializedContent? ToSerializedContent(AIContent content) => content switch
    {
        TextReasoningContent r => new SerializedContent("reasoning", Text: r.Text, ProtectedData: r.ProtectedData),
        TextContent t => new SerializedContent("text", Text: t.Text),
        FunctionCallContent f => new SerializedContent("call", CallId: f.CallId, Name: f.Name, Arguments: f.Arguments),
        FunctionResultContent r => new SerializedContent("result", CallId: r.CallId, Result: r.Result?.ToString()),
        _ => null
    };

    private static AIContent? FromSerializedContent(SerializedContent c)
    {
        switch (c.Type)
        {
            case "reasoning":
                return new TextReasoningContent(c.Text) { ProtectedData = c.ProtectedData };
            case "text":
                return new TextContent(c.Text);
            case "call":
                var args = c.Arguments != null
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(c.Arguments))
                    : null;
                return new FunctionCallContent(c.CallId ?? Guid.NewGuid().ToString("N"), c.Name ?? string.Empty, args);
            case "result":
                return new FunctionResultContent(c.CallId ?? Guid.NewGuid().ToString("N"), c.Result);
            default:
                return null;
        }
    }

    public static List<ChatMessage> DeserializeSession(string json, ILogger? logger = null)
    {
        var result = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            var items = JsonSerializer.Deserialize<List<SerializedMessageItem>>(json);
            if (items == null) return result;

            foreach (var item in items)
            {
                var role = new ChatRole(item.Role);
                var contents = new List<AIContent>();

                if (item.Contents != null)
                {
                    foreach (var serialized in item.Contents)
                    {
                        var restored = FromSerializedContent(serialized);
                        if (restored != null)
                        {
                            contents.Add(restored);
                        }
                    }

                    result.Add(new ChatMessage(role, contents) { AuthorName = item.AuthorName });
                    continue;
                }

                // Legacy checkpoint format (text, then calls, then results).
                if (!string.IsNullOrEmpty(item.Text))
                {
                    contents.Add(new TextContent(item.Text));
                }

                if (item.FunctionCalls != null)
                {
                    foreach (var fc in item.FunctionCalls)
                    {
                        var args = fc.Arguments != null ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fc.Arguments)) : null;
                        contents.Add(new FunctionCallContent(fc.CallId ?? Guid.NewGuid().ToString("N"), fc.Name, args));
                    }
                }

                if (item.FunctionResults != null)
                {
                    foreach (var fr in item.FunctionResults)
                    {
                        contents.Add(new FunctionResultContent(fr.CallId ?? Guid.NewGuid().ToString("N"), fr.Result));
                    }
                }

                result.Add(new ChatMessage(role, contents));
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize agent session JSON");
        }

        return result;
    }


    /// <summary>
    /// Resolves the maximum iterations per request, giving absolute precedence to profile.MaxTurns
    /// over harnessConfig.MaxTurns to avoid premature agent stalls.
    /// </summary>
    public static int ResolveMaxTurns(CrucibleHarnessConfig harnessConfig, AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(harnessConfig);
        ArgumentNullException.ThrowIfNull(profile);

        return (profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0)
            ? profile.MaxTurns.Value
            : (harnessConfig.MaxTurns > 0 ? harnessConfig.MaxTurns : CrucibleHarnessConfig.DefaultMaxTurns);
    }

    /// <summary>
    /// Rolls back any incomplete assistant tool call turns whose tool results were not recorded.
    /// This prevents HTTP 400 Bad Request syntax errors on session resumption.
    /// </summary>
    public static void SanitizeInFlightTurns(List<ChatMessage> history)
    {
        if (history == null || history.Count == 0) return;

        var callIdsWithResults = new HashSet<string>(
            history.SelectMany(m => m.Contents)
                .OfType<FunctionResultContent>()
                .Where(frc => !string.IsNullOrEmpty(frc.CallId))
                .Select(frc => frc.CallId!),
            StringComparer.Ordinal);

        var lastAssistantMsg = history.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastAssistantMsg != null)
        {
            var toolCalls = lastAssistantMsg.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0 && toolCalls.Any(tc => string.IsNullOrEmpty(tc.CallId) || !callIdsWithResults.Contains(tc.CallId)))
            {
                var index = history.IndexOf(lastAssistantMsg);
                if (index >= 0)
                {
                    history.RemoveRange(index, history.Count - index);
                }
            }
        }
    }

    private record SerializedMessageItem(
        string Role,
        string? Text,
        List<SerializedFunctionCall>? FunctionCalls,
        List<SerializedFunctionResult>? FunctionResults,
        string? AuthorName = null,
        List<SerializedContent>? Contents = null);

    private record SerializedContent(
        string Type,
        string? Text = null,
        string? CallId = null,
        string? Name = null,
        object? Arguments = null,
        string? Result = null,
        string? ProtectedData = null);

    private record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    private record SerializedFunctionResult(string? CallId, string? Result);
}
