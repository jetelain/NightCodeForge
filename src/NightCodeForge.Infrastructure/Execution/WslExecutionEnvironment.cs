using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Execution environment executing shell commands directly inside a WSL2 distribution using wsl.exe via IProcessRunner.
/// </summary>
public class WslExecutionEnvironment : IExecutionEnvironment
{
    private readonly string? _distribution;
    private readonly string? _defaultWorkingDirectory;
    private readonly IProcessRunner _processRunner;

    public ExecutionTier Tier => ExecutionTier.WslDirect;
    public string? Distribution => _distribution;
    public string? DefaultWorkingDirectory => _defaultWorkingDirectory;

    [ActivatorUtilitiesConstructor]
    public WslExecutionEnvironment(
        IProcessRunner processRunner,
        string? distribution = null,
        string? defaultWorkingDirectory = null)
    {
        _processRunner = processRunner ?? new SystemProcessRunner();
        _distribution = distribution;
        _defaultWorkingDirectory = defaultWorkingDirectory;
    }

    public WslExecutionEnvironment(
        string? distribution = null,
        string? defaultWorkingDirectory = null)
        : this(new SystemProcessRunner(), distribution, defaultWorkingDirectory)
    {
    }

    public async Task<NightCodeForge.Core.Interfaces.CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        var targetDir = workingDirectory ?? _defaultWorkingDirectory ?? Directory.GetCurrentDirectory();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? 300);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        ProcessStartSpec spec;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var wslTargetDir = WslPathConverter.ToWslPath(targetDir);
            var wslCmd = $"cd \"{wslTargetDir}\" && {command}";

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

            if (res.ExitCode == 124 && res.StandardError.Contains("Process timed out"))
            {
                return new NightCodeForge.Core.Interfaces.CommandResult(
                    124,
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
            return new NightCodeForge.Core.Interfaces.CommandResult(
                124,
                string.Empty,
                $"WSL command execution timed out after {timeout.TotalSeconds:F0} seconds.",
                sw.Elapsed,
                Truncated: true);
        }
    }
}
