using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record IdeationProposal(
    string Title,
    string Description,
    int Priority = 1,
    IReadOnlyList<string>? DependsOnTitles = null);

public record IdeationResult(
    string AssistantMessage,
    IReadOnlyList<IdeationProposal> ProposedTasks);

public enum IdeationStreamUpdateKind
{
    ThinkingDelta,
    MessageDelta,
    ProposalDiscovered,
    Completed
}

public record IdeationStreamUpdate(
    IdeationStreamUpdateKind Kind,
    string? TextDelta = null,
    IdeationProposal? Proposal = null,
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
