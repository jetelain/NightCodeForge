using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Execution context for a workflow pipeline execution instance.
/// </summary>
public class PipelineContext
{
    public ForgeTask ForgeTask { get; set; }
    public Workspace Workspace { get; set; }
    public PipelineDataBag DataBag { get; set; }
    public AgentProfile? CurrentProfile { get; set; }
    public IWorkflowPipeline? Pipeline { get; set; }
    public string? WorkingDirectory { get; set; }
    public int RetryCount { get; set; }
    public Dictionary<string, int> StageRetries { get; } = new();

    public PipelineContext(ForgeTask task, Workspace workspace, PipelineDataBag? dataBag = null)
    {
        ForgeTask = task ?? throw new ArgumentNullException(nameof(task));
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        DataBag = dataBag ?? PipelineDataBag.FromJson(task.PipelineDataJson);
        RetryCount = DataBag.Get<int?>("Workflow.RetryCount") ?? 0;
        var savedRetries = DataBag.Get<Dictionary<string, int>>("Workflow.StageRetries");
        if (savedRetries != null)
        {
            foreach (var kvp in savedRetries)
            {
                StageRetries[kvp.Key] = kvp.Value;
            }
        }
    }

    public void SyncDataBagToTask()
    {
        DataBag.Set("Workflow.RetryCount", RetryCount);
        DataBag.Set("Workflow.StageRetries", new Dictionary<string, int>(StageRetries));
        ForgeTask.PipelineDataJson = DataBag.ToJson();
    }

    public int IncrementRetry(string stageId)
    {
        RetryCount++;
        if (!StageRetries.ContainsKey(stageId))
        {
            StageRetries[stageId] = 0;
        }
        var next = ++StageRetries[stageId];
        DataBag.Set("Workflow.RetryCount", RetryCount);
        DataBag.Set("Workflow.StageRetries", new Dictionary<string, int>(StageRetries));
        return next;
    }

    public void ResetRetries()
    {
        RetryCount = 0;
        StageRetries.Clear();
        DataBag.Set("Workflow.RetryCount", 0);
        DataBag.Set("Workflow.StageRetries", new Dictionary<string, int>());
    }
}
