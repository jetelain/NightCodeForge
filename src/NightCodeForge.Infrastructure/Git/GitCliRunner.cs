using System.Diagnostics;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Native Git CLI runner powered by CliWrap with timeout policies, buffered output capture, and typed exceptions.
/// </summary>
public class GitCliRunner : IGitCliRunner
{
    private readonly ILogger<GitCliRunner> _logger;

    public GitCliRunner(ILogger<GitCliRunner>? logger = null)
    {
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

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory '{workingDirectory}' does not exist.");
        }

        var timeoutDuration = ResolveTimeout(arguments, timeoutSeconds);
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(timeoutDuration);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        _logger.LogDebug("Executing 'git {Arguments}' in '{WorkingDirectory}' (Timeout: {TimeoutSeconds}s)",
            arguments, workingDirectory, timeoutDuration.TotalSeconds);

        try
        {
            var cmd = Cli.Wrap("git")
                .WithWorkingDirectory(workingDirectory)
                .WithArguments(arguments)
                .WithEnvironmentVariables(env => env
                    .Set("PAGER", "cat")
                    .Set("GIT_PAGER", "cat")
                    .Set("GIT_TERMINAL_PROMPT", "0"))
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOutBuffer))
                .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
                .WithValidation(CommandResultValidation.None);

            var result = await cmd.ExecuteAsync(linkedCts.Token);
            stopwatch.Stop();

            var stdout = stdOutBuffer.ToString();
            var stderr = stdErrBuffer.ToString();

            if (result.ExitCode != 0)
            {
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
