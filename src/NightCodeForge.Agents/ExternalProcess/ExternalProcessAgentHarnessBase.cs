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
        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken);

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

        var (exePath, args) = ResolveLaunchCommand(context);
        var workingDir = ResolveLaunchWorkingDirectory(context);

        IProcessStreamSession session;
        try
        {
            session = _processRunner.StartSession(new ProcessStartSpec
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = workingDir,
                TimeoutSeconds = ProcessTimeoutSeconds
            });
        }
        catch (Exception ex)
        {
            return await FailLaunchAsync(ex, exePath, context, sessionId, sw, cancellationToken);
        }

        await using (session.ConfigureAwait(false))
        {
            return await RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, sessionId, sw, cancellationToken);
        }
    }

    private async Task<AgentRunResult> FailLaunchAsync(Exception ex, string target, AgentSessionContext context, string sessionId, Stopwatch sw, CancellationToken cancellationToken)
    {
        Logger.LogError(ex, "Failed to launch {AgentName} at {ExecutablePath}", AgentDisplayName, target);
        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
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

    private string ResolveLaunchWorkingDirectory(AgentSessionContext context)
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

        var agentWorkingDir = context.Workspace?.ExecutionMode == ExecutionMode.ContainerSandbox
            ? ContainerWorkspacePath
            : localWorkspaceRoot;
        var envInfo = $"<environment>\n- Current Working Directory (PWD): {agentWorkingDir}\n- Perform all edits, builds, and tests within this directory; the canonical clone on 'main' is read-only.\n</environment>";
        basePromptText = $"{basePromptText}\n\n{envInfo}";

        return !string.IsNullOrWhiteSpace(context.PendingHumanAnswer)
            ? $"[Human Answer]: {context.PendingHumanAnswer}\n\n{basePromptText}"
            : basePromptText;
    }

    protected Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken cancellationToken)
        => _recorder.BroadcastAsync(sessionId, taskId, stageId, kind, content, cancellationToken);

    protected Task SaveTranscriptAsync(
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken)
        => _recorder.SaveTranscriptAsync(HarnessId, context, workspacePath, promptText, summaryText, elapsedMs, promptTokens, completionTokens, status, cancellationToken);
}

