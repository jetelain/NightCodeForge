using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Tailscale;

/// <summary>Executes the <c>tailscale</c> CLI.</summary>
public interface ITailscaleCliRunner
{
    Task<TailscaleCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken);
}

/// <summary><see cref="ITailscaleCliRunner"/> backed by <see cref="IProcessRunner"/> (no shell, argument list).</summary>
public class TailscaleCliRunner : ITailscaleCliRunner
{
    public const string ExecutableName = "tailscale";

    private readonly IProcessRunner _processRunner;

    public TailscaleCliRunner(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<TailscaleCliResult> RunAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessStartSpec
            {
                FileName = ExecutableName,
                ArgumentList = arguments,
                TimeoutSeconds = timeoutSeconds
            },
            cancellationToken);

        return new TailscaleCliResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

