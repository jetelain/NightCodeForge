using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Models;

namespace NightCodeForge.Web.Services;

public interface IWorkspaceProvisioningService
{
    /// <summary>
    /// Probes a local file system path for Git repository structure, branches, and tech stack markers.
    /// </summary>
    Task<RepositoryProbeResult> ProbeRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes a Git repository in an existing directory if one does not exist.
    /// </summary>
    Task<bool> InitializeGitRepositoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the complete workspace onboarding pipeline: persistence, Ext4 mirror clone,
    /// directory scaffolding, default agent profiles, and markdown watcher startup.
    /// </summary>
    Task<Workspace> ProvisionWorkspaceAsync(
        WorkspaceCreationModel model,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
