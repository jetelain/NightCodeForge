using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Validates workflow definitions for sequential correctness, valid queue tiers, and contiguous cohort tier groupings.
/// </summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>
    /// Validates that a workflow definition has valid queue tiers and contiguous cohort tier groupings.
    /// </summary>
    public static void Validate(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        if (workflow.Stages == null || workflow.Stages.Count == 0)
        {
            return;
        }

        var orderedStages = workflow.Stages.OrderBy(s => s.SequenceOrder).ToList();

        // 1. Validate each stage has a valid defined QueueTier
        foreach (var stage in orderedStages)
        {
            if (!Enum.IsDefined(typeof(StageQueueTier), stage.QueueTier) || (int)stage.QueueTier == 0)
            {
                throw new WorkflowValidationException(
                    $"Stage '{stage.StageId}' does not have a designated QueueTier. Workflow stages must define a valid QueueTier.");
            }
        }

        // 2. Validate cohort contiguity: stages for any given QueueTier must appear contiguously in sequence
        StageQueueTier? activeTier = null;
        var seenTiers = new HashSet<StageQueueTier>();

        foreach (var stage in orderedStages)
        {
            var tier = stage.QueueTier;
            if (activeTier != tier)
            {
                if (seenTiers.Contains(tier))
                {
                    throw new WorkflowValidationException(
                        $"Queue tier cohort '{tier}' has non-contiguous stages in sequence. All stages in tier '{tier}' must appear contiguously.");
                }

                seenTiers.Add(tier);
                activeTier = tier;
            }
        }
    }

    /// <summary>
    /// Validates that a sequence of workflow stages has valid queue tiers and contiguous cohort tier groupings.
    /// </summary>
    public static void Validate(IEnumerable<IWorkflowStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var stageList = stages.ToList();

        // 1. Validate each stage has a valid defined QueueTier
        foreach (var stage in stageList)
        {
            if (!Enum.IsDefined(typeof(StageQueueTier), stage.QueueTier) || (int)stage.QueueTier == 0)
            {
                throw new WorkflowValidationException(
                    $"Stage '{stage.StageId}' does not have a designated QueueTier. Workflow stages must define a valid QueueTier.");
            }
        }

        // 2. Validate cohort contiguity: stages for any given QueueTier must appear contiguously in sequence
        StageQueueTier? activeTier = null;
        var seenTiers = new HashSet<StageQueueTier>();

        foreach (var stage in stageList)
        {
            var tier = stage.QueueTier;
            if (activeTier != tier)
            {
                if (seenTiers.Contains(tier))
                {
                    throw new WorkflowValidationException(
                        $"Queue tier cohort '{tier}' has non-contiguous stages in sequence. All stages in tier '{tier}' must appear contiguously.");
                }

                seenTiers.Add(tier);
                activeTier = tier;
            }
        }
    }
}
