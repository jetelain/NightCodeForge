namespace NightCodeForge.Core.Workflow;

using NightCodeForge.Core.Domain;

/// <summary>
/// Helper for building bootstrap prompts for workflow stages, handling both Scriban templates and legacy string concatenation.
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

        if (!string.IsNullOrWhiteSpace(promptTemplate))
        {
            renderer ??= new ScribanPromptTemplateRenderer();
            var effectiveDirective = !string.IsNullOrWhiteSpace(stageDirective)
                ? stageDirective
                : stage.StageDirective;

            var reviewerFeedback = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback);
            var approvedPlan = context.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan) ?? context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview));
            var taskPath = context.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
            var implSummary = context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.Implementation));
            var compilerErrors = context.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
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
                    implementation_summary = string.IsNullOrWhiteSpace(implSummary) ? null : implSummary,
                    compiler_errors = compilerErrors is { Count: > 0 } ? compilerErrors : null,
                    answered_questions = answeredQuestions is { Count: > 0 } ? answeredQuestions : null
                }
            };

            var rendered = renderer.Render(promptTemplate, model).Trim();
            if (!string.IsNullOrWhiteSpace(effectiveDirective) && !rendered.Contains(effectiveDirective))
            {
                rendered = $"{rendered}\n\n[Stage Directive]:\n{effectiveDirective}".Trim();
            }

            return rendered;
        }

        return BuildLegacyFormat(stage.StageId, task, context, stageDirective);
    }

    private static string BuildLegacyFormat(string stageId, ForgeTask task, PipelineContext context, string? stageDirective)
    {
        var reviewerFeedback = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback);
        var feedbackSection = !string.IsNullOrWhiteSpace(reviewerFeedback)
            ? $"\n\n[Reviewer Requested Changes / Feedback]:\n{reviewerFeedback}\n"
            : string.Empty;

        var compilerErrors = context.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        var diagnosticSection = compilerErrors != null && compilerErrors.Count > 0
            ? $"\n\n[Prior Diagnostic Failures to Remediate]:\n{string.Join("\n", compilerErrors)}\n"
            : string.Empty;

        var taskPath = context.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
        var taskPathSection = !string.IsNullOrWhiteSpace(taskPath)
            ? $"\n\n[ForgeTask Specification Document]:\n{taskPath}\n"
            : string.Empty;

        var implSummary = context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.Implementation));
        var implHandoffSection = !string.IsNullOrWhiteSpace(implSummary)
            ? $"\n\n[Implementation Agent Completion Summary]:\n{TruncateText(implSummary, 1500)}\n"
            : string.Empty;

        var bootstrapOverride = WorkspacePromptResolver.ResolveBootstrapPromptOverride(context.WorkingDirectory, stageId);
        if (!string.IsNullOrWhiteSpace(bootstrapOverride))
        {
            return $"{bootstrapOverride}{taskPathSection}{implHandoffSection}{feedbackSection}{diagnosticSection}".Trim();
        }

        var approvedPlan = context.DataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan) ?? context.DataBag.Get<string>(PipelineDataBagKeys.StageSummary(StandardStageIds.TaskReview));
        var planSection = !string.IsNullOrWhiteSpace(approvedPlan)
            ? $"\n\n[Approved Implementation Plan]:\n{approvedPlan}\n"
            : string.Empty;

        var answeredQuestions = task.Questions?.Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer)).ToList();
        var qaSection = answeredQuestions != null && answeredQuestions.Count > 0
            ? $"\n\n[Human Clarifications & Answers]:\n" + string.Join("\n", answeredQuestions.Select(q => $"Q: {q.Prompt}\nA: {q.Answer}")) + "\n"
            : string.Empty;

        var directiveSection = !string.IsNullOrWhiteSpace(stageDirective)
            ? $"\n\n[Stage Directive]:\n{stageDirective}\n"
            : string.Empty;

        return $"Execute stage '{stageId}' for task #{task.Id}: {task.Title}\n{task.Description}{taskPathSection}{directiveSection}{planSection}{implHandoffSection}{qaSection}{feedbackSection}{diagnosticSection}".Trim();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }
        return text.Substring(0, maxLength) + "\n... [truncated]";
    }
}
