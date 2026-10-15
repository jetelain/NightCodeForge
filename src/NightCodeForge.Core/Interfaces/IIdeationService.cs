using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record IdeationProposal(
    string Title,
    string Description,
    int Priority = 1,
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
    Completed
}

public record IdeationStreamUpdate(
    IdeationStreamUpdateKind Kind,
    string? TextDelta = null,
    IdeationProposal? Proposal = null,
    IdeationToolCallInfo? ToolCall = null,
    IdeationResult? FinalResult = null);

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
