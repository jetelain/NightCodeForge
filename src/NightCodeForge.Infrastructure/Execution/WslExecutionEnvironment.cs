using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Execution environment executing shell commands directly inside a WSL2 distribution using wsl.exe.
/// </summary>
public class WslExecutionEnvironment : IExecutionEnvironment
{
    private readonly string? _distribution;
    private readonly string? _defaultWorkingDirectory;

    public ExecutionTier Tier => ExecutionTier.WslDirect;

    public WslExecutionEnvironment(string? distribution = null, string? defaultWorkingDirectory = null)
    {
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
        var wslTargetDir = WslPathConverter.ToWslPath(targetDir);
        var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? 300);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        string wslCmd = $"cd \"{wslTargetDir}\" && {command}";
        string arguments;

        if (!string.IsNullOrWhiteSpace(_distribution))
        {
            arguments = $"-d \"{_distribution}\" -- bash -c \"{wslCmd.Replace("\"", "\\\"")}\"";
        }
        else
        {
            arguments = $"-- bash -c \"{wslCmd.Replace("\"", "\\\"")}\"";
        }

        var cmd = Cli.Wrap("wsl.exe")
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None);

        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            cmd = cmd.WithEnvironmentVariables(environmentVariables.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value));
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var res = await cmd.ExecuteBufferedAsync(cts.Token);
            sw.Stop();
            return new NightCodeForge.Core.Interfaces.CommandResult(
                res.ExitCode,
                res.StandardOutput,
                res.StandardError,
                sw.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new NightCodeForge.Core.Interfaces.CommandResult(
                124,
                string.Empty,
                $"WSL command execution timed out after {timeoutSeconds ?? 300} seconds.",
                sw.Elapsed,
                Truncated: true);
        }
    }
}
