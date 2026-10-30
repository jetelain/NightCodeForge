namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Service contract for discovering, loading, and formatting skills according to progressive disclosure.
/// </summary>
public interface ISkillsService
{
    /// <summary>
    /// Discovers skills in the workspace using Microsoft Agent Framework's AgentFileSkillsSource.
    /// </summary>
    Task<IReadOnlyList<SkillDescriptor>> DiscoverSkillsAsync(
        string workspaceRoot,
        string? globalSkillsDirectory = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the full content of a skill using Microsoft Agent Framework's AgentFileSkillsSource.
    /// </summary>
    Task<string> LoadSkillAsync(
        string workspaceRoot,
        string skillName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats skills catalog prompt following the minimalist Pi approach to minimize token overhead (~25 tokens/skill).
    /// </summary>
    string FormatSkillsCatalogPrompt(IReadOnlyList<SkillDescriptor> skills);
}

