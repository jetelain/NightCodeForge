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
    public string? ContainerName { get; set; }
    public string? Ext4MirrorPath { get; set; }
    public bool UseWslForContainers { get; set; }
    public string? WslDistribution { get; set; }
    public int? BuildTimeoutSeconds { get; set; }
    public int? TestTimeoutSeconds { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ForgeTask> Tasks { get; set; } = new List<ForgeTask>();
    public ICollection<AgentProfile> AgentProfiles { get; set; } = new List<AgentProfile>();
}
