using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record IdeationProposal(
    string Title,
    string Description,
    int Priority = TaskPriority.Normal,
    IReadOnlyList<string>? DependsOnTitles = null,
    int? TaskId = null,
    IReadOnlyList<int>? DependsOnIds = null,
    string? Difficulty = null,
    IReadOnlyList<int>? AttachmentIds = null);

public record IdeationToolCallInfo(
    string ToolCallId,
    string ToolName,
    string Arguments,
    string? Result = null,
    bool IsError = false,
    long DurationMs = 0);

public record IdeationResult(
    string AssistantMessage,
    IReadOnlyList<IdeationProposal> ProposedTasks,
    IReadOnlyList<IdeationToolCallInfo>? ToolCalls = null,
    string? TranscriptPath = null,
    string? FullTranscriptMarkdown = null,
    SessionUsageDelta? UsageDelta = null,
    bool IsCancelled = false);

public enum IdeationStreamUpdateKind
{
    ThinkingDelta,
    MessageDelta,
    ProposalDiscovered,
    ToolCallStarted,
    ToolCallCompleted,
    Completed,
    QuestionAsked,
    OptimizationNotice,
    Error
}

/// <summary>
/// A question raised by the agent's ask tool during a live ideation turn. The agent turn stays
/// suspended until the user calls Submit (or the turn is cancelled).
/// </summary>
public sealed class IdeationQuestion
{
    private readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IdeationQuestion(string prompt, string? rationale, IReadOnlyList<string>? options)
    {
        Prompt = prompt;
        Rationale = rationale;
        Options = options ?? Array.Empty<string>();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Prompt { get; }
    public string? Rationale { get; }
    public IReadOnlyList<string> Options { get; }
    public bool IsAnswered => _answer.Task.IsCompleted;
    public Task<string> AnswerTask => _answer.Task;

    public bool Submit(string answer) => _answer.TrySetResult(answer);

    public bool Cancel() => _answer.TrySetCanceled();
}

public record IdeationStreamUpdate(
    IdeationStreamUpdateKind Kind,
    string? TextDelta = null,
    IdeationProposal? Proposal = null,
    IdeationToolCallInfo? ToolCall = null,
    IdeationResult? FinalResult = null,
    IdeationQuestion? Question = null,
    IReadOnlyList<IdeationQuestion>? Questions = null,
    string? ErrorMessage = null,
    int? ConversationId = null,
    Guid? QuestionGroupId = null,
    int? PreCompactionTokens = null,
    int? PostCompactionTokens = null,
    double? DurationMs = null,
    string? ToolMetadataJson = null);

/// <summary>
/// User-configurable options applied to a warm Ideation conversation session.
/// </summary>
public sealed record IdeationSessionOptions(
    OllamaRoutingTarget RoutingTarget = OllamaRoutingTarget.Auto,
    string? ModelOverride = null,
    string? CustomSystemPrompt = null,
    AgentProfile? ExplicitProfile = null);

public enum IdeationInFlightPartKind
{
    Thinking,
    Text,
    ToolCall,
    Question,
    Optimization
}

public sealed record IdeationInFlightToolCall(
    IdeationToolCallInfo Info,
    bool IsCompleted);

public sealed record IdeationInFlightPart(
    IdeationInFlightPartKind Kind,
    string? Text = null,
    IdeationToolCallInfo? ToolCall = null,
    bool IsToolCompleted = false,
    Guid? QuestionGroupId = null,
    IReadOnlyList<IdeationQuestion>? Questions = null,
    string? QuestionAnswer = null,
    string? QuestionMetadataJson = null,
    int? PreCompactionTokens = null,
    int? PostCompactionTokens = null,
    double? DurationMs = null);

/// <summary>
/// Authoritative snapshot of an active in-flight ideation turn, enabling Blazor circuits
/// to seamlessly re-attach after browser reloads or navigation.
/// </summary>
public sealed record IdeationInFlightTurnSnapshot(
    int WorkspaceId,
    int ConversationId,
    bool IsGenerating,
    int? ActiveUserTurnIndex,
    string CurrentThinkingBuffer,
    string CurrentMessageBuffer,
    IReadOnlyList<IdeationInFlightToolCall> CurrentToolCalls,
    IReadOnlyList<IdeationInFlightPart> CurrentParts,
    IReadOnlyList<IdeationProposal> DiscoveredProposals,
    string? LatestTranscriptPath,
    string? LatestFullTranscriptMarkdown,
    IdeationSessionOptions Options);

/// <summary>
/// Persisted conversation state paired with any live in-flight turn snapshot for that session.
/// </summary>
public sealed record IdeationSessionHandle(
    Conversation Conversation,
    IdeationInFlightTurnSnapshot? InFlightTurn,
    IdeationSessionOptions? SessionOptions = null,
    string? LatestTranscriptPath = null,
    string? LatestFullTranscriptMarkdown = null);

/// <summary>
/// Request payload to dispatch a user message in an ideation conversation.
/// </summary>
public sealed record IdeationSendRequest(
    int WorkspaceId,
    int ConversationId,
    string Prompt,
    IdeationSessionOptions? Options = null,
    string? UserTurnMetadataJson = null);

/// <summary>
/// Live stream subscription returned by <see cref="IIdeationConversationCoordinator.Subscribe"/>.
/// Carries the atomic <see cref="InitialSnapshot"/> captured at subscription registration time.
/// </summary>
public sealed class IdeationStreamSubscription : IDisposable
{
    private Action? _unsubscribe;

    public IdeationStreamSubscription(IdeationInFlightTurnSnapshot? initialSnapshot, Action unsubscribe)
    {
        InitialSnapshot = initialSnapshot;
        _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
    }

    public IdeationInFlightTurnSnapshot? InitialSnapshot { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}

/// <summary>
/// Singleton coordinator managing interactive Ideation conversations, warm agent execution contexts,
/// proactive 48k model preloading, structured turn persistence, and circuit-independent in-flight turns.
/// </summary>
public interface IIdeationConversationCoordinator : IAsyncDisposable
{
    /// <summary>Default 48k context intent sent to JIT model preload on Ideation warm-up.</summary>
    public const int DefaultContextIntentTokens = 49_152;

    /// <summary>Notice appended to assistant responses when an ideation turn is cancelled by the user.</summary>
    public const string CancelledAssistantNotice = "\n\n*(Generation stopped by user)*";

    /// <summary>Notice appended or persisted when an ideation turn fails after executing tools.</summary>
    public const string ErrorAfterToolsAssistantNotice = "*(Turn stopped due to an error after executing tools.)*";

    bool HasActiveTurn { get; }

    event Action<bool>? OnTurnActivityChanged;

    Task<IReadOnlyList<Conversation>> GetWorkspaceConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<IdeationSessionHandle> GetOrCreateActiveConversationAsync(
        int workspaceId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IdeationSessionHandle?> LoadConversationAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<IdeationSessionHandle> StartConversationAsync(
        int workspaceId,
        string? title = null,
        int? archivePreviousConversationId = null,
        string? archiveSummary = null,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task WarmupSessionAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    Task UpdateSessionOptionsAsync(
        int workspaceId,
        int conversationId,
        IdeationSessionOptions options,
        CancellationToken cancellationToken = default);

    IdeationStreamSubscription Subscribe(
        int conversationId,
        Action<IdeationStreamUpdate> onUpdate);

    IdeationInFlightTurnSnapshot? GetInFlightSnapshot(int conversationId);

    bool IsConversationGenerating(int conversationId);

    Task<IdeationResult> SendMessageAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> StartTurnAsync(
        IdeationSendRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> SubmitQuestionAnswersAsync(
        int conversationId,
        Guid questionGroupId,
        IReadOnlyDictionary<int, string> answersByIndex,
        string? formattedAnswer = null,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);

    Task CancelActiveTurnAsync(
        int conversationId,
        CancellationToken cancellationToken = default);

    Task<bool> ReleaseConversationSessionAsync(
        int workspaceId,
        int conversationId,
        bool cancelIfActive = false,
        CancellationToken cancellationToken = default);

    void MarkWorkspaceContextDirty(int workspaceId);
}
