using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Models;

/// <summary>
/// Tracks user selections for from-scratch template scaffolding in Step 1 of the workspace wizard.
/// </summary>
public class WorkspaceTemplateSelectionModel
{
    public string? SelectedTemplateId { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    public string TargetDirectoryPath { get; set; } = string.Empty;

    public bool IsTargetDirectoryManuallyEdited { get; set; }

    public bool InitializeGit { get; set; } = true;

    public bool PublishToRemoteForge { get; set; }

    public VcsHostingProviderType? RemoteProviderType { get; set; }

    public string RemoteRepositoryName { get; set; } = string.Empty;

    public bool IsRemoteRepositoryNameManuallyEdited { get; set; }

    public bool RemoteIsPrivate { get; set; } = true;

    public string RemoteDescription { get; set; } = string.Empty;
}
