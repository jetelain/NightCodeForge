namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Sequence of stages defining an end-to-end task execution workflow.
/// </summary>
public interface IWorkflowPipeline
{
    IReadOnlyList<IWorkflowStage> Stages { get; }
    IWorkflowStage? GetNextStage(string currentStageId);
    IWorkflowStage? GetStage(string stageId);
}

/// <summary>
/// Default implementation of an ordered workflow pipeline.
/// </summary>
public class WorkflowPipeline : IWorkflowPipeline
{
    public IReadOnlyList<IWorkflowStage> Stages { get; }

    public WorkflowPipeline(IEnumerable<IWorkflowStage> stages)
    {
        if (stages == null) throw new ArgumentNullException(nameof(stages));
        Stages = stages.ToList().AsReadOnly();
    }

    public IWorkflowStage? GetStage(string stageId)
    {
        return Stages.FirstOrDefault(s => string.Equals(s.StageId, stageId, StringComparison.OrdinalIgnoreCase));
    }

    public IWorkflowStage? GetNextStage(string currentStageId)
    {
        if (string.IsNullOrWhiteSpace(currentStageId))
        {
            return Stages.FirstOrDefault();
        }

        var index = Stages.ToList().FindIndex(s => string.Equals(s.StageId, currentStageId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < Stages.Count)
        {
            return Stages[index + 1];
        }

        return null;
    }
}
