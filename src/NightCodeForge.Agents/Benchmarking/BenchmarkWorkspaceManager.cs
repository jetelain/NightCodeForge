using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Benchmarking;

/// <summary>
/// Options configuring local repository cache and ephemeral workspace locations for benchmarks.
/// </summary>
public sealed class BenchmarkWorkspaceOptions
{
    public const string SectionName = "BenchmarkWorkspace";

    public string RootDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nightcodeforge",
        BenchmarkPaths.DefaultRootSubdir);
}

/// <summary>
/// Provisions leak-free ephemeral git workspaces from a cached canonical clone, materializes category branches/files,
/// and handles clean patch extraction/application without exposing post-<c>BaseCommit</c> git history.
/// </summary>
public sealed class BenchmarkWorkspaceManager : IBenchmarkWorkspaceManager
{
    private const int AddIntentBatchSize = 50;
    private const int MaxWorkspaceIdPrefixLength = 48;
    private const int WorkspaceUniqueSuffixLength = 12;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IGitCliRunner _gitCliRunner;
    private readonly string _rootDirectory;
    private readonly ILogger<BenchmarkWorkspaceManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoCacheLocks = new(StringComparer.OrdinalIgnoreCase);

    public BenchmarkWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IOptions<BenchmarkWorkspaceOptions> options,
        ILogger<BenchmarkWorkspaceManager> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(opts.RootDirectory))
        {
            throw new BenchmarkWorkspaceException("BenchmarkWorkspaceOptions.RootDirectory cannot be empty.");
        }

        _rootDirectory = Path.GetFullPath(opts.RootDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string RootDirectory => _rootDirectory;
    public string ReposCacheDirectory => Path.Combine(_rootDirectory, BenchmarkPaths.ReposCacheSubdir);
    public string WorkspacesDirectory => Path.Combine(_rootDirectory, BenchmarkPaths.WorkspacesSubdir);

    public async Task<string> EnsureRepositoryCachedAsync(
        string repo,
        string baseCommit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            throw new BenchmarkWorkspaceException("Repository identifier cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(baseCommit))
        {
            throw new BenchmarkWorkspaceException("BaseCommit cannot be empty.");
        }

        var cacheFolderName = BuildCacheFolderName(repo);
        var cachedRepoPath = Path.Combine(ReposCacheDirectory, cacheFolderName);
        var repoLock = _repoCacheLocks.GetOrAdd(cachedRepoPath, _ => new SemaphoreSlim(1, 1));

        await repoLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ReposCacheDirectory);
            var sourceUrl = ResolveCloneSource(repo);

            if (!IsBareOrGitDirectory(cachedRepoPath))
            {
                DeleteDirectoryRobustly(cachedRepoPath);
                _logger.LogInformation("Caching benchmark repository '{Repo}' into '{CachedPath}'.", repo, cachedRepoPath);
                try
                {
                    await _gitCliRunner.ExecuteAsync(
                        ReposCacheDirectory,
                        ["clone", "--bare", sourceUrl, cachedRepoPath],
                        timeoutSeconds: 300,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DeleteDirectoryRobustly(cachedRepoPath);
                    throw new BenchmarkWorkspaceException(
                        $"Failed to clone benchmark repository '{repo}' from '{sourceUrl}': {ex.Message}",
                        ex,
                        workspacePath: cachedRepoPath);
                }
            }

            if (!await CommitExistsAsync(cachedRepoPath, baseCommit, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Commit '{BaseCommit}' not yet in cache '{CachedPath}'; fetching from origin.", baseCommit, cachedRepoPath);
                try
                {
                    await _gitCliRunner.ExecuteAsync(
                        cachedRepoPath,
                        ["fetch", "origin", "+refs/heads/*:refs/heads/*", "+refs/tags/*:refs/tags/*", "--prune"],
                        timeoutSeconds: 300,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Wildcard fetch from origin failed for '{CachedPath}', attempting direct commit fetch.", cachedRepoPath);
                }

                if (!await CommitExistsAsync(cachedRepoPath, baseCommit, cancellationToken).ConfigureAwait(false))
                {
                    try
                    {
                        await _gitCliRunner.ExecuteAsync(
                            cachedRepoPath,
                            ["fetch", "origin", baseCommit],
                            timeoutSeconds: 180,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new BenchmarkWorkspaceException(
                            $"BaseCommit '{baseCommit}' does not exist in repository '{repo}' and could not be fetched: {ex.Message}",
                            ex,
                            workspacePath: cachedRepoPath);
                    }
                }

                if (!await CommitExistsAsync(cachedRepoPath, baseCommit, cancellationToken).ConfigureAwait(false))
                {
                    throw new BenchmarkWorkspaceException(
                        $"BaseCommit '{baseCommit}' was not found in repository '{repo}' (cache '{cachedRepoPath}').",
                        workspacePath: cachedRepoPath);
                }
            }

            return cachedRepoPath;
        }
        finally
        {
            repoLock.Release();
        }
    }

    public async Task<BenchmarkWorkspaceHandle> ProvisionAsync(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        string? instanceWorkspacePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var cachedRepoPath = await EnsureRepositoryCachedAsync(instance.Repo, instance.BaseCommit, cancellationToken).ConfigureAwait(false);

        var workspacePath = !string.IsNullOrWhiteSpace(instanceWorkspacePath)
            ? Path.GetFullPath(instanceWorkspacePath)
            : BuildUniqueWorkspacePath(instance.InstanceId);

        DeleteDirectoryRobustly(workspacePath);
        Directory.CreateDirectory(workspacePath);

        try
        {
            var resolvedBaseSha = (await _gitCliRunner.ExecuteAsync(
                cachedRepoPath,
                ["rev-parse", "--verify", $"{instance.BaseCommit}^{{commit}}"],
                timeoutSeconds: 15,
                cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

            await _gitCliRunner.ExecuteAsync(workspacePath, ["init"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "core.autocrlf", "false"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "core.quotepath", "false"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "commit.gpgsign", "false"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "user.email", BenchmarkPaths.GitEmail], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "user.name", BenchmarkPaths.GitUserName], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["fetch", cachedRepoPath, resolvedBaseSha], timeoutSeconds: 180, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["checkout", "--detach", "FETCH_HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);

            var remotes = await _gitCliRunner.ExecuteAsync(workspacePath, ["remote"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (remotes.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(r => string.Equals(r, "origin", StringComparison.OrdinalIgnoreCase)))
            {
                await _gitCliRunner.ExecuteAsync(workspacePath, ["remote", "remove", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var resolvedHead = (await _gitCliRunner.ExecuteAsync(workspacePath, ["rev-parse", "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false))
                .StandardOutput.Trim();

            return category switch
            {
                BenchmarkProblemCategory.Implementation => new BenchmarkWorkspaceHandle
                {
                    InstanceId = instance.InstanceId,
                    Category = category,
                    WorkspacePath = workspacePath,
                    BaseCommit = resolvedHead,
                    BaseBranch = resolvedHead
                },
                BenchmarkProblemCategory.CodeReview => await MaterializeCodeReviewAsync(instance, workspacePath, resolvedHead, cancellationToken).ConfigureAwait(false),
                BenchmarkProblemCategory.TaskReview => await MaterializeTaskReviewAsync(instance, workspacePath, resolvedHead, cancellationToken).ConfigureAwait(false),
                _ => throw new BenchmarkWorkspaceException($"Unsupported category '{category}' during workspace provisioning.", instance.InstanceId, workspacePath)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BenchmarkWorkspaceException)
        {
            DeleteDirectoryRobustly(workspacePath);
            throw new BenchmarkWorkspaceException(
                $"Failed to provision leak-free benchmark workspace for '{instance.InstanceId}': {ex.Message}",
                ex,
                instance.InstanceId,
                workspacePath);
        }
    }

    public async Task<string> ExtractPatchAsync(
        string workspacePath,
        string baseCommitOrRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new BenchmarkWorkspaceException($"Workspace path '{workspacePath}' does not exist.", workspacePath: workspacePath);
        }

        if (string.IsNullOrWhiteSpace(baseCommitOrRef))
        {
            throw new BenchmarkWorkspaceException("Base commit/ref is required to extract patch.", workspacePath: workspacePath);
        }

        try
        {
            var status = await _gitCliRunner.ExecuteAsync(
                workspacePath,
                ["status", "--porcelain", "-uall"],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var untrackedFiles = ParseUntrackedFiles(status.StandardOutput)
                .Where(path => !IsExcludedArtifactPath(path))
                .ToList();

            for (int i = 0; i < untrackedFiles.Count; i += AddIntentBatchSize)
            {
                var batch = untrackedFiles.Skip(i).Take(AddIntentBatchSize).ToList();
                var addArgs = new List<string> { "add", "-N", "--" };
                addArgs.AddRange(batch);
                await _gitCliRunner.ExecuteAsync(workspacePath, addArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var diffArgs = new List<string> { "diff", "--binary", baseCommitOrRef, "--", "." };
            diffArgs.AddRange(BenchmarkDefaults.ExcludedPatchPathSpecs);

            var diff = await _gitCliRunner.ExecuteAsync(workspacePath, diffArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
            return diff.StandardOutput ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BenchmarkWorkspaceException)
        {
            throw new BenchmarkWorkspaceException(
                $"Failed to extract patch from '{workspacePath}' against '{baseCommitOrRef}': {ex.Message}",
                ex,
                workspacePath: workspacePath);
        }
    }

    public async Task<IReadOnlyList<string>> GetModifiedFilesAsync(
        string workspacePath,
        string? baseCommitOrRef = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new BenchmarkWorkspaceException($"Workspace path '{workspacePath}' does not exist.", workspacePath: workspacePath);
        }

        var status = await _gitCliRunner.ExecuteAsync(
            workspacePath,
            ["status", "--porcelain", "-uall"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var paths = new List<string>();
        foreach (var rawLine in (status.StandardOutput ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Length < 4)
            {
                continue;
            }

            var pathPart = rawLine[3..].Trim();
            var renameIdx = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameIdx >= 0)
            {
                pathPart = pathPart[(renameIdx + 4)..].Trim();
            }

            var unquoted = UnquoteGitPath(pathPart).Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(unquoted) && !IsExcludedArtifactPath(unquoted))
            {
                paths.Add(unquoted);
            }
        }

        if (!string.IsNullOrWhiteSpace(baseCommitOrRef))
        {
            var committedDiff = await _gitCliRunner.ExecuteAsync(
                workspacePath,
                ["diff", "--name-only", baseCommitOrRef],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var line in (committedDiff.StandardOutput ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = UnquoteGitPath(line).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(normalized) && !IsExcludedArtifactPath(normalized))
                {
                    paths.Add(normalized);
                }
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task ResetToBaseAsync(
        string workspacePath,
        string baseCommitOrRef,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new BenchmarkWorkspaceException($"Workspace path '{workspacePath}' does not exist.", workspacePath: workspacePath);
        }

        try
        {
            await _gitCliRunner.ExecuteAsync(workspacePath, ["reset", "--hard", baseCommitOrRef], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["clean", "-fd"], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BenchmarkWorkspaceException(
                $"Failed to reset workspace '{workspacePath}' to '{baseCommitOrRef}': {ex.Message}",
                ex,
                workspacePath: workspacePath);
        }
    }

    public async Task ApplyPatchAsync(
        string workspacePath,
        string patchContent,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(patchContent))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new BenchmarkWorkspaceException($"Workspace path '{workspacePath}' does not exist.", workspacePath: workspacePath);
        }

        var normalizedPatch = patchContent.Replace("\r\n", "\n");
        if (!normalizedPatch.EndsWith('\n'))
        {
            normalizedPatch += "\n";
        }

        var tempPatchFile = Path.Combine(Path.GetTempPath(), $"ncf-bench-patch-{Guid.NewGuid():N}.diff");
        try
        {
            await File.WriteAllTextAsync(tempPatchFile, normalizedPatch, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(
                workspacePath,
                ["apply", "--whitespace=nowarn", tempPatchFile],
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BenchmarkWorkspaceException(
                $"Failed to apply patch in workspace '{workspacePath}': {ex.Message}",
                ex,
                workspacePath: workspacePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPatchFile))
                {
                    File.Delete(tempPatchFile);
                }
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not delete temporary patch file '{PatchFile}'.", tempPatchFile);
            }
        }
    }

    public Task CleanupAsync(BenchmarkWorkspaceHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();
        DeleteDirectoryRobustly(handle.WorkspacePath);
        return Task.CompletedTask;
    }

    private async Task<BenchmarkWorkspaceHandle> MaterializeCodeReviewAsync(
        BenchmarkInstance instance,
        string workspacePath,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.CandidatePatch))
        {
            throw new BenchmarkWorkspaceException(
                $"CodeReview instance '{instance.InstanceId}' is missing CandidatePatch.",
                instance.InstanceId,
                workspacePath);
        }

        await _gitCliRunner.ExecuteAsync(workspacePath, ["branch", "-f", BenchmarkBranchNames.BaseBranch, "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);
        await _gitCliRunner.ExecuteAsync(workspacePath, ["checkout", "-B", BenchmarkBranchNames.CandidateBranch], cancellationToken: cancellationToken).ConfigureAwait(false);

        await ApplyPatchAsync(workspacePath, instance.CandidatePatch, cancellationToken).ConfigureAwait(false);
        await _gitCliRunner.ExecuteAsync(workspacePath, ["add", "-A"], cancellationToken: cancellationToken).ConfigureAwait(false);
        await _gitCliRunner.ExecuteAsync(workspacePath, ["commit", "--no-gpg-sign", "-m", "Candidate patch for benchmark code review"], cancellationToken: cancellationToken).ConfigureAwait(false);

        var candidateCommit = (await _gitCliRunner.ExecuteAsync(
            workspacePath,
            ["rev-parse", "HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();

        var diffResult = await _gitCliRunner.ExecuteAsync(
            workspacePath,
            ["diff", $"{BenchmarkBranchNames.BaseBranch}...HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var statResult = await _gitCliRunner.ExecuteAsync(
            workspacePath,
            ["diff", "--stat", $"{BenchmarkBranchNames.BaseBranch}...HEAD"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new BenchmarkWorkspaceHandle
        {
            InstanceId = instance.InstanceId,
            Category = BenchmarkProblemCategory.CodeReview,
            WorkspacePath = workspacePath,
            BaseCommit = baseCommit,
            BaseBranch = BenchmarkBranchNames.BaseBranch,
            FeatureBranch = BenchmarkBranchNames.CandidateBranch,
            CandidateCommit = candidateCommit,
            ReviewDiff = diffResult.StandardOutput,
            ReviewDiffStat = statResult.StandardOutput
        };
    }

    private async Task<BenchmarkWorkspaceHandle> MaterializeTaskReviewAsync(
        BenchmarkInstance instance,
        string workspacePath,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.TaskSpecification))
        {
            throw new BenchmarkWorkspaceException(
                $"TaskReview instance '{instance.InstanceId}' is missing TaskSpecification.",
                instance.InstanceId,
                workspacePath);
        }

        await _gitCliRunner.ExecuteAsync(workspacePath, ["checkout", "-B", BenchmarkBranchNames.TaskReviewBranch], cancellationToken: cancellationToken).ConfigureAwait(false);

        var relativeTaskPath = $"{BenchmarkPaths.DocsTasksPrefix}{SanitizePathSegment(instance.InstanceId)}.md";
        var fullTaskPath = Path.Combine(workspacePath, relativeTaskPath.Replace('/', Path.DirectorySeparatorChar));
        var taskDir = Path.GetDirectoryName(fullTaskPath);
        if (!string.IsNullOrEmpty(taskDir))
        {
            Directory.CreateDirectory(taskDir);
        }

        var content = EnsureTaskFrontmatter(instance.TaskSpecification, instance);
        await File.WriteAllTextAsync(fullTaskPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        return new BenchmarkWorkspaceHandle
        {
            InstanceId = instance.InstanceId,
            Category = BenchmarkProblemCategory.TaskReview,
            WorkspacePath = workspacePath,
            BaseCommit = baseCommit,
            BaseBranch = baseCommit,
            FeatureBranch = BenchmarkBranchNames.TaskReviewBranch,
            TaskMarkdownPath = relativeTaskPath,
            TaskMarkdownInitialContent = content
        };
    }

    internal static string EnsureTaskFrontmatter(string specification, BenchmarkInstance instance)
    {
        var normalized = specification.TrimStart('\uFEFF').Replace("\r\n", "\n");
        var trimmedStart = normalized.TrimStart();
        if (trimmedStart.StartsWith("---\n", StringComparison.Ordinal) ||
            string.Equals(trimmedStart, "---", StringComparison.Ordinal))
        {
            return trimmedStart;
        }

        var rawTitle = instance.ProblemStatement.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? instance.InstanceId;
        var cleanTitle = new string(rawTitle.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (string.IsNullOrEmpty(cleanTitle))
        {
            cleanTitle = instance.InstanceId;
        }
        if (cleanTitle.Length > 120)
        {
            cleanTitle = cleanTitle[..120];
        }

        var safeTitle = cleanTitle
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("id: 1");
        sb.AppendLine($"title: \"{safeTitle}\"");
        sb.AppendLine("status: Open");
        sb.AppendLine($"difficulty: {BenchmarkDefaults.DefaultTaskDifficulty}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(normalized);
        return sb.ToString();
    }

    private string BuildUniqueWorkspacePath(string instanceId)
    {
        var sanitizedId = SanitizePathSegment(instanceId);
        var idPrefix = sanitizedId.Length > MaxWorkspaceIdPrefixLength
            ? sanitizedId[..MaxWorkspaceIdPrefixLength]
            : sanitizedId;
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..WorkspaceUniqueSuffixLength];
        return Path.Combine(WorkspacesDirectory, $"{idPrefix}-{uniqueSuffix}");
    }

    private async Task<bool> CommitExistsAsync(string repoPath, string commit, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(
                repoPath,
                ["cat-file", "-e", $"{commit}^{{commit}}"],
                timeoutSeconds: 15,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (GitCommandException)
        {
            return false;
        }
    }

    private static bool IsBareOrGitDirectory(string path) =>
        Directory.Exists(Path.Combine(path, "objects")) &&
        File.Exists(Path.Combine(path, "HEAD"));

    private static string ResolveCloneSource(string repo)
    {
        var trimmed = repo.Trim();
        if (Directory.Exists(trimmed))
        {
            return Path.GetFullPath(trimmed);
        }

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"https://github.com/{trimmed.Trim('/')}.git";
    }

    public static string BuildCacheFolderName(string repo)
    {
        var trimmed = repo.Trim().TrimEnd('/', '\\');
        var segments = trimmed.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 &&
            !trimmed.Contains(':') &&
            !trimmed.Contains('@'))
        {
            return $"{SanitizePathSegment(segments[0])}__{SanitizePathSegment(segments[1])}";
        }

        var tail = segments.Length >= 2
            ? $"{SanitizePathSegment(segments[^2])}__{SanitizePathSegment(segments[^1])}"
            : SanitizePathSegment(trimmed);
        if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail[..^4];
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8];
        return $"{tail}-{hash}";
    }

    public static string SanitizePathSegment(string input)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            sb.Append(invalidChars.Contains(ch) || ch is '/' or '\\' or ':' or ' ' ? '_' : ch);
        }
        return sb.Length > 0 ? sb.ToString() : "item";
    }

    private static IReadOnlyList<string> ParseUntrackedFiles(string porcelainOutput)
    {
        var list = new List<string>();
        foreach (var line in (porcelainOutput ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("?? ", StringComparison.Ordinal) && line.Length > 3)
            {
                var path = UnquoteGitPath(line[3..].Trim()).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(path))
                {
                    list.Add(path);
                }
            }
        }
        return list;
    }

    private static string UnquoteGitPath(string path)
    {
        if (path.Length >= 2 && path.StartsWith('"') && path.EndsWith('"'))
        {
            return path[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return path;
    }

    internal static bool IsExcludedArtifactPath(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        if (norm.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parts = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "TestResults", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, BenchmarkPaths.BenchmarkResultDirRelative, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return norm.StartsWith("docs/transcripts/", StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteDirectoryRobustly(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete directory '{Path}'.", path);
        }
    }
}
