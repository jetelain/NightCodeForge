using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Execution context for a workflow pipeline execution instance.
/// </summary>
public class PipelineContext
{
    public Issue Issue { get; set; }
    public Workspace Workspace { get; set; }
    public PipelineDataBag DataBag { get; set; }
    public AgentProfile? CurrentProfile { get; set; }
    public int RetryCount { get; set; }
    public Dictionary<string, int> StageRetries { get; } = new();

    public PipelineContext(Issue issue, Workspace workspace, PipelineDataBag? dataBag = null)
    {
        Issue = issue ?? throw new ArgumentNullException(nameof(issue));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        DataBag = dataBag ?? PipelineDataBag.FromJson(issue.PipelineDataJson);
    }

    public void SyncDataBagToIssue()
    {
        Issue.PipelineDataJson = DataBag.ToJson();
    }

    public int IncrementRetry(string stageId)
    {
        RetryCount++;
        if (!StageRetries.ContainsKey(stageId))
        {
            StageRetries[stageId] = 0;
        }
        return ++StageRetries[stageId];
    }

    public void ResetRetries()
    {
        RetryCount = 0;
        StageRetries.Clear();
    }
}
