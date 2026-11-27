using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI runner powered by IProcessRunner with timeout policies, buffered output capture, and typed exceptions.
/// Supports transparent execution on Windows host or routed through WSL2 (wsl.exe -d <distro> -- git -C <dir>)
/// when targeting ext4 mirrors or Linux working trees.
/// </summary>
public class GitCliRunner : IGitCliRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly ILogger<GitCliRunner> _logger;

    [ActivatorUtilitiesConstructor]
    public GitCliRunner(
        IProcessRunner processRunner,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        ILogger<GitCliRunner> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(workingDirectory, argumentsList, timeoutSeconds, null, cancellationToken);

    /// <inheritdoc />
    public Task<GitRawCommandResult> ExecuteRawAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(workingDirectory, argumentsList, timeoutSeconds, null, cancellationToken);

    internal bool SkipDirectoryValidation { get; set; }

    /// <inheritdoc />
    public Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            workingDirectory,
            argumentsList,
            timeoutSeconds,
            environmentVariables,
            (spec, ct) => _processRunner.RunAsync(spec, ct),
            (result, elapsed) => (result.ExitCode, result.StandardOutput, result.StandardError,
                new GitCommandResult(result.ExitCode, result.StandardOutput, result.StandardError, elapsed)),
            cancellationToken);

    /// <inheritdoc />
    public Task<GitRawCommandResult> ExecuteRawAsync(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds,
        IReadOnlyDictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken = default) =>
        ExecuteCoreAsync(
            workingDirectory,
            argumentsList,
            timeoutSeconds,
            environmentVariables,
            (spec, ct) => _processRunner.RunRawAsync(spec, ct),
            (result, elapsed) => (result.ExitCode, string.Empty, result.StandardError,
                new GitRawCommandResult(result.ExitCode, result.StandardOutputBytes, result.StandardError, elapsed)),
            cancellationToken);

    private async Task<TResult> ExecuteCoreAsync<TProcessResult, TResult>(
        string workingDirectory,
        IEnumerable<string> argumentsList,
        int? timeoutSeconds,
        IReadOnlyDictionary<string, string>? environmentVariables,
        Func<ProcessStartSpec, CancellationToken, Task<TProcessResult>> runProcess,
        Func<TProcessResult, TimeSpan, (int ExitCode, string SafeStdOut, string StdErr, TResult Result)> mapResult,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(argumentsList);

        var argsArray = argumentsList as string[] ?? argumentsList.ToArray();

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string distro = string.Empty;
        string linuxDir = workingDirectory;
        var isWslTarget = isWindows && TryResolveWslTarget(workingDirectory, out distro, out linuxDir);

        if (!SkipDirectoryValidation)
        {
            if (isWslTarget)
            {
                ValidateWslDirectoryExists(workingDirectory, distro, linuxDir);
            }
            else if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist.");
            }
        }

        var timeoutDuration = ResolveTimeout(argsArray, timeoutSeconds);
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(timeoutDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var redactedArgs = GitCredentialRedactor.RedactArguments(argsArray);
        var displayArgs = string.Join(" ", redactedArgs);
        _logger.LogDebug("Executing 'git {Arguments}' in '{WorkingDirectory}' (WSL: {IsWsl}, Timeout: {TimeoutSeconds}s)",
            displayArgs, workingDirectory, isWslTarget, timeoutDuration.TotalSeconds);

        try
        {
            var spec = BuildProcessStartSpec(workingDirectory, argsArray, isWslTarget, distro, linuxDir, timeoutDuration, environmentVariables);
            var processResult = await runProcess(spec, linkedCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var (exitCode, safeStdOut, stderr, finalResult) = mapResult(processResult, stopwatch.Elapsed);

            if (exitCode == WorkflowDefaults.ProcessTimeoutExitCode && stderr.Contains("Process timed out"))
            {
                throw new TimeoutException($"Git command 'git {displayArgs}' timed out after {timeoutDuration.TotalSeconds:F0}s in '{workingDirectory}'.");
            }

            if (exitCode != 0)
            {
                var redactedStdErr = GitCredentialRedactor.Redact(stderr);
                if (isWslTarget && stderr.Contains("cannot change to") && stderr.Contains("No such file or directory"))
                {
                    throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist: {redactedStdErr.Trim()}");
                }

                _logger.LogWarning("Git command 'git {Arguments}' failed with exit code {ExitCode}: {StdErr}",
                    displayArgs, exitCode, redactedStdErr);

                throw new GitCommandException(workingDirectory, redactedArgs, exitCode, GitCredentialRedactor.Redact(safeStdOut), redactedStdErr);
            }

            return finalResult;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogError("Git command 'git {Arguments}' timed out after {TimeoutSeconds}s in '{WorkingDirectory}'",
                displayArgs, timeoutDuration.TotalSeconds, workingDirectory);

            throw new TimeoutException($"Git command 'git {displayArgs}' timed out after {timeoutDuration.TotalSeconds:F0}s in '{workingDirectory}'.");
        }
    }

    private static ProcessStartSpec BuildProcessStartSpec(
        string workingDirectory,
        string[] argsArray,
        bool isWslTarget,
        string distro,
        string linuxDir,
        TimeSpan timeoutDuration,
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PAGER"] = "cat",
            ["GIT_PAGER"] = "cat",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                env[kvp.Key] = kvp.Value;
            }
        }

        if (isWslTarget)
        {
            var wslArgs = new List<string> { "-d", distro, "--exec", "env" };
            foreach (var kvp in env)
            {
                wslArgs.Add($"{kvp.Key}={kvp.Value}");
            }
            wslArgs.AddRange(["git", "-c", "core.pager=cat", "-C", linuxDir]);
            wslArgs.AddRange(argsArray);

            return new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = wslArgs,
                TimeoutSeconds = (int)Math.Max(1, timeoutDuration.TotalSeconds),
                EnvironmentVariables = env
            };
        }

        return new ProcessStartSpec
        {
            FileName = "git",
            ArgumentList = argsArray,
            WorkingDirectory = workingDirectory,
            TimeoutSeconds = (int)Math.Max(1, timeoutDuration.TotalSeconds),
            EnvironmentVariables = env
        };
    }

    private bool TryResolveWslTarget(string workingDirectory, out string distro, out string linuxDir)
    {
        distro = _wslMetadataService.GetDefaultDistributionName();
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

        var effectiveDistro = !string.IsNullOrWhiteSpace(distro) ? distro : _wslMetadataService.GetDefaultDistributionName();
        var user = _wslMetadataService.GetDefaultUserName();
        var checkPath = WslPathConverter.ToWslUncPath(linuxDir, effectiveDistro, user);

        if (!Directory.Exists(checkPath))
        {
            throw new DirectoryNotFoundException($"Working directory '{originalPath}' does not exist (checked '{checkPath}').");
        }
    }

    public const int DefaultTimeoutSeconds = 30;
    public const int NetworkOperationTimeoutSeconds = 120;

    private static readonly HashSet<string> NetworkGitSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "clone",
        "fetch",
        "push",
        "pull",
        "ls-remote"
    };

    private static readonly HashSet<string> GlobalOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-c",
        "-C",
        "--git-dir",
        "--work-tree",
        "--namespace",
        "--exec-path",
        "--super-prefix",
        "--config-env"
    };

    public static int ResolveTimeoutSeconds(IReadOnlyList<string> args, int? timeoutSeconds)
    {
        if (timeoutSeconds.HasValue)
        {
            return timeoutSeconds.Value;
        }

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (GlobalOptionsWithValue.Contains(arg))
            {
                i++; // skip option value
                continue;
            }

            if (arg.StartsWith('-'))
            {
                continue;
            }

            return NetworkGitSubcommands.Contains(arg)
                ? NetworkOperationTimeoutSeconds
                : DefaultTimeoutSeconds;
        }

        return DefaultTimeoutSeconds;
    }

    private static TimeSpan ResolveTimeout(IReadOnlyList<string> args, int? timeoutSeconds) =>
        TimeSpan.FromSeconds(ResolveTimeoutSeconds(args, timeoutSeconds));
}
