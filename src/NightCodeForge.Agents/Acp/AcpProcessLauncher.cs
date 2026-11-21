using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Acp;

/// <summary>
/// Resolved process launch specification and session working directory for an ACP harness run.
/// </summary>
public sealed record AcpProcessLaunchPlan(
    ProcessStartSpec ProcessSpec,
    string SessionWorkingDirectory,
    string LocalWorkspaceRoot);

/// <summary>
/// Resolves environment-aware ACP process launch specs across <see cref="ExecutionMode.BareHost"/>,
/// <see cref="ExecutionMode.WslDirect"/>, and <see cref="ExecutionMode.ContainerSandbox"/>.
/// </summary>
public interface IAcpProcessLauncher
{
    Task<AcpProcessLaunchPlan> PrepareLaunchAsync(
        AgentSessionContext context,
        string configuredExecutablePath,
        string configuredArguments,
        string defaultLaunchWorkingDir,
        string defaultSessionWorkingDir,
        string localWorkspaceRoot,
        int timeoutSeconds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class AcpProcessLauncher : IAcpProcessLauncher
{
    public const string DangerouslySkipPermissionsFlag = "--dangerously-skip-permissions";
    private const string DefaultAgyCommand = "agy";
    private const string WslAgyBinaryPath = "~/.local/bin/agy";
    private const string WslExecutableName = "wsl.exe";
    private const string DockerExecutableName = "docker";

    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<AcpProcessLauncher> _logger;

    public AcpProcessLauncher(
        IExecutionEnvironmentProvider envProvider,
        IWorkspacePathResolver pathResolver,
        ILogger<AcpProcessLauncher> logger)
    {
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AcpProcessLaunchPlan> PrepareLaunchAsync(
        AgentSessionContext context,
        string configuredExecutablePath,
        string configuredArguments,
        string defaultLaunchWorkingDir,
        string defaultSessionWorkingDir,
        string localWorkspaceRoot,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var effectiveArgs = IsReadOnlySession(context)
            ? StripSkipPermissionsFlag(configuredArguments)
            : (configuredArguments ?? string.Empty).Trim();

        if (context.Workspace?.ExecutionMode == ExecutionMode.ContainerSandbox)
        {
            return await PrepareContainerLaunchAsync(
                context,
                configuredExecutablePath,
                effectiveArgs,
                defaultLaunchWorkingDir,
                localWorkspaceRoot,
                timeoutSeconds,
                cancellationToken);
        }

        if (context.Workspace?.ExecutionMode == ExecutionMode.WslDirect)
        {
            return PrepareWslDirectLaunch(
                context,
                configuredExecutablePath,
                effectiveArgs,
                defaultLaunchWorkingDir,
                localWorkspaceRoot,
                timeoutSeconds);
        }

        var hostExecutable = ExternalProcessAgentHarnessBase.ExpandHostTildePath(configuredExecutablePath);
        return new AcpProcessLaunchPlan(
            new ProcessStartSpec
            {
                FileName = hostExecutable,
                Arguments = effectiveArgs,
                WorkingDirectory = defaultLaunchWorkingDir,
                TimeoutSeconds = timeoutSeconds
            },
            SessionWorkingDirectory: defaultSessionWorkingDir,
            LocalWorkspaceRoot: localWorkspaceRoot);
    }

    private async Task<AcpProcessLaunchPlan> PrepareContainerLaunchAsync(
        AgentSessionContext context,
        string configuredExecutablePath,
        string effectiveArgs,
        string defaultLaunchWorkingDir,
        string localWorkspaceRoot,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var workspace = context.Workspace!;
        var env = context.SandboxScope != null
            ? await _envProvider.GetEnvironmentAsync(context.SandboxScope, cancellationToken)
            : await _envProvider.GetEnvironmentAsync(workspace, context.Profile.SecurityTier, context.ForgeTask.Id, cancellationToken);

        var containerExe = ResolveContainerExecutable(configuredExecutablePath);
        var argTokens = TokenizeArguments(effectiveArgs);

        if (env is NightCodeForgeContainerExecutor containerExecutor)
        {
            containerExecutor.EnsureContainerRunning();

            var containerTaskWorkDir = !string.IsNullOrWhiteSpace(context.SandboxScope?.DefaultWorkingDirectory)
                ? context.SandboxScope.DefaultWorkingDirectory
                : ContainerMountAndDriftBuilder.ResolveContainerWorkingDirectory(context.WorkspacePath, containerExecutor.Options);

            var computedPath = ContainerMountAndDriftBuilder.ComputePathEnvironmentVariable(containerExecutor.Options);
            var effectiveUser = containerExecutor.Options.IsRestrictedReadOnly
                ? ContainerExecutorOptions.RestrictedReadOnlyUserUidGid
                : containerExecutor.Options.UserUidGid;

            var dockerArgs = BuildDockerExecArgs(
                containerExecutor.Options.ContainerNameOrId,
                effectiveUser,
                computedPath,
                containerTaskWorkDir,
                containerExe,
                argTokens);

            var spec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
                containerExecutor.Options,
                dockerArgs,
                timeoutSeconds,
                workingDirectory: defaultLaunchWorkingDir);

            spec.Arguments = FormatArguments(spec.ArgumentList);

            _logger.LogDebug(
                "Prepared containerized ACP launch inside '{Container}' at '{WorkDir}' executing '{Executable}'",
                containerExecutor.Options.ContainerNameOrId,
                containerTaskWorkDir,
                containerExe);

            return new AcpProcessLaunchPlan(spec, containerTaskWorkDir, localWorkspaceRoot);
        }

        var fallbackWorkDir = !string.IsNullOrWhiteSpace(context.SandboxScope?.DefaultWorkingDirectory)
            ? context.SandboxScope.DefaultWorkingDirectory
            : (context.ForgeTask.Id > 0 ? $"/workspace/task-{context.ForgeTask.Id}" : "/workspace");

        var fallbackContainerName = !string.IsNullOrWhiteSpace(context.SandboxScope?.ContainerName)
            ? context.SandboxScope.ContainerName
            : (!string.IsNullOrWhiteSpace(workspace.ContainerName)
                ? (context.ForgeTask.Id > 0 ? $"{workspace.ContainerName}-task-{context.ForgeTask.Id}" : workspace.ContainerName)
                : (context.ForgeTask.Id > 0 ? $"ncf-sandbox-{workspace.Id}-task-{context.ForgeTask.Id}" : $"ncf-sandbox-{workspace.Id}"));

        var fallbackDockerArgs = BuildDockerExecArgs(
            fallbackContainerName,
            effectiveUser: null,
            computedPath: ContainerExecutorOptions.HostAgyContainerDirectory + ":/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            fallbackWorkDir,
            containerExe,
            argTokens);

        var fallbackOptions = new ContainerExecutorOptions
        {
            ContainerNameOrId = fallbackContainerName,
            ShellExecutable = DockerExecutableName,
            UseWsl = workspace.UseWslForContainers || !string.IsNullOrWhiteSpace(workspace.WslDistribution),
            WslDistribution = workspace.WslDistribution,
            WslExecutable = WslExecutableName
        };

        var fallbackSpec = NightCodeForgeContainerExecutor.BuildDockerProcessSpec(
            fallbackOptions,
            fallbackDockerArgs,
            timeoutSeconds,
            workingDirectory: defaultLaunchWorkingDir);
        fallbackSpec.Arguments = FormatArguments(fallbackSpec.ArgumentList);

        return new AcpProcessLaunchPlan(fallbackSpec, fallbackWorkDir, localWorkspaceRoot);
    }

    private AcpProcessLaunchPlan PrepareWslDirectLaunch(
        AgentSessionContext context,
        string configuredExecutablePath,
        string effectiveArgs,
        string defaultLaunchWorkingDir,
        string localWorkspaceRoot,
        int timeoutSeconds)
    {
        var workspace = context.Workspace!;
        var distro = !string.IsNullOrWhiteSpace(workspace.WslDistribution)
            ? workspace.WslDistribution.Trim()
            : _pathResolver.GetWslDistributionName();

        var wslWorkDir = ExternalProcessAgentHarnessBase.ResolveWslDirectSessionWorkingDirectory(context, _pathResolver);
        var rawWslExe = ResolveWslExecutable(configuredExecutablePath);
        var wslExe = ExternalProcessAgentHarnessBase.ExpandWslTildePath(rawWslExe, _pathResolver.GetWslUserName());

        var argList = new List<string>();
        if (!string.IsNullOrWhiteSpace(distro))
        {
            argList.Add("-d");
            argList.Add(distro);
        }

        if (wslExe.StartsWith("~/", StringComparison.Ordinal))
        {
            var shCommand = string.IsNullOrWhiteSpace(effectiveArgs)
                ? $"exec \"$HOME/{wslExe[2..]}\""
                : $"exec \"$HOME/{wslExe[2..]}\" {effectiveArgs}";
            argList.Add("--");
            argList.Add("sh");
            argList.Add("-c");
            argList.Add(shCommand);
        }
        else
        {
            argList.Add("--");
            argList.Add(wslExe);
            argList.AddRange(TokenizeArguments(effectiveArgs));
        }

        var arguments = FormatArguments(argList);

        return new AcpProcessLaunchPlan(
            new ProcessStartSpec
            {
                FileName = WslExecutableName,
                ArgumentList = argList,
                Arguments = arguments,
                WorkingDirectory = defaultLaunchWorkingDir,
                TimeoutSeconds = timeoutSeconds
            },
            SessionWorkingDirectory: wslWorkDir,
            LocalWorkspaceRoot: localWorkspaceRoot);
    }

    private static string ResolveWslExecutable(string configuredExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(configuredExecutablePath) ||
            string.Equals(configuredExecutablePath, DefaultAgyCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuredExecutablePath, "agy.exe", StringComparison.OrdinalIgnoreCase) ||
            configuredExecutablePath.EndsWith("\\agy.exe", StringComparison.OrdinalIgnoreCase))
        {
            return WslAgyBinaryPath;
        }

        return configuredExecutablePath;
    }

    private static List<string> BuildDockerExecArgs(
        string containerName,
        string? effectiveUser,
        string computedPath,
        string workingDirectory,
        string executablePath,
        IReadOnlyList<string> argTokens)
    {
        var args = new List<string> { "exec", "-i" };
        if (!string.IsNullOrWhiteSpace(effectiveUser))
        {
            args.AddRange(["-u", effectiveUser]);
        }

        args.AddRange(["-e", $"HOME={ContainerExecutorOptions.ContainerHomePath}"]);
        args.AddRange(["-e", $"PATH={computedPath}"]);
        args.AddRange(["-e", $"{ContainerExecutorOptions.VsTestArtifactsBaseDirectoryEnvVar}={ContainerExecutorOptions.ContainerVsTestArtifactsBaseDirectory}"]);
        args.AddRange(["-w", workingDirectory]);
        args.Add(containerName);
        args.Add(executablePath);
        args.AddRange(argTokens);
        return args;
    }

    private static string ResolveContainerExecutable(string configuredExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(configuredExecutablePath) ||
            string.Equals(configuredExecutablePath, DefaultAgyCommand, StringComparison.OrdinalIgnoreCase) ||
            configuredExecutablePath.EndsWith("/agy", StringComparison.OrdinalIgnoreCase) ||
            configuredExecutablePath.EndsWith("\\agy.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerExecutorOptions.HostAgyContainerBinaryPath;
        }

        return configuredExecutablePath;
    }

    public static bool IsReadOnlySession(AgentSessionContext context) =>
        context.SandboxScope?.IsReadOnly == true ||
        context.Profile.Mode == AgentMode.ReadOnly ||
        context.Profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly ||
        context.RequiredMode == AgentMode.ReadOnly;

    private static string StripSkipPermissionsFlag(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        var tokens = TokenizeArguments(arguments)
            .Where(t => !string.Equals(t, DangerouslySkipPermissionsFlag, StringComparison.OrdinalIgnoreCase));
        return string.Join(' ', tokens);
    }

    private static IReadOnlyList<string> TokenizeArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quoteChar = '\0';

        foreach (var ch in arguments)
        {
            if (inQuotes)
            {
                if (ch == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string FormatArguments(IEnumerable<string>? argumentList)
    {
        if (argumentList == null)
        {
            return string.Empty;
        }

        return string.Join(' ', argumentList.Select(arg => arg.Contains(' ') ? $"\"{arg}\"" : arg));
    }
}
