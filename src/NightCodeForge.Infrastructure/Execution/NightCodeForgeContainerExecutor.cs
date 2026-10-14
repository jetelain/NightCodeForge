using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
    public string ContainerNameOrId { get; set; } = "ncf-sandbox";
    public string? ContainerImage { get; set; } = "ubuntu:24.04";
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
    /// List of host SDK directories mounted read-only (:ro) into the container.
    /// Defaults to .NET (/usr/share/dotnet), JVM (/usr/lib/jvm), and Android SDK (/opt/android-sdk).
    /// </summary>
    public IList<string> SdkMountPaths { get; set; } = new List<string>
    {
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
        "/usr/lib/x86_64-linux-gnu",
        "/usr/lib/jvm",
        "/opt/android-sdk",
        "/etc/ssl/certs"
    };

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
            await _stdin!.WriteLineAsync($"cd \"{workDir}\" && {cleanCommand}");
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
                    throw new TimeoutException($"Command timed out after {timeout}s and the container shell was forcefully restarted.");
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        while (!cts.IsCancellationRequested)
        {
            var readLineTask = _stdout!.ReadLineAsync(cts.Token).AsTask();
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
                    return (buffer, exitCode);
                }
            }

            buffer.Append(line + "\n");
        }

        throw new TimeoutException($"Timed out waiting for sentinel '{sentinel}'.");
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
                    var expectedHostWks = ResolveHostPath(_options.HostWorkspacePath, _options).Replace('\\', '/').TrimEnd('/');
                    var mountInspectCmd = $"inspect -f \"{{{{range .Mounts}}}}{{{{if eq .Destination \\\"{_options.ContainerWorkspacePath}\\\"}}}}{{{{.Source}}}}{{{{end}}}}{{{{end}}}}\" {_options.ContainerNameOrId}";
                    var (mountExe, mountArgs) = BuildDockerCommand(
                        mountInspectCmd,
                        _options.UseWsl,
                        _options.WslDistribution,
                        _options.ShellExecutable,
                        _options.WslExecutable);
                    var mountResult = _processRunner.Run(mountExe, mountArgs, timeoutSeconds: 5);
                    var currentSource = mountResult.StandardOutput.Trim().Replace('\\', '/').TrimEnd('/');
                    if (!string.IsNullOrWhiteSpace(currentSource) && !string.Equals(currentSource, expectedHostWks, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("Recreating container {Container} because mounted workspace '{Current}' does not match expected '{Expected}'",
                            _options.ContainerNameOrId, currentSource, expectedHostWks);
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
                _processRunner.Run(startExe, startArgs, timeoutSeconds: 15);
                return;
            }

            _logger.LogInformation("Spawning container {Container} with volume mounts", _options.ContainerNameOrId);
            var runSpec = BuildDockerRunProcessSpec(_options);
            _processRunner.Run(runSpec);
        }
        catch (NotImplementedException)
        {
            // Unit test mock runner did not implement Run - ignore
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure container {Container} is running; attempting to proceed with exec session", _options.ContainerNameOrId);
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

        if (options.UseWsl && (path.Contains(":\\") || path.Contains(":/")))
        {
            return Git.WslPathConverter.ToWslPath(path);
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
        var targetDir = !string.IsNullOrWhiteSpace(workingDirectory)
            ? workingDirectory.Trim()
            : options.DefaultWorkingDirectory;

        if (string.IsNullOrWhiteSpace(targetDir) ||
            targetDir == "/workspace" ||
            targetDir.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(targetDir) ? "/workspace" : targetDir;
        }

        var containerWks = !string.IsNullOrWhiteSpace(options.ContainerWorkspacePath)
            ? options.ContainerWorkspacePath
            : "/workspace";

        var hostWks = options.HostWorkspacePath;
        if (!string.IsNullOrWhiteSpace(hostWks))
        {
            var normTarget = targetDir.Replace('\\', '/').TrimEnd('/');
            var normHost = hostWks.Replace('\\', '/').TrimEnd('/');
            var normHostWsl = Git.WslPathConverter.ToWslPath(hostWks).Replace('\\', '/').TrimEnd('/');

            if (normTarget.Equals(normHost, StringComparison.OrdinalIgnoreCase) ||
                normTarget.Equals(normHostWsl, StringComparison.OrdinalIgnoreCase))
            {
                return containerWks;
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
            return containerWks;
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
                }
                else if (sdk.Contains("android", StringComparison.OrdinalIgnoreCase))
                {
                    pathEntries.Add($"{sdk.TrimEnd('/')}/platform-tools");
                    pathEntries.Add($"{sdk.TrimEnd('/')}/cmdline-tools/latest/bin");
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
            args.AddRange(["--user", "1000:1000"]);
        }

        var image = !string.IsNullOrWhiteSpace(options.ContainerImage)
            ? options.ContainerImage
            : "ubuntu:24.04";
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
        catch { }

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
