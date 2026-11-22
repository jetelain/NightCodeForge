using System.Reflection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Captures benchmark provenance metadata (NCF version, git commit/branch, and installed CLI tool versions)
/// for cross-commit and cross-branch regression comparisons.
/// </summary>
public sealed class BenchmarkProvenanceProvider : IBenchmarkProvenanceProvider
{
    private const int ProbeTimeoutSeconds = 5;
    private const string UnknownValue = "unknown";

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<BenchmarkProvenanceProvider> _logger;

    public BenchmarkProvenanceProvider(
        IProcessRunner processRunner,
        ILogger<BenchmarkProvenanceProvider> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkRunProvenance> CaptureAsync(
        LlmProviderType providerType,
        string endpoint,
        bool enableStackSkills,
        LocalOptimizationFlags? optimizationFlags,
        CancellationToken cancellationToken = default)
    {
        var assembly = typeof(BenchmarkProvenanceProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? UnknownValue;

        var gitCommit = await ProbeCommandOutputAsync("git", ["rev-parse", "HEAD"], cancellationToken) ?? UnknownValue;
        var gitBranch = await ProbeCommandOutputAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken) ?? UnknownValue;

        var toolVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await ProbeToolVersionAsync(toolVersions, "dotnet", ["--version"], cancellationToken);
        await ProbeToolVersionAsync(toolVersions, "csnav", ["--version"], cancellationToken);
        await ProbeToolVersionAsync(toolVersions, "codecondenser", ["--version"], cancellationToken);

        return new BenchmarkRunProvenance(
            NcfVersion: informationalVersion,
            GitCommit: gitCommit,
            GitBranch: gitBranch,
            RunTimestampUtc: DateTimeOffset.UtcNow,
            ProviderType: providerType,
            Endpoint: endpoint ?? string.Empty,
            EnableStackSkills: enableStackSkills,
            OptimizationFlags: optimizationFlags?.Clone(),
            DetectedToolVersions: toolVersions);
    }

    private async Task ProbeToolVersionAsync(
        Dictionary<string, string> target,
        string toolName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var output = await ProbeCommandOutputAsync(toolName, args, cancellationToken);
        if (!string.IsNullOrWhiteSpace(output))
        {
            target[toolName] = output;
        }
    }

    private async Task<string?> ProbeCommandOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(new ProcessStartSpec
            {
                FileName = fileName,
                ArgumentList = arguments,
                TimeoutSeconds = ProbeTimeoutSeconds
            }, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _logger.LogDebug("Provenance probe '{FileName} {Args}' exited with code {ExitCode}.",
                    fileName, string.Join(" ", arguments), result.ExitCode);
                return null;
            }

            var firstLine = (result.StandardOutput ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Provenance probe failed for '{FileName}'.", fileName);
            return null;
        }
    }
}
