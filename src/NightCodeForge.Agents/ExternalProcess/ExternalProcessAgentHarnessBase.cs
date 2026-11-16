using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.ExternalProcess;

/// <summary>
/// Shared lifecycle for harnesses driving an external agent process over stdio: process launch,
/// <c>HarnessConfigurationJson</c> parsing, workspace path resolution, prompt enrichment,
/// activity broadcasting and transcript persistence. Subclasses implement the protocol-specific stdio loop.
/// </summary>
public abstract class ExternalProcessAgentHarnessBase : IAgentHarness
{
    private const int ProcessTimeoutSeconds = 600;
    private const string JsonExecutablePath = "executablePath";
    private const string JsonExecutable = "executable";
    private const string JsonCommand = "command";
    private const string JsonArguments = "arguments";
    private const string JsonArgs = "args";
    private const string ContainerWorkspacePath = "/workspace";

    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly AgentsMdService _agentsMdService;
    private readonly IHarnessActivityRecorder _recorder;

    protected ExternalProcessAgentHarnessBase(
        IHarnessActivityRecorder recorder,
        AgentsMdService agentsMdService,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        ILogger logger)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _agentsMdService = agentsMdService ?? throw new ArgumentNullException(nameof(agentsMdService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public abstract string HarnessId { get; }

    protected ILogger Logger { get; }

    protected IHarnessActivityRecorder Recorder => _recorder;

    /// <summary>Human readable agent name used in launch failure messages (e.g. "ACP agent").</summary>
    protected abstract string AgentDisplayName { get; }

    protected abstract string DefaultExecutablePath { get; }

    protected abstract string DefaultArguments { get; }

    protected abstract Func<Process>? CustomProcessFactory { get; }

    /// <summary>
    /// Executes the protocol-specific message exchange against input and output streams.
    /// Public to allow direct unit testing without spawning external OS processes.
    /// </summary>
    public abstract Task<AgentRunResult> RunSessionStreamAsync(
        StreamWriter stdin,
        StreamReader stdout,
        AgentSessionContext context,
        string sessionId,
        Stopwatch sw,
        CancellationToken cancellationToken = default);

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = Guid.NewGuid().ToString("N");
        await _recorder.BeginConversationAsync(sessionId, HarnessId, context, cancellationToken);
        var stageStartedMetadata = new Dictionary<string, object?>
        {
            [AgentActivityMetadataKeys.HarnessId] = HarnessId,
            [AgentActivityMetadataKeys.ModelName] = context.Profile.ModelName
        };
        await BroadcastAsync(
            sessionId,
            context.ForgeTask.Id,
            context.StageId,
            AgentActivityKind.StageStarted,
            $"Starting stage '{context.StageId}'",
            stageStartedMetadata,
            cancellationToken);

        var customFactory = CustomProcessFactory;
        if (customFactory != null)
        {
            Process process;
            try
            {
                process = customFactory();
                process.Start();
            }
            catch (Exception ex)
            {
                return await FailLaunchAsync(ex, "custom factory", context, sessionId, sw, cancellationToken);
            }

            using var stdin = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            using var stdout = process.StandardOutput;

            try
            {
                return await RunSessionStreamAsync(stdin, stdout, context, sessionId, sw, cancellationToken);
            }
            finally
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "Failed to terminate custom factory process during validation");
                }
                process.Dispose();
            }
        }

        IProcessStreamSession session;
        string launchTarget = DefaultExecutablePath;
        try
        {
            var spec = await CreateProcessStartSpecAsync(context, cancellationToken);
            launchTarget = spec.FileName;
            session = _processRunner.StartSession(spec);
        }
        catch (Exception ex)
        {
            return await FailLaunchAsync(ex, launchTarget, context, sessionId, sw, cancellationToken);
        }

        await using (session.ConfigureAwait(false))
        {
            return await RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, sessionId, sw, cancellationToken);
        }
    }

    protected virtual Task<ProcessStartSpec> CreateProcessStartSpecAsync(AgentSessionContext context, CancellationToken cancellationToken)
    {
        var (exePath, args) = ResolveLaunchCommand(context);
        var workingDir = ResolveLaunchWorkingDirectory(context);

        return Task.FromResult(new ProcessStartSpec
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = workingDir,
            TimeoutSeconds = ProcessTimeoutSeconds
        });
    }

    private async Task<AgentRunResult> FailLaunchAsync(Exception ex, string target, AgentSessionContext context, string sessionId, Stopwatch sw, CancellationToken cancellationToken)
    {
        sw.Stop();
        Logger.LogError(ex, "Failed to launch {AgentName} at {ExecutablePath}", AgentDisplayName, target);
        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
        await SaveTranscriptAsync(
            context,
            ResolveLaunchWorkingDirectory(context),
            string.Empty,
            $"Failed to launch {AgentDisplayName}: {ex.Message}",
            sw.ElapsedMilliseconds,
            0,
            0,
            AgentRunStatus.Failed,
            cancellationToken,
            sessionId);
        return AgentRunResult.Failed($"Failed to launch {AgentDisplayName}: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
    }

    /// <summary>
    /// Resolves executable and arguments from <c>HarnessConfigurationJson</c>, falling back to the harness defaults
    /// when the JSON is empty, malformed, or contains non-string values.
    /// </summary>
    protected (string ExecutablePath, string Arguments) ResolveLaunchCommand(AgentSessionContext context)
    {
        var exePath = DefaultExecutablePath;
        var args = DefaultArguments;

        if (string.IsNullOrWhiteSpace(context.Profile.HarnessConfigurationJson))
        {
            return (exePath, args);
        }

        try
        {
            using var doc = JsonDocument.Parse(context.Profile.HarnessConfigurationJson);
            var root = doc.RootElement;
            if (root.TryGetProperty(JsonExecutablePath, out var exeProp) ||
                root.TryGetProperty(JsonExecutable, out exeProp) ||
                root.TryGetProperty(JsonCommand, out exeProp))
            {
                var val = exeProp.GetString();
                if (!string.IsNullOrWhiteSpace(val)) exePath = val;
            }
            if (root.TryGetProperty(JsonArguments, out var argsProp) ||
                root.TryGetProperty(JsonArgs, out argsProp))
            {
                var val = argsProp.GetString();
                if (val != null) args = val;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to parse HarnessConfigurationJson on AgentProfile '{ProfileName}', using defaults.", context.Profile.Name);
            return (DefaultExecutablePath, DefaultArguments);
        }

        return (exePath, args);
    }

    protected const int DefaultProcessTimeoutSeconds = ProcessTimeoutSeconds;

    protected string ResolveLaunchWorkingDirectory(AgentSessionContext context)
    {
        var workingDir = !string.IsNullOrEmpty(context.WorkspacePath) ? context.WorkspacePath : Directory.GetCurrentDirectory();
        if (IsForeignPath(workingDir))
        {
            if (context.Workspace != null)
            {
                workingDir = _pathResolver.ResolveCruciblePath(context.Workspace.Id, string.Empty, context.ForgeTask.Id);
            }
            else
            {
                workingDir = Directory.GetCurrentDirectory();
            }
        }

        if (!Directory.Exists(workingDir))
        {
            workingDir = !string.IsNullOrEmpty(context.Workspace?.RepositoryPath) && Directory.Exists(context.Workspace.RepositoryPath)
                ? context.Workspace.RepositoryPath
                : Directory.GetCurrentDirectory();
        }

        return workingDir;
    }

    /// <summary>
    /// Resolves the agent-visible workspace path and the local (host) workspace root,
    /// translating Linux/WSL/container style paths on Windows via <see cref="IWorkspacePathResolver"/>.
    /// </summary>
    protected (string WorkspacePath, string LocalWorkspaceRoot) ResolveWorkspacePaths(AgentSessionContext context)
    {
        var workspacePath = !string.IsNullOrEmpty(context.WorkspacePath)
            ? context.WorkspacePath
            : (context.SandboxScope?.DefaultWorkingDirectory ?? context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory());

        var localWorkspaceRoot = workspacePath;
        if (IsForeignPath(localWorkspaceRoot))
        {
            if (context.Workspace != null)
            {
                localWorkspaceRoot = _pathResolver.ResolveCruciblePath(context.Workspace.Id, string.Empty, context.ForgeTask.Id);
            }
            else if (context.SandboxScope != null)
            {
                var resolver = new SandboxPathResolver(context.SandboxScope);
                localWorkspaceRoot = resolver.ResolveBashWorkingDirectory(null, ExecutionTier.BareHost);
            }
            else if (!string.IsNullOrEmpty(context.Workspace?.RepositoryPath))
            {
                localWorkspaceRoot = context.Workspace.RepositoryPath;
            }
        }

        return (workspacePath, localWorkspaceRoot);
    }

    private static bool IsForeignPath(string path)
        => OperatingSystem.IsWindows() && (path.StartsWith('/') || path.StartsWith('~'));

    /// <summary>
    /// Resolves the container-side working directory for <see cref="ExecutionMode.ContainerSandbox"/> runs,
    /// mapping task runs to <c>/workspace/task-{id}</c> via <see cref="SandboxScopeDescriptor.DefaultWorkingDirectory"/>.
    /// </summary>
    protected static string ResolveContainerSessionWorkingDirectory(AgentSessionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.SandboxScope?.DefaultWorkingDirectory))
        {
            return context.SandboxScope.DefaultWorkingDirectory;
        }

        if (!string.IsNullOrWhiteSpace(context.WorkspacePath) &&
            context.WorkspacePath.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            return context.WorkspacePath;
        }

        return context.ForgeTask.Id > 0
            ? $"{ContainerWorkspacePath}/task-{context.ForgeTask.Id}"
            : ContainerWorkspacePath;
    }

    /// <summary>
    /// Expands a leading tilde (<c>~</c>, <c>~/...</c>, or <c>~\...</c>) using the current host user's profile folder.
    /// Bare command names (such as <c>agy</c>) and already-rooted paths are returned unchanged.
    /// </summary>
    public static string ExpandHostTildePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('~'))
        {
            return path;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return path;
        }

        if (string.Equals(path, "~", StringComparison.Ordinal))
        {
            return userProfile;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return Path.Combine(userProfile, path[2..]);
        }

        return path;
    }

    /// <summary>
    /// Expands a leading POSIX tilde (<c>~</c> or <c>~/...</c>) into the WSL user's home directory
    /// (<c>/home/&lt;user&gt;/...</c> or <c>/root/...</c>) when <paramref name="wslUserName"/> is known,
    /// because direct process spawns without a shell and Go <c>os.Chdir</c> do not expand <c>~</c>.
    /// </summary>
    public static string ExpandWslTildePath(string path, string? wslUserName)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('~') || string.IsNullOrWhiteSpace(wslUserName))
        {
            return path;
        }

        var trimmedUser = wslUserName.Trim();
        var homeDir = string.Equals(trimmedUser, "root", StringComparison.Ordinal)
            ? "/root"
            : $"/home/{trimmedUser}";

        if (string.Equals(path, "~", StringComparison.Ordinal))
        {
            return homeDir;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return $"{homeDir}/{path[2..]}";
        }

        return path;
    }

    /// <summary>
    /// Resolves the WSL2-side working directory for <see cref="ExecutionMode.WslDirect"/> runs.
    /// </summary>
    public static string ResolveWslDirectSessionWorkingDirectory(AgentSessionContext context, IWorkspacePathResolver pathResolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pathResolver);

        string resolved;
        if (!string.IsNullOrWhiteSpace(context.WorkspacePath) &&
            (context.WorkspacePath.StartsWith('/') || context.WorkspacePath.StartsWith('~')))
        {
            resolved = context.WorkspacePath;
        }
        else if (context.SandboxScope != null)
        {
            resolved = new SandboxPathResolver(context.SandboxScope).ResolveBashWorkingDirectory(null, ExecutionTier.WslDirect);
        }
        else if (!string.IsNullOrWhiteSpace(context.Workspace?.Ext4MirrorPath))
        {
            var norm = context.Workspace.Ext4MirrorPath.Replace('\\', '/').TrimEnd('/');
            if (context.ForgeTask.Id > 0)
            {
                var baseDir = norm.EndsWith("/clone", StringComparison.OrdinalIgnoreCase)
                    ? norm[..^"/clone".Length]
                    : norm;
                resolved = $"{baseDir}/task-{context.ForgeTask.Id}";
            }
            else
            {
                resolved = norm;
            }
        }
        else if (context.Workspace != null)
        {
            resolved = pathResolver.GetAgentWorkingClonePath(context.Workspace.Id);
        }
        else
        {
            resolved = !string.IsNullOrWhiteSpace(context.WorkspacePath)
                ? context.WorkspacePath
                : Directory.GetCurrentDirectory();
        }

        return ExpandWslTildePath(resolved, pathResolver.GetWslUserName());
    }

    protected string ResolveWslDirectSessionWorkingDirectory(AgentSessionContext context)
        => ResolveWslDirectSessionWorkingDirectory(context, _pathResolver);

    /// <summary>
    /// Assembles the prompt sent to the agent: stage directive, resumed history, execution guidelines,
    /// environment block and pending human answer.
    /// </summary>
    protected string BuildEnrichedPrompt(AgentSessionContext context, string localWorkspaceRoot)
    {
        var projectConfig = CodeStackPresets.ResolveConfig(localWorkspaceRoot, null);
        var guidelines = _agentsMdService.FormatExecutionGuidelines(projectConfig, context.Workspace);

        var basePromptText = !string.IsNullOrEmpty(context.BootstrapPrompt)
            ? context.BootstrapPrompt
            : (!string.IsNullOrWhiteSpace(context.StageDirective)
                ? $"[Stage Directive]: {context.StageDirective}\n\n{context.ForgeTask.Title}\n{context.ForgeTask.Description}"
                : context.ForgeTask.Description);

        if (ConversationHistorySerializer.IsSerializedHistory(context.SerializedStateJson))
        {
            var historySummary = ConversationHistorySerializer.FormatForPrompt(context.SerializedStateJson!);
            if (!string.IsNullOrWhiteSpace(historySummary))
            {
                basePromptText = $"{basePromptText}\n\n[Resumed Conversation History]:\n{historySummary}\n\nPlease resume execution from the previous conversation turn.";
            }
        }

        if (!string.IsNullOrWhiteSpace(guidelines))
        {
            basePromptText = $"{basePromptText}\n\n{guidelines}";
        }

        if ((context.RequiredMode == AgentMode.Review ||
             context.Profile.Mode == AgentMode.Review ||
             string.Equals(context.StageId, StandardStageIds.CodeReviewAndFix, StringComparison.OrdinalIgnoreCase)) &&
            !basePromptText.Contains("request_changes", StringComparison.OrdinalIgnoreCase) &&
            !basePromptText.Contains("request-changes", StringComparison.OrdinalIgnoreCase))
        {
            basePromptText = $"{basePromptText}\n\n[Stage Completion Contract]:\nConclude your review with either `<approved>...</approved>` if all changes meet the specification and tests pass, or `<request_changes>[actionable feedback]</request_changes>` if issues remain for the implementation stage.";
        }

        var agentWorkingDir = context.Workspace?.ExecutionMode switch
        {
            ExecutionMode.ContainerSandbox => ResolveContainerSessionWorkingDirectory(context),
            ExecutionMode.WslDirect => ResolveWslDirectSessionWorkingDirectory(context),
            _ => localWorkspaceRoot
        };
        var envInfo = $"<environment>\n- Current Working Directory (PWD): {agentWorkingDir}\n- Perform all edits, builds, and tests within this directory; the canonical clone on 'main' is read-only.\n</environment>";
        basePromptText = $"{basePromptText}\n\n{envInfo}";

        return !string.IsNullOrWhiteSpace(context.PendingHumanAnswer)
            ? $"[Human Answer]: {context.PendingHumanAnswer}\n\n{basePromptText}"
            : basePromptText;
    }

    protected Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken cancellationToken)
        => _recorder.BroadcastAsync(sessionId, taskId, stageId, kind, content, cancellationToken);

    protected Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        IReadOnlyDictionary<string, object?>? metadata,
        CancellationToken cancellationToken)
        => _recorder.BroadcastAsync(sessionId, taskId, stageId, kind, content, metadata, cancellationToken);

    protected Task SaveTranscriptAsync(
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken,
        string? sessionId = null,
        int? cachedInputTokens = null)
        => _recorder.SaveTranscriptAsync(
            HarnessId,
            context,
            workspacePath,
            promptText,
            summaryText,
            elapsedMs,
            promptTokens,
            completionTokens,
            status,
            cancellationToken,
            sessionId,
            cachedInputTokens);
}

