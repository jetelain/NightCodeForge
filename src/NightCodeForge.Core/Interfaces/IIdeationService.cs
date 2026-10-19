using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record IdeationProposal(
    string Title,
    string Description,
    int Priority = TaskPriority.Normal,
    IReadOnlyList<string>? DependsOnTitles = null);

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
    string? FullTranscriptMarkdown = null);

public enum IdeationStreamUpdateKind
{
    ThinkingDelta,
    MessageDelta,
    ProposalDiscovered,
    ToolCallStarted,
    ToolCallCompleted,
    Completed,
    QuestionAsked
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
    IdeationQuestion? Question = null);

/// <summary>
/// Service contract for interactive discovery chat and autonomous backlog task formulation.
/// </summary>
public interface IIdeationService
{
    Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default);

    Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default);
}
