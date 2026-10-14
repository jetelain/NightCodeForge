using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Execution;

namespace NightCodeForge.Agents.Acp;

public class AcpHarnessOptions
{
    public string ExecutablePath { get; set; } = "gemini";
    public string Arguments { get; set; } = "--experimental-acp";
    public Func<Process>? CustomProcessFactory { get; set; }
}

/// <summary>
/// Open Agent Client Protocol (ACP) adapter supporting ACP v2 negotiation with backward-compatible v1 fallback.
/// Intercepts terminal, filesystem, and tool requests and routes them into the execution sandbox,
/// streaming live activity to IAgentActivityStreamer and persisting session markdown transcripts.
/// </summary>
public class AcpAgentHarness : IAgentHarness
{
    private readonly AcpHarnessOptions _options;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IMcpClientManager? _mcpManager;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly SessionTranscriptService? _transcriptService;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<AcpAgentHarness> _logger;

    public string HarnessId => "acp";

    public AcpAgentHarness(
        AcpHarnessOptions? options = null,
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IMcpClientManager? mcpManager = null,
        IEditorConfigService? editorConfigService = null,
        IEditorConfigFileApplier? editorConfigFileApplier = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        SessionTranscriptService? transcriptService = null,
        IWorkspacePathResolver? pathResolver = null,
        IProcessRunner? processRunner = null,
        ILogger<AcpAgentHarness>? logger = null)
    {
        _options = options ?? new AcpHarnessOptions();
        _executionEnv = executionEnv;
        _envProvider = envProvider;
        _mcpManager = mcpManager;
        _editorConfigService = editorConfigService ?? new EditorConfigService();
        _editorConfigFileApplier = editorConfigFileApplier ?? new EditorConfigFileApplier();
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _transcriptService = transcriptService;
        _pathResolver = pathResolver;
        _processRunner = processRunner ?? new SystemProcessRunner();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpAgentHarness>.Instance;
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = Guid.NewGuid().ToString("N");
        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken);

        if (_options.CustomProcessFactory != null)
        {
            Process process;
            try
            {
                process = _options.CustomProcessFactory();
                process.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch ACP agent via custom factory");
                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
                return AgentRunResult.Failed($"Failed to launch ACP agent: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
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
                catch { }
                process.Dispose();
            }
        }

        var exePath = _options.ExecutablePath;
        var args = _options.Arguments;

        if (!string.IsNullOrWhiteSpace(context.Profile.HarnessConfigurationJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(context.Profile.HarnessConfigurationJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("executablePath", out var exeProp) ||
                    root.TryGetProperty("executable", out exeProp) ||
                    root.TryGetProperty("command", out exeProp))
                {
                    var val = exeProp.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) exePath = val;
                }
                if (root.TryGetProperty("arguments", out var argsProp) ||
                    root.TryGetProperty("args", out argsProp))
                {
                    var val = argsProp.GetString();
                    if (val != null) args = val;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse HarnessConfigurationJson on AgentProfile '{ProfileName}', using defaults.", context.Profile.Name);
            }
        }

        var workingDir = !string.IsNullOrEmpty(context.WorkspacePath) ? context.WorkspacePath : Directory.GetCurrentDirectory();
        if (OperatingSystem.IsWindows() && (workingDir.StartsWith('/') || workingDir.StartsWith('~')))
        {
            if (_pathResolver != null && context.Workspace != null)
            {
                workingDir = _pathResolver.ResolveCruciblePath(context.Workspace.Id, string.Empty);
            }
            else if (!string.IsNullOrEmpty(context.Workspace?.RepositoryPath))
            {
                workingDir = context.Workspace.RepositoryPath;
            }
            else
            {
                workingDir = Directory.GetCurrentDirectory();
            }
        }

        if (!Directory.Exists(workingDir))
        {
            if (!string.IsNullOrEmpty(context.Workspace?.RepositoryPath) && Directory.Exists(context.Workspace.RepositoryPath))
            {
                workingDir = context.Workspace.RepositoryPath;
            }
            else
            {
                workingDir = Directory.GetCurrentDirectory();
            }
        }

        IProcessStreamSession session;
        try
        {
            var spec = new ProcessStartSpec
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = workingDir,
                TimeoutSeconds = 600
            };
            session = _processRunner.StartSession(spec);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch ACP agent at {ExecutablePath}", _options.ExecutablePath);
            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
            return AgentRunResult.Failed($"Failed to launch ACP agent: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
        }

        await using (session.ConfigureAwait(false))
        {
            return await RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, sessionId, sw, cancellationToken);
        }
    }

    /// <summary>
    /// Executes the full bidirectional ACP JSON-RPC message exchange against input and output streams.
    /// Internal to allow direct unit testing without spawning external OS processes.
    /// </summary>
    public async Task<AgentRunResult> RunSessionStreamAsync(
        StreamWriter stdin,
        StreamReader stdout,
        AgentSessionContext context,
        string sessionId,
        Stopwatch sw,
        CancellationToken cancellationToken = default)
    {
        long promptTokens = 0;
        long completionTokens = 0;
        var activeTerminals = new ConcurrentDictionary<string, (int ExitCode, string Output)>(StringComparer.OrdinalIgnoreCase);
        var workspacePath = !string.IsNullOrEmpty(context.WorkspacePath)
            ? context.WorkspacePath
            : (context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory());

        var localWorkspaceRoot = workspacePath;
        if (OperatingSystem.IsWindows() && (localWorkspaceRoot.StartsWith('/') || localWorkspaceRoot.StartsWith('~')))
        {
            if (_pathResolver != null && context.Workspace != null)
            {
                localWorkspaceRoot = _pathResolver.ResolveCruciblePath(context.Workspace.Id, string.Empty);
            }
            else if (!string.IsNullOrEmpty(context.Workspace?.RepositoryPath))
            {
                localWorkspaceRoot = context.Workspace.RepositoryPath;
            }
        }

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace, context.Profile.SecurityTier)
            : _executionEnv;

        try
        {
            // 1. Handshake & Protocol Negotiation (Negotiates v2, falls back to v1)
            int negotiatedVersion = await NegotiateProtocolAsync(stdin, stdout, cancellationToken);
            _logger.LogInformation("Successfully negotiated ACP protocol version {Version}", negotiatedVersion);

            // 2. Initialize or Resume Session
            string activeSessionId = sessionId;
            if (!string.IsNullOrEmpty(context.SerializedStateJson))
            {
                var resumeMethod = negotiatedVersion >= 2 ? "session/resume" : "session/load";
                var resumeReq = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "session-1",
                    method = resumeMethod,
                    @params = new
                    {
                        sessionId = context.SerializedStateJson
                    }
                });
                await stdin.WriteLineAsync(resumeReq.AsMemory(), cancellationToken);
                activeSessionId = context.SerializedStateJson;
            }
            else
            {
                var newSessionReq = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = "session-1",
                    method = "session/new",
                    @params = new
                    {
                        cwd = workspacePath,
                        mcpServers = _mcpManager != null ? await _mcpManager.GetAcpMcpServersAsync(context.Workspace?.Id, localWorkspaceRoot, cancellationToken) : null
                    }
                });
                await stdin.WriteLineAsync(newSessionReq.AsMemory(), cancellationToken);
            }

            // 3. Dispatch Prompt Request (incorporating human answer if resuming from permission request)
            var projectConfig = CodeStackPresets.ResolveConfig(localWorkspaceRoot, null);
            var guidelines = _agentsMdService.FormatExecutionGuidelines(projectConfig, context.Workspace);

            var basePromptText = !string.IsNullOrEmpty(context.BootstrapPrompt)
                ? context.BootstrapPrompt
                : (!string.IsNullOrWhiteSpace(context.StageDirective)
                    ? $"[Stage Directive]: {context.StageDirective}\n\n{context.Issue.Title}\n{context.Issue.Description}"
                    : context.Issue.Description);

            if (!string.IsNullOrWhiteSpace(guidelines))
            {
                basePromptText = $"{basePromptText}\n\n{guidelines}";
            }

            var promptText = !string.IsNullOrWhiteSpace(context.PendingHumanAnswer)
                ? $"[Human Answer]: {context.PendingHumanAnswer}\n\n{basePromptText}"
                : basePromptText;

            var promptReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "prompt-1",
                method = "session/prompt",
                @params = new
                {
                    sessionId = activeSessionId,
                    prompt = new[] { new { text = promptText } }
                }
            });
            await stdin.WriteLineAsync(promptReq.AsMemory(), cancellationToken);

            string? lastAgentSummary = null;

            // 4. Message Handling Loop
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    var method = node?["method"]?.ToString();
                    var id = node?["id"]?.ToString();

                    // 4.1 Telemetry Notification: session/update
                    if (method == "session/update")
                    {
                        var update = node?["params"]?["update"];
                        var updateType = update?["sessionUpdate"]?.ToString();

                        if (updateType == "agent_thought_chunk")
                        {
                            var thought = update?["content"]?["text"]?.ToString() ?? update?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(thought))
                            {
                                await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.ThinkingDelta, thought, cancellationToken);
                            }
                        }
                        else if (updateType == "agent_message_chunk")
                        {
                            var text = update?["content"]?["text"]?.ToString() ?? update?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                lastAgentSummary = (lastAgentSummary ?? "") + text;
                                await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.MessageDelta, text, cancellationToken);
                            }
                        }
                        else if (updateType == "tool_call")
                        {
                            var title = update?["title"]?.ToString() ?? update?["toolCallId"]?.ToString() ?? "tool";
                            var rawInput = update?["rawInput"]?.ToString() ?? "";
                            await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolStarted, $"{title}: {rawInput}", cancellationToken);
                        }
                        else if (updateType == "tool_call_update")
                        {
                            var status = update?["status"]?.ToString() ?? "completed";
                            var rawOutput = update?["rawOutput"]?.ToString() ?? "";
                            await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, $"{status}: {rawOutput}", cancellationToken);
                        }
                        continue;
                    }

                    // 4.2 Terminal Execution (terminal/create or terminal/run)
                    if (method == "terminal/create" || method == "terminal/run")
                    {
                        var cmd = node?["params"]?["command"]?.ToString() ?? "";
                        var rawCwd = node?["params"]?["cwd"]?.ToString();
                        string cwd;
                        if (string.IsNullOrWhiteSpace(rawCwd) ||
                            string.Equals(rawCwd, "/workspace", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(rawCwd, "workspace", StringComparison.OrdinalIgnoreCase))
                        {
                            cwd = workspacePath;
                        }
                        else if (rawCwd.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
                        {
                            var rel = rawCwd.Substring("/workspace/".Length);
                            cwd = Path.Combine(workspacePath, rel).Replace('\\', '/');
                        }
                        else
                        {
                            cwd = rawCwd;
                        }

                        if (effectiveEnv == null && OperatingSystem.IsWindows() && (cwd.StartsWith('/') || cwd.StartsWith('~')))
                        {
                            cwd = localWorkspaceRoot;
                        }

                        var terminalId = Guid.NewGuid().ToString("N");

                        string output = "";
                        int exitCode = 0;
                        if (effectiveEnv != null)
                        {
                            var execRes = await effectiveEnv.ExecuteAsync(cmd, cwd, 300, null, cancellationToken);
                            output = execRes.StandardOutput;
                            if (!string.IsNullOrEmpty(execRes.StandardError))
                            {
                                output = string.IsNullOrEmpty(output) ? execRes.StandardError : output + Environment.NewLine + execRes.StandardError;
                            }
                            exitCode = execRes.ExitCode;
                        }
                        else
                        {
                            try
                            {
                                var fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "bash";
                                var args = OperatingSystem.IsWindows() ? new[] { "/c", cmd } : new[] { "-c", cmd };
                                var execRes = await _processRunner.RunAsync(fileName, args, cwd, 300, cancellationToken);
                                output = execRes.StandardOutput;
                                if (!string.IsNullOrEmpty(execRes.StandardError))
                                {
                                    output = string.IsNullOrEmpty(output) ? execRes.StandardError : output + Environment.NewLine + execRes.StandardError;
                                }
                                exitCode = execRes.ExitCode;
                            }
                            catch (Exception ex)
                            {
                                output = ex.Message;
                                exitCode = 1;
                            }
                        }

                        activeTerminals[terminalId] = (exitCode, output);

                        var terminalResp = JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id,
                            result = new { terminalId, exitCode, output }
                        });
                        await stdin.WriteLineAsync(terminalResp.AsMemory(), cancellationToken);
                        continue;
                    }

                    // 4.3 Terminal Output & Exit Polling
                    if (method == "terminal/output")
                    {
                        var termId = node?["params"]?["terminalId"]?.ToString() ?? "";
                        var termOut = activeTerminals.TryGetValue(termId, out var cached) ? cached.Output : "";
                        var outResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { output = termOut } });
                        await stdin.WriteLineAsync(outResp.AsMemory(), cancellationToken);
                        continue;
                    }
                    if (method == "terminal/wait_for_exit")
                    {
                        var termId = node?["params"]?["terminalId"]?.ToString() ?? "";
                        var exitCode = activeTerminals.TryGetValue(termId, out var cached) ? cached.ExitCode : 0;
                        var exitResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { exitCode } });
                        await stdin.WriteLineAsync(exitResp.AsMemory(), cancellationToken);
                        continue;
                    }
                    if (method == "terminal/release" || method == "terminal/kill")
                    {
                        var termId = node?["params"]?["terminalId"]?.ToString() ?? "";
                        activeTerminals.TryRemove(termId, out _);
                        var relResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { } });
                        await stdin.WriteLineAsync(relResp.AsMemory(), cancellationToken);
                        continue;
                    }

                    // 4.4 Filesystem Operations (fs/read_text_file & fs/write_text_file)
                    if (method == "fs/read_text_file")
                    {
                        var filePath = node?["params"]?["path"]?.ToString() ?? "";
                        int? offset = node?["params"]?["offset"]?.GetValue<int?>();
                        int? limit = node?["params"]?["limit"]?.GetValue<int?>();

                        try
                        {
                            var content = ReadWorkspaceFile(localWorkspaceRoot, filePath, offset, limit, context.Workspace?.Id);
                            var readResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content } });
                            await stdin.WriteLineAsync(readResp.AsMemory(), cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            var errResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32602, message = ex.Message } });
                            await stdin.WriteLineAsync(errResp.AsMemory(), cancellationToken);
                        }
                        continue;
                    }
                    if (method == "fs/write_text_file")
                    {
                        var filePath = node?["params"]?["path"]?.ToString() ?? "";
                        var content = node?["params"]?["content"]?.ToString() ?? "";

                        try
                        {
                            await WriteWorkspaceFileAsync(localWorkspaceRoot, filePath, content, context.Workspace?.Id, cancellationToken);
                            var writeResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { } });
                            await stdin.WriteLineAsync(writeResp.AsMemory(), cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            var errResp = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = -32602, message = ex.Message } });
                            await stdin.WriteLineAsync(errResp.AsMemory(), cancellationToken);
                        }
                        continue;
                    }

                    // 4.5 Permission Request (Human-in-the-loop interruption)
                    if (method == "session/request_permission")
                    {
                        var prompt = node?["params"]?["prompt"]?.ToString();
                        var toolCall = node?["params"]?["toolCall"]?.ToString();
                        var rationale = node?["params"]?["rationale"]?.ToString();
                        var finalPrompt = prompt ?? toolCall ?? "Permission requested by ACP agent";

                        if (!string.IsNullOrWhiteSpace(rationale))
                        {
                            finalPrompt += $" ({rationale})";
                        }

                        if (node?["params"]?["options"] is JsonArray optsArr && optsArr.Count > 0)
                        {
                            finalPrompt += "\nOptions:\n" + string.Join("\n", optsArr.Select(o => $"- {o?["name"]?.ToString() ?? o?.ToString()}"));
                        }

                        sw.Stop();
                        await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.Interrupted, finalPrompt, cancellationToken);

                        await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, lastAgentSummary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken);

                        return AgentRunResult.WaitingForHuman(
                            finalPrompt,
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            updatedState: activeSessionId,
                            summary: "ACP agent paused awaiting developer permission");
                    }

                    // 4.6 Prompt Response Completion
                    if (node?["result"] != null && (id == "prompt-1" || node["result"]?["stopReason"] != null))
                    {
                        var stopReason = node["result"]?["stopReason"]?.ToString() ?? "completed";
                        lastAgentSummary = lastAgentSummary ?? $"ACP agent finished: {stopReason}";
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json debug lines
                }
            }

            sw.Stop();
            var finalSummary = lastAgentSummary ?? "ACP agent completed successfully.";
            await BroadcastAsync(activeSessionId, context.Issue.Id, context.StageId, AgentActivityKind.StageCompleted, finalSummary, cancellationToken);

            await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, finalSummary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: activeSessionId,
                summary: finalSummary);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var cancelReq = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/cancel" });
                await stdin.WriteLineAsync(cancelReq.AsMemory());
            }
            catch { }
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
            await SaveTranscriptAsync(context, localWorkspaceRoot, "", ex.Message, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Failed, cancellationToken);
            throw;
        }
    }

    private string ResolveFilePath(string workspacePath, string filePath, int? workspaceId)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            if (_pathResolver != null && workspaceId.HasValue)
            {
                return _pathResolver.ResolveCruciblePath(workspaceId.Value, string.Empty);
            }
            return workspacePath;
        }

        if (filePath.StartsWith(@"\\") || (filePath.Length >= 2 && filePath[1] == ':'))
        {
            return Path.GetFullPath(filePath);
        }

        if (_pathResolver != null && workspaceId.HasValue)
        {
            return _pathResolver.ResolveCruciblePath(workspaceId.Value, filePath);
        }

        var normalized = filePath.Replace('\\', '/');
        if (normalized.StartsWith("/workspace/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized.Substring("/workspace/".Length);
            return Path.GetFullPath(Path.Combine(workspacePath, rel));
        }
        if (string.Equals(normalized, "/workspace", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "workspace", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(workspacePath);
        }
        if (normalized.StartsWith("workspace/", StringComparison.OrdinalIgnoreCase))
        {
            var rel = normalized.Substring("workspace/".Length);
            return Path.GetFullPath(Path.Combine(workspacePath, rel));
        }

        if (Path.IsPathRooted(filePath))
        {
            return Path.GetFullPath(filePath);
        }

        return Path.GetFullPath(Path.Combine(workspacePath, filePath));
    }

    private string ReadWorkspaceFile(string workspacePath, string filePath, int? offset, int? limit, int? workspaceId)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId);
        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        if (!offset.HasValue && !limit.HasValue)
        {
            return File.ReadAllText(resolvedPath);
        }

        var lines = File.ReadAllLines(resolvedPath);
        int skip = offset.HasValue && offset.Value > 0 ? offset.Value - 1 : 0;
        int take = limit.HasValue && limit.Value > 0 ? limit.Value : lines.Length - skip;
        var slice = lines.Skip(skip).Take(take);
        return string.Join(Environment.NewLine, slice);
    }

    private async Task WriteWorkspaceFileAsync(string workspacePath, string filePath, string content, int? workspaceId, CancellationToken ct)
    {
        var resolvedPath = ResolveFilePath(workspacePath, filePath, workspaceId);
        var dir = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var editorConfig = await _editorConfigService.GetSettingsForPathAsync(resolvedPath, ct);
        string? existing = File.Exists(resolvedPath) ? await File.ReadAllTextAsync(resolvedPath, ct) : null;
        var bytes = _editorConfigFileApplier.ApplyFormattingAndEncode(content, editorConfig, existing);
        await File.WriteAllBytesAsync(resolvedPath, bytes, ct);
    }

    private async Task BroadcastAsync(string sessionId, int issueId, string stageId, AgentActivityKind kind, string content, CancellationToken ct)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, issueId, stageId, kind, content, DateTime.UtcNow);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
    }

    private async Task SaveTranscriptAsync(
        AgentSessionContext context,
        string workspacePath,
        string promptText,
        string? summaryText,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken)
    {
        if (_transcriptService == null) return;

        try
        {
            var entries = new List<TranscriptEntry>();
            var now = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(promptText))
            {
                entries.Add(new TranscriptEntry(now, "User", promptText, null, null, null));
            }
            if (!string.IsNullOrEmpty(summaryText))
            {
                entries.Add(new TranscriptEntry(now, "Assistant", summaryText, null, null, null));
            }

            var meta = new TranscriptMetadata(
                context.Issue.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                elapsedMs,
                new AgentRunMetrics(elapsedMs, promptTokens, completionTokens, promptTokens + completionTokens),
                context.OptimizationFlags,
                status);

            var transcriptDir = Path.Combine(workspacePath, "docs", "transcripts");
            await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist markdown transcript in {HarnessId} harness", HarnessId);
        }
    }

    public static async Task<int> NegotiateProtocolAsync(StreamWriter stdin, StreamReader stdout, CancellationToken cancellationToken)
    {
        // Offer ACP v2 first
        var initV2 = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "init-1",
            method = "initialize",
            @params = new
            {
                protocolVersion = 2,
                clientCapabilities = new
                {
                    fs = new { readTextFile = true, writeTextFile = true },
                    terminal = new { create = true, output = true }
                }
            }
        });
        await stdin.WriteLineAsync(initV2.AsMemory(), cancellationToken);

        var responseLine = await stdout.ReadLineAsync(cancellationToken);
        if (responseLine != null)
        {
            try
            {
                var node = JsonNode.Parse(responseLine);
                if (int.TryParse(node?["result"]?["protocolVersion"]?.ToString(), out var version))
                {
                    return version;
                }
            }
            catch { }
        }

        // Default negotiated to v1
        return 1;
    }
}

