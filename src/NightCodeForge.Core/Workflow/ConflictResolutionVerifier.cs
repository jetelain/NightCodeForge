using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Deterministic verifier for autonomous merge conflict resolution.
/// Verifies that all conflict markers are eliminated, stages the resolved files selectively,
/// verifies that no unmerged index entries remain, and commits the merge if MERGE_HEAD is present.
/// </summary>
public class ConflictResolutionVerifier : IConflictResolutionVerifier
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IGitWorkspaceManager _gitWorkspaceManager;
    private readonly IExecutionEnvironmentProvider _environmentProvider;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<ConflictResolutionVerifier> _logger;

    public ConflictResolutionVerifier(
        IGitCliRunner gitCliRunner,
        IGitWorkspaceManager gitWorkspaceManager,
        IExecutionEnvironmentProvider environmentProvider,
        IWorkspacePathResolver pathResolver,
        ILogger<ConflictResolutionVerifier> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _environmentProvider = environmentProvider ?? throw new ArgumentNullException(nameof(environmentProvider));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ConflictResolutionVerificationResult> VerifyResolutionAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        IReadOnlyList<string> conflictedFiles,
        PipelineDataBag? dataBag = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentNullException.ThrowIfNull(conflictedFiles);

        var worktreePath = _gitWorkspaceManager.GetWorktreePath(workspace, taskId);
        var hostWorktreePath = _pathResolver.ToHostPath(worktreePath);
        if (string.IsNullOrWhiteSpace(hostWorktreePath))
        {
            hostWorktreePath = worktreePath;
        }

        _logger.LogInformation("Verifying conflict resolution for ForgeTask #{TaskId} in '{WorktreePath}' (host: '{HostWorktreePath}', {Count} files)",
            taskId, worktreePath, hostWorktreePath, conflictedFiles.Count);

        // Step 1: Marker scan on every conflicted file
        var markerErrors = new List<string>();
        var filesWithMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relPath in conflictedFiles)
        {
            var fullPath = Path.Combine(hostWorktreePath, relPath);
            if (!File.Exists(fullPath))
            {
                // File deleted in conflict resolution (e.g. delete/modify conflict resolved by deletion)
                _logger.LogDebug("File '{RelPath}' does not exist on disk; skipping marker scan", relPath);
                continue;
            }

            if (await IsBinaryFileAsync(fullPath, cancellationToken))
            {
                _logger.LogDebug("File '{RelPath}' is binary; skipping text marker scan", relPath);
                continue;
            }

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
            var insideConflictBlock = false;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];

                if (line.StartsWith(ConflictResolutionConstants.MarkerStart, StringComparison.Ordinal))
                {
                    insideConflictBlock = true;
                    markerErrors.Add($"[{relPath}:{lineIdx + 1}] Found conflict start marker: '{line.Trim()}'");
                    filesWithMarkers.Add(relPath);
                }
                else if (line.StartsWith(ConflictResolutionConstants.MarkerAncestor, StringComparison.Ordinal))
                {
                    markerErrors.Add($"[{relPath}:{lineIdx + 1}] Found diff3 ancestor marker: '{line.Trim()}'");
                    filesWithMarkers.Add(relPath);
                }
                else if (line.StartsWith(ConflictResolutionConstants.MarkerEnd, StringComparison.Ordinal))
                {
                    insideConflictBlock = false;
                    markerErrors.Add($"[{relPath}:{lineIdx + 1}] Found conflict end marker: '{line.Trim()}'");
                    filesWithMarkers.Add(relPath);
                }
                else if (insideConflictBlock && string.Equals(line.Trim(), ConflictResolutionConstants.MarkerSeparator, StringComparison.Ordinal))
                {
                    markerErrors.Add($"[{relPath}:{lineIdx + 1}] Found conflict separator marker inside open block");
                    filesWithMarkers.Add(relPath);
                }
            }

            if (insideConflictBlock)
            {
                markerErrors.Add($"[{relPath}] Unclosed conflict marker block at end of file");
                filesWithMarkers.Add(relPath);
            }
        }

        if (markerErrors.Count > 0)
        {
            var msg = $"Conflict markers remain in resolved files:\n{string.Join("\n", markerErrors)}";
            _logger.LogWarning("Conflict resolution verification failed for task #{TaskId}: {Message}", taskId, msg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: msg,
                RemainingConflictedFiles: filesWithMarkers.ToList());
        }

        // Step 2: Selective staging of resolved files (no blind git add -A)
        // Non-textual conflicts (delete/modify conflicts and binary conflicts) must be decided
        // explicitly by the agent (via git rm or git add); the verifier must not auto-stage them.
        var undecidedNonTextual = await GetUndecidedNonTextualConflictsAsync(worktreePath, hostWorktreePath, cancellationToken);

        foreach (var relPath in conflictedFiles)
        {
            if (undecidedNonTextual.Contains(relPath))
            {
                continue;
            }

            var fullPath = Path.Combine(hostWorktreePath, relPath);
            if (File.Exists(fullPath))
            {
                await _gitCliRunner.ExecuteAsync(worktreePath, ["add", "--", relPath], cancellationToken: cancellationToken);
            }
            else
            {
                await _gitCliRunner.ExecuteAsync(worktreePath, ["rm", "--ignore-unmatch", "--", relPath], cancellationToken: cancellationToken);
            }
        }

        // Stage newly created source files BEFORE executing build command,
        // so build outputs (bin/, obj/) generated during the build are not accidentally staged.
        await StageNewSourceFilesAsync(worktreePath, cancellationToken);

        // Step 3: Verify git ls-files -u is empty
        var lsResult = await _gitCliRunner.ExecuteAsync(worktreePath, ["ls-files", "-u"], cancellationToken: cancellationToken);
        var unmergedOutput = lsResult.StandardOutput.Trim();
        if (!string.IsNullOrWhiteSpace(unmergedOutput))
        {
            var unmergedFiles = unmergedOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line =>
                {
                    var tabIdx = line.IndexOf('\t');
                    return tabIdx >= 0 ? line[(tabIdx + 1)..].Trim() : line;
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var msg = $"Unmerged index entries remain after staging: {string.Join(", ", unmergedFiles)}";
            _logger.LogWarning("Conflict resolution verification failed for task #{TaskId}: {Message}", taskId, msg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: msg,
                RemainingConflictedFiles: unmergedFiles);
        }

        // Step 4: Deterministic build verification before merge commit
        var config = CodeStackPresets.ResolveConfig(workspace.RepositoryPath, dataBag, _logger);
        var buildCommand = config.BuildCommand;

        if (string.IsNullOrWhiteSpace(buildCommand))
        {
            var noBuildMsg = "Deterministic build verification failed: no build command configured or detected for workspace.";
            _logger.LogWarning("Conflict resolution verification failed for task #{TaskId}: {Message}", taskId, noBuildMsg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: noBuildMsg);
        }

        var env = _environmentProvider.GetEnvironment(workspace, SandboxSecurityTier.Standard, taskId)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var timeoutSeconds = workspace.BuildTimeoutSeconds ?? config.BuildTimeoutSeconds ?? WorkflowDefaults.DefaultBuildTimeoutSeconds;

        _logger.LogInformation("Executing deterministic build command '{BuildCommand}' for task #{TaskId} (timeout: {Timeout}s)",
            buildCommand, taskId, timeoutSeconds);

        CommandResult buildResult;
        try
        {
            buildResult = await env.ExecuteAsync(buildCommand, worktreePath, timeoutSeconds: timeoutSeconds, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            var timeoutMsg = $"Deterministic build timed out after {timeoutSeconds} seconds";
            _logger.LogWarning("Conflict resolution verification build timed out for task #{TaskId}: {Message}", taskId, timeoutMsg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: timeoutMsg,
                CompilerErrors: timeoutMsg);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var timeoutMsg = $"Deterministic build timed out after {timeoutSeconds} seconds";
            _logger.LogWarning("Conflict resolution verification build timed out for task #{TaskId}: {Message}", taskId, timeoutMsg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: timeoutMsg,
                CompilerErrors: timeoutMsg);
        }

        bool isTimeout = buildResult.ExitCode == WorkflowDefaults.ProcessTimeoutExitCode;

        if (isTimeout)
        {
            var timeoutMsg = $"Deterministic build timed out after {timeoutSeconds} seconds";
            _logger.LogWarning("Conflict resolution verification build timed out for task #{TaskId}: {Message}", taskId, timeoutMsg);
            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: timeoutMsg,
                CompilerErrors: timeoutMsg);
        }

        if (!buildResult.Success)
        {
            var fullOutput = string.IsNullOrWhiteSpace(buildResult.StandardError)
                ? buildResult.StandardOutput
                : $"{buildResult.StandardOutput}\n{buildResult.StandardError}";

            var truncatedErrors = TruncatePreservingTail(fullOutput, ConflictResolutionConstants.MaxCompilerErrorsLength);
            var errorMsg = $"Deterministic build failed after conflict resolution (exit code {buildResult.ExitCode})";
            _logger.LogWarning("Conflict resolution build verification failed for task #{TaskId}: {Message}", taskId, errorMsg);

            return new ConflictResolutionVerificationResult(
                IsSuccess: false,
                ErrorMessage: errorMsg,
                CompilerErrors: truncatedErrors);
        }

        // Step 5: If MERGE_HEAD is present, create merge commit
        var isMergeInProgress = await CheckMergeHeadExistsAsync(worktreePath, cancellationToken);
        if (isMergeInProgress)
        {
            var commitMsg = string.Format(ConflictResolutionConstants.MergeCommitMessageFormat, baseBranch);
            _logger.LogInformation("Creating merge commit for task #{TaskId} with message: '{CommitMsg}'", taskId, commitMsg);
            await _gitCliRunner.ExecuteAsync(
                worktreePath,
                ["commit", "--no-edit", "-m", commitMsg],
                cancellationToken: cancellationToken);
        }

        _logger.LogInformation("Conflict resolution verification passed for task #{TaskId}", taskId);
        return new ConflictResolutionVerificationResult(IsSuccess: true);
    }

    private async Task StageNewSourceFilesAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var statusResult = await _gitCliRunner.ExecuteAsync(worktreePath, ["status", "--porcelain", "-z"], cancellationToken: cancellationToken);
        var output = statusResult?.StandardOutput ?? string.Empty;
        var lines = output.Split(new[] { '\0', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("?? ", StringComparison.Ordinal))
            {
                var filePath = line[3..].Trim().Trim('"');
                if (IsExcludedPath(filePath))
                {
                    continue;
                }

                await _gitCliRunner.ExecuteAsync(worktreePath, ["add", "--", filePath], cancellationToken: cancellationToken);
            }
        }
    }

    private static bool IsExcludedPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("node_modules/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/node_modules", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncatePreservingTail(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        const string truncationPrefix = "... [truncated, showing tail of compiler diagnostics] ...\n";
        var availableLength = maxLength - truncationPrefix.Length;
        if (availableLength <= 0)
        {
            return text[^maxLength..];
        }

        return truncationPrefix + text[^availableLength..];
    }

    private async Task<HashSet<string>> GetUndecidedNonTextualConflictsAsync(string worktreePath, string hostWorktreePath, CancellationToken cancellationToken)
    {
        var ls = await _gitCliRunner.ExecuteAsync(worktreePath, ["ls-files", "-u"], cancellationToken: cancellationToken);
        var stagesByPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in (ls.StandardOutput ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tabIdx = line.IndexOf('\t');
            if (tabIdx < 0)
            {
                continue;
            }

            var meta = line[..tabIdx].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3 || !int.TryParse(meta[2], out var stage))
            {
                continue;
            }

            var path = line[(tabIdx + 1)..].Trim();
            if (!stagesByPath.TryGetValue(path, out var stages))
            {
                stages = new HashSet<int>();
                stagesByPath[path] = stages;
            }
            stages.Add(stage);
        }

        var undecided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, stages) in stagesByPath)
        {
            // Delete/modify conflict: ancestor present, but only one side (ours or theirs) remains
            var isDeleteModify = stages.Contains(ConflictResolutionConstants.IndexStageAncestor) &&
                                 !(stages.Contains(ConflictResolutionConstants.IndexStageOurs) && stages.Contains(ConflictResolutionConstants.IndexStageTheirs));

            if (isDeleteModify)
            {
                undecided.Add(path);
                continue;
            }

            // Binary file conflict: binary files have no text conflict markers; if still in ls-files -u, agent hasn't decided
            var fullPath = Path.Combine(hostWorktreePath, path);
            if (await IsBinaryFileAsync(fullPath, cancellationToken))
            {
                undecided.Add(path);
            }
        }

        return undecided;
    }

    private static async Task<bool> IsBinaryFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return false;
        const int bufferSize = 8000;
        var buffer = new byte[bufferSize];
        try
        {
            await using var stream = File.OpenRead(filePath);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken);
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // If file cannot be read, treat as non-binary so standard error paths handle it
        }
        return false;
    }

    private async Task<bool> CheckMergeHeadExistsAsync(string worktreePath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(worktreePath, ["rev-parse", "-q", "--verify", "MERGE_HEAD"], cancellationToken: cancellationToken);
            return result?.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (GitCommandException)
        {
            return false;
        }
    }
}
