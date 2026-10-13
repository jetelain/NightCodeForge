using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record IdeationProposal(
    string Title,
    string Description,
    int Priority = 1,
    IReadOnlyList<string>? DependsOnTitles = null);

public record IdeationResult(
    string AssistantMessage,
    IReadOnlyList<IdeationProposal> ProposedIssues);

/// <summary>
/// Service contract for interactive discovery chat and autonomous backlog issue formulation.
/// </summary>
public interface IIdeationService
{
    Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default);
}
