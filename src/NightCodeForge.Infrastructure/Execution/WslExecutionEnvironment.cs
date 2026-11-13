using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Execution environment executing shell commands directly inside a WSL2 distribution using wsl.exe via IProcessRunner.
/// </summary>
public class WslExecutionEnvironment : IExecutionEnvironment
{
    private const int DefaultTimeoutSeconds = 300;
    private const int TimeoutExitCode = 124;
    private const string ProbeSentinel = "__NCF_PROBE_OK__";
    private const int InitialProbeTimeoutSeconds = 5;
    private const int WslShutdownTimeoutSeconds = 120;
    private const int RecoveryProbeTimeoutSeconds = 30;

    private readonly string? _distribution;
    private readonly string? _defaultWorkingDirectory;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger _logger;

    public ExecutionTier Tier => ExecutionTier.WslDirect;
    public string? Distribution => _distribution;
    public string? DefaultWorkingDirectory => _defaultWorkingDirectory;

    [ActivatorUtilitiesConstructor]
    public WslExecutionEnvironment(IProcessRunner processRunner, ILogger<WslExecutionEnvironment> logger)
        : this(processRunner, distribution: null, defaultWorkingDirectory: null, logger)
    {
    }

    public WslExecutionEnvironment(
        IProcessRunner processRunner,
        string? distribution,
        string? defaultWorkingDirectory)
        : this(processRunner, distribution, defaultWorkingDirectory, NullLogger<WslExecutionEnvironment>.Instance)
    {
    }

    public WslExecutionEnvironment(
        IProcessRunner processRunner,
        string? distribution,
        string? defaultWorkingDirectory,
        ILogger logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _distribution = distribution;
        _defaultWorkingDirectory = defaultWorkingDirectory;
    }

    public async Task<NightCodeForge.Core.Interfaces.CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var targetDir = workingDirectory ?? _defaultWorkingDirectory ?? Directory.GetCurrentDirectory();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? DefaultTimeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        ProcessStartSpec spec;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wslTargetDir = WslPathConverter.ToWslPath(targetDir);
            string wslCmd;
            if (wslTargetDir == "~" || wslTargetDir.StartsWith("~/", StringComparison.Ordinal))
            {
                var homeSub = wslTargetDir.Length > 1 ? wslTargetDir[2..] : string.Empty;
                var homeTarget = string.IsNullOrEmpty(homeSub) ? "$HOME" : $"$HOME/{homeSub}";
                wslCmd = $"mkdir -p \"{homeTarget}\" && cd \"{homeTarget}\" && {command}";
            }
            else
            {
                wslCmd = $"cd \"{wslTargetDir}\" && {command}";
            }

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(_distribution))
            {
                args.Add("-d");
                args.Add(_distribution);
            }
            args.Add("--");
            args.Add("bash");
            args.Add("-c");
            args.Add(wslCmd);

            spec = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = args,
                TimeoutSeconds = (int)timeout.TotalSeconds,
                EnvironmentVariables = environmentVariables
            };
        }
        else
        {
            if (targetDir == "~" || targetDir.StartsWith("~/", StringComparison.Ordinal))
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(profile))
                {
                    targetDir = targetDir.Length > 1 ? Path.Combine(profile, targetDir[2..]) : profile;
                    try
                    {
                        Directory.CreateDirectory(targetDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Best-effort creation of WSL marker working directory '{TargetDir}' failed", targetDir);
                    }
                }
            }

            var unixCmd = $"cd \"{targetDir}\" && {command}";
            spec = new ProcessStartSpec
            {
                FileName = "/bin/bash",
                ArgumentList = ["-c", unixCmd],
                WorkingDirectory = targetDir,
                TimeoutSeconds = (int)timeout.TotalSeconds,
                EnvironmentVariables = environmentVariables
            };
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var res = await _processRunner.RunAsync(spec, cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (res.ExitCode == TimeoutExitCode)
            {
                await RunWslTimeoutRecoveryAsync().ConfigureAwait(false);
                return new NightCodeForge.Core.Interfaces.CommandResult(
                    TimeoutExitCode,
                    string.Empty,
                    $"WSL command execution timed out after {timeout.TotalSeconds:F0} seconds.",
                    sw.Elapsed,
                    Truncated: true);
            }

            var headTailOut = new HeadTailBuffer();
            headTailOut.Append(res.StandardOutput);

            var headTailErr = new HeadTailBuffer();
            headTailErr.Append(res.StandardError);

            return new NightCodeForge.Core.Interfaces.CommandResult(
                res.ExitCode,
                headTailOut.GetFormattedString(),
                headTailErr.GetFormattedString(),
                sw.Elapsed,
                headTailOut.IsTruncated || headTailErr.IsTruncated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            await RunWslTimeoutRecoveryAsync().ConfigureAwait(false);
            return new NightCodeForge.Core.Interfaces.CommandResult(
                TimeoutExitCode,
                string.Empty,
                $"WSL command execution timed out after {timeout.TotalSeconds:F0} seconds.",
                sw.Elapsed,
                Truncated: true);
        }
    }

    private async Task RunWslTimeoutRecoveryAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var probeArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(_distribution))
            {
                probeArgs.AddRange(["-d", _distribution.Trim()]);
            }
            probeArgs.AddRange(["--", "echo", ProbeSentinel]);

            var probeSpec5 = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = probeArgs,
                TimeoutSeconds = InitialProbeTimeoutSeconds
            };

            var probe5Result = await _processRunner.RunAsync(probeSpec5, CancellationToken.None).ConfigureAwait(false);
            if (probe5Result.ExitCode == 0 && probe5Result.StandardOutput.Contains(ProbeSentinel))
            {
                return;
            }

            _logger.LogWarning("WSL 5s probe failed after command timeout (distribution: {Distribution}). Executing wsl.exe --shutdown.",
                _distribution ?? "default");

            var shutdownSpec = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = ["--shutdown"],
                TimeoutSeconds = WslShutdownTimeoutSeconds
            };
            await _processRunner.RunAsync(shutdownSpec, CancellationToken.None).ConfigureAwait(false);

            var probeSpec30 = new ProcessStartSpec
            {
                FileName = "wsl.exe",
                ArgumentList = probeArgs,
                TimeoutSeconds = RecoveryProbeTimeoutSeconds
            };
            await _processRunner.RunAsync(probeSpec30, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WSL timeout recovery encountered an exception (distribution: {Distribution}).",
                _distribution ?? "default");
        }
    }
}
