namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a managed repository workspace.
/// </summary>
public class Workspace
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string DefaultBaseBranch { get; set; } = "main";
    public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.ContainerSandbox;
    public string? ContainerImage { get; set; }
    public string? Ext4MirrorPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Issue> Issues { get; set; } = new List<Issue>();
    public ICollection<AgentProfile> AgentProfiles { get; set; } = new List<AgentProfile>();
}
