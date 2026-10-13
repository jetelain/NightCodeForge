using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Central implementation of IIssueOrchestrator orchestrating DAG validation, workspace locks,
/// Git branch checkouts, stage execution, metric recording, clean human interruption, and cascade unblocking.
/// </summary>
public class IssueOrchestrator : IIssueOrchestrator
{
    private readonly IIssueRepository _issueRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IDependencyResolutionEngine _dependencyEngine;
    private readonly IWorkspaceLockProvider _workspaceLockProvider;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly IIssueJobQueue _jobQueue;
    private readonly IIssueDependencyStore? _dependencyStore;
    private readonly IHarnessRegistry? _harnessRegistry;
    private readonly IGitMirrorSyncService? _gitMirrorSyncService;
    private readonly Services.IIssueMarkdownSyncService? _markdownSyncService;
    private readonly ILogger<IssueOrchestrator> _logger;

    public IssueOrchestrator(
        IIssueRepository issueRepository,
        IWorkspaceRepository workspaceRepository,
        IDependencyResolutionEngine dependencyEngine,
        IWorkspaceLockProvider workspaceLockProvider,
        IGitWorkspaceManager gitWorkspaceManager,
        IWorkflowPipelineProvider pipelineProvider,
        IIssueJobQueue jobQueue,
        IIssueDependencyStore? dependencyStore = null,
        IHarnessRegistry? harnessRegistry = null,
        IGitMirrorSyncService? gitMirrorSyncService = null,
        Services.IIssueMarkdownSyncService? markdownSyncService = null,
        ILogger<IssueOrchestrator>? logger = null)
    {
        _issueRepository = issueRepository ?? throw new ArgumentNullException(nameof(issueRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _workspaceLockProvider = workspaceLockProvider ?? throw new ArgumentNullException(nameof(workspaceLockProvider));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
        _jobQueue = jobQueue ?? throw new ArgumentNullException(nameof(jobQueue));
        _dependencyStore = dependencyStore;
        _harnessRegistry = harnessRegistry;
        _gitMirrorSyncService = gitMirrorSyncService;
        _markdownSyncService = markdownSyncService;
        _logger = logger ?? NullLogger<IssueOrchestrator>.Instance;
    }

    public async Task ProcessIssueAsync(int issueId, CancellationToken cancellationToken = default)
    {
        var issue = await _issueRepository.GetByIdAsync(issueId, cancellationToken);
        if (issue == null)
        {
            _logger.LogError("Issue #{IssueId} not found for processing.", issueId);
            return;
        }

        // 1. DAG Readiness Check
        var isReady = await _dependencyEngine.IsReadyForExecutionAsync(issueId, cancellationToken);
        if (!isReady)
        {
            _logger.LogInformation("Issue #{IssueId} prerequisites not yet completed; transitioning to WaitingOnDependencies.", issueId);
            issue.Status = IssueOverallStatus.WaitingOnDependencies;
            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            if (_dependencyStore != null)
            {
                await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.WaitingOnDependencies, cancellationToken);
            }
            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Information,
                Message = $"Issue #{issue.Id} is waiting on prerequisite issues before execution.",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 2. Workspace Lock Acquisition
        using var lockReleaser = await _workspaceLockProvider.AcquireLockAsync(issue.WorkspaceId, cancellationToken);

        var workspace = issue.Workspace ?? await _workspaceRepository.GetByIdAsync(issue.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            var err = $"Workspace #{issue.WorkspaceId} not found for issue #{issueId}.";
            _logger.LogError(err);
            issue.Status = IssueOverallStatus.Failed;
            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            if (_dependencyStore != null)
            {
                await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Failed, cancellationToken);
            }
            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Error,
                Message = err,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 3. Branch & Environment Setup
        if (string.IsNullOrWhiteSpace(issue.FeatureBranch))
        {
            issue.FeatureBranch = $"feature/issue-{issue.Id}";
        }

        try
        {
            await _gitWorkspaceManager.CheckoutIssueBranchAsync(workspace, issue.Id, issue.BaseBranch, cancellationToken);

            if (_gitMirrorSyncService != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
            {
                try
                {
                    await _gitMirrorSyncService.ConfigureHostRepositoryAsync(workspace.RepositoryPath, cancellationToken);
                    await _gitMirrorSyncService.FetchFromHostAsync(workspace.Ext4MirrorPath, issue.BaseBranch, cancellationToken);
                }
                catch (Exception mex)
                {
                    _logger.LogWarning(mex, "Ext4 mirror fetch synchronization failed for issue #{IssueId}", issueId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed checking out feature branch for issue #{IssueId}", issueId);
            issue.Status = IssueOverallStatus.Failed;
            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            if (_dependencyStore != null)
            {
                await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Failed, cancellationToken);
            }
            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Error,
                Message = $"Failed checkout branch: {ex.Message}",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // 4. Resolve Stage & Pipeline
        var pipeline = _pipelineProvider.GetPipeline(issue);
        IWorkflowStage? stage;

        if (string.IsNullOrWhiteSpace(issue.CurrentStageId))
        {
            stage = pipeline.Stages.FirstOrDefault();
            if (stage != null)
            {
                issue.CurrentStageId = stage.StageId;
            }
        }
        else
        {
            stage = pipeline.GetStage(issue.CurrentStageId);
        }

        if (stage == null)
        {
            var err = $"Stage '{issue.CurrentStageId}' not found in pipeline for issue #{issueId}.";
            _logger.LogError(err);
            issue.Status = IssueOverallStatus.Failed;
            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            if (_dependencyStore != null)
            {
                await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Failed, cancellationToken);
            }
            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Error,
                Message = err,
                Timestamp = DateTime.UtcNow
            }, cancellationToken);
            return;
        }

        // Resolve Agent Profile matching the current stage, or fallback to first
        var profile = workspace.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, stage.StageId, StringComparison.OrdinalIgnoreCase) ||
            (p.StageDirectives != null && p.StageDirectives.ContainsKey(stage.StageId)))
            ?? workspace.AgentProfiles?.FirstOrDefault();

        issue.Status = IssueOverallStatus.Running;
        await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
        if (_dependencyStore != null)
        {
            await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Running, cancellationToken);
        }

        var pipelineContext = new PipelineContext(issue, workspace);
        pipelineContext.CurrentProfile = profile;

        var startedAt = DateTime.UtcNow;
        StageExecutionResult result;

        try
        {
            if (stage.Kind == WorkflowStageKind.Agentic && _harnessRegistry != null && !HasCustomHandler(stage))
            {
                var harnessId = profile?.HarnessId ?? "crucible";
                if (_harnessRegistry.TryGetHarness(harnessId, out var harness) && harness != null)
                {
                    var lastCheckpoint = issue.Checkpoints?.OrderByDescending(c => c.CreatedAt).FirstOrDefault();
                    var lastHumanAnswer = pipelineContext.DataBag.Get<string>("LastHumanAnswer");

                    var stageDirective = (profile?.StageDirectives != null && profile.StageDirectives.TryGetValue(stage.StageId, out var customDirective) && !string.IsNullOrWhiteSpace(customDirective))
                        ? customDirective
                        : stage.StageDirective;

                    if (stage.RequiredMode == AgentMode.Full && profile != null && profile.Mode == AgentMode.ReadOnly)
                    {
                        var errorMsg = $"Stage '{stage.StageId}' requires AgentMode.Full capabilities, but profile '{profile.Id}' is configured as AgentMode.ReadOnly.";
                        _logger.LogError(errorMsg);
                        result = StageExecutionResult.Failure(errorMsg);
                    }
                    else
                    {
                        var agentContext = new AgentSessionContext
                        {
                            Issue = issue,
                            Workspace = workspace,
                            Profile = profile ?? new AgentProfile { Id = "default", HarnessId = harnessId, ModelName = "default" },
                            StageId = stage.StageId,
                            StageDirective = stageDirective,
                            RequiredMode = stage.RequiredMode,
                            WorkspacePath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) ? workspace.Ext4MirrorPath : workspace.RepositoryPath,
                            SerializedStateJson = lastCheckpoint?.SerializedMemory,
                            PendingHumanAnswer = lastHumanAnswer,
                            BootstrapPrompt = BuildBootstrapPrompt(stage.StageId, issue, pipelineContext, stageDirective),
                            OptimizationFlags = profile?.OptimizationFlags ?? new LocalOptimizationFlags(),
                            PipelineContext = pipelineContext
                        };

                        var runResult = await harness.RunStageAsync(agentContext, cancellationToken);

                        if (!string.IsNullOrEmpty(runResult.UpdatedState))
                        {
                            pipelineContext.DataBag.Set("SerializedMemory", runResult.UpdatedState);
                        }

                        if (!string.IsNullOrEmpty(runResult.Summary))
                        {
                            pipelineContext.DataBag.Set($"StageSummary_{stage.StageId}", runResult.Summary);
                            if (string.Equals(stage.StageId, "issue-review", StringComparison.OrdinalIgnoreCase))
                            {
                                pipelineContext.DataBag.Set("Stage1.ApprovedPlan", runResult.Summary);
                            }
                        }

                        result = MapAgentRunResultToStageResult(runResult, stage, profile, startedAt);
                    }
                }
                else
                {
                    result = await stage.ExecuteAsync(pipelineContext, cancellationToken);
                }
            }
            else
            {
                result = await stage.ExecuteAsync(pipelineContext, cancellationToken);
            }
        }
        catch (HumanInterruptionException hex)
        {
            _logger.LogInformation("Stage '{StageId}' triggered HumanInterruptionException: {Prompt}", stage.StageId, hex.Question.Prompt);
            result = StageExecutionResult.WaitingForHuman(hex.Question);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stage '{StageId}' failed with unhandled exception for issue #{IssueId}", stage.StageId, issueId);
            result = StageExecutionResult.Failure(ex.Message);
        }

        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;

        // Save execution metrics record
        var record = result.ExecutionRecord ?? new StageExecutionRecord();
        record.IssueId = issue.Id;
        record.StageId = stage.StageId;
        record.StageKind = stage.Kind.ToString();
        record.AgentProfileId = profile?.Id ?? string.Empty;
        record.HarnessId = profile?.HarnessId ?? string.Empty;
        record.ModelName = profile?.ModelName ?? string.Empty;
        record.StartedAt = startedAt;
        record.CompletedAt = completedAt;
        record.DurationMs = durationMs;
        record.Status = result.NeedsClarification
            ? StageExecutionStatus.Completed
            : (result.IsSuccess ? StageExecutionStatus.Completed : StageExecutionStatus.Failed);

        await _issueRepository.AddExecutionRecordAsync(record, cancellationToken);

        // 5. Result Evaluation
        if (result.NeedsClarification)
        {
            // Case A: WaitingForHuman - clean job success, freeing single GPU worker
            var question = result.ClarificationQuestion ?? new Question
            {
                Prompt = "Human input required to proceed.",
                Status = QuestionStatus.Pending
            };
            question.IssueId = issue.Id;
            question.StageId = stage.StageId;
            question.Status = QuestionStatus.Pending;
            question.AskedAt = DateTime.UtcNow;

            issue.Questions.Add(question);

            string commitHash = string.Empty;
            try
            {
                commitHash = await _gitWorkspaceManager.CreateWipCheckpointAsync(
                    workspace,
                    issue.Id,
                    stage.StageId,
                    $"WIP checkpoint before human clarification: {question.Prompt}",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed creating WIP checkpoint commit for issue #{IssueId}", issue.Id);
            }

            var checkpoint = new StageCheckpoint
            {
                IssueId = issue.Id,
                StageId = stage.StageId,
                GitCommitHash = commitHash,
                SerializedMemory = pipelineContext.DataBag.Get<string>("SerializedMemory") ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            await _issueRepository.AddCheckpointAsync(checkpoint, cancellationToken);

            issue.Status = IssueOverallStatus.BlockedQuestion;
            pipelineContext.SyncDataBagToIssue();
            await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
            if (_dependencyStore != null)
            {
                await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.BlockedQuestion, cancellationToken);
            }
            await SyncIssueMarkdownAndMirrorAsync(issue, workspace, cancellationToken);

            await _issueRepository.AddExecutionLogAsync(new ExecutionLog
            {
                IssueId = issue.Id,
                Level = LogLevel.Information,
                Message = $"Issue #{issue.Id} paused waiting for human input to: {question.Prompt}",
                Timestamp = DateTime.UtcNow
            }, cancellationToken);

            // Clean return: Hangfire marks job as Succeeded, GPU worker freed
            return;
        }

        if (result.IsSuccess)
        {
            // Case B: Stage Completed
            pipelineContext.ResetRetries();

            var checkpoint = new StageCheckpoint
            {
                IssueId = issue.Id,
                StageId = stage.StageId,
                GitCommitHash = string.Empty,
                SerializedMemory = pipelineContext.DataBag.Get<string>("SerializedMemory") ?? string.Empty,
                CreatedAt = DateTime.UtcNow
            };
            await _issueRepository.AddCheckpointAsync(checkpoint, cancellationToken);

            var nextStageId = result.TargetRoutingStageId;
            IWorkflowStage? nextStage = !string.IsNullOrWhiteSpace(nextStageId)
                ? pipeline.GetStage(nextStageId)
                : pipeline.GetNextStage(stage.StageId);

            if (nextStage != null)
            {
                issue.CurrentStageId = nextStage.StageId;
                issue.Status = IssueOverallStatus.Pending;
                pipelineContext.SyncDataBagToIssue();
                await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
                if (_dependencyStore != null)
                {
                    await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Pending, cancellationToken);
                }
                await SyncIssueMarkdownAndMirrorAsync(issue, workspace, cancellationToken);

                // Enqueue next stage to standard inference queue
                _jobQueue.EnqueueInference(issue.Id);
            }
            else
            {
                // Final stage reached: Transition to Review and generate review diff
                issue.Status = IssueOverallStatus.Review;

                string diff = string.Empty;
                try
                {
                    diff = await _gitWorkspaceManager.GenerateDiffAsync(workspace, issue.Id, issue.BaseBranch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed generating review diff for issue #{IssueId}", issue.Id);
                }

                pipelineContext.DataBag.Set("ReviewDiff", diff);
                pipelineContext.SyncDataBagToIssue();
                await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
                if (_dependencyStore != null)
                {
                    await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Review, cancellationToken);
                }
                await SyncIssueMarkdownAndMirrorAsync(issue, workspace, cancellationToken);

                await _issueRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    IssueId = issue.Id,
                    Level = LogLevel.Information,
                    Message = $"Pipeline completed all stages for issue #{issue.Id}. Transitioned to Review.",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);
            }
            return;
        }

        // Loopback / Retry Routing (e.g. deterministic build, test, or lint quality gate failed and routed back to implementation)
        if (!string.IsNullOrWhiteSpace(result.TargetRoutingStageId))
        {
            var retries = pipelineContext.IncrementRetry(stage.StageId);
            var effectiveMaxRetries = pipelineContext.DataBag.Get<int?>("Workflow.MaxRetries") ?? 3;
            if (retries <= effectiveMaxRetries)
            {
                _logger.LogWarning("Stage '{StageId}' failed, routing backward to '{TargetStage}' (Retry {Retry}/{Max}): {Reason}",
                    stage.StageId, result.TargetRoutingStageId, retries, effectiveMaxRetries, result.ErrorMessage);

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    var errors = pipelineContext.DataBag.Get<List<string>>("diagnostics.compiler_errors") ?? new List<string>();
                    errors.Add($"[{stage.StageId} attempt {retries}] {result.ErrorMessage}");
                    pipelineContext.DataBag.Set("diagnostics.compiler_errors", errors);
                }

                issue.CurrentStageId = result.TargetRoutingStageId;
                issue.Status = IssueOverallStatus.Pending;
                pipelineContext.SyncDataBagToIssue();
                await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
                if (_dependencyStore != null)
                {
                    await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Pending, cancellationToken);
                }
                await SyncIssueMarkdownAndMirrorAsync(issue, workspace, cancellationToken);

                await _issueRepository.AddExecutionLogAsync(new ExecutionLog
                {
                    IssueId = issue.Id,
                    Level = LogLevel.Warning,
                    Message = $"Quality gate '{stage.StageId}' failed: {result.ErrorMessage}. Looping back to '{result.TargetRoutingStageId}' (attempt {retries}/{effectiveMaxRetries}).",
                    Timestamp = DateTime.UtcNow
                }, cancellationToken);

                _jobQueue.EnqueueUrgent(issue.Id);
                return;
            }
        }

        // Case C: Permanent Failure
        issue.Status = IssueOverallStatus.Failed;
        pipelineContext.SyncDataBagToIssue();
        await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
        if (_dependencyStore != null)
        {
            await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Failed, cancellationToken);
        }
        await SyncIssueMarkdownAndMirrorAsync(issue, workspace, cancellationToken);
        await _issueRepository.AddExecutionLogAsync(new ExecutionLog
        {
            IssueId = issue.Id,
            Level = LogLevel.Error,
            Message = $"Stage '{stage.StageId}' failed: {result.ErrorMessage}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);
    }

    public async Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default)
    {
        var question = await _issueRepository.GetQuestionByIdAsync(questionId, cancellationToken);
        if (question == null)
        {
            throw new KeyNotFoundException($"Question #{questionId} not found.");
        }

        question.Answer = answer;
        question.Status = QuestionStatus.Answered;
        question.AnsweredAt = DateTime.UtcNow;

        var issue = question.Issue ?? await _issueRepository.GetByIdAsync(question.IssueId, cancellationToken);
        if (issue == null)
        {
            throw new KeyNotFoundException($"Associated Issue #{question.IssueId} not found.");
        }

        issue.Status = IssueOverallStatus.Pending;

        var bag = PipelineDataBag.FromJson(issue.PipelineDataJson);
        bag.Set("LastHumanAnswer", answer);
        bag.Set($"QuestionAnswer_{question.Id}", answer);
        issue.PipelineDataJson = bag.ToJson();

        await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
        if (_dependencyStore != null)
        {
            await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Pending, cancellationToken);
        }

        await _issueRepository.AddExecutionLogAsync(new ExecutionLog
        {
            IssueId = issue.Id,
            Level = LogLevel.Information,
            Message = $"Human answer submitted for question #{questionId}: {answer}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Preemptive top-priority resumption on llm-urgent
        _jobQueue.EnqueueUrgent(issue.Id);
    }

    public async Task RequestChangesAsync(int issueId, string revisionNotes, CancellationToken cancellationToken = default)
    {
        var issue = await _issueRepository.GetByIdAsync(issueId, cancellationToken);
        if (issue == null)
        {
            throw new KeyNotFoundException($"Issue #{issueId} not found.");
        }

        // Re-route issue back to Implementation stage (Stage 2)
        issue.Status = IssueOverallStatus.Pending;
        issue.CurrentStageId = ImplementationStage.DefaultStageId;

        var bag = PipelineDataBag.FromJson(issue.PipelineDataJson);
        bag.Set("ReviewerFeedback", revisionNotes);
        issue.PipelineDataJson = bag.ToJson();

        await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
        if (_dependencyStore != null)
        {
            await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Pending, cancellationToken);
        }

        await _issueRepository.AddExecutionLogAsync(new ExecutionLog
        {
            IssueId = issue.Id,
            Level = LogLevel.Information,
            Message = $"Reviewer requested changes for issue #{issue.Id}: {revisionNotes}",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // Enqueue to llm-urgent for rapid rework
        _jobQueue.EnqueueUrgent(issue.Id);
    }

    public async Task CompleteIssueAndCascadeAsync(int issueId, string finalCommitMessage, CancellationToken cancellationToken = default)
    {
        var issue = await _issueRepository.GetByIdAsync(issueId, cancellationToken);
        if (issue == null)
        {
            throw new KeyNotFoundException($"Issue #{issueId} not found.");
        }

        using var lockReleaser = await _workspaceLockProvider.AcquireLockAsync(issue.WorkspaceId, cancellationToken);
        var workspace = issue.Workspace ?? await _workspaceRepository.GetByIdAsync(issue.WorkspaceId, cancellationToken);
        if (workspace == null)
        {
            throw new InvalidOperationException($"Workspace #{issue.WorkspaceId} not found.");
        }

        // 1. Squash & Merge feature branch into base branch
        await _gitWorkspaceManager.SquashAndMergeAsync(
            workspace,
            issue.Id,
            issue.BaseBranch,
            finalCommitMessage,
            cancellationToken);

        // 2. Mark issue as completed
        issue.Status = IssueOverallStatus.Completed;
        await _issueRepository.UpdateAsync(issue, cancellationToken: cancellationToken);
        if (_dependencyStore != null)
        {
            await _dependencyStore.UpdateIssueStatusAsync(issue.Id, IssueOverallStatus.Completed, cancellationToken);
        }

        await _issueRepository.AddExecutionLogAsync(new ExecutionLog
        {
            IssueId = issue.Id,
            Level = LogLevel.Information,
            Message = $"Issue #{issue.Id} completed and squash-merged to {issue.BaseBranch}.",
            Timestamp = DateTime.UtcNow
        }, cancellationToken);

        // 3. Cascade unblocking to dependent DAG tasks
        var unblocked = await _dependencyEngine.ResolveCascadeUnblockedIssuesAsync(issue.Id);
        foreach (var unblockedId in unblocked)
        {
            _logger.LogInformation("Issue #{PrereqId} completed; unblocked dependent issue #{UnblockedId}.", issue.Id, unblockedId);
            _jobQueue.EnqueueInference(unblockedId);
        }

        await SyncMarkdownIssueSafeAsync(workspace, issue, cancellationToken);
    }

    private static bool HasCustomHandler(IWorkflowStage stage)
    {
        var prop = stage.GetType().GetProperty("Handler");
        return prop != null && prop.GetValue(stage) != null;
    }

    private static StageExecutionResult MapAgentRunResultToStageResult(
        AgentRunResult runResult,
        IWorkflowStage stage,
        AgentProfile? profile,
        DateTime startedAt)
    {
        var completedAt = DateTime.UtcNow;
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;
        var metrics = runResult.Metrics ?? new AgentRunMetrics(durationMs);

        var record = new StageExecutionRecord
        {
            StageId = stage.StageId,
            StageKind = stage.Kind.ToString(),
            AgentProfileId = profile?.Id ?? string.Empty,
            HarnessId = profile?.HarnessId ?? string.Empty,
            ModelName = profile?.ModelName ?? string.Empty,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = durationMs,
            PromptTokens = (int)metrics.PromptTokens,
            CompletionTokens = (int)metrics.CompletionTokens,
            TotalTokens = (int)metrics.TotalTokens,
            Status = runResult.Status == AgentRunStatus.Failed ? StageExecutionStatus.Failed : StageExecutionStatus.Completed
        };

        if (runResult.Status == AgentRunStatus.WaitingForHuman)
        {
            var question = new Question
            {
                StageId = stage.StageId,
                Prompt = runResult.PendingQuestion ?? "Human clarification required.",
                Rationale = runResult.Summary ?? string.Empty,
                QuestionType = QuestionType.FreeText,
                Status = QuestionStatus.Pending
            };
            return StageExecutionResult.WaitingForHuman(question, record);
        }

        if (runResult.Status == AgentRunStatus.Completed)
        {
            return StageExecutionResult.Success(record: record);
        }

        return StageExecutionResult.Failure(runResult.Error ?? "Stage failed.", record: record);
    }

    private static string BuildBootstrapPrompt(string stageId, Issue issue, PipelineContext context, string? stageDirective = null)
    {
        var reviewerFeedback = context.DataBag.Get<string>("ReviewerFeedback");
        var feedbackSection = !string.IsNullOrWhiteSpace(reviewerFeedback)
            ? $"\n\n[Reviewer Requested Changes / Feedback]:\n{reviewerFeedback}\n"
            : string.Empty;

        var compilerErrors = context.DataBag.Get<List<string>>("diagnostics.compiler_errors");
        var diagnosticSection = compilerErrors != null && compilerErrors.Count > 0
            ? $"\n\n[Prior Diagnostic Failures to Remediate]:\n{string.Join("\n", compilerErrors)}\n"
            : string.Empty;

        var approvedPlan = context.DataBag.Get<string>("Stage1.ApprovedPlan") ?? context.DataBag.Get<string>("StageSummary_issue-review");
        var planSection = !string.IsNullOrWhiteSpace(approvedPlan) && !string.Equals(stageId, "issue-review", StringComparison.OrdinalIgnoreCase)
            ? $"\n\n[Approved Implementation Plan]:\n{approvedPlan}\n"
            : string.Empty;

        var answeredQuestions = issue.Questions?.Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer)).ToList();
        var qaSection = answeredQuestions != null && answeredQuestions.Count > 0
            ? $"\n\n[Human Clarifications & Answers]:\n" + string.Join("\n", answeredQuestions.Select(q => $"Q: {q.Prompt}\nA: {q.Answer}")) + "\n"
            : string.Empty;

        var directiveSection = !string.IsNullOrWhiteSpace(stageDirective)
            ? $"\n\n[Stage Directive]:\n{stageDirective}\n"
            : string.Empty;

        return $"Execute stage '{stageId}' for issue #{issue.Id}: {issue.Title}\n{issue.Description}{directiveSection}{planSection}{qaSection}{feedbackSection}{diagnosticSection}".Trim();
    }

    private async Task SyncMarkdownIssueSafeAsync(Workspace workspace, Issue issue, CancellationToken cancellationToken)
    {
        if (_markdownSyncService != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.SyncIssueToDiskAsync(issue, workspace.RepositoryPath, cancellationToken: cancellationToken);
            }
            catch (Exception mex)
            {
                _logger.LogWarning(mex, "Failed to mirror issue #{IssueId} to docs/issues/*.md", issue.Id);
            }
        }
    }

    private async Task SyncIssueMarkdownAndMirrorAsync(Issue issue, Workspace workspace, CancellationToken cancellationToken)
    {
        await SyncMarkdownIssueSafeAsync(workspace, issue, cancellationToken);

        if (_gitMirrorSyncService != null && !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            try
            {
                await _gitMirrorSyncService.PushToHostAsync(workspace.Ext4MirrorPath, issue.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed pushing ext4 mirror changes for issue #{IssueId} to host repository", issue.Id);
            }
        }
    }
}
