namespace NightCodeForge.Core.Workflow;

using NightCodeForge.Core.Domain;

/// <summary>
/// Helper for building bootstrap prompts for workflow stages using Scriban templates.
/// </summary>
public static class DefaultBootstrapPromptBuilder
{
    public static string Build(
        IWorkflowStage stage,
        ForgeTask task,
        PipelineContext context,
        string? stageDirective = null,
        IPromptTemplateRenderer? renderer = null)
    {
        var promptTemplate = WorkspacePromptResolver.ResolveBootstrapPromptOverride(context.WorkingDirectory, stage.StageId)
            ?? stage.PromptTemplate;

        if (string.IsNullOrWhiteSpace(promptTemplate))
        {
            throw new InvalidOperationException($"Stage '{stage.StageId}' does not have a prompt template configured.");
        }

        renderer ??= new ScribanPromptTemplateRenderer();
        var effectiveDirective = !string.IsNullOrWhiteSpace(stageDirective)
            ? stageDirective
            : stage.StageDirective;

        var reviewerFeedback = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback);
        var reviewDiff = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiff);
        var approvedPlan = context.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan) ?? context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview));
        var taskPath = context.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
        var implSummary = context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.Implementation));
        var compilerErrors = context.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        var failedTests = context.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
        var buildFailure = context.DataBag.Get<string>(PipelineDataBagKeys.BuildFailureReason);
        var testFailure = context.DataBag.Get<string>(PipelineDataBagKeys.TestFailureReason);
        var answeredQuestions = task.Questions?
            .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer))
            .Select(q => new { prompt = q.Prompt, answer = q.Answer })
            .ToList();

        var model = new
        {
            task = new { id = task.Id, title = task.Title, description = task.Description },
            stage = new { id = stage.StageId, display_name = stage.DisplayName, directive = effectiveDirective },
            stage_directive = effectiveDirective,
            context = new
            {
                task_markdown_path = string.IsNullOrWhiteSpace(taskPath) ? null : taskPath,
                approved_plan = string.IsNullOrWhiteSpace(approvedPlan) ? null : approvedPlan,
                reviewer_feedback = string.IsNullOrWhiteSpace(reviewerFeedback) ? null : reviewerFeedback,
                review_diff = string.IsNullOrWhiteSpace(reviewDiff) ? null : reviewDiff,
                implementation_summary = string.IsNullOrWhiteSpace(implSummary) ? null : implSummary,
                compiler_errors = compilerErrors is { Count: > 0 } ? compilerErrors : null,
                failed_tests = failedTests is { Count: > 0 } ? failedTests : null,
                failure_reason = testFailure ?? buildFailure,
                build_failure_reason = string.IsNullOrWhiteSpace(buildFailure) ? null : buildFailure,
                test_failure_reason = string.IsNullOrWhiteSpace(testFailure) ? null : testFailure,
                answered_questions = answeredQuestions is { Count: > 0 } ? answeredQuestions : null
            }
        };

        var rendered = renderer.Render(promptTemplate, model).Trim();
        if (!string.IsNullOrWhiteSpace(effectiveDirective) && !rendered.Contains(effectiveDirective))
        {
            rendered = $"{rendered}\n\n[Stage Directive]:\n{effectiveDirective}".Trim();
        }

        if (!string.IsNullOrWhiteSpace(reviewDiff) && !rendered.Contains(reviewDiff))
        {
            rendered = $"{rendered}\n\n[Git Diff to Review]:\n{reviewDiff}".Trim();
        }

        return rendered;
    }
}
