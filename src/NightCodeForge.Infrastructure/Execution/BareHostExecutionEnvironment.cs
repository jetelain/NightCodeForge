using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Execution environment directly executing on the host system or WSL.
/// </summary>
public class BareHostExecutionEnvironment : IExecutionEnvironment
{
    private readonly ExecutionTier _tier;
    private readonly string? _defaultWorkingDirectory;
    private readonly string? _distribution;
    private readonly IWorkspacePathResolver? _pathResolver;

    public ExecutionTier Tier => _tier;

    public BareHostExecutionEnvironment(
        ExecutionTier tier = ExecutionTier.BareHost,
        string? defaultWorkingDirectory = null,
        string? distribution = null,
        IWorkspacePathResolver? pathResolver = null)
    {
        _tier = tier;
        _defaultWorkingDirectory = defaultWorkingDirectory;
        _distribution = distribution;
        _pathResolver = pathResolver;
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

        string shell;
        string args;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            shell = "wsl.exe";
            var distro = _distribution ?? _pathResolver?.GetWslDistributionName();
            var wslTargetDir = WslPathConverter.ToWslPath(targetDir);
            var wslCmd = $"cd \"{wslTargetDir}\" && {command}";

            if (!string.IsNullOrWhiteSpace(distro))
            {
                args = $"-d \"{distro}\" -- bash -c \"{wslCmd.Replace("\"", "\\\"")}\"";
            }
            else
            {
                args = $"-- bash -c \"{wslCmd.Replace("\"", "\\\"")}\"";
            }
        }
        else
        {
            shell = "/bin/bash";
            var unixCmd = $"cd \"{targetDir}\" && {command}";
            args = $"-c \"{unixCmd.Replace("\"", "\\\"")}\"";
        }

        var cmd = Cli.Wrap(shell)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None);

        if (environmentVariables != null)
        {
            cmd = cmd.WithEnvironmentVariables(environmentVariables.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var res = await cmd.ExecuteBufferedAsync(cts.Token);
            sw.Stop();

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
