using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Benchmark.Cli.Verification;

/// <summary>
/// Builds and verifies benchmark-scoped custom container images (such as <c>ncf-bench-python-3.11:v1</c>)
/// on demand with per-tag locking and fail-fast error reporting.
/// </summary>
public sealed class BenchmarkCustomImageBuilder : IBenchmarkCustomImageBuilder
{
    private const int ImageInspectTimeoutSeconds = 15;
    private const int ImageBuildTimeoutSeconds = 600;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal const string Python311ChildDockerfile = $"""
FROM {BenchmarkContainerImages.Python311BaseImage}
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    ripgrep \
    git \
    jq \
    procps \
    curl \
    ca-certificates \
    unzip \
    && rm -rf /var/lib/apt/lists/*
""";

    private static readonly IReadOnlyDictionary<string, string> ManagedImageDockerfiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BenchmarkContainerImages.Python311ChildImage] = Python311ChildDockerfile
        };

    private readonly IProcessRunner _processRunner;
    private readonly ExecutionEnvironmentProviderOptions _options;
    private readonly BenchmarkWorkspaceOptions _workspaceOptions;
    private readonly ILogger<BenchmarkCustomImageBuilder> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _imageBuildLocks = new(StringComparer.OrdinalIgnoreCase);

    public BenchmarkCustomImageBuilder(
        IProcessRunner processRunner,
        IOptions<ExecutionEnvironmentProviderOptions> executionOptions,
        IOptions<BenchmarkWorkspaceOptions> workspaceOptions,
        ILogger<BenchmarkCustomImageBuilder> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options = (executionOptions ?? throw new ArgumentNullException(nameof(executionOptions))).Value ?? new ExecutionEnvironmentProviderOptions();
        _workspaceOptions = (workspaceOptions ?? throw new ArgumentNullException(nameof(workspaceOptions))).Value ?? new BenchmarkWorkspaceOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsManagedBenchmarkImage(string? containerImage) =>
        !string.IsNullOrWhiteSpace(containerImage) &&
        ManagedImageDockerfiles.ContainsKey(containerImage.Trim());

    internal static string GetDockerfile(string containerImage)
    {
        if (string.IsNullOrWhiteSpace(containerImage) ||
            !ManagedImageDockerfiles.TryGetValue(containerImage.Trim(), out var dockerfile))
        {
            throw new BenchmarkException($"Container image '{containerImage}' is not a managed benchmark custom image.");
        }

        return dockerfile;
    }

    public async Task EnsureImageAsync(string containerImage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerImage))
        {
            throw new BenchmarkException("Benchmark custom container image name cannot be empty.");
        }

        var normalizedImage = containerImage.Trim();
        var dockerfile = GetDockerfile(normalizedImage);
        var tagLock = _imageBuildLocks.GetOrAdd(normalizedImage, _ => new SemaphoreSlim(1, 1));

        await tagLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dockerOptions = BuildDockerTargetOptions();
            var inspectSpec = ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
                dockerOptions,
                ["image", "inspect", normalizedImage],
                timeoutSeconds: ImageInspectTimeoutSeconds);

            var inspectResult = await RunDockerCommandAsync(inspectSpec, normalizedImage, cancellationToken).ConfigureAwait(false);
            if (inspectResult.ExitCode == 0)
            {
                return;
            }

            string? tempDir = null;
            try
            {
                tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-img-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var dockerfilePath = Path.Combine(tempDir, "Dockerfile");
                await File.WriteAllTextAsync(
                    dockerfilePath,
                    dockerfile.Replace("\r\n", "\n", StringComparison.Ordinal),
                    Utf8NoBom,
                    cancellationToken).ConfigureAwait(false);

                var buildContext = dockerOptions.UseWsl
                    ? WslPathConverter.ToWslPath(tempDir)
                    : tempDir;

                var buildSpec = ContainerMountAndDriftBuilder.BuildDockerProcessSpec(
                    dockerOptions,
                    ["build", "-t", normalizedImage, buildContext],
                    timeoutSeconds: ImageBuildTimeoutSeconds);

                _logger.LogInformation("Building benchmark Python child container image '{Image}'...", normalizedImage);
                var buildResult = await RunDockerCommandAsync(buildSpec, normalizedImage, cancellationToken).ConfigureAwait(false);
                if (buildResult.ExitCode != 0)
                {
                    var detail = !string.IsNullOrWhiteSpace(buildResult.StandardError)
                        ? buildResult.StandardError.Trim()
                        : buildResult.StandardOutput.Trim();
                    throw new BenchmarkException(
                        $"Failed to build benchmark custom image '{normalizedImage}' (exit code {buildResult.ExitCode}): {detail}");
                }

                _logger.LogInformation("Successfully built benchmark custom container image '{Image}'.", normalizedImage);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                {
                    try
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Failed to clean up temporary benchmark image build directory '{TempDir}'.", tempDir);
                    }
                }
            }
        }
        finally
        {
            tagLock.Release();
        }
    }

    private async Task<ProcessExecutionResult> RunDockerCommandAsync(
        ProcessStartSpec spec,
        string normalizedImage,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _processRunner.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BenchmarkException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new BenchmarkException(
                $"Failed to ensure benchmark custom image '{normalizedImage}': {ex.Message}",
                ex);
        }
    }

    private ContainerExecutorOptions BuildDockerTargetOptions()
    {
        if (!string.IsNullOrWhiteSpace(_workspaceOptions.WorkspacesDirectory) &&
            WslPathConverter.TryExtractWslUnc(_workspaceOptions.WorkspacesDirectory.Trim(), out var workspaceWslDistro, out _))
        {
            return new ContainerExecutorOptions
            {
                UseWsl = true,
                WslDistribution = !string.IsNullOrWhiteSpace(workspaceWslDistro)
                    ? workspaceWslDistro
                    : _options.DefaultWslDistribution,
                WslExecutable = _options.WslExecutable
            };
        }

        if (_workspaceOptions.UseWslExt4Workspaces && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new ContainerExecutorOptions
            {
                UseWsl = true,
                WslDistribution = _options.DefaultWslDistribution,
                WslExecutable = _options.WslExecutable
            };
        }

        return new ContainerExecutorOptions
        {
            UseWsl = ShouldUseWslForContainers(),
            WslDistribution = _options.DefaultWslDistribution,
            WslExecutable = _options.WslExecutable
        };
    }

    private bool ShouldUseWslForContainers() =>
        _options.UseWslForContainers ||
        !string.IsNullOrWhiteSpace(_options.DefaultWslDistribution) ||
        (_options.AutoDetectWsl && ShouldAutoUseWslForContainers());

    private static bool ShouldAutoUseWslForContainers()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        var envVar = Environment.GetEnvironmentVariable("NCF_USE_WSL_DOCKER") ??
                     Environment.GetEnvironmentVariable("NCF_USE_WSL");
        if (string.Equals(envVar, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(envVar, "1", StringComparison.Ordinal))
        {
            return true;
        }

        return !IsExecutableOnWindowsPath("docker.exe") &&
               !IsExecutableOnWindowsPath("docker") &&
               (IsExecutableOnWindowsPath("wsl.exe") ||
                File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")));
    }

    private static bool IsExecutableOnWindowsPath(string executable)
    {
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                return false;
            }

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim(), executable);
                if (File.Exists(full))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: malformed PATH entries should not prevent WSL fallback probing.
        }

        return false;
    }
}
