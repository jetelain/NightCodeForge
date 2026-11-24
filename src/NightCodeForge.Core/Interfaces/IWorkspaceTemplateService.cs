using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Parameters for scaffolding a new workspace from a native CLI template.
/// </summary>
public record TemplateScaffoldingRequest(
    string TemplateId,
    string ProjectName,
    string TargetDirectoryPath,
    bool InitializeGit,
    bool PublishToRemoteForge,
    VcsHostingProviderType? RemoteProviderType,
    bool RemoteIsPrivate,
    string? RemoteDescription,
    string? RemoteRepositoryName = null);

/// <summary>
/// Progress notification emitted during template scaffolding and optional remote publishing.
/// </summary>
public record TemplateScaffoldingProgress(
    string CurrentStep,
    int Percentage,
    bool IsCompleted,
    string? ErrorMessage,
    bool LocalScaffoldCompleted = false);

/// <summary>
/// Outcome of scaffolding a workspace from a template.
/// </summary>
public record TemplateScaffoldingResult(
    string TargetDirectoryPath,
    string TemplateId,
    string ProjectName,
    bool LocalScaffoldCompleted,
    bool GitInitialized,
    VcsRepositorySummary? PublishedRepository);

/// <summary>
/// Provides built-in workspace templates and executes native CLI scaffolding, hygiene setup,
/// initial Git commit, and optional remote forge publishing.
/// </summary>
public interface IWorkspaceTemplateService
{
    IReadOnlyList<WorkspaceTemplateDefinition> GetAvailableTemplates();
    WorkspaceTemplateDefinition? GetTemplate(string templateId);

    Task<TemplateScaffoldingResult> ScaffoldWorkspaceAsync(
        TemplateScaffoldingRequest request,
        IProgress<TemplateScaffoldingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<VcsRepositorySummary> PublishToRemoteForgeAsync(
        string targetDirectoryPath,
        VcsHostingProviderType providerType,
        string repositoryName,
        string? description,
        bool isPrivate,
        IProgress<TemplateScaffoldingProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
