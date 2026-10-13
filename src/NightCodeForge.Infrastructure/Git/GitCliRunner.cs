using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI runner powered by CliWrap with timeout policies, buffered output capture, and typed exceptions.
/// Supports transparent execution on Windows host or routed through WSL2 (wsl.exe -d <distro> -- git -C <dir>)
/// when targeting ext4 mirrors or Linux working trees.
/// </summary>
public class GitCliRunner : IGitCliRunner
{
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<GitCliRunner> _logger;

    public GitCliRunner(
        IWorkspacePathResolver? pathResolver = null,
        ILogger<GitCliRunner>? logger = null)
    {
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<GitCliRunner>.Instance;
    }

    /// <inheritdoc />
    public async Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string distro = string.Empty;
        string linuxDir = workingDirectory;
        var isWslTarget = isWindows && TryResolveWslTarget(workingDirectory, out distro, out linuxDir);

        if (isWslTarget)
        {
            ValidateWslDirectoryExists(workingDirectory, distro, linuxDir);
        }
        else
        {
            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist.");
            }
        }

        var timeoutDuration = ResolveTimeout(arguments, timeoutSeconds);
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(timeoutDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        _logger.LogDebug("Executing 'git {Arguments}' in '{WorkingDirectory}' (WSL: {IsWsl}, Timeout: {TimeoutSeconds}s)",
            arguments, workingDirectory, isWslTarget, timeoutDuration.TotalSeconds);

        try
        {
            Command cmd;
            if (isWslTarget)
            {
                var wslArgs = $"-d {distro} -- git -c core.pager=cat -C \"{linuxDir}\" {arguments}";
                cmd = Cli.Wrap("wsl.exe")
                    .WithArguments(wslArgs)
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.None);
            }
            else
            {
                cmd = Cli.Wrap("git")
                    .WithWorkingDirectory(workingDirectory)
                    .WithArguments(arguments)
                    .WithEnvironmentVariables(env => env
                        .Set("PAGER", "cat")
                        .Set("GIT_PAGER", "cat")
                        .Set("GIT_TERMINAL_PROMPT", "0"))
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                    .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                    .WithValidation(CommandResultValidation.None);
            }

            var result = await cmd.ExecuteAsync(linkedCts.Token);
            stopwatch.Stop();

            var stdout = stdOutBuffer.ToString();
            var stderr = stdErrBuffer.ToString();

            if (result.ExitCode != 0)
            {
                if (isWslTarget && stderr.Contains("cannot change to") && stderr.Contains("No such file or directory"))
                {
                    throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist: {stderr.Trim()}");
                }

                _logger.LogWarning("Git command 'git {Arguments}' failed with exit code {ExitCode}: {StdErr}",
                    arguments, result.ExitCode, stderr);

                throw new GitCommandException(workingDirectory, arguments, result.ExitCode, stdout, stderr);
            }

            return new GitCommandResult(result.ExitCode, stdout, stderr, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogError("Git command 'git {Arguments}' timed out after {TimeoutSeconds}s in '{WorkingDirectory}'",
                arguments, timeoutDuration.TotalSeconds, workingDirectory);

            throw new TimeoutException($"Git command 'git {arguments}' timed out after {timeoutDuration.TotalSeconds:F0}s in '{workingDirectory}'.");
        }
    }

    private bool TryResolveWslTarget(string workingDirectory, out string distro, out string linuxDir)
    {
        distro = _pathResolver?.GetWslDistributionName() ?? Environment.GetEnvironmentVariable("NCF_WSL_DISTRIBUTION") ?? "Ubuntu";
        linuxDir = workingDirectory;

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return false;
        }

        if (WslPathConverter.IsWslUncPath(workingDirectory))
        {
            if (WslPathConverter.TryExtractWslUnc(workingDirectory, out var extractedDistro, out var extractedLinux))
            {
                if (!string.IsNullOrWhiteSpace(extractedDistro))
                {
                    distro = extractedDistro;
                }
                linuxDir = extractedLinux;
                return true;
            }
        }

        if (workingDirectory.StartsWith('/') || workingDirectory.StartsWith('~'))
        {
            return true;
        }

        return false;
    }

    private void ValidateWslDirectoryExists(string originalPath, string distro, string linuxDir)
    {
        if (WslPathConverter.IsWslUncPath(originalPath))
        {
            if (!Directory.Exists(originalPath))
            {
                throw new DirectoryNotFoundException($"Working directory '{originalPath}' does not exist.");
            }
            return;
        }

        string checkPath;
        if (linuxDir.StartsWith('~'))
        {
            var user = _pathResolver?.GetWslUserName() ?? Environment.UserName.ToLowerInvariant();
            var expanded = $"/home/{user}/{linuxDir.TrimStart('~', '/')}";
            checkPath = WslPathConverter.ToWslUncPath(expanded, distro);
        }
        else
        {
            checkPath = WslPathConverter.ToWslUncPath(linuxDir, distro);
        }

        if (!Directory.Exists(checkPath))
        {
            throw new DirectoryNotFoundException($"Working directory '{originalPath}' does not exist (checked '{checkPath}').");
        }
    }

    private static TimeSpan ResolveTimeout(string arguments, int? timeoutSeconds)
    {
        if (timeoutSeconds.HasValue)
        {
            return TimeSpan.FromSeconds(timeoutSeconds.Value);
        }

        var trimmed = arguments.TrimStart();
        if (trimmed.StartsWith("clone", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("fetch", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromSeconds(120);
        }

        return TimeSpan.FromSeconds(30);
    }
}
