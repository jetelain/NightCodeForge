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
    private readonly ILogger<ConflictResolutionVerifier> _logger;

    public ConflictResolutionVerifier(
        IGitCliRunner gitCliRunner,
        IGitWorkspaceManager gitWorkspaceManager,
        ILogger<ConflictResolutionVerifier> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _gitWorkspaceManager = gitWorkspaceManager ?? throw new ArgumentNullException(nameof(gitWorkspaceManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ConflictResolutionVerificationResult> VerifyResolutionAsync(
        Workspace workspace,
        int taskId,
        string baseBranch,
        IReadOnlyList<string> conflictedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentNullException.ThrowIfNull(conflictedFiles);

        var worktreePath = _gitWorkspaceManager.GetWorktreePath(workspace, taskId);

        _logger.LogInformation("Verifying conflict resolution for ForgeTask #{TaskId} in '{WorktreePath}' ({Count} files)",
            taskId, worktreePath, conflictedFiles.Count);

        // Step 1: Marker scan on every conflicted file
        var markerErrors = new List<string>();
        var filesWithMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relPath in conflictedFiles)
        {
            var fullPath = Path.Combine(worktreePath, relPath);
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
        var undecidedNonTextual = await GetUndecidedNonTextualConflictsAsync(worktreePath, cancellationToken);

        foreach (var relPath in conflictedFiles)
        {
            if (undecidedNonTextual.Contains(relPath))
            {
                continue;
            }

            var fullPath = Path.Combine(worktreePath, relPath);
            if (File.Exists(fullPath))
            {
                await _gitCliRunner.ExecuteAsync(worktreePath, ["add", "--", relPath], cancellationToken: cancellationToken);
            }
            else
            {
                await _gitCliRunner.ExecuteAsync(worktreePath, ["rm", "--ignore-unmatch", "--", relPath], cancellationToken: cancellationToken);
            }
        }

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

        // Step 4: If MERGE_HEAD is present, create merge commit
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

    private async Task<HashSet<string>> GetUndecidedNonTextualConflictsAsync(string worktreePath, CancellationToken cancellationToken)
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
            var fullPath = Path.Combine(worktreePath, path);
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
