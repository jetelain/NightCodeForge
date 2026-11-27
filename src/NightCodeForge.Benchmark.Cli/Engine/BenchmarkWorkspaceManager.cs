using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Benchmark.Cli.Engine;

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

    public string? WorkspacesDirectory { get; set; }

    public bool UseWslExt4Workspaces { get; set; }
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
    private const int WslCleanupTimeoutSeconds = 60;
    private const int MinimumSafeWslCleanupSegments = 4;
    private const string WslExecutableName = "wsl.exe";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly string _rootDirectory;
    private readonly string? _configuredWorkspacesDirectory;
    private readonly bool _useWslExt4Workspaces;
    private readonly Lazy<string> _workspacesDirectory;
    private readonly ILogger<BenchmarkWorkspaceManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoCacheLocks = new(StringComparer.OrdinalIgnoreCase);

    public BenchmarkWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IOptions<BenchmarkWorkspaceOptions> options,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        ILogger<BenchmarkWorkspaceManager> logger)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(opts.RootDirectory))
        {
            throw new BenchmarkWorkspaceException("BenchmarkWorkspaceOptions.RootDirectory cannot be empty.");
        }

        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _rootDirectory = Path.GetFullPath(opts.RootDirectory);
        _configuredWorkspacesDirectory = opts.WorkspacesDirectory;
        _useWslExt4Workspaces = opts.UseWslExt4Workspaces;
        _workspacesDirectory = new Lazy<string>(ResolveWorkspacesDirectory);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string RootDirectory => _rootDirectory;
    public string ReposCacheDirectory => Path.Combine(_rootDirectory, BenchmarkPaths.ReposCacheSubdir);
    public string WorkspacesDirectory => _workspacesDirectory.Value;

    private string ResolveWorkspacesDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_configuredWorkspacesDirectory))
        {
            var resolved = _pathResolver.ToHostPath(_configuredWorkspacesDirectory.Trim());
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new BenchmarkWorkspaceException("BenchmarkWorkspaceOptions.WorkspacesDirectory resolved to an empty path.");
            }

            return WslPathConverter.IsWslUncPath(resolved) ? resolved : Path.GetFullPath(resolved);
        }

        if (_useWslExt4Workspaces && OperatingSystem.IsWindows())
        {
            var wslUser = _pathResolver.GetWslUserName();
            if (string.IsNullOrWhiteSpace(wslUser))
            {
                throw new BenchmarkWorkspaceException("Failed to resolve WSL user name for WSL2 ext4 benchmark workspaces.");
            }

            var wslLinuxDir = $"/home/{wslUser.Trim()}/.nightcodeforge/{BenchmarkPaths.DefaultRootSubdir}/{BenchmarkPaths.WorkspacesSubdir}";
            var hostPath = _pathResolver.ToHostPath(wslLinuxDir);
            if (string.IsNullOrWhiteSpace(hostPath) || !WslPathConverter.IsWslUncPath(hostPath))
            {
                throw new BenchmarkWorkspaceException(
                    $"Failed to resolve WSL2 UNC path for benchmark workspaces directory '{wslLinuxDir}' (got '{hostPath}').");
            }

            return hostPath;
        }

        return Path.Combine(_rootDirectory, BenchmarkPaths.WorkspacesSubdir);
    }

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
            ? (WslPathConverter.IsWslUncPath(instanceWorkspacePath) ? instanceWorkspacePath : Path.GetFullPath(instanceWorkspacePath))
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
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "core.longpaths", "true"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "commit.gpgsign", "false"], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "user.email", BenchmarkPaths.GitEmail], cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["config", "user.name", BenchmarkPaths.GitUserName], cancellationToken: cancellationToken).ConfigureAwait(false);
            var isWslWorkspace = WslPathConverter.IsWslPath(workspacePath);
            var fetchSource = isWslWorkspace ? WslPathConverter.ToWslPath(cachedRepoPath) : cachedRepoPath;
            string[] fetchArgs = isWslWorkspace
                ? ["-c", "safe.directory=*", "fetch", fetchSource, resolvedBaseSha]
                : ["fetch", fetchSource, resolvedBaseSha];
            await _gitCliRunner.ExecuteAsync(workspacePath, fetchArgs, timeoutSeconds: 180, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _gitCliRunner.ExecuteAsync(workspacePath, ["checkout", "--detach", "FETCH_HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);

            var remotes = await _gitCliRunner.ExecuteAsync(workspacePath, ["remote"], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (remotes.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(r => string.Equals(r, "origin", StringComparison.OrdinalIgnoreCase)))
            {
                await _gitCliRunner.ExecuteAsync(workspacePath, ["remote", "remove", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var fetchHeadPath = Path.Combine(workspacePath, ".git", "FETCH_HEAD");
            if (File.Exists(fetchHeadPath))
            {
                try
                {
                    File.Delete(fetchHeadPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Could not remove FETCH_HEAD from '{WorkspacePath}'.", workspacePath);
                }
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
            await _gitCliRunner.ExecuteAsync(workspacePath, ["clean", "-fdx", "-e", "node_modules", "-e", ".venv"], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BenchmarkWorkspaceException(
                $"Failed to reset workspace '{workspacePath}' to '{baseCommitOrRef}': {ex.Message}",
                ex,
                workspacePath: workspacePath);
        }
    }

    public async Task ResetPathsToBaseAsync(
        string workspacePath,
        string baseCommitOrRef,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            throw new BenchmarkWorkspaceException($"Workspace path '{workspacePath}' does not exist.", workspacePath: workspacePath);
        }

        if (string.IsNullOrWhiteSpace(baseCommitOrRef))
        {
            throw new BenchmarkWorkspaceException("Base commit/ref is required to reset paths.", workspacePath: workspacePath);
        }

        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
        {
            return;
        }

        var normalizedPaths = new List<string>(relativePaths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in relativePaths)
        {
            var normalized = NormalizeSafeRelativePath(rawPath, workspacePath);
            if (seen.Add(normalized))
            {
                normalizedPaths.Add(normalized);
            }
        }

        try
        {
            var lsTreeArgs = new List<string> { "-c", "core.quotepath=false", "ls-tree", "-r", "--name-only", baseCommitOrRef, "--" };
            lsTreeArgs.AddRange(normalizedPaths);

            var lsTreeResult = await _gitCliRunner.ExecuteAsync(
                workspacePath,
                lsTreeArgs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var existingInBase = (lsTreeResult.StandardOutput ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => UnquoteGitPath(p).Replace('\\', '/').TrimStart('/'))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pathsToRestore = normalizedPaths.Where(existingInBase.Contains).ToList();
            if (pathsToRestore.Count > 0)
            {
                var checkoutArgs = new List<string> { "checkout", baseCommitOrRef, "--" };
                checkoutArgs.AddRange(pathsToRestore);
                await _gitCliRunner.ExecuteAsync(workspacePath, checkoutArgs, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            foreach (var relPath in normalizedPaths)
            {
                if (existingInBase.Contains(relPath))
                {
                    continue;
                }

                var fullPath = Path.Combine(workspacePath, relPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullPath))
                {
                    var attrs = File.GetAttributes(fullPath);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(fullPath);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not BenchmarkWorkspaceException)
        {
            throw new BenchmarkWorkspaceException(
                $"Failed to reset patch paths in workspace '{workspacePath}' against '{baseCommitOrRef}': {ex.Message}",
                ex,
                workspacePath: workspacePath);
        }
    }

    internal static IReadOnlyList<string> ExtractPatchTargetPaths(string? patchContent)
    {
        if (string.IsNullOrWhiteSpace(patchContent))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? pendingOld = null;
        string? pendingNew = null;
        bool sectionHasHeaderPath = false;

        void FlushGitFallback()
        {
            if (!sectionHasHeaderPath)
            {
                AddTargetPathIfValid(pendingOld, paths, seen);
                AddTargetPathIfValid(pendingNew, paths, seen);
            }

            pendingOld = null;
            pendingNew = null;
            sectionHasHeaderPath = false;
        }

        foreach (var rawLine in patchContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushGitFallback();
                TryParseGitDiffHeaderPaths(line, out pendingOld, out pendingNew);
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (TryExtractDiffHeaderPath(line[4..], expectedSidePrefix: "a/", out var oldPath))
                {
                    AddTargetPathIfValid(oldPath, paths, seen);
                    sectionHasHeaderPath = true;
                }
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (TryExtractDiffHeaderPath(line[4..], expectedSidePrefix: "b/", out var newPath))
                {
                    AddTargetPathIfValid(newPath, paths, seen);
                    sectionHasHeaderPath = true;
                }
            }
        }

        FlushGitFallback();
        return paths;
    }

    private static void AddTargetPathIfValid(string? candidate, List<string> target, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        var norm = candidate.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(norm) ||
            Path.IsPathRooted(norm) ||
            norm.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
        {
            return;
        }

        if (seen.Add(norm))
        {
            target.Add(norm);
        }
    }

    private static bool TryExtractDiffHeaderPath(string rawToken, string expectedSidePrefix, out string? path)
    {
        path = null;
        var trimmed = rawToken.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("/dev/null", StringComparison.Ordinal))
        {
            return false;
        }

        string candidate;
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = FindClosingQuoteIndex(trimmed);
            if (closingQuote <= 0)
            {
                return false;
            }
            candidate = UnquoteGitPath(trimmed[..(closingQuote + 1)]);
        }
        else
        {
            var tabIdx = trimmed.IndexOf('\t');
            candidate = tabIdx >= 0 ? trimmed[..tabIdx] : trimmed;
        }

        candidate = candidate.Replace('\\', '/');
        if (candidate.StartsWith(expectedSidePrefix, StringComparison.Ordinal))
        {
            candidate = candidate[expectedSidePrefix.Length..];
        }

        path = candidate;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static void TryParseGitDiffHeaderPaths(string line, out string? oldPath, out string? newPath)
    {
        oldPath = null;
        newPath = null;
        var rest = line["diff --git ".Length..].Trim();
        if (string.IsNullOrEmpty(rest))
        {
            return;
        }

        if (rest.StartsWith('"'))
        {
            var firstEnd = FindClosingQuoteIndex(rest);
            if (firstEnd <= 0)
            {
                return;
            }

            var first = UnquoteGitPath(rest[..(firstEnd + 1)]).Replace('\\', '/');
            if (first.StartsWith("a/", StringComparison.Ordinal))
            {
                oldPath = first[2..];
            }

            var secondPart = rest[(firstEnd + 1)..].TrimStart();
            if (secondPart.StartsWith('"'))
            {
                var secondEnd = FindClosingQuoteIndex(secondPart);
                if (secondEnd > 0)
                {
                    var second = UnquoteGitPath(secondPart[..(secondEnd + 1)]).Replace('\\', '/');
                    if (second.StartsWith("b/", StringComparison.Ordinal))
                    {
                        newPath = second[2..];
                    }
                }
            }
            else if (secondPart.StartsWith("b/", StringComparison.Ordinal))
            {
                newPath = secondPart[2..];
            }

            return;
        }

        var splitIdx = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (splitIdx < 0)
        {
            return;
        }

        var aToken = rest[..splitIdx].Replace('\\', '/');
        var bToken = rest[(splitIdx + 1)..].Replace('\\', '/');
        oldPath = aToken.StartsWith("a/", StringComparison.Ordinal) ? aToken[2..] : aToken;
        newPath = bToken.StartsWith("b/", StringComparison.Ordinal) ? bToken[2..] : bToken;
    }

    private static int FindClosingQuoteIndex(string text)
    {
        bool escaped = false;
        for (int i = 1; i < text.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (text[i] == '\\')
            {
                escaped = true;
                continue;
            }
            if (text[i] == '"')
            {
                return i;
            }
        }
        return -1;
    }

    private static string NormalizeSafeRelativePath(string rawPath, string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new BenchmarkWorkspaceException("Patch target path cannot be empty.", workspacePath: workspacePath);
        }

        var normalized = rawPath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
        {
            throw new BenchmarkWorkspaceException($"Unsafe relative path '{rawPath}' for workspace reset.", workspacePath: workspacePath);
        }

        return normalized;
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

        var primaryPatch = patchContent.EndsWith('\n') ? patchContent : patchContent + "\n";

        var tempPatchFile = Path.Combine(Path.GetTempPath(), $"ncf-bench-patch-{Guid.NewGuid():N}.diff");
        var gitPatchPath = WslPathConverter.IsWslPath(workspacePath)
            ? WslPathConverter.ToWslPath(tempPatchFile)
            : tempPatchFile;
        try
        {
            await File.WriteAllTextAsync(tempPatchFile, primaryPatch, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            try
            {
                await _gitCliRunner.ExecuteAsync(
                    workspacePath,
                    ["apply", "--whitespace=nowarn", gitPatchPath],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && patchContent.Contains("\r\n", StringComparison.Ordinal))
            {
                _logger.LogDebug(ex, "git apply with preserved line endings failed in '{WorkspacePath}'; retrying with LF line endings.", workspacePath);
                var lfPatch = primaryPatch.Replace("\r\n", "\n");
                await File.WriteAllTextAsync(tempPatchFile, lfPatch, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                await _gitCliRunner.ExecuteAsync(
                    workspacePath,
                    ["apply", "--whitespace=nowarn", gitPatchPath],
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
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
                string.Equals(part, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "dist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, ".venv", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "__pycache__", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, ".pytest_cache", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, BenchmarkPaths.BenchmarkResultDirRelative, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return norm.StartsWith("docs/transcripts/", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeWslCleanupTarget(string? linuxPath) =>
        IsSafeWslCleanupTarget(linuxPath, allowedWslRoot: null);

    private static bool IsSafeWslCleanupTarget(string? linuxPath, string? allowedWslRoot)
    {
        if (string.IsNullOrWhiteSpace(linuxPath))
        {
            return false;
        }

        var normalized = linuxPath.Trim();
        if (!normalized.StartsWith('/') || normalized.Contains('\0'))
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < MinimumSafeWslCleanupSegments ||
            segments.Any(seg => seg == "." || seg == ".."))
        {
            return false;
        }

        if (normalized.Contains("/.nightcodeforge/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(allowedWslRoot))
        {
            var normAllowed = allowedWslRoot.Trim().TrimEnd('/');
            if (normAllowed.StartsWith('/') &&
                normalized.StartsWith(normAllowed + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void DeleteDirectoryRobustly(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows() && WslPathConverter.TryExtractWslUnc(path, out var distro, out var linuxPath))
        {
            string? configuredWslPrefix = null;
            if (!string.IsNullOrWhiteSpace(_configuredWorkspacesDirectory) &&
                WslPathConverter.IsWslPath(_configuredWorkspacesDirectory))
            {
                configuredWslPrefix = WslPathConverter.ToWslPath(_pathResolver.ToHostPath(_configuredWorkspacesDirectory.Trim()));
            }

            if (IsSafeWslCleanupTarget(linuxPath, configuredWslPrefix))
            {
                try
                {
                    var wslArgs = new List<string>();
                    if (!string.IsNullOrWhiteSpace(distro))
                    {
                        wslArgs.AddRange(["-d", distro]);
                    }
                    wslArgs.AddRange(["--", "rm", "-rf", "--", linuxPath]);

                    var result = _processRunner.Run(new ProcessStartSpec
                    {
                        FileName = WslExecutableName,
                        ArgumentList = wslArgs,
                        TimeoutSeconds = WslCleanupTimeoutSeconds
                    });
                    if (result.ExitCode == 0 && !Directory.Exists(path))
                    {
                        return;
                    }

                    _logger.LogDebug(
                        "Native WSL rm -rf for '{Path}' exited with {ExitCode}: {StdErr}",
                        path, result.ExitCode, result.StandardError);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Native WSL rm -rf failed for '{Path}'; falling back to managed recursive delete.", path);
                }
            }
        }

        try
        {
            var pending = new Stack<string>();
            var dirsToDelete = new Stack<string>();
            pending.Push(path);

            while (pending.Count > 0)
            {
                var currentDir = pending.Pop();
                FileAttributes dirAttrs;
                try
                {
                    dirAttrs = File.GetAttributes(currentDir);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    TryDeleteDirectoryEntry(currentDir);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to inspect directory attributes for '{Path}'.", currentDir);
                    continue;
                }

                if ((dirAttrs & FileAttributes.ReparsePoint) != 0)
                {
                    TryDeleteDirectoryEntry(currentDir);
                    continue;
                }

                if ((dirAttrs & FileAttributes.ReadOnly) != 0)
                {
                    try
                    {
                        File.SetAttributes(currentDir, dirAttrs & ~FileAttributes.ReadOnly);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "Failed to clear ReadOnly attribute on directory '{Path}'.", currentDir);
                    }
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(currentDir))
                    {
                        try
                        {
                            var fileAttrs = File.GetAttributes(file);
                            if ((fileAttrs & FileAttributes.ReparsePoint) == 0 &&
                                (fileAttrs & FileAttributes.ReadOnly) != 0)
                            {
                                File.SetAttributes(file, fileAttrs & ~FileAttributes.ReadOnly);
                            }

                            File.Delete(file);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            TryDeleteFileEntry(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            if (ex is UnauthorizedAccessException)
                            {
                                TryDeleteDirectoryEntry(file);
                            }

                            _logger.LogDebug(ex, "Failed to delete file '{Path}'.", file);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to enumerate files in directory '{Path}'.", currentDir);
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        pending.Push(subDir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to enumerate subdirectories in '{Path}'.", currentDir);
                }

                dirsToDelete.Push(currentDir);
            }

            while (dirsToDelete.Count > 0)
            {
                var dir = dirsToDelete.Pop();
                if (Directory.Exists(dir))
                {
                    TryDeleteDirectoryEntry(dir);
                }
            }

            if (Directory.Exists(path))
            {
                _logger.LogWarning("Failed to fully delete directory '{Path}'.", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete directory '{Path}'.", path);
        }
    }

    private void TryDeleteDirectoryEntry(string dirPath)
    {
        try
        {
            Directory.Delete(dirPath, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Failed to delete directory entry '{Path}'.", dirPath);
        }
    }

    private void TryDeleteFileEntry(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "Failed to delete file entry '{Path}'; attempting directory-entry unlink.", filePath);
            TryDeleteDirectoryEntry(filePath);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to delete file entry '{Path}'.", filePath);
        }
    }
}
