using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record SkillDescriptor(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillFilePath,
    string? Stack = null,
    bool IsBuiltIn = false);

public interface IWorkspaceSkillsService
{
    /// <summary>
    /// Returns all candidate skill descriptors discovered for this workspace stack,
    /// before applying workspace or repository opt-in/opt-out policies.
    /// </summary>
    Task<IReadOnlyList<SkillDescriptor>> GetCandidateSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active skill descriptors available for this workspace context,
    /// filtered by stack applicability and cascading opt-in/opt-out configuration.
    /// </summary>
    Task<IReadOnlyList<SkillDescriptor>> GetActiveSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats the lean skills catalog prompt (~25 tokens/skill) for active skills.
    /// </summary>
    Task<string> GetSkillsCatalogPromptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the full instructions/content of a skill by name. Resolves from local repository skills,
    /// bundled skills, or custom global skills.
    /// </summary>
    Task<string> LoadSkillAsync(string skillName, CancellationToken cancellationToken = default);
}

public interface IWorkspaceSkillsServiceFactory
{
    IWorkspaceSkillsService Create(Workspace workspace, string workspacePath, int? taskId = null);
}

