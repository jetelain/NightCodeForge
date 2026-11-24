using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Models;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Availability and identity of an authenticated remote VCS hosting provider for publishing newly scaffolded repositories.
/// </summary>
public record RemotePublishAvailability(
    bool IsAvailable,
    VcsHostingProviderType? ProviderType,
    string? ProviderDisplayName,
    string? AuthenticatedUsername);

/// <summary>
/// Presentation helper service for the template scaffolding mode of the workspace creation wizard.
/// </summary>
public interface IWorkspaceTemplateUiService
{
    IReadOnlyList<WorkspaceTemplateDefinition> GetTemplates();

    WorkspaceTemplateDefinition? GetTemplate(string? templateId);

    ProjectNameValidationResult ValidateProjectName(string? projectName);

    ProjectNameValidationResult ValidateRemoteRepositoryName(string? repositoryName);

    TargetPathValidationResult ValidateTargetDirectoryPath(
        string? targetPath,
        string? alreadyScaffoldedDirectoryPath = null);

    string ResolveDefaultTargetDirectoryPath(string projectName);

    Task<RemotePublishAvailability> GetRemotePublishAvailabilityAsync(
        CancellationToken cancellationToken = default);

    bool CanProceed(
        WorkspaceTemplateSelectionModel selection,
        string? alreadyScaffoldedDirectoryPath = null);

    IReadOnlyList<string> ResolvePreviewProjects(
        WorkspaceTemplateDefinition template,
        string? projectName);

    TemplateScaffoldingRequest BuildScaffoldingRequest(WorkspaceTemplateSelectionModel selection);

    GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(
        VcsHostingProviderType? providerType,
        VcsRepositorySummary? repository);

    string GetStackBadgeLabel(TemplateStackCategory category);

    string GetArchetypeLabel(TemplateArchitectureArchetype archetype);
}
