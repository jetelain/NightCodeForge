namespace NightCodeForge.Core.Workflow.Stages;

using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow.Configuration;

/// <summary>
/// Universal data-driven agentic stage executing an LLM persona configured via AgenticStageConfig.
/// </summary>
public class GenericAgenticStage : IWorkflowStage
{
    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;
    public AgentMode RequiredMode { get; }
    public string StageDirective { get; set; }
    public string? PromptTemplate { get; set; }
    public string? AgentProfileId { get; }
    public string? LoopBackStageId { get; }
    public int MaxRetries { get; }
    public int? TimeoutMinutes { get; }

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    private readonly IPromptTemplateRenderer _renderer;
    private readonly TimeProvider _timeProvider;

    public GenericAgenticStage(
        string stageId,
        string displayName,
        AgenticStageConfig config,
        IPromptTemplateRenderer renderer,
        TimeProvider timeProvider)
    {
        StageId = stageId;
        DisplayName = displayName;
        RequiredMode = config.RequiredMode;
        StageDirective = config.StageDirective ?? string.Empty;
        PromptTemplate = config.PromptTemplate;
        AgentProfileId = config.AgentProfileId;
        LoopBackStageId = config.LoopBackStageId;
        MaxRetries = config.MaxRetries;
        TimeoutMinutes = config.TimeoutMinutes;
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public virtual string BuildBootstrapPrompt(ForgeTask task, PipelineContext context, string? stageDirective = null)
    {
        return DefaultBootstrapPromptBuilder.Build(this, task, context, stageDirective, _renderer, _timeProvider);
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}
