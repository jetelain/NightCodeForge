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
        var reviewDiff = context.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiff);
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

        var repoPath = !string.IsNullOrWhiteSpace(context.WorkingDirectory)
            ? context.WorkingDirectory
            : context.Workspace?.RepositoryPath;
        var projectConfig = CodeStackPresets.ResolveConfig(repoPath, context.DataBag);
        var buildCommand = !string.IsNullOrWhiteSpace(projectConfig.BuildCommand)
            ? projectConfig.BuildCommand
            : "dotnet build";
        var testCommand = !string.IsNullOrWhiteSpace(projectConfig.TestCommand)
            ? projectConfig.TestCommand
            : "dotnet test";

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
                review_diff = string.IsNullOrWhiteSpace(reviewDiff) ? null : reviewDiff,
                review_diff_stat = string.IsNullOrWhiteSpace(reviewDiffStat) ? null : reviewDiffStat,
                implementation_summary = string.IsNullOrWhiteSpace(implSummary) ? null : implSummary,
                compiler_errors = compilerErrors is { Count: > 0 } ? compilerErrors : null,
                failed_tests = failedTests is { Count: > 0 } ? failedTests : null,
                failure_reason = testFailure ?? buildFailure,
                build_failure_reason = string.IsNullOrWhiteSpace(buildFailure) ? null : buildFailure,
                test_failure_reason = string.IsNullOrWhiteSpace(testFailure) ? null : testFailure,
                answered_questions = answeredQuestions is { Count: > 0 } ? answeredQuestions : null,
                build_command = buildCommand,
                test_command = testCommand
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

        bool diffAlreadyRendered = (!string.IsNullOrWhiteSpace(reviewDiff) && rendered.Contains(reviewDiff)) ||
                                   (!string.IsNullOrWhiteSpace(reviewDiffStat) && rendered.Contains(reviewDiffStat));

        if (!diffAlreadyRendered)
        {
            if (!string.IsNullOrWhiteSpace(reviewDiff))
            {
                rendered = $"{rendered}\n\n[Git Diff ({baseBranch}...{branchName})]:\n```diff\n{reviewDiff}\n```\nTo inspect additional details or run git commands, use 'bash' (e.g. git diff, git log).".Trim();
            }
            else if (!string.IsNullOrWhiteSpace(reviewDiffStat))
            {
                rendered = $"{rendered}\n\n[Git Diff Summary]:\n```diff\n{reviewDiffStat}\n```\nTo inspect the diff of a specific file against base branch '{baseBranch}', run:\ngit diff {baseBranch}...{branchName} -- <file-path>".Trim();
            }
        }

        var conflictFiles = context.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles);
        if (conflictFiles is { Count: > 0 } && !rendered.Contains(ConflictResolutionConstants.ConflictDirectiveHeader))
        {
            var conflictBaseBranch = context.DataBag.Get<string>(PipelineDataBagKeys.ConflictBaseBranch) ?? baseBranch;
            var fileListText = string.Join("\n", conflictFiles.Select(f => $"- {f}"));
            var diagnosticsBlock = string.Empty;
            if (compilerErrors is { Count: > 0 })
            {
                var errorsText = string.Join("\n", compilerErrors);
                diagnosticsBlock = $"\nPREVIOUS COMPILER DIAGNOSTICS (Resolve these compilation errors):\n```\n{errorsText}\n```\n";
            }

            var conflictDirective = string.Format(
                ConflictResolutionConstants.ConflictDirectiveFormat,
                conflictBaseBranch,
                fileListText,
                buildCommand,
                testCommand,
                diagnosticsBlock).Trim();

            rendered = $"{conflictDirective}\n\n{rendered}".Trim();
        }

        return rendered;
    }
}

