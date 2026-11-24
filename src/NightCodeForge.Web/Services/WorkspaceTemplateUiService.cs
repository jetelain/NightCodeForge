using System.Security;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Models;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation helper service encapsulating template catalog access, validation, path resolution,
/// remote forge availability checks, and scaffolding request construction.
/// </summary>
public class WorkspaceTemplateUiService : IWorkspaceTemplateUiService
{
    private readonly IWorkspaceTemplateService _templateService;
    private readonly IWorkspaceIntakeUiService _intakeUiService;
    private readonly IVcsHostingProviderRegistry _providerRegistry;
    private readonly ILogger<WorkspaceTemplateUiService> _logger;

    public WorkspaceTemplateUiService(
        IWorkspaceTemplateService templateService,
        IWorkspaceIntakeUiService intakeUiService,
        IVcsHostingProviderRegistry providerRegistry,
        ILogger<WorkspaceTemplateUiService> logger)
    {
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _intakeUiService = intakeUiService ?? throw new ArgumentNullException(nameof(intakeUiService));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<WorkspaceTemplateDefinition> GetTemplates() =>
        _templateService.GetAvailableTemplates();

    /// <inheritdoc />
    public WorkspaceTemplateDefinition? GetTemplate(string? templateId) =>
        string.IsNullOrWhiteSpace(templateId) ? null : _templateService.GetTemplate(templateId);

    /// <inheritdoc />
    public ProjectNameValidationResult ValidateProjectName(string? projectName) =>
        WorkspaceTemplateProjectNameValidator.Validate(projectName);

    /// <inheritdoc />
    public ProjectNameValidationResult ValidateRemoteRepositoryName(string? repositoryName) =>
        WorkspaceTemplateProjectNameValidator.ValidateRepositoryName(repositoryName);

    /// <inheritdoc />
    public TargetPathValidationResult ValidateTargetDirectoryPath(
        string? targetPath,
        string? alreadyScaffoldedDirectoryPath = null)
    {
        if (!string.IsNullOrWhiteSpace(targetPath) &&
            !string.IsNullOrWhiteSpace(alreadyScaffoldedDirectoryPath) &&
            IsSameCanonicalPath(targetPath, alreadyScaffoldedDirectoryPath) &&
            Directory.Exists(Path.GetFullPath(targetPath.Trim())))
        {
            return new TargetPathValidationResult(
                IsValid: true,
                ErrorMessage: null,
                DirectoryExists: true,
                IsNonEmpty: true);
        }

        return _intakeUiService.ValidateCloneTargetPath(targetPath);
    }

    /// <inheritdoc />
    public string ResolveDefaultTargetDirectoryPath(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return string.Empty;
        }

        try
        {
            return _intakeUiService.ResolveDefaultClonePath(projectName.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or LocalRepositoryDiscoveryException)
        {
            _logger.LogDebug(ex, "Unable to resolve default target directory path for project name '{ProjectName}'.", projectName);
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<RemotePublishAvailability> GetRemotePublishAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        var provider = await _providerRegistry.GetPrimaryAuthenticatedProviderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (provider is null)
        {
            return new RemotePublishAvailability(
                IsAvailable: false,
                ProviderType: null,
                ProviderDisplayName: null,
                AuthenticatedUsername: null);
        }

        string? username = null;
        try
        {
            username = await provider.GetAuthenticatedUsernameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Username lookup is informational; provider is already authenticated.
            _logger.LogWarning(ex, "Failed to retrieve authenticated username from VCS provider {ProviderType}.", provider.ProviderType);
        }

        return new RemotePublishAvailability(
            IsAvailable: true,
            ProviderType: provider.ProviderType,
            ProviderDisplayName: provider.DisplayName,
            AuthenticatedUsername: username);
    }

    /// <inheritdoc />
    public bool CanProceed(
        WorkspaceTemplateSelectionModel selection,
        string? alreadyScaffoldedDirectoryPath = null)
    {
        if (selection is null)
        {
            return false;
        }

        if (GetTemplate(selection.SelectedTemplateId) is null)
        {
            return false;
        }

        if (!ValidateProjectName(selection.ProjectName).IsValid)
        {
            return false;
        }

        if (!ValidateTargetDirectoryPath(selection.TargetDirectoryPath, alreadyScaffoldedDirectoryPath).IsValid)
        {
            return false;
        }

        if (selection.PublishToRemoteForge)
        {
            if (!selection.InitializeGit || !selection.RemoteProviderType.HasValue)
            {
                return false;
            }

            var effectiveRepoName = string.IsNullOrWhiteSpace(selection.RemoteRepositoryName)
                ? selection.ProjectName
                : selection.RemoteRepositoryName;
            if (!ValidateRemoteRepositoryName(effectiveRepoName).IsValid)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ResolvePreviewProjects(
        WorkspaceTemplateDefinition template,
        string? projectName)
    {
        ArgumentNullException.ThrowIfNull(template);

        var name = ValidateProjectName(projectName).IsValid
            ? projectName!.Trim()
            : WorkspaceTemplateConstants.ProjectNamePlaceholder;

        return template.GeneratedProjects
            .Select(p => p.Replace(WorkspaceTemplateConstants.ProjectNamePlaceholder, name, StringComparison.Ordinal))
            .ToList();
    }

    /// <inheritdoc />
    public TemplateScaffoldingRequest BuildScaffoldingRequest(WorkspaceTemplateSelectionModel selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        var projectName = selection.ProjectName.Trim();
        var effectiveRepoName = string.IsNullOrWhiteSpace(selection.RemoteRepositoryName)
            ? projectName
            : selection.RemoteRepositoryName.Trim();

        return new TemplateScaffoldingRequest(
            TemplateId: selection.SelectedTemplateId ?? string.Empty,
            ProjectName: projectName,
            TargetDirectoryPath: selection.TargetDirectoryPath.Trim(),
            InitializeGit: selection.InitializeGit,
            PublishToRemoteForge: selection.PublishToRemoteForge,
            RemoteProviderType: selection.PublishToRemoteForge ? selection.RemoteProviderType : null,
            RemoteIsPrivate: selection.RemoteIsPrivate,
            RemoteDescription: string.IsNullOrWhiteSpace(selection.RemoteDescription) ? null : selection.RemoteDescription.Trim(),
            RemoteRepositoryName: selection.PublishToRemoteForge ? effectiveRepoName : null);
    }

    /// <inheritdoc />
    public GitHubIntegrationSettings? BuildGitHubSettingsIfApplicable(
        VcsHostingProviderType? providerType,
        VcsRepositorySummary? repository)
    {
        if (repository is null)
        {
            return null;
        }

        return _intakeUiService.BuildGitHubSettingsIfApplicable(providerType, repository);
    }

    /// <inheritdoc />
    public string GetStackBadgeLabel(TemplateStackCategory category) => category switch
    {
        TemplateStackCategory.DotNet => ".NET 10",
        TemplateStackCategory.TypeScriptNode => "Node / TS",
        TemplateStackCategory.Rust => "Rust",
        TemplateStackCategory.Python => "Python",
        _ => category.ToString()
    };

    /// <inheritdoc />
    public string GetArchetypeLabel(TemplateArchitectureArchetype archetype) => archetype switch
    {
        TemplateArchitectureArchetype.WebApp => "Clean Architecture",
        TemplateArchitectureArchetype.WebApi => "Clean Architecture",
        TemplateArchitectureArchetype.CliTool => "Clean Architecture",
        TemplateArchitectureArchetype.ClassLibrary => "Modular Library",
        _ => archetype.ToString()
    };

    private static bool IsSameCanonicalPath(string? candidatePath, string? scaffoldedPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(scaffoldedPath))
        {
            return false;
        }

        try
        {
            var left = Path.GetFullPath(candidatePath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var right = Path.GetFullPath(scaffoldedPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(left, right, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return false;
        }
    }
}
