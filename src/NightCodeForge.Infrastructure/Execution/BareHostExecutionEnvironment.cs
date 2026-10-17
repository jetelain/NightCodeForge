using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Execution environment directly executing on the host system or WSL via IProcessRunner.
/// </summary>
public class BareHostExecutionEnvironment : IExecutionEnvironment
{
    private readonly ExecutionTier _tier;
    private readonly string? _defaultWorkingDirectory;
    private readonly string? _distribution;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IProcessRunner _processRunner;

    public ExecutionTier Tier => _tier;

    [ActivatorUtilitiesConstructor]
    public BareHostExecutionEnvironment(
        IProcessRunner processRunner,
        IWorkspacePathResolver pathResolver)
        : this(processRunner, ExecutionTier.BareHost, null, null, pathResolver)
    {
    }

    public BareHostExecutionEnvironment(
        IProcessRunner processRunner,
        ExecutionTier tier,
        string? defaultWorkingDirectory,
        string? distribution,
        IWorkspacePathResolver pathResolver)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _tier = tier;
        _defaultWorkingDirectory = defaultWorkingDirectory;
        _distribution = distribution;
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
            var distro = _distribution ?? _pathResolver?.GetWslDistributionName();
            var wslTargetDir = WslPathConverter.ToWslPath(targetDir);
            var wslCmd = $"cd \"{wslTargetDir}\" && {command}";

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(distro))
            {
                args.Add("-d");
                args.Add(distro);
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
                throw new TimeoutException($"Command timed out after {timeout.TotalSeconds} seconds: {command}");
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
                headTailOut.IsTruncated || headTailErr.IsTruncated
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            throw new TimeoutException($"Command timed out after {timeout.TotalSeconds} seconds: {command}");
        }
    }
}
