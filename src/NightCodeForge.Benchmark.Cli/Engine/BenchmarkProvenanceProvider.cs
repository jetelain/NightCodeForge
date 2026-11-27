using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Captures benchmark provenance metadata (NCF version, git commit/branch, and installed CLI tool versions)
/// for cross-commit and cross-branch regression comparisons.
/// </summary>
public sealed class BenchmarkProvenanceProvider : IBenchmarkProvenanceProvider
{
    public const string ToolKeyDotNetHost = "dotnet_host";
    public const string ToolKeyDotNetContainer = "dotnet_container";
    public const string ToolKeyCsNav = "csnav";
    public const string ToolKeyCodeCondenser = "codecondenser";

    private const int ProbeTimeoutSeconds = 5;
    private const int WslProbeTimeoutSeconds = 10;
    private const string UnknownValue = "unknown";
    private const string DotNetExecutable = "dotnet";
    private const string DefaultWslExecutable = "wsl.exe";
    private const string LinuxSystemDotNetPath = "/usr/share/dotnet/dotnet";
    private const string ContainerDotNetProbeCommand = "dotnet --version 2>/dev/null || /usr/share/dotnet/dotnet --version";

    private readonly IProcessRunner _processRunner;
    private readonly IContainerEnvironmentFactory _containerFactory;
    private readonly ExecutionEnvironmentProviderOptions _envOptions;
    private readonly bool? _isWindowsOverride;
    private readonly ILogger<BenchmarkProvenanceProvider> _logger;

    [ActivatorUtilitiesConstructor]
    public BenchmarkProvenanceProvider(
        IProcessRunner processRunner,
        IContainerEnvironmentFactory containerFactory,
        IOptions<ExecutionEnvironmentProviderOptions> envOptions,
        ILogger<BenchmarkProvenanceProvider> logger)
        : this(processRunner, containerFactory, envOptions, isWindowsOverride: null, logger)
    {
    }

    internal BenchmarkProvenanceProvider(
        IProcessRunner processRunner,
        IContainerEnvironmentFactory containerFactory,
        IOptions<ExecutionEnvironmentProviderOptions> envOptions,
        bool? isWindowsOverride,
        ILogger<BenchmarkProvenanceProvider> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
        ArgumentNullException.ThrowIfNull(envOptions);
        _envOptions = envOptions.Value ?? throw new ArgumentException("Options value cannot be null.", nameof(envOptions));
        _isWindowsOverride = isWindowsOverride;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private bool IsWindows => _isWindowsOverride ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public async Task<BenchmarkRunProvenance> CaptureAsync(
        LlmProviderType providerType,
        string endpoint,
        bool enableStackSkills,
        LocalOptimizationFlags? optimizationFlags,
        ExecutionMode executionMode,
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
        var hostDotNetVersion = await ProbeToolVersionAsync(toolVersions, ToolKeyDotNetHost, DotNetExecutable, ["--version"], cancellationToken);

        if (executionMode is ExecutionMode.ContainerSandbox or ExecutionMode.WslDirect)
        {
            await ProbeContainerDotNetVersionAsync(toolVersions, hostDotNetVersion, cancellationToken).ConfigureAwait(false);
        }

        await ProbeToolVersionAsync(toolVersions, ToolKeyCsNav, ToolKeyCsNav, ["--version"], cancellationToken);
        await ProbeToolVersionAsync(toolVersions, ToolKeyCodeCondenser, ToolKeyCodeCondenser, ["--version"], cancellationToken);

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

    private async Task ProbeContainerDotNetVersionAsync(
        Dictionary<string, string> target,
        string? hostDotNetVersion,
        CancellationToken cancellationToken)
    {
        if (IsWindows)
        {
            var wslExe = !string.IsNullOrWhiteSpace(_envOptions.WslExecutable)
                ? _envOptions.WslExecutable
                : DefaultWslExecutable;
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(_containerFactory.DefaultWslDistribution))
            {
                args.Add("-d");
                args.Add(_containerFactory.DefaultWslDistribution);
            }
            args.Add("--");
            args.Add("bash");
            args.Add("-lc");
            args.Add(ContainerDotNetProbeCommand);

            var wslVersion = await ProbeCommandOutputAsync(
                wslExe,
                args,
                WslProbeTimeoutSeconds,
                takeLastLine: true,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(wslVersion))
            {
                target[ToolKeyDotNetContainer] = wslVersion;
            }
        }
        else
        {
            var containerVersion = await ProbeCommandOutputAsync(LinuxSystemDotNetPath, ["--version"], cancellationToken).ConfigureAwait(false);
            var effective = !string.IsNullOrWhiteSpace(containerVersion) ? containerVersion : hostDotNetVersion;
            if (!string.IsNullOrWhiteSpace(effective))
            {
                target[ToolKeyDotNetContainer] = effective;
            }
        }
    }

    private async Task<string?> ProbeToolVersionAsync(
        Dictionary<string, string> target,
        string key,
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var output = await ProbeCommandOutputAsync(fileName, args, cancellationToken);
        if (!string.IsNullOrWhiteSpace(output))
        {
            target[key] = output;
        }

        return output;
    }

    private Task<string?> ProbeCommandOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        ProbeCommandOutputAsync(fileName, arguments, ProbeTimeoutSeconds, takeLastLine: false, cancellationToken);

    private async Task<string?> ProbeCommandOutputAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        bool takeLastLine,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(new ProcessStartSpec
            {
                FileName = fileName,
                ArgumentList = arguments,
                TimeoutSeconds = timeoutSeconds
            }, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _logger.LogDebug("Provenance probe '{FileName} {Args}' exited with code {ExitCode}.",
                    fileName, string.Join(" ", arguments), result.ExitCode);
                return null;
            }

            var lines = (result.StandardOutput ?? string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var selectedLine = takeLastLine ? lines.LastOrDefault() : lines.FirstOrDefault();
            return string.IsNullOrWhiteSpace(selectedLine) ? null : selectedLine;
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
