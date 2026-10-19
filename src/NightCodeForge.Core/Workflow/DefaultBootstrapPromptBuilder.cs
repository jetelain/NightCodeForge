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
        IPromptTemplateRenderer? renderer = null,
        TimeProvider? timeProvider = null,
        string? currentDate = null)
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

        var provider = timeProvider ?? TimeProvider.System;
        var effectiveDate = !string.IsNullOrWhiteSpace(currentDate)
            ? currentDate
            : provider.GetUtcNow().ToString("yyyy-MM-dd");

        var reviewerFeedback = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback);
        var reviewDiffStat = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiffStat);
        var approvedPlan = context.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan) ?? context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview));
        if (!string.IsNullOrWhiteSpace(approvedPlan) &&
            !string.IsNullOrWhiteSpace(task.Description) &&
            string.Equals(approvedPlan.Trim(), task.Description.Trim(), StringComparison.Ordinal))
        {
            approvedPlan = null;
        }
        var taskPath = context.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
        var implSummary = context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.Implementation));
        var compilerErrors = context.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        var failedTests = context.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
        var buildFailure = context.DataBag.Get<string>(PipelineDataBagKeys.BuildFailureReason);
        var testFailure = context.DataBag.Get<string>(PipelineDataBagKeys.TestFailureReason);
        var latestAnsweredBatchId = task.Questions?
            .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrEmpty(q.BatchId))
            .OrderByDescending(q => q.AnsweredAt)
            .Select(q => q.BatchId)
            .FirstOrDefault();

        var answeredQuestions = task.Questions?
            .Where(q => q.Status == QuestionStatus.Answered &&
                        !string.IsNullOrWhiteSpace(q.Answer) &&
                        (latestAnsweredBatchId == null || q.BatchId == latestAnsweredBatchId))
            .Select(q => new { prompt = q.Prompt, answer = q.Answer })
            .ToList();

        var baseBranch = !string.IsNullOrWhiteSpace(task.BaseBranch) ? task.BaseBranch : "main";
        var branchName = !string.IsNullOrWhiteSpace(task.FeatureBranch) ? task.FeatureBranch : $"feature/task-{task.Id}";

        var model = new
        {
            task = new
            {
                id = task.Id,
                title = task.Title,
                description = task.Description,
                base_branch = baseBranch,
                feature_branch = branchName
            },
            stage = new { id = stage.StageId, display_name = stage.DisplayName, directive = effectiveDirective },
            stage_directive = effectiveDirective,
            current_date = effectiveDate,
            context = new
            {
                current_date = effectiveDate,
                task_markdown_path = string.IsNullOrWhiteSpace(taskPath) ? null : taskPath,
                approved_plan = string.IsNullOrWhiteSpace(approvedPlan) ? null : approvedPlan,
                reviewer_feedback = string.IsNullOrWhiteSpace(reviewerFeedback) ? null : reviewerFeedback,
                review_diff_stat = string.IsNullOrWhiteSpace(reviewDiffStat) ? null : reviewDiffStat,
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

        if (!promptTemplate.Contains("current_date", StringComparison.OrdinalIgnoreCase))
        {
            if (rendered.Contains("[Context]:", StringComparison.Ordinal))
            {
                var contextIdx = rendered.IndexOf("[Context]:", StringComparison.Ordinal);
                rendered = rendered.Insert(contextIdx + "[Context]:".Length, $"\n- Current Date: {effectiveDate}");
            }
            else
            {
                rendered = $"{rendered}\n\n[Context]:\n- Current Date: {effectiveDate}".Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(effectiveDirective) && !rendered.Contains(effectiveDirective))
        {
            rendered = $"{rendered}\n\n[Stage Directive]:\n{effectiveDirective}".Trim();
        }

        if (!string.IsNullOrWhiteSpace(reviewDiffStat) && !rendered.Contains(reviewDiffStat))
        {
            rendered = $"{rendered}\n\n[Git Diff Summary]:\n```diff\n{reviewDiffStat}\n```\nTo inspect the diff of a specific file against base branch '{baseBranch}', run:\ngit diff {baseBranch}...{branchName} -- <file-path>".Trim();
        }

        return rendered;
    }
}
