using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI workspace manager handling branch isolation, WIP checkpoints, noise-filtered diffs, and squash merges.
/// </summary>
public class GitWorkspaceManager : IGitWorkspaceManager
{
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<GitWorkspaceManager> _logger;

    public GitWorkspaceManager(
        IGitCliRunner gitCliRunner,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<GitWorkspaceManager>? logger = null)
    {
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<GitWorkspaceManager>.Instance;
    }

    /// <inheritdoc />
    public async Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);

        if (!Directory.Exists(repoPath))
        {
            throw new DirectoryNotFoundException($"Directory '{repoPath}' does not exist.");
        }

        try
        {
            var result = await _gitCliRunner.ExecuteAsync(repoPath, "rev-parse --is-inside-work-tree", cancellationToken: cancellationToken);
            if (!result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Directory '{repoPath}' is not inside a Git working tree.");
            }
        }
        catch (GitCommandException ex)
        {
            throw new InvalidOperationException($"Directory '{repoPath}' is not a valid Git repository.", ex);
        }
    }

    /// <inheritdoc />
    public async Task CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace);
        var branchName = $"feature/issue-{issueId}";

        _logger.LogInformation("Checking out branch '{BranchName}' for workspace '{WorkspaceName}' (base: '{BaseBranch}')",
            branchName, workspace.Name, baseBranch);

        // Check if branch already exists
        var branchListResult = await _gitCliRunner.ExecuteAsync(workingPath, $"branch --list {branchName}", cancellationToken: cancellationToken);
        var branchExists = branchListResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', ' ').Equals(branchName, StringComparison.OrdinalIgnoreCase));

        if (branchExists)
        {
            await _gitCliRunner.ExecuteAsync(workingPath, $"checkout {branchName}", cancellationToken: cancellationToken);
        }
        else
        {
            await _gitCliRunner.ExecuteAsync(workingPath, $"checkout -B {branchName} {baseBranch}", cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateWipCheckpointAsync(
        Workspace workspace,
        int issueId,
        string stageName,
        string summary,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace);
        var safeSummary = SanitizeMessage(summary);
        var commitMessage = $"WIP: [Issue #{issueId}][Stage: {stageName}] {safeSummary}";

        _logger.LogInformation("Creating WIP checkpoint for Issue #{IssueId} [Stage: {StageName}] in '{WorkingPath}'",
            issueId, stageName, workingPath);

        // Stage all changes
        await _gitCliRunner.ExecuteAsync(workingPath, "add -A", cancellationToken: cancellationToken);

        // Check if there are changes to commit
        var status = await _gitCliRunner.ExecuteAsync(workingPath, "status --porcelain", cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            // Empty commit to guarantee checkpoint creation
            await _gitCliRunner.ExecuteAsync(
                workingPath,
                $"commit --allow-empty -m \"{commitMessage}\" --no-verify",
                cancellationToken: cancellationToken);
        }
        else
        {
            await _gitCliRunner.ExecuteAsync(
                workingPath,
                $"commit -m \"{commitMessage}\" --no-verify",
                cancellationToken: cancellationToken);
        }

        // Return exact 40-char SHA commit hash
        var revParseResult = await _gitCliRunner.ExecuteAsync(workingPath, "rev-parse HEAD", cancellationToken: cancellationToken);
        var sha = revParseResult.StandardOutput.Trim();

        _logger.LogInformation("WIP checkpoint created for Issue #{IssueId}: {Sha}", issueId, sha);
        return sha;
    }

    /// <inheritdoc />
    public async Task<string> GenerateDiffAsync(
        Workspace workspace,
        int issueId,
        string baseBranch,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace);
        var branchName = $"feature/issue-{issueId}";

        _logger.LogInformation("Generating noise-filtered diff for Issue #{IssueId} against '{BaseBranch}' in '{WorkingPath}'",
            issueId, baseBranch, workingPath);

        // -U2 context window reduction and noisy artifact exclusions
        var diffCommand = $"diff {baseBranch}...{branchName} -U2 -- . " +
                          "\":(exclude)package-lock.json\" " +
                          "\":(exclude)pnpm-lock.yaml\" " +
                          "\":(exclude)yarn.lock\" " +
                          "\":(exclude)*.Designer.cs\" " +
                          "\":(exclude)*.generated.cs\"";

        var result = await _gitCliRunner.ExecuteAsync(workingPath, diffCommand, cancellationToken: cancellationToken);
        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task SquashAndMergeAsync(
        Workspace workspace,
        int issueId,
        string baseBranch,
        string commitMessage,
        CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace);
        var branchName = $"feature/issue-{issueId}";
        var safeMessage = SanitizeMessage(commitMessage);

        _logger.LogInformation("Squashing and merging '{BranchName}' into '{BaseBranch}' in '{WorkingPath}'",
            branchName, baseBranch, workingPath);

        // 1. Checkout target base branch
        try
        {
            await _gitCliRunner.ExecuteAsync(workingPath, $"checkout {baseBranch}", cancellationToken: cancellationToken);
        }
        catch (GitCommandException)
        {
            await _gitCliRunner.ExecuteAsync(workingPath, $"checkout -B {baseBranch}", cancellationToken: cancellationToken);
        }

        // Ensure feature branch exists locally (fetch from host-origin or origin if working in mirror)
        var branchCheck = await _gitCliRunner.ExecuteAsync(workingPath, $"branch --list {branchName}", cancellationToken: cancellationToken);
        var hasBranch = branchCheck.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => b.TrimStart('*', ' ').Equals(branchName, StringComparison.OrdinalIgnoreCase));

        if (!hasBranch)
        {
            try
            {
                await _gitCliRunner.ExecuteAsync(workingPath, $"fetch host-origin {branchName}:{branchName}", cancellationToken: cancellationToken);
            }
            catch
            {
                try
                {
                    await _gitCliRunner.ExecuteAsync(workingPath, $"fetch origin {branchName}:{branchName}", cancellationToken: cancellationToken);
                }
                catch { }
            }
        }

        // 2. Perform squash merge
        try { await _gitCliRunner.ExecuteAsync(workingPath, "clean -fd", cancellationToken: cancellationToken); } catch { }
        await _gitCliRunner.ExecuteAsync(workingPath, $"merge --squash {branchName}", cancellationToken: cancellationToken);

        // 3. Commit squashed changeset
        await _gitCliRunner.ExecuteAsync(
            workingPath,
            $"commit -m \"{safeMessage}\"",
            cancellationToken: cancellationToken);

        _logger.LogInformation("Squash merge completed for Issue #{IssueId} on '{BaseBranch}'", issueId, baseBranch);
    }

    /// <inheritdoc />
    public async Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        var workingPath = GetWorkingPath(workspace);

        _logger.LogWarning("Reverting and cleaning repository working tree in '{WorkingPath}'", workingPath);

        await _gitCliRunner.ExecuteAsync(workingPath, "reset --hard HEAD", cancellationToken: cancellationToken);
        await _gitCliRunner.ExecuteAsync(workingPath, "clean -fd", cancellationToken: cancellationToken);
    }

    private string GetWorkingPath(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            if (Directory.Exists(workspace.Ext4MirrorPath))
            {
                return workspace.Ext4MirrorPath;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string checkPath;
                if (WslPathConverter.IsWslUncPath(workspace.Ext4MirrorPath))
                {
                    checkPath = workspace.Ext4MirrorPath;
                }
                else
                {
                    var distro = _pathResolver?.GetWslDistributionName() ?? Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION") ?? "Ubuntu";
                    checkPath = WslPathConverter.ToWslUncPath(workspace.Ext4MirrorPath, distro);
                }

                if (Directory.Exists(checkPath))
                {
                    return workspace.Ext4MirrorPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
        {
            return workspace.RepositoryPath;
        }

        throw new DirectoryNotFoundException(
            $"Neither workspace RepositoryPath '{workspace.RepositoryPath}' nor Ext4MirrorPath '{workspace.Ext4MirrorPath}' exists.");
    }

    private static string SanitizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Automated commit";
        }

        return message
            .Replace("\"", "\\\"")
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Trim();
    }
}
