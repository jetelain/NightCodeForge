using YamlDotNet.Serialization;

namespace NightCodeForge.Core.Services;

/// <summary>
/// YAML frontmatter representation for mirrored issue markdown files.
/// </summary>
public class IssueFrontmatter
{
    public int? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Workspace { get; set; }
    public string? Status { get; set; }
    
    [YamlMember(Alias = "current_stage")]
    public string? CurrentStage { get; set; }
    
    public int Priority { get; set; } = 1;
    
    [YamlMember(Alias = "base_branch")]
    public string BaseBranch { get; set; } = "main";
    
    [YamlMember(Alias = "feature_branch")]
    public string? FeatureBranch { get; set; }
    
    [YamlMember(Alias = "depends_on")]
    public List<int> DependsOn { get; set; } = new();
    
    [YamlMember(Alias = "agent_profile")]
    public string? AgentProfile { get; set; }
    
    [YamlMember(Alias = "created_at")]
    public DateTime? CreatedAt { get; set; }
    
    [YamlMember(Alias = "updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
