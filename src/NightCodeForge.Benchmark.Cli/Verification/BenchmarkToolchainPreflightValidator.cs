using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Validates host-mounted Rust toolchain availability before provisioning or executing Rust benchmark
/// instances in <see cref="ExecutionMode.ContainerSandbox"/> mode without a <c>custom_container_image</c>.
/// </summary>
public sealed class BenchmarkToolchainPreflightValidator : IBenchmarkToolchainPreflightValidator
{
    private const string CargoCommandName = "cargo";
    private const string RustcCommandName = "rustc";
    private const string CargoProxySegment = "/.cargo/";
    private const string RustupToolchainsSegment = "/toolchains/";
    private const string RustupSuffixDot = "/.rustup";
    private const string RustupSuffixPlain = "/rustup";

    private readonly IHostSdkDiscoveryService _sdkDiscoveryService;
    private readonly IContainerEnvironmentFactory _containerFactory;
    private readonly IBenchmarkCustomImageBuilder _customImageBuilder;

    public BenchmarkToolchainPreflightValidator(
        IHostSdkDiscoveryService sdkDiscoveryService,
        IContainerEnvironmentFactory containerFactory,
        IBenchmarkCustomImageBuilder customImageBuilder)
    {
        _sdkDiscoveryService = sdkDiscoveryService ?? throw new ArgumentNullException(nameof(sdkDiscoveryService));
        _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
        _customImageBuilder = customImageBuilder ?? throw new ArgumentNullException(nameof(customImageBuilder));
    }

    public async Task<string?> ValidateAsync(
        BenchmarkInstance instance,
        BenchmarkProblemCategory category,
        ExecutionMode executionMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (executionMode == ExecutionMode.ContainerSandbox &&
            !string.IsNullOrWhiteSpace(instance.CustomContainerImage) &&
            _customImageBuilder.IsManagedBenchmarkImage(instance.CustomContainerImage))
        {
            try
            {
                await _customImageBuilder
                    .EnsureImageAsync(instance.CustomContainerImage, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BenchmarkException ex)
            {
                return ex.Message;
            }
        }

        if (executionMode != ExecutionMode.ContainerSandbox ||
            category is not (BenchmarkProblemCategory.Implementation or BenchmarkProblemCategory.CodeReview) ||
            !string.Equals(BenchmarkLanguages.Normalize(instance.Language), BenchmarkLanguages.Rust, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(instance.CustomContainerImage) && !ContainerMountAndDriftBuilder.IsManagedBaseImage(instance.CustomContainerImage)))
        {
            return null;
        }

        var details = await _sdkDiscoveryService
            .DiscoverSdkDetailsAsync(_containerFactory.DefaultWslDistribution, cancellationToken)
            .ConfigureAwait(false);

        details.CommandPaths.TryGetValue(CargoCommandName, out var cargoPath);
        details.CommandPaths.TryGetValue(RustcCommandName, out var rustcPath);

        if (string.IsNullOrWhiteSpace(cargoPath) || string.IsNullOrWhiteSpace(rustcPath))
        {
            return $"Pre-flight Rust toolchain check failed for '{instance.InstanceId}': host SDK discovery did not find both 'cargo' and 'rustc' on PATH. " +
                   "Install rustup on the host/WSL distribution (so ~/.cargo and ~/.rustup can be bind-mounted) or configure 'custom_container_image'.";
        }

        var mountablePrefixes = details.MountPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && !HostSdkDiscoveryService.IsExcludedDirectory(p))
            .Select(HostSdkDiscoveryService.NormalizePath)
            .ToList();

        var normalizedCargo = HostSdkDiscoveryService.NormalizePath(cargoPath);
        var normalizedRustc = HostSdkDiscoveryService.NormalizePath(rustcPath);

        bool cargoMountable = IsBackedByMountableDirectory(normalizedCargo, mountablePrefixes);
        bool rustcMountable = IsBackedByMountableDirectory(normalizedRustc, mountablePrefixes);
        if (!cargoMountable || !rustcMountable)
        {
            return $"Pre-flight Rust toolchain check failed for '{instance.InstanceId}': host 'cargo' ('{normalizedCargo}') and/or 'rustc' ('{normalizedRustc}') " +
                   "reside in an excluded system directory (such as /usr/bin) that is not bind-mounted into containers (or rustup has no active toolchain configured). " +
                   "Install rustup on the host/WSL distribution and ensure an active toolchain is configured (e.g. 'rustup default stable' providing mountable ~/.rustup/toolchains/... or ~/.cargo and ~/.rustup), or configure 'custom_container_image'.";
        }

        bool usesCargoProxy = UsesCargoProxyDirectory(normalizedCargo, details.EnvironmentVariables) ||
                              UsesCargoProxyDirectory(normalizedRustc, details.EnvironmentVariables);
        bool usesToolchainPath = IsRustupToolchainPath(normalizedCargo) ||
                                 IsRustupToolchainPath(normalizedRustc);
        if ((usesCargoProxy || usesToolchainPath) && !HasMountableRustupDirectory(mountablePrefixes))
        {
            return $"Pre-flight Rust toolchain check failed for '{instance.InstanceId}': host 'cargo'/'rustc' use rustup without a mountable rustup toolchain directory (~/.rustup / RUSTUP_HOME). " +
                   "Install rustup on the host/WSL distribution (or configure an active toolchain with 'rustup default stable') or configure 'custom_container_image'.";
        }

        return null;
    }

    private static bool IsRustupToolchainPath(string normalizedCommandPath) =>
        normalizedCommandPath.Contains(RustupToolchainsSegment, StringComparison.OrdinalIgnoreCase);

    private static bool IsBackedByMountableDirectory(string normalizedCommandPath, IReadOnlyList<string> mountablePrefixes) =>
        mountablePrefixes.Any(prefix =>
            normalizedCommandPath.StartsWith(prefix + "/", StringComparison.Ordinal) ||
            string.Equals(normalizedCommandPath, prefix, StringComparison.Ordinal));

    private static bool UsesCargoProxyDirectory(
        string normalizedCommandPath,
        IReadOnlyDictionary<string, string> environmentVariables)
    {
        if (normalizedCommandPath.Contains(CargoProxySegment, StringComparison.Ordinal))
        {
            return true;
        }

        if (environmentVariables.TryGetValue(ContainerExecutorOptions.CargoHomeEnvVarName, out var cargoHome) &&
            !string.IsNullOrWhiteSpace(cargoHome))
        {
            var normalizedCargoHome = HostSdkDiscoveryService.NormalizePath(cargoHome);
            if (normalizedCommandPath.StartsWith(normalizedCargoHome + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMountableRustupDirectory(IReadOnlyList<string> mountablePrefixes) =>
        mountablePrefixes.Any(IsRustupDirectory);

    private static bool IsRustupDirectory(string normalizedPath) =>
        normalizedPath.EndsWith(RustupSuffixDot, StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.EndsWith(RustupSuffixPlain, StringComparison.OrdinalIgnoreCase);
}
