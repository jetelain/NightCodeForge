using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Models;

public class WorkspaceCreationModel
{
    [Required(ErrorMessage = "Workspace name is required")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 100 characters")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Repository directory path is required")]
    public string RepositoryPath { get; set; } = string.Empty;

    [Required(ErrorMessage = "Default base branch is required")]
    public string DefaultBaseBranch { get; set; } = "main";

    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.ContainerSandbox;

    public string? ContainerImage { get; set; } = "ncf-agent-base:latest";

    public string? WslDistribution { get; set; }

    [Range(30, 1800, ErrorMessage = "Build timeout must be between 30 and 1800 seconds")]
    public int BuildTimeoutSeconds { get; set; } = 180;

    [Range(30, 1800, ErrorMessage = "Test timeout must be between 30 and 1800 seconds")]
    public int TestTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// If false (default), the workspace inherits global system agent profiles (WorkspaceId == null).
    /// If true, creates dedicated workspace-scoped profiles (WorkspaceId == workspace.Id).
    /// </summary>
    public bool UseDedicatedAgentProfiles { get; set; } = false;

    /// <summary>
    /// Custom model name override if UseDedicatedAgentProfiles is true.
    /// </summary>
    public string? DedicatedModelName { get; set; }

    /// <summary>
    /// When true, configures .editorconfig, .gitattributes (* text=auto eol=lf), and local core.autocrlf false.
    /// Defaults to true on Windows.
    /// </summary>
    public bool ConfigureCrossPlatformHygiene { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
