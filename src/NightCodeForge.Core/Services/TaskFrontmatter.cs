using NightCodeForge.Core.Domain;
using YamlDotNet.Serialization;

namespace NightCodeForge.Core.Services;

/// <summary>
/// YAML frontmatter representation for mirrored task markdown files.
/// </summary>
public class TaskFrontmatter
{
    public int? Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Workspace { get; set; }
    public string? Status { get; set; }
    
    [YamlMember(Alias = "origin")]
    public string? Origin { get; set; }
    
    [YamlMember(Alias = "current_stage")]
    public string? CurrentStage { get; set; }
    
    [YamlMember(Alias = "merge_mode")]
    public string? MergeMode { get; set; }

    [YamlMember(Alias = "waiting_for_human")]
    public bool WaitingForHuman { get; set; } = false;

    public int Priority { get; set; } = TaskPriority.Normal;
    
    [YamlMember(Alias = "base_branch")]
    public string BaseBranch { get; set; } = "main";
    
    [YamlMember(Alias = "feature_branch")]
    public string? FeatureBranch { get; set; }
    
    [YamlMember(Alias = "depends_on")]
    public TaskDependencyReferenceList? DependsOn { get; set; } = null;
    
    [YamlMember(Alias = "agent_profile")]
    public string? AgentProfile { get; set; }
    
    [YamlMember(Alias = "created_at")]
    public DateTime? CreatedAt { get; set; }
    
    [YamlMember(Alias = "updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
