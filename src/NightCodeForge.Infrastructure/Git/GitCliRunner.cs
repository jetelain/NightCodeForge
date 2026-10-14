using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI runner powered by IProcessRunner with timeout policies, buffered output capture, and typed exceptions.
/// Supports transparent execution on Windows host or routed through WSL2 (wsl.exe -d <distro> -- git -C <dir>)
/// when targeting ext4 mirrors or Linux working trees.
/// </summary>
public class GitCliRunner : IGitCliRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<GitCliRunner> _logger;

    [ActivatorUtilitiesConstructor]
    public GitCliRunner(
        IProcessRunner processRunner,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<GitCliRunner>? logger = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<GitCliRunner>.Instance;
    }

    public GitCliRunner(
        IWorkspacePathResolver? pathResolver = null,
        ILogger<GitCliRunner>? logger = null)
        : this(new SystemProcessRunner(), pathResolver, logger)
    {
    }

    /// <inheritdoc />
    public async Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(argumentsList);

        var argsArray = argumentsList as string[] ?? argumentsList.ToArray();

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

        var timeoutDuration = ResolveTimeout(argsArray, timeoutSeconds);
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(timeoutDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var displayArgs = string.Join(" ", argsArray);
        _logger.LogDebug("Executing 'git {Arguments}' in '{WorkingDirectory}' (WSL: {IsWsl}, Timeout: {TimeoutSeconds}s)",
            displayArgs, workingDirectory, isWslTarget, timeoutDuration.TotalSeconds);

        try
        {
            ProcessStartSpec spec;
            if (isWslTarget)
            {
                var wslArgs = new List<string> { "-d", distro, "--", "git", "-c", "core.pager=cat", "-C", linuxDir };
                wslArgs.AddRange(argsArray);

                spec = new ProcessStartSpec
                {
                    FileName = "wsl.exe",
                    ArgumentList = wslArgs,
                    TimeoutSeconds = (int)Math.Max(1, timeoutDuration.TotalSeconds)
                };
            }
            else
            {
                spec = new ProcessStartSpec
                {
                    FileName = "git",
                    ArgumentList = argsArray,
                    WorkingDirectory = workingDirectory,
                    TimeoutSeconds = (int)Math.Max(1, timeoutDuration.TotalSeconds),
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        ["PAGER"] = "cat",
                        ["GIT_PAGER"] = "cat",
                        ["GIT_TERMINAL_PROMPT"] = "0"
                    }
                };
            }

            var result = await _processRunner.RunAsync(spec, linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var stdout = result.StandardOutput;
            var stderr = result.StandardError;

            if (result.ExitCode == 124 && stderr.Contains("Process timed out"))
            {
                throw new TimeoutException($"Git command 'git {displayArgs}' timed out after {timeoutDuration.TotalSeconds:F0}s in '{workingDirectory}'.");
            }

            if (result.ExitCode != 0)
            {
                if (isWslTarget && stderr.Contains("cannot change to") && stderr.Contains("No such file or directory"))
                {
                    throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist: {stderr.Trim()}");
                }

                _logger.LogWarning("Git command 'git {Arguments}' failed with exit code {ExitCode}: {StdErr}",
                    displayArgs, result.ExitCode, stderr);

                throw new GitCommandException(workingDirectory, argsArray, result.ExitCode, stdout, stderr);
            }

            return new GitCommandResult(result.ExitCode, stdout, stderr, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogError("Git command 'git {Arguments}' timed out after {TimeoutSeconds}s in '{WorkingDirectory}'",
                displayArgs, timeoutDuration.TotalSeconds, workingDirectory);

            throw new TimeoutException($"Git command 'git {displayArgs}' timed out after {timeoutDuration.TotalSeconds:F0}s in '{workingDirectory}'.");
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

    private static TimeSpan ResolveTimeout(IReadOnlyList<string> args, int? timeoutSeconds)
    {
        if (timeoutSeconds.HasValue)
        {
            return TimeSpan.FromSeconds(timeoutSeconds.Value);
        }

        var firstArg = args.Count > 0 ? args[0] : string.Empty;
        if (string.Equals(firstArg, "clone", StringComparison.OrdinalIgnoreCase) ||
            args.Any(a => a.Contains("fetch", StringComparison.OrdinalIgnoreCase)))
        {
            return TimeSpan.FromSeconds(120);
        }

        return TimeSpan.FromSeconds(30);
    }
}
