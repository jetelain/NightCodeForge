using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow.Filtering;

namespace NightCodeForge.Agents.Crucible;

/// <summary>
/// Captures the in-memory conversation state, turns, and transcript path of a stateless Crucible run.
/// </summary>
public sealed class InMemoryConversationCapture
{
    public string? TargetTranscriptPath { get; }
    public Conversation? Conversation { get; internal set; }
    public CrucibleConversationState? ConversationState { get; internal set; }
    public InMemoryConversationTurnSink TurnSink { get; internal set; } = new();
    public string? SavedTranscriptPath { get; internal set; }
    public long TotalModelCalls { get; internal set; }

    public InMemoryConversationCapture(string? targetTranscriptPath = null)
    {
        TargetTranscriptPath = targetTranscriptPath;
    }
}

/// <summary>
/// Stateless, database-free <see cref="ICrucibleConversationCoordinator"/> backed by
/// <see cref="InMemoryConversationTurnSink"/> and <see cref="SessionTranscriptService"/>.
/// Used by benchmark runs so zero <c>Workspace</c>, <c>ForgeTask</c>, or <c>Conversation</c> rows touch SQLite.
/// </summary>
public sealed class InMemoryCrucibleConversationCoordinator : ICrucibleConversationCoordinator
{
    private const string TranscriptsFolder = "transcripts";
    private const string DocsFolder = "docs";

    private static readonly AsyncLocal<InMemoryConversationCapture?> _activeCapture = new();

    private readonly SessionTranscriptService _transcriptService;
    private readonly ILogger<InMemoryCrucibleConversationCoordinator> _logger;
    private int _nextConversationId;

    public InMemoryCrucibleConversationCoordinator(
        SessionTranscriptService transcriptService,
        ILogger<InMemoryCrucibleConversationCoordinator> logger)
    {
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static InMemoryConversationCapture? CurrentCapture => _activeCapture.Value;

    /// <summary>
    /// Scopes an <see cref="InMemoryConversationCapture"/> to the current async execution flow so callers
    /// can inspect captured turns and redirect markdown transcript writes away from the benchmarked git tree.
    /// </summary>
    public static IDisposable BeginCapture(InMemoryConversationCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var previous = _activeCapture.Value;
        _activeCapture.Value = capture;
        return new CaptureScopeRestore(previous);
    }

    public Task<CrucibleConversationState> BeginAsync(
        AgentSessionContext context,
        string harnessId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var conversationId = Interlocked.Increment(ref _nextConversationId);
        var conversation = new Conversation
        {
            Id = conversationId,
            TaskId = context.ForgeTask.Id > 0 ? context.ForgeTask.Id : null,
            WorkspaceId = context.Workspace?.Id > 0 ? context.Workspace.Id : null,
            ConversationType = ConversationType.StageRun,
            Title = context.ForgeTask.Title,
            StageId = context.StageId,
            StageKind = WorkflowStageKind.Agentic,
            AttemptIndex = 1,
            AgentProfileId = context.Profile.Id,
            HarnessId = harnessId,
            ModelName = context.Profile.ModelName,
            StartedAt = DateTime.UtcNow,
            Status = StageExecutionStatus.Running,
            Turns = new List<ConversationTurn>()
        };

        var sink = _activeCapture.Value?.TurnSink ?? new InMemoryConversationTurnSink();
        var state = new CrucibleConversationState(conversation, attemptIndex: 1, sink, _logger)
        {
            SessionId = $"bench-{Guid.NewGuid():N}",
            IsResumedConversation = !string.IsNullOrWhiteSpace(context.SerializedStateJson)
        };

        var initialPrompt = !string.IsNullOrWhiteSpace(context.BootstrapPrompt)
            ? context.BootstrapPrompt
            : CrucibleConversationCoordinator.DefaultBootstrapPrompt;

        state.ChatHistory = new List<ChatMessage> { new(ChatRole.User, initialPrompt) };
        state.EnqueueTurn(
            StageTurnRole.User,
            $"Failed to record initial prompt turn for in-memory conversation #{conversationId}",
            content: initialPrompt);

        if (context.SyntheticPriming != null)
        {
            ApplySyntheticPriming(state, context.SyntheticPriming, context.OptimizationFlags ?? context.Profile.OptimizationFlags, context.WorkspacePath);
        }

        if (_activeCapture.Value is { } capture)
        {
            capture.Conversation = conversation;
            capture.ConversationState = state;
            capture.TurnSink = sink;
        }

        return Task.FromResult(state);
    }

    public async Task<string?> SaveTranscriptAsync(
        CrucibleConversationState state,
        CrucibleTranscriptRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        await state.DrainAsync();

        var context = request.Context;
        var tracingClient = request.TracingClient;
        var harnessOptions = request.HarnessOptions;
        var capture = _activeCapture.Value;

        if (capture != null && tracingClient != null)
        {
            capture.TotalModelCalls = tracingClient.TotalModelCalls;
        }

        var systemPrompt = tracingClient?.LastCapturedSystemPrompt
            ?? request.ChatHistory.FirstOrDefault(m => m.Role == ChatRole.System)?.Text
            ?? context.Profile.SystemPrompt;

        IReadOnlyList<string>? availableTools = tracingClient is { LastCapturedTools.Count: > 0 }
            ? tracingClient.LastCapturedTools
            : context.Profile.PermittedTools;

        var mafConfig = harnessOptions != null
            ? MafConfigurationSummary.Build(harnessOptions, tracingClient?.DetectedMafConfiguration)
            : null;
        var entries = CrucibleConversationCoordinator.BuildTranscriptEntries(request.ChatHistory);

        TranscriptCompactionMetadata? compactionMeta = null;
        if (harnessOptions?.CompactionStrategy is PiStyleCrucibleCompactionStrategy piCompaction)
        {
            var opts = piCompaction.Options;
            compactionMeta = new TranscriptCompactionMetadata(
                piCompaction.GetType().Name,
                opts.ContextWindowTokens,
                opts.ReserveTokens,
                opts.TriggerThresholdTokens,
                opts.KeepRecentTokens,
                opts.ResolutionSource ?? "Unknown");
        }

        var metrics = new AgentRunMetrics(
            DurationMs: request.ElapsedMs,
            PromptTokens: request.PromptTokens,
            CompletionTokens: request.CompletionTokens,
            TotalTokens: request.PromptTokens + request.CompletionTokens);

        var meta = new TranscriptMetadata(
            context.ForgeTask.Id,
            context.StageId,
            context.Profile.Name,
            context.Profile.ModelName,
            request.ElapsedMs,
            metrics,
            context.OptimizationFlags ?? new LocalOptimizationFlags(),
            request.Status,
            KvCacheHitRate: null,
            SystemPrompt: systemPrompt,
            AvailableTools: availableTools,
            ActiveSkills: request.ActiveSkills,
            MafConfiguration: mafConfig,
            ModelCalls: tracingClient?.ModelCalls,
            AttemptIndex: state.AttemptIndex,
            Compaction: compactionMeta,
            ConversationId: state.Conversation.Id);

        string savedPath;
        if (!string.IsNullOrWhiteSpace(capture?.TargetTranscriptPath))
        {
            var destDir = Path.GetDirectoryName(capture.TargetTranscriptPath)
                ?? Path.Combine(request.WorkspacePath, DocsFolder, TranscriptsFolder);
            Directory.CreateDirectory(destDir);
            var tempDir = Path.Combine(destDir, $".tmp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                var generatedPath = await _transcriptService.SaveTranscriptAsync(meta, entries, tempDir, cancellationToken).ConfigureAwait(false);
                File.Move(generatedPath, capture.TargetTranscriptPath, overwrite: true);
                savedPath = capture.TargetTranscriptPath;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to clean up temporary transcript staging directory '{TempDir}'.", tempDir);
                }
            }
        }
        else
        {
            var repoRoot = context.Workspace?.RepositoryPath ?? request.WorkspacePath;
            var targetDir = Path.Combine(repoRoot, DocsFolder, TranscriptsFolder);
            savedPath = await _transcriptService.SaveTranscriptAsync(meta, entries, targetDir, cancellationToken).ConfigureAwait(false);
        }

        state.Conversation.Status = request.Status switch
        {
            AgentRunStatus.Completed => StageExecutionStatus.Completed,
            AgentRunStatus.WaitingForHuman => StageExecutionStatus.WaitingForHuman,
            AgentRunStatus.Cancelled => StageExecutionStatus.Cancelled,
            AgentRunStatus.Interrupted => StageExecutionStatus.Interrupted,
            _ => StageExecutionStatus.Failed
        };
        state.Conversation.CompletedAt = DateTime.UtcNow;
        state.Conversation.Summary = request.Summary;
        state.Conversation.TranscriptFileName = Path.GetFileName(savedPath);

        if (capture != null)
        {
            capture.SavedTranscriptPath = savedPath;
        }

        return Path.GetFileName(savedPath);
    }

    public Task ReleaseSessionAffinityAsync(string? sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private static void ApplySyntheticPriming(
        CrucibleConversationState state,
        SyntheticPrimingExecution priming,
        LocalOptimizationFlags? optimizationFlags,
        string? workspaceRoot)
    {
        var filtered = CliOutputFilterHelpers.FilterPrimingOutput(
            CliOutputFilterRegistry.Default,
            priming.Command,
            priming.Output,
            priming.IsSuccess,
            workspaceRoot ?? string.Empty,
            smartFilteringEnabled: optimizationFlags?.SmartCliOutputFiltering ?? true,
            Core.Workflow.WorkflowDefaults.MaxToolInlineChars);

        var primeCallId = $"prime_{Guid.NewGuid():N}";
        state.TryRecordToolCall(primeCallId);

        state.ChatHistory.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(primeCallId, CanonicalToolNames.Bash, new Dictionary<string, object?> { ["command"] = priming.Command })
        }));
        state.ChatHistory.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent(primeCallId, filtered)
        }));

        state.EnqueueTurn(
            StageTurnRole.Tool,
            $"Failed to record primed Tool turn for in-memory conversation #{state.Conversation.Id}",
            content: $"Execute {priming.Command}",
            toolName: CanonicalToolNames.Bash,
            toolArguments: JsonSerializer.Serialize(new { command = priming.Command }),
            durationMs: priming.DurationMs);

        state.EnqueueTurn(
            StageTurnRole.ToolResult,
            $"Failed to record primed ToolResult turn for in-memory conversation #{state.Conversation.Id}",
            content: filtered,
            toolName: CanonicalToolNames.Bash,
            toolResult: filtered,
            isError: !priming.IsSuccess,
            durationMs: priming.DurationMs);

        if (!priming.IsSuccess)
        {
            state.ChatHistory.Add(new ChatMessage(ChatRole.User, CrucibleConversationCoordinator.FixDirectivePrompt));
            state.EnqueueTurn(
                StageTurnRole.User,
                $"Failed to record fix directive User turn for in-memory conversation #{state.Conversation.Id}",
                content: CrucibleConversationCoordinator.FixDirectivePrompt);
        }
    }

    private sealed class CaptureScopeRestore(InMemoryConversationCapture? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activeCapture.Value = previous;
        }
    }
}
