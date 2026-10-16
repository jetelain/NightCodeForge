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
            var reviewerFeedback = context.DataBag.Get<string>("ReviewerFeedback");
            var approvedPlan = context.DataBag.Get<string>("Stage1.ApprovedPlan") ?? context.DataBag.Get<string>("StageSummary_task-review");
            var taskPath = context.DataBag.Get<string>("TaskMarkdownPath");
            var implSummary = context.DataBag.Get<string>("StageSummary_implementation");
            var compilerErrors = context.DataBag.Get<List<string>>("diagnostics.compiler_errors");
            var answeredQuestions = task.Questions?
                .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer))
                .Select(q => new { prompt = q.Prompt, answer = q.Answer })
                .ToList();

            var model = new
            {
                task = new { id = task.Id, title = task.Title, description = task.Description },
                stage = new { id = stage.StageId, display_name = stage.DisplayName },
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

            return renderer.Render(promptTemplate, model).Trim();
        }

        return BuildLegacyFormat(stage.StageId, task, context, stageDirective);
    }

    private static string BuildLegacyFormat(string stageId, ForgeTask task, PipelineContext context, string? stageDirective)
    {
        var reviewerFeedback = context.DataBag.Get<string>("ReviewerFeedback");
        var feedbackSection = !string.IsNullOrWhiteSpace(reviewerFeedback)
            ? $"\n\n[Reviewer Requested Changes / Feedback]:\n{reviewerFeedback}\n"
            : string.Empty;

        var compilerErrors = context.DataBag.Get<List<string>>("diagnostics.compiler_errors");
        var diagnosticSection = compilerErrors != null && compilerErrors.Count > 0
            ? $"\n\n[Prior Diagnostic Failures to Remediate]:\n{string.Join("\n", compilerErrors)}\n"
            : string.Empty;

        var taskPath = context.DataBag.Get<string>("TaskMarkdownPath");
        var taskPathSection = !string.IsNullOrWhiteSpace(taskPath)
            ? (string.Equals(stageId, "task-review", StringComparison.OrdinalIgnoreCase)
                ? $"\n\n[ForgeTask Specification Document]:\n{taskPath}\n(CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write' to document your approved architecture, step-by-step implementation plan, and acceptance criteria. Your disk changes will be read back automatically upon stage completion to update the task specification and guide the implementation agent.)\n"
                : $"\n\n[ForgeTask Specification Document]:\n{taskPath} (You can read or update this document with read/edit tools.)\n")
            : string.Empty;

        var implSummary = context.DataBag.Get<string>("StageSummary_implementation");
        var implHandoffSection = !string.IsNullOrWhiteSpace(implSummary) && string.Equals(stageId, "code-review-fix", StringComparison.OrdinalIgnoreCase)
            ? $"\n\n[Implementation Agent Completion Summary]:\n{TruncateText(implSummary, 1500)}\n(Note: Treat these reported achievements as claims to verify against the git diff and test results. Do not assume claims are true without checking the code.)\n"
            : string.Empty;

        var bootstrapOverride = WorkspacePromptResolver.ResolveBootstrapPromptOverride(context.WorkingDirectory, stageId);
        if (!string.IsNullOrWhiteSpace(bootstrapOverride))
        {
            return $"{bootstrapOverride}{taskPathSection}{implHandoffSection}{feedbackSection}{diagnosticSection}".Trim();
        }

        var approvedPlan = context.DataBag.Get<string>("Stage1.ApprovedPlan") ?? context.DataBag.Get<string>("StageSummary_task-review");
        var planSection = !string.IsNullOrWhiteSpace(approvedPlan) && !string.Equals(stageId, "task-review", StringComparison.OrdinalIgnoreCase)
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
