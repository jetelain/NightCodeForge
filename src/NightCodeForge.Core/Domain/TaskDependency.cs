namespace NightCodeForge.Core.Domain;

/// <summary>
/// Represents a dependency relationship where TaskId depends on DependsOnTaskId.
/// </summary>
public class TaskDependency
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int DependsOnTaskId { get; set; }

    public ForgeTask? ForgeTask { get; set; }
    public ForgeTask? DependsOnTask { get; set; }
}
