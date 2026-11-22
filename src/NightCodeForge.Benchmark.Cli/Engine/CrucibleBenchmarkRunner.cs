using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using Pmad.AI.Tools;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Stateless, single-shot Crucible benchmark runner executing model candidates across Implementation,
/// Code Review, and ForgeTask Review datasets using leak-free workspaces, in-memory conversation state,
/// live tool telemetry, and deterministic/judge verification.
/// </summary>
public sealed partial class CrucibleBenchmarkRunner : ICrucibleBenchmarkRunner
{
    private const int MaxInlineReviewDiffChars = 32_000;
    private const int MaxTaskTitleLength = 80;

    [GeneratedRegex(@"\bcsnav\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CsNavInvocationRegex();

    [GeneratedRegex(@"\bcodecondenser\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeCondenserInvocationRegex();

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: true) }
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly IBenchmarkDatasetLoader _datasetLoader;
    private readonly IBenchmarkWorkspaceManager _workspaceManager;
    private readonly IHarnessRegistry _harnessRegistry;
    private readonly IBenchmarkVerifier _verifier;
    private readonly IBenchmarkProvenanceProvider _provenanceProvider;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly ILogger<CrucibleBenchmarkRunner> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _predictionFileLocks = new(StringComparer.OrdinalIgnoreCase);

    public CrucibleBenchmarkRunner(
        IBenchmarkDatasetLoader datasetLoader,
        IBenchmarkWorkspaceManager workspaceManager,
        IHarnessRegistry harnessRegistry,
        IBenchmarkVerifier verifier,
        IBenchmarkProvenanceProvider provenanceProvider,
        IExecutionEnvironmentProvider envProvider,
        ILogger<CrucibleBenchmarkRunner> logger)
    {
        _datasetLoader = datasetLoader ?? throw new ArgumentNullException(nameof(datasetLoader));
        _workspaceManager = workspaceManager ?? throw new ArgumentNullException(nameof(workspaceManager));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _provenanceProvider = provenanceProvider ?? throw new ArgumentNullException(nameof(provenanceProvider));
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkRunReport> RunAsync(
        BenchmarkRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SuiteName))
        {
            throw new BenchmarkException("BenchmarkRunRequest.SuiteName cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new BenchmarkException("BenchmarkRunRequest.OutputDirectory cannot be empty.");
        }
        if (request.Candidates == null || request.Candidates.Count == 0)
        {
            throw new BenchmarkException("BenchmarkRunRequest.Candidates must contain at least one model candidate.");
        }
        if (request.MaxInstances is <= 0)
        {
            throw new BenchmarkException("BenchmarkRunRequest.MaxInstances must be greater than zero when specified.");
        }
        if (request.Concurrency <= 0)
        {
            throw new BenchmarkException("BenchmarkRunRequest.Concurrency must be greater than zero.");
        }

        var candidateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in request.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.CandidateId))
            {
                throw new BenchmarkException("BenchmarkModelCandidate.CandidateId cannot be empty.");
            }
            if (string.IsNullOrWhiteSpace(candidate.ModelName))
            {
                throw new BenchmarkException($"BenchmarkModelCandidate '{candidate.CandidateId}' must specify a non-empty ModelName.");
            }
            if (!candidateIds.Add(candidate.CandidateId.Trim()))
            {
                throw new BenchmarkException($"Duplicate CandidateId '{candidate.CandidateId}' in BenchmarkRunRequest.Candidates.");
            }
        }

        var allInstances = await _datasetLoader.LoadAsync(
            request.DatasetPath,
            request.CategoryOverride,
            cancellationToken).ConfigureAwait(false);

        IEnumerable<BenchmarkInstance> filtered = allInstances;
        if (request.InstanceIdFilter is { Count: > 0 })
        {
            var idSet = request.InstanceIdFilter
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(i => idSet.Contains(i.InstanceId));
        }

        if (request.MaxInstances is > 0)
        {
            filtered = filtered.Take(request.MaxInstances.Value);
        }

        var selectedInstances = filtered.ToList();
        if (selectedInstances.Count == 0)
        {
            throw new BenchmarkDatasetException("No dataset instances matched the requested filters.", request.DatasetPath);
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var existingResults = await LoadResumedResultsAsync(request.ResumeFromDir, cancellationToken).ConfigureAwait(false);
        var resumedByKey = new Dictionary<(string CandidateId, string InstanceId), BenchmarkInstanceResult>(CandidateInstanceKeyComparer.Instance);
        foreach (var existing in existingResults)
        {
            if (existing.Outcome is BenchmarkInstanceOutcome.Passed or BenchmarkInstanceOutcome.Failed)
            {
                resumedByKey[(existing.CandidateId, existing.InstanceId)] = existing;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeFromDir) &&
            !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ResumeFromDir)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.OutputDirectory)),
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var resumed in resumedByKey.Values)
            {
                if (resumed.Category == BenchmarkProblemCategory.Implementation ||
                    (resumed.Category == BenchmarkProblemCategory.CodeReview && !string.IsNullOrWhiteSpace(resumed.ModelPatch)))
                {
                    await AppendPredictionAsync(
                        request.OutputDirectory,
                        BenchmarkWorkspaceManager.SanitizePathSegment(resumed.CandidateId),
                        resumed.InstanceId,
                        resumed.ModelName,
                        resumed.ModelPatch ?? string.Empty,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var primaryCandidate = request.Candidates[0];
        var provenance = await _provenanceProvider.CaptureAsync(
            request.ProviderType,
            request.Endpoint,
            primaryCandidate.EnableStackSkills,
            primaryCandidate.OptimizationFlags,
            cancellationToken).ConfigureAwait(false);

        var crucibleHarness = _harnessRegistry.GetHarness(StandardHarnessIds.Crucible);

        var collectedResults = new ConcurrentBag<BenchmarkInstanceResult>(resumedByKey.Values);

        var workItems = new List<(BenchmarkModelCandidate Candidate, BenchmarkInstance Instance)>();
        foreach (var candidate in request.Candidates)
        {
            foreach (var instance in selectedInstances)
            {
                if (resumedByKey.ContainsKey((candidate.CandidateId, instance.InstanceId)))
                {
                    request.ProgressCallback?.Invoke($"[SKIP] {candidate.CandidateId} | {instance.InstanceId} (already completed in resume set)");
                    continue;
                }

                workItems.Add((candidate, instance));
            }
        }

        int maxConcurrency = Math.Max(1, request.Concurrency);
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = workItems.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await RunSingleEvaluationAsync(
                    crucibleHarness,
                    item.Candidate,
                    item.Instance,
                    request,
                    cancellationToken).ConfigureAwait(false);
                collectedResults.Add(result);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var orderedResults = collectedResults
            .OrderBy(r => r.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var report = BenchmarkSummaryAggregator.BuildReport(
            request.SuiteName,
            request.DatasetPath,
            provenance,
            request.Candidates,
            orderedResults);

        var reportPath = Path.Combine(request.OutputDirectory, BenchmarkPaths.ResultsFileName);
        var reportJson = JsonSerializer.Serialize(report, ReportJsonOptions);
        await File.WriteAllTextAsync(reportPath, reportJson, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private async Task<BenchmarkInstanceResult> RunSingleEvaluationAsync(
        IAgentHarness crucibleHarness,
        BenchmarkModelCandidate candidate,
        BenchmarkInstance instance,
        BenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        var category = instance.ResolveEffectiveCategory(request.CategoryOverride);
        var policy = BenchmarkStagePolicyResolver.Resolve(category, candidate.EnableStackSkills);
        var candidateSlug = BenchmarkWorkspaceManager.SanitizePathSegment(candidate.CandidateId);
        var instanceSlug = BenchmarkWorkspaceManager.SanitizePathSegment(instance.InstanceId);
        var containerName = $"ncf-bench-{Guid.NewGuid():N}"[..28];

        request.ProgressCallback?.Invoke($"[START] {candidate.EffectiveDisplayName} | {instance.InstanceId} ({category}/{instance.Language})");

        BenchmarkWorkspaceHandle? workspace = null;
        SandboxScopeDescriptor? scope = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            workspace = await _workspaceManager.ProvisionAsync(instance, category, cancellationToken: cancellationToken).ConfigureAwait(false);
            workspace = workspace with { ContainerName = containerName };

            var ephemeralWorkspace = new Workspace
            {
                Id = 0,
                Name = instance.Repo,
                RepositoryPath = workspace.WorkspacePath,
                DefaultBaseBranch = workspace.BaseBranch,
                ExecutionMode = request.ExecutionMode,
                ContainerImage = instance.CustomContainerImage,
                ContainerName = containerName
            };
            BenchmarkStagePolicyResolver.ConfigureWorkspaceSkills(ephemeralWorkspace, candidate.EnableStackSkills);

            var ephemeralTask = new ForgeTask
            {
                Id = 0,
                WorkspaceId = 0,
                Title = $"{instance.InstanceId}: {TruncateTitle(instance.ProblemStatement)}",
                Description = BuildTaskDescription(instance, category, request.IncludeHints, workspace),
                Status = TaskOverallStatus.Open,
                CurrentStageId = policy.StageId,
                BaseBranch = workspace.BaseBranch,
                FeatureBranch = workspace.FeatureBranch ?? string.Empty
            };

            var profile = new AgentProfile
            {
                Id = $"bench-{candidateSlug}-{policy.StageId}",
                Name = $"Benchmark ({candidate.EffectiveDisplayName})",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = candidate.ModelName,
                ThinkingEffort = candidate.ThinkingEffort,
                ThinkingTokenBudget = candidate.ThinkingTokenBudget,
                ContextWindowTokens = candidate.ContextWindowTokens,
                MaxOutputTokens = candidate.MaxOutputTokens,
                Temperature = candidate.Temperature,
                MaxTurns = candidate.MaxTurns,
                OptimizationFlags = candidate.OptimizationFlags?.Clone(),
                PermittedTools = policy.PermittedTools,
                Mode = policy.RequiredMode,
                SecurityTier = SandboxSecurityTier.Standard,
                RoutingTarget = candidate.RoutingTarget
            };

            scope = BuildExecutionScope(workspace.WorkspacePath, instance.CustomContainerImage, request.ExecutionMode, containerName, candidate.EnableStackSkills);

            var dispatchTelemetry = new LenientDispatchTelemetry();

            var transcriptPath = Path.Combine(
                request.OutputDirectory,
                BenchmarkPaths.TranscriptsSubdir,
                candidateSlug,
                $"{instanceSlug}.md");
            var capture = new InMemoryConversationCapture(transcriptPath);

            var sessionContext = new AgentSessionContext
            {
                ForgeTask = ephemeralTask,
                Workspace = ephemeralWorkspace,
                SandboxScope = scope,
                Profile = profile,
                OptimizationFlags = candidate.OptimizationFlags?.Clone(),
                StageId = policy.StageId,
                StageDirective = policy.StageDirective,
                RequiredMode = policy.RequiredMode,
                BootstrapPrompt = BuildBootstrapPrompt(instance, category, workspace),
                WorkspacePath = workspace.WorkspacePath,
                TaskMarkdownPath = workspace.TaskMarkdownPath,
                ReviewDiffStat = workspace.ReviewDiffStat
            };

            AgentRunResult runResult;
            bool timedOut = false;

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, candidate.InstanceTimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var agentStopwatch = Stopwatch.StartNew();
            try
            {
                using var _ = CrucibleDispatchTelemetryScope.Begin(dispatchTelemetry);
                using var __ = InMemoryCrucibleConversationCoordinator.BeginCapture(capture);
                runResult = await crucibleHarness.RunStageAsync(sessionContext, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                runResult = AgentRunResult.Cancelled(
                    $"Benchmark instance timed out after {candidate.InstanceTimeoutSeconds}s.",
                    new AgentRunMetrics(DurationMs: agentStopwatch.ElapsedMilliseconds));
            }
            finally
            {
                agentStopwatch.Stop();
            }

            if (capture.ConversationState != null)
            {
                await capture.ConversationState.DrainAsync().ConfigureAwait(false);
            }

            if (!timedOut && runResult.Status is AgentRunStatus.Cancelled or AgentRunStatus.Interrupted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutCts.IsCancellationRequested)
                {
                    timedOut = true;
                }
                else
                {
                    stopwatch.Stop();
                    var abnormalBreakdown = BuildTelemetryBreakdown(capture.TurnSink.Turns, dispatchTelemetry.CreateSnapshot());
                    var abnormalResult = BuildAbnormalResult(
                        instance,
                        candidate,
                        category,
                        BenchmarkInstanceOutcome.Error,
                        stopwatch.ElapsedMilliseconds,
                        capture,
                        runResult,
                        abnormalBreakdown,
                        runResult.Error ?? $"Agent execution ended unexpectedly with status '{runResult.Status}'.");
                    request.ProgressCallback?.Invoke($"[ERROR] {candidate.CandidateId} | {instance.InstanceId}: {abnormalResult.ErrorMessage}");
                    return abnormalResult;
                }
            }

            if (timedOut)
            {
                stopwatch.Stop();
                var timeoutBreakdown = BuildTelemetryBreakdown(capture.TurnSink.Turns, dispatchTelemetry.CreateSnapshot());
                var timeoutResult = BuildAbnormalResult(
                    instance,
                    candidate,
                    category,
                    BenchmarkInstanceOutcome.Timeout,
                    stopwatch.ElapsedMilliseconds,
                    capture,
                    runResult,
                    timeoutBreakdown,
                    runResult.Error ?? $"Benchmark instance timed out after {candidate.InstanceTimeoutSeconds}s.");
                request.ProgressCallback?.Invoke($"[TIMEOUT] {candidate.CandidateId} | {instance.InstanceId}");
                return timeoutResult;
            }

            if (runResult.Status == AgentRunStatus.Failed)
            {
                stopwatch.Stop();
                var failedBreakdown = BuildTelemetryBreakdown(capture.TurnSink.Turns, dispatchTelemetry.CreateSnapshot());
                var errorResult = BuildAbnormalResult(
                    instance,
                    candidate,
                    category,
                    BenchmarkInstanceOutcome.Error,
                    stopwatch.ElapsedMilliseconds,
                    capture,
                    runResult,
                    failedBreakdown,
                    runResult.Error ?? "Agent execution failed.");
                request.ProgressCallback?.Invoke($"[ERROR] {candidate.CandidateId} | {instance.InstanceId}: {errorResult.ErrorMessage}");
                return errorResult;
            }

            string? modelPatch = null;
            string? updatedTaskMarkdown = null;
            IReadOnlyList<string> modifiedOutsideDocsTasks = Array.Empty<string>();

            if (category == BenchmarkProblemCategory.Implementation)
            {
                modelPatch = await _workspaceManager.ExtractPatchAsync(workspace.WorkspacePath, workspace.BaseCommit, cancellationToken).ConfigureAwait(false);
            }
            else if (category == BenchmarkProblemCategory.CodeReview)
            {
                modelPatch = await _workspaceManager.ExtractPatchAsync(workspace.WorkspacePath, workspace.CandidateCommit ?? "HEAD", cancellationToken).ConfigureAwait(false);
            }
            else if (category == BenchmarkProblemCategory.TaskReview)
            {
                if (!string.IsNullOrWhiteSpace(workspace.TaskMarkdownPath))
                {
                    var fullTaskPath = Path.Combine(
                        workspace.WorkspacePath,
                        workspace.TaskMarkdownPath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(fullTaskPath))
                    {
                        updatedTaskMarkdown = await File.ReadAllTextAsync(fullTaskPath, cancellationToken).ConfigureAwait(false);
                    }
                }

                var modified = await _workspaceManager.GetModifiedFilesAsync(workspace.WorkspacePath, workspace.BaseCommit, cancellationToken).ConfigureAwait(false);
                modifiedOutsideDocsTasks = modified
                    .Where(path => !path.Replace('\\', '/').StartsWith(BenchmarkPaths.DocsTasksPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (category == BenchmarkProblemCategory.Implementation ||
                (category == BenchmarkProblemCategory.CodeReview && !string.IsNullOrWhiteSpace(modelPatch)))
            {
                await AppendPredictionAsync(
                    request.OutputDirectory,
                    candidateSlug,
                    instance.InstanceId,
                    candidate.ModelName,
                    modelPatch ?? string.Empty,
                    cancellationToken).ConfigureAwait(false);
            }

            var agentOutcome = new BenchmarkAgentOutcome
            {
                RunStatus = runResult.Status,
                ModelPatch = modelPatch,
                Summary = runResult.Summary,
                ReviewerFeedback = runResult.ReviewerFeedback,
                TargetRouting = runResult.TargetRouting,
                PendingQuestions = runResult.PendingQuestions ?? Array.Empty<Question>(),
                UpdatedTaskMarkdown = updatedTaskMarkdown,
                TaskMarkdownBefore = workspace.TaskMarkdownInitialContent,
                ModifiedFilesOutsideDocsTasks = modifiedOutsideDocsTasks
            };

            var verification = await _verifier.VerifyAsync(
                instance,
                workspace,
                agentOutcome,
                request.ExecutionMode,
                request.VerifyWithTests,
                request.JudgeConfig,
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            long promptTokens = runResult.Metrics.PromptTokens;
            long completionTokens = runResult.Metrics.CompletionTokens;
            long totalTokens = runResult.Metrics.TotalTokens > 0 ? runResult.Metrics.TotalTokens : (promptTokens + completionTokens);
            long durationMs = Math.Max(1, stopwatch.ElapsedMilliseconds);
            long agentDurationMs = runResult.Metrics.DurationMs > 0
                ? runResult.Metrics.DurationMs
                : Math.Max(1, agentStopwatch.ElapsedMilliseconds);
            double tokensPerSecond = completionTokens > 0 ? completionTokens / (agentDurationMs / 1000.0) : 0.0;

            var telemetry = BuildTelemetryBreakdown(capture.TurnSink.Turns, dispatchTelemetry.CreateSnapshot());
            var finalOutcome = verification.Passed ? BenchmarkInstanceOutcome.Passed : BenchmarkInstanceOutcome.Failed;

            var instanceResult = new BenchmarkInstanceResult
            {
                InstanceId = instance.InstanceId,
                CandidateId = candidate.CandidateId,
                ModelName = candidate.ModelName,
                Category = category,
                Language = instance.Language,
                Outcome = finalOutcome,
                DurationMs = durationMs,
                Turns = ResolveTurnCount(capture),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                TokensPerSecond = tokensPerSecond,
                ModelPatch = modelPatch,
                TranscriptPath = capture.SavedTranscriptPath,
                ActualVerdict = verification.ActualVerdict,
                DeterministicScore = verification.DeterministicScore,
                JudgeScore = verification.JudgeScore,
                JudgeRationale = verification.JudgeRationale,
                TaskDifficultyResult = verification.TaskDifficultyResult,
                FailToPassPassed = verification.FailToPassPassed,
                FailToPassTotal = verification.FailToPassTotal,
                PassToPassPassed = verification.PassToPassPassed,
                PassToPassTotal = verification.PassToPassTotal,
                Telemetry = telemetry,
                ErrorMessage = verification.ErrorMessage,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            request.ProgressCallback?.Invoke($"[{finalOutcome.ToString().ToUpperInvariant()}] {candidate.CandidateId} | {instance.InstanceId} ({durationMs}ms, score={verification.DeterministicScore:F2})");
            return instanceResult;
        }
        finally
        {
            if (scope != null && request.ExecutionMode != ExecutionMode.BareHost)
            {
                try
                {
                    await _envProvider.CleanupScopeEnvironmentAsync(scope, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up benchmark execution environment '{ContainerName}'.", containerName);
                }
            }

            if (workspace != null)
            {
                try
                {
                    await _workspaceManager.CleanupAsync(workspace, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up benchmark workspace '{WorkspacePath}'.", workspace.WorkspacePath);
                }
            }
        }
    }

    private async Task AppendPredictionAsync(
        string outputDirectory,
        string candidateSlug,
        string instanceId,
        string modelName,
        string modelPatch,
        CancellationToken cancellationToken)
    {
        var candidateDir = Path.Combine(outputDirectory, candidateSlug);
        Directory.CreateDirectory(candidateDir);
        var predictionsPath = Path.Combine(candidateDir, BenchmarkPaths.PredictionsFileName);

        var lineObj = new Dictionary<string, string>
        {
            ["instance_id"] = instanceId,
            ["model_name_or_path"] = modelName,
            ["model_patch"] = modelPatch
        };
        var lineJson = JsonSerializer.Serialize(lineObj, CompactJsonOptions) + Environment.NewLine;

        var fileLock = _predictionFileLocks.GetOrAdd(predictionsPath, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(predictionsPath, lineJson, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            fileLock.Release();
        }
    }

    private static async Task<IReadOnlyList<BenchmarkInstanceResult>> LoadResumedResultsAsync(
        string? resumeFromDir,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resumeFromDir))
        {
            return Array.Empty<BenchmarkInstanceResult>();
        }

        var resultsFile = Path.Combine(resumeFromDir, BenchmarkPaths.ResultsFileName);
        if (!File.Exists(resultsFile))
        {
            throw new BenchmarkException($"Resume directory '{resumeFromDir}' does not contain '{BenchmarkPaths.ResultsFileName}'.");
        }

        var json = await File.ReadAllTextAsync(resultsFile, cancellationToken).ConfigureAwait(false);
        BenchmarkRunReport? priorReport;
        try
        {
            priorReport = JsonSerializer.Deserialize<BenchmarkRunReport>(json, ReportJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new BenchmarkException($"Failed to parse prior benchmark report at '{resultsFile}': {ex.Message}", ex);
        }

        if (priorReport == null)
        {
            throw new BenchmarkException($"Failed to deserialize prior benchmark report at '{resultsFile}'.");
        }

        return priorReport.Results;
    }

    internal static SandboxScopeDescriptor BuildExecutionScope(
        string workspacePath,
        string? customContainerImage,
        ExecutionMode executionMode,
        string containerName,
        bool enableStackSkills)
    {
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: workspacePath,
            isReadOnly: false);

        return new SandboxScopeDescriptor(
            scopeKind: SandboxScopeKind.WorkspaceClone,
            executionMode: executionMode,
            securityTier: SandboxSecurityTier.Standard,
            mountTable: mountTable,
            workspaceId: null,
            taskId: null,
            containerImage: customContainerImage,
            containerName: containerName,
            repositoryPath: workspacePath,
            virtualRootPath: SandboxMountTable.WorkspaceVirtualRoot,
            defaultWorkingDirectory: SandboxMountTable.WorkspaceVirtualRoot,
            scopeCacheKey: $"bench-run:{containerName}",
            hostWorkspacePath: workspacePath,
            canonicalClonePath: null,
            mountHostDotnetTools: enableStackSkills,
            protectCloneWorkingTree: false);
    }

    private static string BuildTaskDescription(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        bool includeHints,
        BenchmarkWorkspaceHandle workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine(instance.ProblemStatement.Trim());

        if (includeHints && !string.IsNullOrWhiteSpace(instance.HintsText))
        {
            sb.AppendLine();
            sb.AppendLine("## Hints");
            sb.AppendLine(instance.HintsText.Trim());
        }

        if (category == BenchmarkProblemCategory.CodeReview && !string.IsNullOrWhiteSpace(instance.TaskSpecification))
        {
            sb.AppendLine();
            sb.AppendLine("## Task Specification");
            sb.AppendLine(instance.TaskSpecification.Trim());
        }
        else if (category == BenchmarkProblemCategory.TaskReview && !string.IsNullOrWhiteSpace(workspace.TaskMarkdownPath))
        {
            sb.AppendLine();
            sb.AppendLine($"Task specification file on disk: {workspace.TaskMarkdownPath}");
        }

        return sb.ToString();
    }

    private static string BuildBootstrapPrompt(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        BenchmarkWorkspaceHandle workspace)
    {
        return category switch
        {
            BenchmarkProblemCategory.Implementation =>
                $"Execute stage 'Implementation' for benchmark instance '{instance.InstanceId}'.\n" +
                $"Inspect the repository and solve the problem described in <task>. Verify your fix with build/test commands using 'bash'.",

            BenchmarkProblemCategory.CodeReview =>
                BuildCodeReviewBootstrapPrompt(instance, workspace),

            BenchmarkProblemCategory.TaskReview =>
                $"Execute stage 'ForgeTask Review' for benchmark instance '{instance.InstanceId}'.\n" +
                $"Please inspect the repository and enrich '{workspace.TaskMarkdownPath}' on disk with your technical architecture, affected files, step-by-step implementation plan, and acceptance criteria. " +
                $"If critical requirements are ambiguous or contradictory, call 'ask' instead of guessing.",

            _ => CrucibleConversationCoordinator.DefaultBootstrapPrompt
        };
    }

    private static string BuildCodeReviewBootstrapPrompt(BenchmarkInstance instance, BenchmarkWorkspaceHandle workspace)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Execute stage 'Code Review & Fix' for benchmark instance '{instance.InstanceId}'.");
        sb.AppendLine($"Compare branch '{workspace.FeatureBranch}' against base branch '{workspace.BaseBranch}'.");

        var diff = workspace.ReviewDiff ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(diff) && diff.Length <= MaxInlineReviewDiffChars)
        {
            sb.AppendLine();
            sb.AppendLine($"[Git Diff ({workspace.BaseBranch}...{workspace.FeatureBranch})]:");
            sb.AppendLine("```diff");
            sb.AppendLine(diff.TrimEnd());
            sb.AppendLine("```");
        }
        else if (!string.IsNullOrWhiteSpace(workspace.ReviewDiffStat))
        {
            sb.AppendLine();
            sb.AppendLine("[Git Diff Summary]:");
            sb.AppendLine("```diff");
            sb.AppendLine(workspace.ReviewDiffStat.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine($"To inspect full changes run: git diff {workspace.BaseBranch}...{workspace.FeatureBranch}");
        }

        return sb.ToString();
    }

    private static string TruncateTitle(string text)
    {
        var firstLine = (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Benchmark";
        return firstLine.Length <= MaxTaskTitleLength ? firstLine : firstLine[..MaxTaskTitleLength];
    }

    private static int ResolveTurnCount(InMemoryConversationCapture capture)
    {
        if (capture.TotalModelCalls > 0)
        {
            return (int)Math.Min(int.MaxValue, capture.TotalModelCalls);
        }

        var turns = capture.TurnSink?.Turns;
        if (turns == null || turns.Count == 0)
        {
            return 0;
        }

        return turns.Count(t => t.Role is StageTurnRole.Assistant or StageTurnRole.Tool);
    }

    private static BenchmarkInstanceResult BuildAbnormalResult(
        BenchmarkInstance instance,
        BenchmarkModelCandidate candidate,
        BenchmarkProblemCategory category,
        BenchmarkInstanceOutcome outcome,
        long durationMs,
        InMemoryConversationCapture capture,
        AgentRunResult runResult,
        ToolCallTelemetryBreakdown telemetry,
        string errorMessage)
    {
        long totalTokens = runResult.Metrics.TotalTokens > 0
            ? runResult.Metrics.TotalTokens
            : (runResult.Metrics.PromptTokens + runResult.Metrics.CompletionTokens);

        return new BenchmarkInstanceResult
        {
            InstanceId = instance.InstanceId,
            CandidateId = candidate.CandidateId,
            ModelName = candidate.ModelName,
            Category = category,
            Language = instance.Language,
            Outcome = outcome,
            DurationMs = Math.Max(1, durationMs),
            Turns = ResolveTurnCount(capture),
            PromptTokens = runResult.Metrics.PromptTokens,
            CompletionTokens = runResult.Metrics.CompletionTokens,
            TotalTokens = totalTokens,
            TranscriptPath = capture.SavedTranscriptPath,
            Telemetry = telemetry,
            ErrorMessage = errorMessage
        };
    }

    private sealed class CandidateInstanceKeyComparer : IEqualityComparer<(string CandidateId, string InstanceId)>
    {
        public static readonly CandidateInstanceKeyComparer Instance = new();

        public bool Equals((string CandidateId, string InstanceId) x, (string CandidateId, string InstanceId) y) =>
            string.Equals(x.CandidateId, y.CandidateId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.InstanceId, y.InstanceId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string CandidateId, string InstanceId) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CandidateId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.InstanceId));
    }

    internal static ToolCallTelemetryBreakdown BuildTelemetryBreakdown(
        IReadOnlyList<ConversationTurn>? turns,
        LenientDispatchTelemetrySnapshot dispatchSnapshot)
    {
        int totalToolCalls = 0;
        int readCalls = 0;
        int editCalls = 0;
        int writeCalls = 0;
        int bashCalls = 0;
        int loadSkillCalls = 0;
        int askCalls = 0;
        int requestChangesCalls = 0;
        int editFailures = 0;
        int csNavBashCalls = 0;
        int codeCondenserBashCalls = 0;
        int auditedBashCount = 0;
        var auditedBashCallIds = new HashSet<string>(StringComparer.Ordinal);

        if (turns is { Count: > 0 })
        {
            foreach (var turn in turns)
            {
                if (turn.Role == StageTurnRole.Tool)
                {
                    totalToolCalls++;
                    switch (turn.ToolName?.Trim().ToLowerInvariant())
                    {
                        case CanonicalToolNames.Read:
                            readCalls++;
                            break;
                        case CanonicalToolNames.Edit:
                            editCalls++;
                            break;
                        case CanonicalToolNames.Write:
                            writeCalls++;
                            break;
                        case CanonicalToolNames.Bash:
                            bashCalls++;
                            break;
                        case CanonicalToolNames.LoadSkill:
                            loadSkillCalls++;
                            break;
                        case CanonicalToolNames.Ask:
                            askCalls++;
                            break;
                        case CanonicalToolNames.RequestChanges:
                            requestChangesCalls++;
                            break;
                    }
                }
                else if (turn.Role == StageTurnRole.ToolResult)
                {
                    if (string.Equals(turn.ToolName, CanonicalToolNames.Edit, StringComparison.OrdinalIgnoreCase) && turn.IsError)
                    {
                        editFailures++;
                    }
                    else if (string.Equals(turn.ToolName, CanonicalToolNames.Bash, StringComparison.OrdinalIgnoreCase))
                    {
                        var (callId, auditedCommand) = ExtractBashAuditMetadata(turn.MetadataJson);
                        if (!string.IsNullOrWhiteSpace(auditedCommand))
                        {
                            if (CsNavInvocationRegex().IsMatch(auditedCommand))
                            {
                                csNavBashCalls++;
                            }
                            if (CodeCondenserInvocationRegex().IsMatch(auditedCommand))
                            {
                                codeCondenserBashCalls++;
                            }
                            if (!string.IsNullOrWhiteSpace(callId))
                            {
                                auditedBashCallIds.Add(callId);
                            }
                            auditedBashCount++;
                        }
                    }
                }
            }

            foreach (var turn in turns)
            {
                if (turn.Role != StageTurnRole.Tool ||
                    !string.Equals(turn.ToolName, CanonicalToolNames.Bash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var callId = CrucibleConversationCoordinator.ExtractCallId(turn);
                if ((!string.IsNullOrEmpty(callId) && auditedBashCallIds.Contains(callId)) ||
                    (string.IsNullOrEmpty(callId) && auditedBashCount > 0))
                {
                    continue;
                }

                var command = ExtractBashCommandFromArguments(turn.ToolArguments);
                if (string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                if (CsNavInvocationRegex().IsMatch(command))
                {
                    csNavBashCalls++;
                }
                if (CodeCondenserInvocationRegex().IsMatch(command))
                {
                    codeCondenserBashCalls++;
                }
            }
        }

        return new ToolCallTelemetryBreakdown
        {
            TotalToolCalls = totalToolCalls,
            ReadCalls = readCalls,
            EditCalls = editCalls,
            WriteCalls = writeCalls,
            BashCalls = bashCalls,
            LoadSkillCalls = loadSkillCalls,
            AskCalls = askCalls,
            RequestChangesCalls = requestChangesCalls,
            EditFailures = editFailures,
            DispatcherRepairs = dispatchSnapshot.TotalHits,
            ToolAliasesResolved = dispatchSnapshot.ToolAliasesResolved,
            ParameterAliasesResolved = dispatchSnapshot.ParameterAliasesResolved,
            StringifiedJsonRepaired = dispatchSnapshot.StringifiedJsonRepaired,
            CsNavBashCalls = csNavBashCalls,
            CodeCondenserBashCalls = codeCondenserBashCalls
        };
    }

    private static (string? CallId, string? BashAuditCommand) ExtractBashAuditMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var callId = TryGetStringPropertyIgnoreCase(doc.RootElement, "CallId");
            string? bashCommand = null;

            if (TryGetPropertyIgnoreCase(doc.RootElement, "BashAudit", out var auditEl) &&
                auditEl.ValueKind == JsonValueKind.Object)
            {
                bashCommand = TryGetStringPropertyIgnoreCase(auditEl, "Command");
            }

            return (callId, bashCommand);
        }
        catch (JsonException)
        {
            // Justification: turn metadata is best-effort diagnostics; malformed payloads fall back to ToolArguments.
            return (null, null);
        }
    }

    private static string? ExtractBashCommandFromArguments(string? toolArguments)
    {
        if (string.IsNullOrWhiteSpace(toolArguments))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolArguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return TryGetStringPropertyIgnoreCase(doc.RootElement, "command");
        }
        catch (JsonException)
        {
            // Justification: malformed tool argument JSON yields no bash command match.
            return null;
        }
    }

    private static string? TryGetStringPropertyIgnoreCase(JsonElement obj, string propertyName) =>
        TryGetPropertyIgnoreCase(obj, propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string propertyName, out JsonElement value)
    {
        if (obj.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
