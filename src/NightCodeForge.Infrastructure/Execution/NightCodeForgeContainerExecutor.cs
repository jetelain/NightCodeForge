using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Execution;

/// <summary>
/// Represents a Docker volume bind mount mapping a host directory to a container path.
/// </summary>
public class ContainerVolumeMount
{
    public string HostPath { get; set; } = string.Empty;
    public string ContainerPath { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }

    public ContainerVolumeMount() { }

    public ContainerVolumeMount(string hostPath, string containerPath, bool isReadOnly = false)
    {
        HostPath = hostPath;
        ContainerPath = containerPath;
        IsReadOnly = isReadOnly;
    }

    public string ToDockerArgument() =>
        IsReadOnly ? $"-v \"{HostPath}:{ContainerPath}:ro\"" : $"-v \"{HostPath}:{ContainerPath}\"";

    public override string ToString() =>
        IsReadOnly ? $"{HostPath}:{ContainerPath}:ro" : $"{HostPath}:{ContainerPath}";
}

public class ContainerExecutorOptions
{
    public const string DefaultAgentBaseImage = "ncf-agent-base:latest";

    public const string DefaultAgentBaseDockerfile = """
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    ripgrep \
    curl \
    ca-certificates \
    jq \
    procps \
    build-essential \
    unzip \
    tar \
    gzip \
    python3 \
    python3-venv \
    python3-pip \
    && rm -rf /var/lib/apt/lists/*
""";

    public string ContainerNameOrId { get; set; } = "ncf-sandbox";
    public string? ContainerImage { get; set; } = DefaultAgentBaseImage;
    public string ShellExecutable { get; set; } = "docker";
    public string DefaultWorkingDirectory { get; set; } = "/workspace";
    public int DefaultTimeoutSeconds { get; set; } = 300;
    public int MaxBufferBytes { get; set; } = HeadTailBuffer.DefaultMaxBytes;
    public bool IsRestrictedReadOnly { get; set; } = false;
    public Func<Process>? CustomProcessFactory { get; set; }

    /// <summary>
    /// If true, container operations (e.g. docker exec) are invoked via wsl.exe into a WSL2 environment.
    /// </summary>
    public bool UseWsl { get; set; } = false;

    /// <summary>
    /// Optional WSL2 distribution name to target (e.g. "Ubuntu", "docker-desktop"). If null, uses the default WSL distribution.
    /// </summary>
    public string? WslDistribution { get; set; }

    /// <summary>
    /// Path or name of the WSL2 executable on Windows (defaults to "wsl.exe").
    /// </summary>
    public string WslExecutable { get; set; } = "wsl.exe";

    /// <summary>
    /// Optional targeted WSL2 user name (used for path expansion of ~ in WSL2).
    /// </summary>
    public string? WslUserName { get; set; }

    /// <summary>
    /// Host path to the workspace clone (~/.nightcodeforge/wks/{workspaceId}) mapped to /workspace.
    /// </summary>
    public string? HostWorkspacePath { get; set; }

    /// <summary>
    /// Host path to the canonical clone (~/.nightcodeforge/wks/{workspaceId}/clone) mapped to /clone.
    /// When specified alongside HostWorkspacePath (which points to a task worktree), the task worktree
    /// is mounted at /workspace and the canonical clone is mounted at /clone:ro with .git at /clone/.git:rw.
    /// </summary>
    public string? CanonicalClonePath { get; set; }

    /// <summary>
    /// Virtual container path where the workspace is mounted (defaults to "/workspace").
    /// </summary>
    public string ContainerWorkspacePath { get; set; } = "/workspace";

    /// <summary>
    /// If true, host package manager caches (NuGet, npm, pnpm, uv, Maven) are bind-mounted into the container.
    /// </summary>
    public bool MountPackageCaches { get; set; } = true;

    /// <summary>
    /// Host path to NuGet global packages cache (defaults to "~/.nuget/packages").
    /// </summary>
    public string? NugetPackageCachePath { get; set; } = "~/.nuget/packages";

    /// <summary>
    /// Host path to npm cache (defaults to "~/.npm").
    /// </summary>
    public string? NpmCachePath { get; set; } = "~/.npm";

    /// <summary>
    /// Host path to pnpm global store (defaults to "~/.local/share/pnpm/store").
    /// </summary>
    public string? PnpmCachePath { get; set; } = "~/.local/share/pnpm/store";

    /// <summary>
    /// Host path to uv / Python cache (defaults to "~/.cache/uv").
    /// </summary>
    public string? UvCachePath { get; set; } = "~/.cache/uv";

    /// <summary>
    /// Host path to Maven / Gradle repository cache (defaults to "~/.m2/repository").
    /// </summary>
    public string? MavenCachePath { get; set; } = "~/.m2/repository";

    /// <summary>
    /// If true, host toolchain SDK directories are mounted read-only into the container.
    /// </summary>
    public bool MountHostSdks { get; set; } = true;

    /// <summary>
    /// <summary>
    /// Default list of host SDK directories mounted read-only (:ro) into the container.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSdkMountPaths = new[]
    {
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/jvm",
        "/opt/android-sdk",
        "/etc/ssl/certs"
    };

    /// <summary>
    /// List of host SDK directories mounted read-only (:ro) into the container.
    /// Defaults to .NET (/usr/share/dotnet, /usr/lib/dotnet), JVM (/usr/lib/jvm), and Android SDK (/opt/android-sdk).
    /// </summary>
    public IList<string> SdkMountPaths { get; set; } = new List<string>(DefaultSdkMountPaths);

    /// <summary>
    /// Additional custom volume mounts to include when starting the container.
    /// </summary>
    public IList<ContainerVolumeMount> AdditionalVolumeMounts { get; set; } = new List<ContainerVolumeMount>();

    /// <summary>
    /// Additional directories to prepend to the container PATH environment variable.
    /// </summary>
    public IList<string> ExtraPathDirectories { get; set; } = new List<string>();

    /// <summary>
    /// Whether to automatically expand '~' in mount host paths to the user's home directory.
    /// </summary>
    public bool ExpandTildeInMounts { get; set; } = false;

    /// <summary>
    /// If true, NightCodeForgeContainerExecutor checks if the container is running and starts it via docker run if not.
    /// </summary>
    public bool AutoStartContainer { get; set; } = false;

    /// <summary>
    /// When true and workspace is not restricted read-only, adds layered mounts for the canonical
    /// clone (/workspace/clone as read-only, and /workspace/clone/.git as read-write) to protect
    /// the canonical repository working tree from inadvertent modification by container processes.
    /// </summary>
    public bool ProtectCloneWorkingTree { get; set; } = true;
}

/// <summary>
/// Persistent REPL execution environment inside a container or interactive shell using cryptographic sentinels.
/// </summary>
public partial class NightCodeForgeContainerExecutor : IExecutionEnvironment, IAsyncDisposable, IDisposable
{
    [GeneratedRegex(@"^\s*(-?\d+)")]
    private static partial Regex SentinelExitCodeRegex();

    private readonly ContainerExecutorOptions _options;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<NightCodeForgeContainerExecutor> _logger;
    private readonly SemaphoreSlim _execLock = new(1, 1);

    public ContainerExecutorOptions Options => _options;

    private IProcessStreamSession? _shellSession;
    private Process? _shellProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private bool _isDisposed;

    public ExecutionTier Tier => ExecutionTier.ContainerSandbox;

    public NightCodeForgeContainerExecutor(
        ContainerExecutorOptions? options,
        ILogger<NightCodeForgeContainerExecutor>? logger)
        : this(options, null, logger)
    {
    }

    public NightCodeForgeContainerExecutor(
        ContainerExecutorOptions? options = null,
        IProcessRunner? processRunner = null,
        ILogger<NightCodeForgeContainerExecutor>? logger = null)
    {
        _options = options ?? new ContainerExecutorOptions();
        _processRunner = processRunner ?? new SystemProcessRunner();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NightCodeForgeContainerExecutor>.Instance;
    }

    public async Task<CommandResult> ExecuteAsync(
        string command,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        CancellationToken cancellationToken = default)
    {
        await _execLock.WaitAsync(cancellationToken);
        try
        {
            EnsureShellProcessRunning();

            var timeout = timeoutSeconds ?? _options.DefaultTimeoutSeconds;
            var sentinel = $"__NCF_SENTINEL_{Guid.NewGuid():N}__";
            var workDir = ResolveContainerWorkingDirectory(workingDirectory, _options);
            var stopwatch = Stopwatch.StartNew();

            // Export computed PATH ensuring toolchains and standard utilities are smoothly accessible
            var computedPath = ComputePathEnvironmentVariable(_options);
            var hasCustomPath = environmentVariables != null && environmentVariables.ContainsKey("PATH");
            if (!hasCustomPath)
            {
                await _stdin!.WriteLineAsync($"export PATH=\"{computedPath}:$PATH\"");
            }
            await _stdin!.WriteLineAsync("export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1");
            await _stdin!.WriteLineAsync("git config --global --add safe.directory '*' 2>/dev/null || true");

            // Set environment variables if any (scrub secrets if RestrictedReadOnly)
            if (environmentVariables != null && environmentVariables.Count > 0)
            {
                foreach (var (k, v) in environmentVariables)
                {
                    if (_options.IsRestrictedReadOnly && IsSensitiveEnvVar(k))
                    {
                        continue;
                    }
                    await _stdin!.WriteLineAsync($"export {k}=\"{v.Replace("\"", "\\\"")}\"");
                }
            }

            if (_options.IsRestrictedReadOnly)
            {
                await _stdin!.WriteLineAsync("export HTTP_PROXY=\"\" HTTPS_PROXY=\"\" ALL_PROXY=\"\"");
            }

            var cleanCommand = command.Replace("\r", "");
            await _stdin!.WriteLineAsync($"cd \"{workDir}\"; ( {cleanCommand}\n) < /dev/null");
            await _stdin.WriteLineAsync($"echo \"{sentinel} $?\"");
            await _stdin.FlushAsync();

            try
            {
                var (output, exitCode) = await ReadUntilSentinelAsync(sentinel, timeout, cancellationToken);
                stopwatch.Stop();
                return new CommandResult(exitCode, output.GetFormattedString(), string.Empty, stopwatch.Elapsed, output.IsTruncated);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Command execution timed out after {Timeout}s. Sending SIGINT (\\x03)", timeout);
                
                // 1. Send SIGINT (Ctrl+C)
                await _stdin!.WriteAsync('\x03');
                await _stdin.FlushAsync();

                try
                {
                    // 2. Wait 2-second grace period for shell to settle and emit sentinel
                    var (graceOutput, exitCode) = await ReadUntilSentinelAsync(sentinel, 2, cancellationToken);
                    stopwatch.Stop();
                    return new CommandResult(exitCode, graceOutput.GetFormattedString(), "Command timed out and was aborted with SIGINT.", stopwatch.Elapsed, graceOutput.IsTruncated);
                }
                catch (Exception)
                {
                    // 3. Unresponsive: forcefully restart shell process
                    _logger.LogError("Container shell process unresponsive after SIGINT grace period. Forcefully restarting shell.");
                    ForceRestartShellProcess();
                    stopwatch.Stop();
                    return new CommandResult(124, string.Empty, $"Command timed out after {timeout}s and was terminated.", stopwatch.Elapsed, false);
                }
            }
        }
        finally
        {
            _execLock.Release();
        }
    }

    private async Task<(HeadTailBuffer Output, int ExitCode)> ReadUntilSentinelAsync(
        string sentinel,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var buffer = new HeadTailBuffer(_options.MaxBufferBytes);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}' after {timeoutSeconds}s.");
            }

            var readLineTask = _stdout!.ReadLineAsync(linkedCts.Token).AsTask();
            var delayTask = Task.Delay(remaining, linkedCts.Token);

            var completedTask = await Task.WhenAny(readLineTask, delayTask);
            if (completedTask == delayTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}' after {timeoutSeconds}s.");
            }

            var line = await readLineTask;

            if (line == null)
            {
                throw new InvalidOperationException("Container shell process terminated unexpectedly (stdout closed).");
            }

            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith(sentinel, StringComparison.Ordinal))
            {
                var suffix = trimmedLine[sentinel.Length..];
                var match = SentinelExitCodeRegex().Match(suffix);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var exitCode))
                {
                    linkedCts.Cancel();
                    return (buffer, exitCode);
                }
            }

            buffer.Append(line + "\n");
        }
    }

    private void EnsureShellProcessRunning()
    {
        if (_shellSession != null && !_shellSession.HasExited)
        {
            return;
        }

        if (_shellProcess != null && !_shellProcess.HasExited)
        {
            return;
        }

        EnsureContainerRunning();
        StartShellProcess();
    }

    /// <summary>
    /// Checks if the container is running and spawns/starts it if necessary using configured volume mappings.
    /// </summary>
    public void EnsureContainerRunning()
    {
        if (!_options.AutoStartContainer || _options.CustomProcessFactory != null)
        {
            return;
        }

        try
        {
            EnsureImageReady();

            var inspectCmd = $"inspect -f \"{{{{.State.Running}}}}\" {_options.ContainerNameOrId}";
            var (inspectExe, inspectArgs) = BuildDockerCommand(
                inspectCmd,
                _options.UseWsl,
                _options.WslDistribution,
                _options.ShellExecutable,
                _options.WslExecutable);

            var inspectResult = _processRunner.Run(inspectExe, inspectArgs, timeoutSeconds: 5);
            var stdout = inspectResult.StandardOutput.Trim();
            if (inspectResult.ExitCode == 0 && stdout.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(_options.HostWorkspacePath))
                {
                    var expectedHostWks = NormalizeMountSourcePath(ResolveHostPath(_options.HostWorkspacePath, _options));
                    var mountInspectCmd = $"inspect -f \"{{{{range .Mounts}}}}{{{{.Destination}}}}={{{{.Source}}}};{{{{end}}}}\" {_options.ContainerNameOrId}";
                    var (mountExe, mountArgs) = BuildDockerCommand(
                        mountInspectCmd,
                        _options.UseWsl,
                        _options.WslDistribution,
                        _options.ShellExecutable,
                        _options.WslExecutable);
                    var mountResult = _processRunner.Run(mountExe, mountArgs, timeoutSeconds: 5);
                    string currentSource = string.Empty;
                    bool currentHasClone = false;
                    if (mountResult.ExitCode == 0)
                    {
                        var mountsStr = mountResult.StandardOutput.Trim();
                        foreach (var entry in mountsStr.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = entry.Split('=', 2);
                            if (parts.Length == 2)
                            {
                                if (string.Equals(parts[0].Trim(), _options.ContainerWorkspacePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    currentSource = NormalizeMountSourcePath(parts[1].Trim());
                                }
                                if (string.Equals(parts[0].Trim(), "/clone", StringComparison.OrdinalIgnoreCase))
                                {
                                    currentHasClone = true;
                                }
                            }
                        }
                    }

                    var expectedMounts = BuildVolumeMounts(_options);
                    var expectedHasClone = expectedMounts.Any(m => string.Equals(m.ContainerPath, "/clone", StringComparison.OrdinalIgnoreCase));

                    if ((!string.IsNullOrWhiteSpace(currentSource) && !string.Equals(currentSource, expectedHostWks, StringComparison.OrdinalIgnoreCase)) ||
                        (currentHasClone != expectedHasClone))
                    {
                        _logger.LogInformation("Recreating container {Container} because mounts do not match expected configuration (source: '{Current}' vs '{Expected}', cloneMount: {CurrentClone} vs {ExpectedClone})",
                            _options.ContainerNameOrId, currentSource, expectedHostWks, currentHasClone, expectedHasClone);
                        var (rmExe, rmArgs) = BuildDockerCommand(
                            $"rm -f {_options.ContainerNameOrId}",
                            _options.UseWsl,
                            _options.WslDistribution,
                            _options.ShellExecutable,
                            _options.WslExecutable);
                        _processRunner.Run(rmExe, rmArgs, timeoutSeconds: 10);
                    }
                    else
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            if (inspectResult.ExitCode == 0 && stdout.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Starting stopped container {Container}", _options.ContainerNameOrId);
                var (startExe, startArgs) = BuildDockerCommand(
                    $"start {_options.ContainerNameOrId}",
                    _options.UseWsl,
                    _options.WslDistribution,
                    _options.ShellExecutable,
                    _options.WslExecutable);
                var startResult = _processRunner.Run(startExe, startArgs, timeoutSeconds: 15);
                if (startResult.ExitCode != 0)
                {
                    _logger.LogError("Failed to start stopped container {Container} (exit code {ExitCode}): {Error}",
                        _options.ContainerNameOrId, startResult.ExitCode, startResult.StandardError);
                    throw new InvalidOperationException($"Failed to start container '{_options.ContainerNameOrId}': {startResult.StandardError}");
                }
                return;
            }

            _logger.LogInformation("Spawning container {Container} with volume mounts", _options.ContainerNameOrId);
            var runSpec = BuildDockerRunProcessSpec(_options);
            var runResult = _processRunner.Run(runSpec);
            if (runResult.ExitCode != 0)
            {
                _logger.LogError("Failed to spawn container {Container} (exit code {ExitCode}): {Error}",
                    _options.ContainerNameOrId, runResult.ExitCode, runResult.StandardError);
                throw new InvalidOperationException(
                    $"Failed to spawn container '{_options.ContainerNameOrId}' (exit code {runResult.ExitCode}): {runResult.StandardError}");
            }
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement Run - ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure container {Container} is running.", _options.ContainerNameOrId);
            throw;
        }
    }

    private static string NormalizeMountSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (norm.StartsWith("/host_mnt/", StringComparison.OrdinalIgnoreCase))
        {
            norm = "/mnt/" + norm["/host_mnt/".Length..];
        }
        else if (norm.StartsWith("/run/desktop/mnt/host/", StringComparison.OrdinalIgnoreCase))
        {
            norm = "/mnt/" + norm["/run/desktop/mnt/host/".Length..];
        }
        return norm;
    }

    /// <summary>
    /// Ensures that the local agent base container image exists and is fresh (&lt; 30 days old).
    /// If absent or expired, generates it locally via docker build using the embedded DefaultAgentBaseDockerfile.
    /// </summary>
    public void EnsureImageReady()
    {
        var image = !string.IsNullOrWhiteSpace(_options.ContainerImage)
            ? _options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;

        bool isManagedBaseImage =
            image.StartsWith("ncf-agent-base", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase);

        if (!isManagedBaseImage)
        {
            return;
        }

        try
        {
            if (IsImageFresh(_processRunner, image, _options, TimeSpan.FromDays(30)))
            {
                return;
            }

            // If the alternate image exists and is fresh, tag it
            var altImage = image.StartsWith("nightcodeforge-agent", StringComparison.OrdinalIgnoreCase)
                ? "ncf-agent-base:latest"
                : "nightcodeforge-agent:latest";

            if (IsImageFresh(_processRunner, altImage, _options, TimeSpan.FromDays(30)))
            {
                var (tagExe, tagArgs) = BuildDockerCommand(
                    $"tag {altImage} {image}",
                    _options.UseWsl,
                    _options.WslDistribution,
                    _options.ShellExecutable,
                    _options.WslExecutable);
                var tagResult = _processRunner.Run(tagExe, tagArgs, timeoutSeconds: 10);
                if (tagResult.ExitCode == 0)
                {
                    _logger.LogInformation("Tagged existing image {AltImage} as {Image}", altImage, image);
                    return;
                }
            }

            BuildLocalAgentBaseImage(_processRunner, image, _options, _logger);
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement Run - ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure container image {Image} is ready; attempting to proceed", image);
        }
    }

    /// <summary>
    /// Checks if a container image exists locally and was created less than maxAge ago.
    /// </summary>
    public static bool IsImageFresh(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        TimeSpan maxAge)
    {
        try
        {
            var inspectCmd = $"image inspect -f \"{{{{.Created}}}}\" {imageName}";
            var (inspectExe, inspectArgs) = BuildDockerCommand(
                inspectCmd,
                options.UseWsl,
                options.WslDistribution,
                options.ShellExecutable,
                options.WslExecutable);

            var inspectResult = processRunner.Run(inspectExe, inspectArgs, timeoutSeconds: 5);
            if (inspectResult.ExitCode == 0)
            {
                var createdStr = inspectResult.StandardOutput.Trim();
                if (DateTime.TryParse(createdStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var createdDate))
                {
                    var age = DateTime.UtcNow - createdDate.ToUniversalTime();
                    return age >= TimeSpan.Zero && age < maxAge;
                }
            }
        }
        catch
        {
            // Fall through to false if inspect fails
        }

        return false;
    }

    /// <summary>
    /// Builds the local agent base image containing git, ripgrep, python3, and essential tools using the embedded Dockerfile.
    /// </summary>
    public static void BuildLocalAgentBaseImage(
        IProcessRunner processRunner,
        string imageName,
        ContainerExecutorOptions options,
        ILogger logger)
    {
        string? tempDir = null;
        try
        {
            tempDir = Path.Combine(Path.GetTempPath(), $"ncf-build-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var dockerfilePath = Path.Combine(tempDir, "Dockerfile");
            File.WriteAllText(dockerfilePath, ContainerExecutorOptions.DefaultAgentBaseDockerfile.Replace("\r\n", "\n"));

            var buildContext = options.UseWsl
                ? Git.WslPathConverter.ToWslPath(tempDir)
                : tempDir;

            var buildCmd = $"build -t {imageName} \"{buildContext}\"";
            var (buildExe, buildArgs) = BuildDockerCommand(
                buildCmd,
                options.UseWsl,
                options.WslDistribution,
                options.ShellExecutable,
                options.WslExecutable);

            logger.LogInformation("Building local base container image {Image} with git, ripgrep, and python3...", imageName);
            var buildResult = processRunner.Run(buildExe, buildArgs, timeoutSeconds: 600);

            if (buildResult.ExitCode != 0)
            {
                logger.LogWarning("Failed to build local base image {Image} (exit code {ExitCode}): {Error}",
                    imageName, buildResult.ExitCode, buildResult.StandardError);
            }
            else
            {
                logger.LogInformation("Successfully built local base image {Image}.", imageName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception while building local base container image {Image}", imageName);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (Exception deleteEx)
                {
                    logger.LogWarning(deleteEx, "Failed to clean up temporary container build directory '{TempDir}'", tempDir);
                }
            }
        }
    }

    private void StartShellProcess()
    {
        if (_options.CustomProcessFactory != null)
        {
            _shellProcess = _options.CustomProcessFactory();
            _shellProcess.Start();
            _stdin = new StreamWriter(_shellProcess.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _stdout = _shellProcess.StandardOutput;
            return;
        }

        var spec = BuildProcessStartSpec(_options);
        _shellSession = _processRunner.StartSession(spec);
        _stdin = _shellSession.StandardInput;
        _stdin.NewLine = "\n";
        _stdout = _shellSession.StandardOutput;
    }

    /// <summary>
    /// Builds the structured ProcessStartSpec with ArgumentList avoiding escaping pitfalls for container or WSL execution.
    /// </summary>
    public static ProcessStartSpec BuildProcessStartSpec(ContainerExecutorOptions options)
    {
        var dockerArgs = new List<string> { "exec", "-i" };
        if (options.IsRestrictedReadOnly)
        {
            dockerArgs.AddRange(["--user", "1000:1000"]);
        }
        dockerArgs.Add(options.ContainerNameOrId);
        dockerArgs.AddRange(["bash", "--noprofile", "--norc"]);

        if (options.UseWsl)
        {
            var wslArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.AddRange(["-d", options.WslDistribution]);
            }
            wslArgs.AddRange(["--", options.ShellExecutable]);
            wslArgs.AddRange(dockerArgs);

            return new ProcessStartSpec
            {
                FileName = options.WslExecutable,
                ArgumentList = wslArgs,
                TimeoutSeconds = options.DefaultTimeoutSeconds
            };
        }

        return new ProcessStartSpec
        {
            FileName = options.ShellExecutable,
            ArgumentList = dockerArgs,
            TimeoutSeconds = options.DefaultTimeoutSeconds
        };
    }

    public static ProcessStartInfo BuildProcessStartInfo(ContainerExecutorOptions options)
    {
        var userArg = options.IsRestrictedReadOnly ? "--user 1000:1000 " : string.Empty;
        var dockerArgs = $"exec -i {userArg}{options.ContainerNameOrId} bash --noprofile --norc";

        if (options.UseWsl)
        {
            var wslArgs = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.Append($"-d \"{options.WslDistribution}\" ");
            }
            wslArgs.Append($"-- {options.ShellExecutable} {dockerArgs}");

            return new ProcessStartInfo
            {
                FileName = options.WslExecutable,
                Arguments = wslArgs.ToString(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = options.ShellExecutable,
            Arguments = dockerArgs,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    /// <summary>
    /// Builds executable and arguments to invoke Docker either directly on the host or inside WSL2 via wsl.exe.
    /// </summary>
    public static (string Executable, string Arguments) BuildDockerCommand(
        string dockerCommand,
        bool useWsl = false,
        string? wslDistribution = null,
        string shellExecutable = "docker",
        string wslExecutable = "wsl.exe")
    {
        if (useWsl)
        {
            var distroArg = !string.IsNullOrWhiteSpace(wslDistribution)
                ? $"-d \"{wslDistribution}\" "
                : string.Empty;
            return (wslExecutable, $"{distroArg}-- {shellExecutable} {dockerCommand}".Trim());
        }

        return (shellExecutable, dockerCommand);
    }

    /// <summary>
    /// Builds the list of volume bind mounts (workspace, SDKs, package caches, custom) according to options.
    /// </summary>
    public static IReadOnlyList<ContainerVolumeMount> BuildVolumeMounts(ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mounts = new List<ContainerVolumeMount>();

        // 1. Workspace mount: HostWorkspacePath -> ContainerWorkspacePath
        if (!string.IsNullOrWhiteSpace(options.HostWorkspacePath))
        {
            var hostWks = ResolveHostPath(options.HostWorkspacePath, options);
            var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
                ? options.ContainerWorkspacePath
                : "/workspace";
            mounts.Add(new ContainerVolumeMount(hostWks, containerWks, isReadOnly: options.IsRestrictedReadOnly));

            // Layered protection and isolated task worktree support:
            var normHost = hostWks.TrimEnd('/', '\\');
            var normContainer = containerWks.TrimEnd('/', '\\');

            if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath))
            {
                var normClone = ResolveHostPath(options.CanonicalClonePath, options).TrimEnd('/', '\\');
                // Only mount /clone if the clone is NOT already within HostWorkspacePath (e.g. isolated task worktree)
                if (!normClone.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase) &&
                    !normClone.Equals(normHost, StringComparison.OrdinalIgnoreCase))
                {
                    mounts.Add(new ContainerVolumeMount(normClone, "/clone", isReadOnly: true));
                    mounts.Add(new ContainerVolumeMount($"{normClone}/.git", "/clone/.git", isReadOnly: options.IsRestrictedReadOnly));
                }
            }
            else if (options.ProtectCloneWorkingTree && !options.IsRestrictedReadOnly)
            {
                mounts.Add(new ContainerVolumeMount($"{normHost}/clone", $"{normContainer}/clone", isReadOnly: true));
                mounts.Add(new ContainerVolumeMount($"{normHost}/clone/.git", $"{normContainer}/clone/.git", isReadOnly: false));
            }
        }

        // 2. Package caches (NuGet, npm, pnpm, uv, Maven)
        if (options.MountPackageCaches)
        {
            if (!string.IsNullOrWhiteSpace(options.NugetPackageCachePath))
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(options.NugetPackageCachePath, options),
                    "/root/.nuget/packages",
                    isReadOnly: false));
            }

            if (!string.IsNullOrWhiteSpace(options.NpmCachePath))
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(options.NpmCachePath, options),
                    "/root/.npm",
                    isReadOnly: false));
            }

            if (!string.IsNullOrWhiteSpace(options.PnpmCachePath))
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(options.PnpmCachePath, options),
                    "/root/.local/share/pnpm/store",
                    isReadOnly: false));
            }

            if (!string.IsNullOrWhiteSpace(options.UvCachePath))
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(options.UvCachePath, options),
                    "/root/.cache/uv",
                    isReadOnly: false));
            }

            if (!string.IsNullOrWhiteSpace(options.MavenCachePath))
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(options.MavenCachePath, options),
                    "/root/.m2/repository",
                    isReadOnly: false));
            }
        }

        // 3. Heavy SDK mounts (Strategy A: Read-Only Toolchain Mounts)
        if (options.MountHostSdks && options.SdkMountPaths != null)
        {
            foreach (var sdkPath in options.SdkMountPaths)
            {
                if (!string.IsNullOrWhiteSpace(sdkPath))
                {
                    mounts.Add(new ContainerVolumeMount(
                        ResolveHostPath(sdkPath, options),
                        sdkPath,
                        isReadOnly: true));
                }
            }
        }

        // 4. Additional custom volume mounts
        if (options.AdditionalVolumeMounts != null)
        {
            foreach (var customMount in options.AdditionalVolumeMounts)
            {
                mounts.Add(new ContainerVolumeMount(
                    ResolveHostPath(customMount.HostPath, options),
                    customMount.ContainerPath,
                    isReadOnly: customMount.IsReadOnly));
            }
        }

        return mounts;
    }

    /// <summary>
    /// Resolves '~' or relative host paths for volume mounting.
    /// </summary>
    public static string ResolveHostPath(string path, ContainerExecutorOptions options)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (options.UseWsl)
        {
            if (path.Contains(":\\") || path.Contains(":/"))
            {
                return Git.WslPathConverter.ToWslPath(path);
            }

            if (path.StartsWith('\\') || path.Contains('\\'))
            {
                var norm = path.Replace('\\', '/');
                if (path.StartsWith('\\') && !norm.StartsWith('/'))
                {
                    norm = "/" + norm;
                }
                return norm;
            }
        }

        if (options.ExpandTildeInMounts && (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\")))
        {
            string homeDir;
            if (options.UseWsl && !string.IsNullOrWhiteSpace(options.WslUserName))
            {
                homeDir = $"/home/{options.WslUserName}";
            }
            else
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                homeDir = !string.IsNullOrWhiteSpace(profile) ? profile.Replace('\\', '/') : "~";
            }

            var subPath = path.Length > 1 ? path[1..].TrimStart('/', '\\') : string.Empty;
            return string.IsNullOrEmpty(subPath) ? homeDir : $"{homeDir}/{subPath}";
        }

        return path;
    }

    /// <summary>
    /// Remaps a working directory path to the virtual container filesystem (/workspace).
    /// </summary>
    public static string ResolveContainerWorkingDirectory(string? workingDirectory, ContainerExecutorOptions options)
    {
        var defaultDir = !string.IsNullOrWhiteSpace(options.DefaultWorkingDirectory)
            ? options.DefaultWorkingDirectory
            : (!string.IsNullOrWhiteSpace(options.ContainerWorkspacePath) ? options.ContainerWorkspacePath : "/workspace");

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return defaultDir;
        }

        var targetDir = workingDirectory.Trim();
        if (targetDir == "/workspace" || string.Equals(targetDir, options.ContainerWorkspacePath, StringComparison.OrdinalIgnoreCase))
        {
            return defaultDir;
        }

        if (targetDir == "." || targetDir == "./")
        {
            return defaultDir;
        }

        if (targetDir.Equals("/clone", StringComparison.OrdinalIgnoreCase))
        {
            return defaultDir;
        }

        if (targetDir.StartsWith("/clone/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = targetDir[7..].TrimStart('/');
            return string.IsNullOrEmpty(rel) ? defaultDir : $"{defaultDir}/{rel}";
        }

        if (targetDir.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            return targetDir;
        }

        if (!Path.IsPathRooted(targetDir) && !targetDir.StartsWith('/') && !targetDir.StartsWith('\\'))
        {
            var cleanRel = targetDir.Replace('\\', '/').TrimStart('.', '/');
            return string.IsNullOrEmpty(cleanRel) ? defaultDir : $"{defaultDir}/{cleanRel}";
        }

        var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? options.ContainerWorkspacePath
            : "/workspace";

        var normTarget = targetDir.Replace('\\', '/').TrimEnd('/');

        // 1. Check CanonicalClonePath match/subpath
        if (!string.IsNullOrWhiteSpace(options.CanonicalClonePath))
        {
            var normClone = options.CanonicalClonePath.Replace('\\', '/').TrimEnd('/');
            if (normTarget.Equals(normClone, StringComparison.OrdinalIgnoreCase))
            {
                return defaultDir;
            }
            if (normTarget.StartsWith(normClone + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normClone.Length + 1)..];
                return $"{defaultDir}/{rel}";
            }
        }

        // 2. Check HostWorkspacePath match/subpath
        var hostWks = options.HostWorkspacePath;
        if (!string.IsNullOrWhiteSpace(hostWks))
        {
            var normHost = hostWks.Replace('\\', '/').TrimEnd('/');
            var normHostWsl = Git.WslPathConverter.ToWslPath(hostWks).Replace('\\', '/').TrimEnd('/');

            if (normTarget.Equals(normHost, StringComparison.OrdinalIgnoreCase) ||
                normTarget.Equals(normHostWsl, StringComparison.OrdinalIgnoreCase))
            {
                return defaultDir;
            }

            if (normTarget.StartsWith(normHost + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normHost.Length + 1)..];
                return $"{containerWks}/{rel}";
            }

            if (normTarget.StartsWith(normHostWsl + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = normTarget[(normHostWsl.Length + 1)..];
                return $"{containerWks}/{rel}";
            }
        }

        if (targetDir.Contains(":\\") || targetDir.Contains(":/") || targetDir.StartsWith("/mnt/") || targetDir.StartsWith("/home/"))
        {
            return defaultDir;
        }

        return targetDir;
    }

    /// <summary>
    /// Computes the complete PATH environment variable including mounted SDKs, user package tool directories,
    /// and standard Unix utility paths provided by Ubuntu.
    /// </summary>
    public static string ComputePathEnvironmentVariable(ContainerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pathEntries = new List<string>();

        // 1. Extra user-configured directories
        if (options.ExtraPathDirectories != null)
        {
            pathEntries.AddRange(options.ExtraPathDirectories.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        // 2. Mounted SDK toolchain bin directories
        if (options.MountHostSdks && options.SdkMountPaths != null)
        {
            foreach (var sdk in options.SdkMountPaths)
            {
                if (string.IsNullOrWhiteSpace(sdk)) continue;
                if (sdk.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase) || sdk == "/usr/share/dotnet")
                {
                    pathEntries.Add(sdk);
                    pathEntries.Add("/root/.dotnet/tools");
                }
                else if (sdk.Contains("jvm", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add($"{sdk.TrimEnd('/')}/bin");
                    pathEntries.Add($"{sdk.TrimEnd('/')}/default-java/bin");
                }
                else if (sdk.Contains("android", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add($"{sdk.TrimEnd('/')}/platform-tools");
                    pathEntries.Add($"{sdk.TrimEnd('/')}/cmdline-tools/latest/bin");
                }
                else if (sdk.EndsWith("/bin", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add(sdk);
                }
                else if (sdk.Contains("cargo", StringComparison.OrdinalIgnoreCase) ||
                         sdk.Contains("go", StringComparison.OrdinalIgnoreCase) ||
                         sdk.Contains("node", StringComparison.OrdinalIgnoreCase) ||
                         sdk.Contains("nvm", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add($"{sdk.TrimEnd('/')}/bin");
                    pathEntries.Add(sdk);
                }
                else
                {
                    pathEntries.Add(sdk);
                }
            }
        }
        else
        {
            pathEntries.Add("/usr/share/dotnet");
            pathEntries.Add("/root/.dotnet/tools");
        }

        // 3. User package manager & tool paths
        pathEntries.Add("/root/.local/bin");
        pathEntries.Add("/root/.cargo/bin");
        pathEntries.Add("/root/.npm-global/bin");

        // 4. Standard Unix utility directories provided by Ubuntu
        pathEntries.Add("/usr/local/sbin");
        pathEntries.Add("/usr/local/bin");
        pathEntries.Add("/usr/sbin");
        pathEntries.Add("/usr/bin");
        pathEntries.Add("/sbin");
        pathEntries.Add("/bin");

        // Deduplicate while preserving order
        var distinct = new List<string>();
        foreach (var entry in pathEntries)
        {
            var trimmed = entry.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !distinct.Contains(trimmed, StringComparer.Ordinal))
            {
                distinct.Add(trimmed);
            }
        }

        return string.Join(":", distinct);
    }

    /// <summary>
    /// Builds the argument list for a detached container 'docker run' command with volume mappings.
    /// </summary>
    public static List<string> BuildDockerRunArgs(ContainerExecutorOptions options)
    {
        var args = new List<string> { "run", "-d" };

        if (!string.IsNullOrWhiteSpace(options.ContainerNameOrId))
        {
            args.AddRange(["--name", options.ContainerNameOrId]);
        }

        var computedPath = ComputePathEnvironmentVariable(options);
        args.AddRange(["-e", $"PATH={computedPath}"]);
        args.AddRange(["-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1"]);

        var mounts = BuildVolumeMounts(options);
        foreach (var mount in mounts)
        {
            args.Add("-v");
            args.Add(mount.ToString());
        }

        var workDir = !string.IsNullOrWhiteSpace(options.DefaultWorkingDirectory)
            ? options.DefaultWorkingDirectory
            : options.ContainerWorkspacePath;
        if (!string.IsNullOrWhiteSpace(workDir))
        {
            args.AddRange(["-w", workDir]);
        }

        if (options.IsRestrictedReadOnly)
        {
            args.AddRange(["--network", "none"]);
            args.AddRange(["--read-only"]);
            args.AddRange(["--tmpfs", "/tmp:rw,noexec,nosuid,size=32m"]);
            args.AddRange(["--cap-drop=ALL"]);
            args.AddRange(["--security-opt", "no-new-privileges:true"]);
            args.AddRange(["--user", "1000:1000"]);
        }

        var image = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : ContainerExecutorOptions.DefaultAgentBaseImage;
        args.Add(image);

        args.AddRange(["tail", "-f", "/dev/null"]);
        return args;
    }

    /// <summary>
    /// Builds the structured ProcessStartSpec for 'docker run' with volume mappings.
    /// </summary>
    public static ProcessStartSpec BuildDockerRunProcessSpec(ContainerExecutorOptions options)
    {
        var runArgs = BuildDockerRunArgs(options);

        if (options.UseWsl)
        {
            var wslArgs = new List<string>();
            if (!string.IsNullOrWhiteSpace(options.WslDistribution))
            {
                wslArgs.AddRange(["-d", options.WslDistribution]);
            }
            wslArgs.AddRange(["--", options.ShellExecutable]);
            wslArgs.AddRange(runArgs);

            return new ProcessStartSpec
            {
                FileName = options.WslExecutable,
                ArgumentList = wslArgs,
                TimeoutSeconds = options.DefaultTimeoutSeconds
            };
        }

        return new ProcessStartSpec
        {
            FileName = options.ShellExecutable,
            ArgumentList = runArgs,
            TimeoutSeconds = options.DefaultTimeoutSeconds
        };
    }

    /// <summary>
    /// Builds (Executable, Arguments) command string pair for 'docker run' with volume mappings.
    /// </summary>
    public static (string Executable, string Arguments) BuildDockerRunCommand(ContainerExecutorOptions options)
    {
        var runArgs = BuildDockerRunArgs(options);
        var formattedArgs = string.Join(" ", runArgs.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
        return BuildDockerCommand(formattedArgs, options.UseWsl, options.WslDistribution, options.ShellExecutable, options.WslExecutable);
    }

    /// <summary>
    /// Builds ProcessStartInfo for 'docker run' with volume mappings.
    /// </summary>
    public static ProcessStartInfo BuildDockerRunProcessStartInfo(ContainerExecutorOptions options)
    {
        var (exe, args) = BuildDockerRunCommand(options);
        return new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    public void ForceRestartShellProcess()
    {
        try
        {
            if (_shellSession != null && !_shellSession.HasExited)
            {
                _shellSession.Kill();
            }
            else if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception while killing unresponsive shell process");
        }
        finally
        {
            _shellSession?.Dispose();
            _shellSession = null;
            _shellProcess?.Dispose();
            _shellProcess = null;
            _stdin = null;
            _stdout = null;
        }

        StartShellProcess();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            _stdin?.WriteLine("exit");
            _shellSession?.Kill();
            if (_shellProcess != null && !_shellProcess.HasExited)
            {
                _shellProcess.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort shell process termination during executor disposal finished with notice");
        }

        _shellSession?.Dispose();
        _shellSession = null;
        _shellProcess?.Dispose();
        _shellProcess = null;
        _execLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsSensitiveEnvVar(string key)
    {
        var upper = key.ToUpperInvariant();
        return upper.Contains("KEY") ||
               upper.Contains("TOKEN") ||
               upper.Contains("SECRET") ||
               upper.Contains("PASSWORD") ||
               upper.Contains("AUTH") ||
               upper.Contains("CREDENTIAL");
    }
}
