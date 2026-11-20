using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Acp;

public class AcpHarnessOptions
{
    public const string DefaultExecutable = "agy";
    public const string DefaultArgumentsValue = $"acp {AcpProcessLauncher.DangerouslySkipPermissionsFlag}";

    public string ExecutablePath { get; set; } = DefaultExecutable;
    public string Arguments { get; set; } = DefaultArgumentsValue;
    public Func<Process>? CustomProcessFactory { get; set; }
}

/// <summary>
/// Exception thrown when an ACP provider reports an authentication or session initialization failure.
/// </summary>
public class AcpAuthenticationException : InvalidOperationException
{
    public AcpAuthenticationException(string message) : base(message)
    {
    }

    public AcpAuthenticationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Handshake capability result returned by <see cref="AcpAgentHarness.NegotiateProtocolWithCapabilitiesAsync"/>.
/// </summary>
public readonly record struct AcpNegotiationResult(int ProtocolVersion, bool SupportsImagePrompt);

/// <summary>
/// Open Agent Client Protocol (ACP) adapter supporting <c>agy acp</c> wire negotiation,
/// multi-environment execution (<see cref="ExecutionMode.BareHost"/>, <see cref="ExecutionMode.WslDirect"/>,
/// <see cref="ExecutionMode.ContainerSandbox"/>), host-side callbacks, live activity streaming,
/// and SQLite conversation/transcript persistence.
/// </summary>
public class AcpAgentHarness : ExternalProcessAgentHarnessBase
{
    private const int JsonRpcInvalidParams = -32602;
    private const int JsonRpcInvalidRequest = -32600;
    private const int JsonRpcAuthRequired = -32001;
    private const string InitRequestId = "init-1";
    private const string SessionRequestId = "session-1";
    private const string FallbackSessionRequestId = "session-fallback-1";
    private const string PromptRequestId = "prompt-1";
    private const string ConfigRequestPrefix = "cfg-";

    private readonly AcpHarnessOptions _options;
    private readonly IMcpClientManager _mcpManager;
    private readonly IAcpHostRpcHandler _hostRpcHandler;
    private readonly IAcpProcessLauncher _processLauncher;
    private readonly ILogger<AcpAgentHarness> _logger;

    public override string HarnessId => StandardHarnessIds.Acp;

    protected override string AgentDisplayName => "ACP agent";
    protected override string DefaultExecutablePath => _options.ExecutablePath;
    protected override string DefaultArguments => _options.Arguments;
    protected override Func<Process>? CustomProcessFactory => _options.CustomProcessFactory;

    public AcpAgentHarness(
        Microsoft.Extensions.Options.IOptions<AcpHarnessOptions> options,
        IMcpClientManager mcpManager,
        IAcpHostRpcHandler hostRpcHandler,
        IAcpProcessLauncher processLauncher,
        IHarnessActivityRecorder recorder,
        AgentsMdService agentsMdService,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        IAgentMemoryTurnTracker memoryTracker,
        ILogger<AcpAgentHarness> logger)
        : base(recorder, agentsMdService, pathResolver, processRunner, memoryTracker, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _hostRpcHandler = hostRpcHandler ?? throw new ArgumentNullException(nameof(hostRpcHandler));
        _processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<ProcessStartSpec> CreateProcessStartSpecAsync(
        AgentSessionContext context,
        CancellationToken cancellationToken)
    {
        var (exePath, args) = ResolveLaunchCommand(context);
        var defaultLaunchWorkingDir = ResolveLaunchWorkingDirectory(context);
        var (defaultSessionWorkingDir, localWorkspaceRoot) = ResolveWorkspacePaths(context);

        var launchPlan = await _processLauncher.PrepareLaunchAsync(
            context,
            exePath,
            args,
            defaultLaunchWorkingDir,
            defaultSessionWorkingDir,
            localWorkspaceRoot,
            DefaultProcessTimeoutSeconds,
            cancellationToken);

        return launchPlan.ProcessSpec;
    }

    /// <summary>
    /// Executes the full bidirectional ACP JSON-RPC message exchange against input and output streams.
    /// Public to allow direct unit testing without spawning external OS processes.
    /// </summary>
    public override async Task<AgentRunResult> RunSessionStreamAsync(
        StreamWriter stdin,
        StreamReader stdout,
        AgentSessionContext context,
        string sessionId,
        Stopwatch sw,
        CancellationToken cancellationToken = default)
    {
        long promptTokens = 0;
        long completionTokens = 0;
        long totalTokens = 0;
        int? cachedInputTokens = null;
        string activeSessionId = sessionId;

        await Recorder.BeginConversationAsync(sessionId, HarnessId, context, cancellationToken);

        var (workspacePath, localWorkspaceRoot) = ResolveWorkspacePaths(context);
        var sessionWorkingDir = ResolveEffectiveSessionWorkingDirectory(context, workspacePath);
        var sandboxPathResolver = context.SandboxScope != null
            ? new SandboxPathResolver(context.SandboxScope)
            : null;
        var hostContext = new AcpHostRpcContext(sessionId, context, sessionWorkingDir, localWorkspaceRoot, sandboxPathResolver);

        try
        {
            // 1. Handshake & Protocol Negotiation (protocolVersion 1, terminal = true, promptCapabilities)
            var negotiation = await NegotiateProtocolWithCapabilitiesAsync(stdin, stdout, cancellationToken, _logger);
            int negotiatedVersion = negotiation.ProtocolVersion;
            _logger.LogInformation("Successfully negotiated ACP protocol version {Version} (supportsImagePrompt={SupportsImage})", negotiatedVersion, negotiation.SupportsImagePrompt);

            // 2. Initialize or Resume Session
            bool isReconstructedHistory = ConversationHistorySerializer.IsSerializedHistory(context.SerializedStateJson);
            bool isResumeAttempt = !string.IsNullOrEmpty(context.SerializedStateJson) && !isReconstructedHistory;
            var mcpServers = await _mcpManager.GetAcpMcpServersAsync(context.Workspace?.Id, localWorkspaceRoot, cancellationToken)
                ?? Array.Empty<object>();

            if (isResumeAttempt)
            {
                var resumeMethod = negotiatedVersion >= 2 ? "session/resume" : "session/load";
                var resumeReq = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = SessionRequestId,
                    method = resumeMethod,
                    @params = new
                    {
                        sessionId = context.SerializedStateJson,
                        cwd = sessionWorkingDir,
                        mcpServers
                    }
                });
                await stdin.WriteLineAsync(resumeReq.AsMemory(), cancellationToken);
                activeSessionId = context.SerializedStateJson!;
                hostContext = hostContext with { SessionId = activeSessionId };
            }
            else
            {
                var newSessionReq = BuildNewSessionRequestJson(SessionRequestId, sessionWorkingDir, mcpServers, context.Profile.SystemPrompt);
                await stdin.WriteLineAsync(newSessionReq.AsMemory(), cancellationToken);
            }

            // 3. Prepare Enriched Prompt (and record User turn)
            var promptText = await BuildEnrichedPromptAsync(context, localWorkspaceRoot, cancellationToken);
            await Recorder.RecordUserTurnAsync(sessionId, HarnessId, context, promptText, cancellationToken);

            // 4. Wait for session/new or session/load response (capturing server-assigned sessionId & applying config options),
            //    while preserving compatibility with synthetic test streams that jump straight to prompt-phase events.
            string? pendingLine = null;
            bool promptDispatched = false;
            string expectedSessionRequestId = SessionRequestId;
            bool fallbackSessionSent = false;

            async Task DispatchPromptAsync()
            {
                if (promptDispatched)
                {
                    return;
                }

                var promptBlocks = await BuildPromptContentBlocksAsync(
                    context,
                    promptText,
                    negotiation.SupportsImagePrompt,
                    sandboxPathResolver,
                    cancellationToken);

                var promptReq = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = PromptRequestId,
                    method = "session/prompt",
                    @params = new
                    {
                        sessionId = activeSessionId,
                        prompt = promptBlocks
                    }
                });
                await stdin.WriteLineAsync(promptReq.AsMemory(), cancellationToken);
                promptDispatched = true;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    await DispatchPromptAsync();
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!TryParseJsonNode(line, out var preNode) || preNode == null)
                {
                    continue;
                }

                var preId = preNode["id"]?.ToString();
                var preMethod = preNode["method"]?.ToString();

                if (string.Equals(preId, expectedSessionRequestId, StringComparison.Ordinal))
                {
                    if (preNode["error"] is JsonNode errNode)
                    {
                        if (isResumeAttempt && !fallbackSessionSent && !IsAuthenticationError(errNode))
                        {
                            _logger.LogWarning(
                                "ACP session resumption for '{SavedSessionId}' returned non-auth error ({Error}); falling back to session/new.",
                                context.SerializedStateJson,
                                errNode["message"]?.ToString() ?? errNode.ToJsonString());

                            fallbackSessionSent = true;
                            expectedSessionRequestId = FallbackSessionRequestId;
                            activeSessionId = sessionId;
                            hostContext = hostContext with { SessionId = activeSessionId };

                            var fallbackReq = BuildNewSessionRequestJson(expectedSessionRequestId, sessionWorkingDir, mcpServers, context.Profile.SystemPrompt);
                            await stdin.WriteLineAsync(fallbackReq.AsMemory(), cancellationToken);
                            continue;
                        }

                        var errMessage = errNode["message"]?.ToString() ?? errNode.ToJsonString();
                        throw new AcpAuthenticationException(
                            $"ACP session initialization failed: {errMessage}. Authenticate Antigravity in Settings or run 'agy login' / 'wsl -e ~/.local/bin/agy login'.");
                    }

                    var sessionResult = preNode["result"];
                    var serverSessionId = sessionResult?["sessionId"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(serverSessionId))
                    {
                        activeSessionId = serverSessionId;
                        hostContext = hostContext with { SessionId = activeSessionId };
                    }

                    await ApplySessionConfigurationAsync(stdin, sessionResult, activeSessionId, context, cancellationToken);
                    await DispatchPromptAsync();
                    break;
                }

                if (string.Equals(preMethod, "session/update", StringComparison.Ordinal))
                {
                    var preUpdateType = preNode["params"]?["update"]?["sessionUpdate"]?.ToString();
                    if (preUpdateType is "available_commands_update" or "current_mode_update" or "config_option_update")
                    {
                        continue;
                    }
                }

                if (IsPromptPhaseMessage(preNode, preMethod, preId))
                {
                    await DispatchPromptAsync();
                    pendingLine = line;
                    break;
                }
            }

            if (!promptDispatched)
            {
                await DispatchPromptAsync();
            }

            var currentTurnText = new StringBuilder();
            var currentTurnThought = new StringBuilder();
            var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
            string? lastTurnConcludingText = null;

            async Task FlushTurnAsync()
            {
                var text = currentTurnText.Length > 0 ? currentTurnText.ToString() : null;
                var thought = currentTurnThought.Length > 0 ? currentTurnThought.ToString() : null;
                currentTurnText.Clear();
                currentTurnThought.Clear();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    lastTurnConcludingText = text;
                }

                if (!string.IsNullOrWhiteSpace(text) || !string.IsNullOrWhiteSpace(thought))
                {
                    await Recorder.RecordAssistantTurnAsync(sessionId, text, thought, cancellationToken);
                }
            }

            // 5. Message Handling Loop
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                if (pendingLine != null)
                {
                    line = pendingLine;
                    pendingLine = null;
                }
                else
                {
                    line = await stdout.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    if (node == null) continue;

                    var method = node["method"]?.ToString();
                    var id = node["id"]?.ToString();

                    if (id != null && id.StartsWith(ConfigRequestPrefix, StringComparison.Ordinal))
                    {
                        if (node["error"] is JsonNode cfgErr)
                        {
                            _logger.LogWarning("ACP configuration request '{RequestId}' returned an error: {Error}", id, cfgErr.ToJsonString());
                        }
                        continue;
                    }

                    // 5.1 Telemetry Notification: session/update
                    if (method == "session/update")
                    {
                        var update = node["params"]?["update"];
                        var updateType = update?["sessionUpdate"]?.ToString();

                        if (updateType == "agent_thought_chunk")
                        {
                            var thought = update?["content"]?["text"]?.ToString() ?? update?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(thought))
                            {
                                currentTurnThought.Append(thought);
                                await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, thought, cancellationToken);
                            }
                        }
                        else if (updateType == "agent_message_chunk")
                        {
                            var text = update?["content"]?["text"]?.ToString() ?? update?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                currentTurnText.Append(text);
                                await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, text, cancellationToken);
                            }
                        }
                        else if (updateType == "tool_call")
                        {
                            await FlushTurnAsync();
                            var toolCallId = update?["toolCallId"]?.ToString() ?? string.Empty;
                            var toolName = update?["name"]?.ToString()
                                ?? update?["title"]?.ToString()
                                ?? (!string.IsNullOrWhiteSpace(toolCallId) ? toolCallId : "tool");
                            if (!string.IsNullOrWhiteSpace(toolCallId))
                            {
                                toolNamesByCallId[toolCallId] = toolName;
                            }

                            var rawInput = FormatJsonOrString(update?["args"] ?? update?["rawInput"]);
                            var metadata = new Dictionary<string, object?>
                            {
                                [AgentActivityMetadataKeys.ToolName] = toolName
                            };
                            await BroadcastAsync(
                                activeSessionId,
                                context.ForgeTask.Id,
                                context.StageId,
                                AgentActivityKind.ToolStarted,
                                $"{toolName}: {rawInput}",
                                metadata,
                                cancellationToken);
                            await Recorder.RecordToolCallTurnAsync(sessionId, toolName, rawInput, toolCallId, cancellationToken);
                        }
                        else if (updateType == "tool_call_update")
                        {
                            var toolCallId = update?["toolCallId"]?.ToString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(toolCallId) &&
                                !toolNamesByCallId.ContainsKey(toolCallId) &&
                                (update?["name"]?.ToString() ?? update?["title"]?.ToString()) is { Length: > 0 } updatedName)
                            {
                                toolNamesByCallId[toolCallId] = updatedName;
                            }

                            var status = update?["status"]?.ToString() ?? "completed";
                            if (IsNonTerminalToolStatus(status))
                            {
                                continue;
                            }

                            var toolName = (!string.IsNullOrWhiteSpace(toolCallId) && toolNamesByCallId.TryGetValue(toolCallId, out var trackedName))
                                ? trackedName
                                : (update?["name"]?.ToString() ?? update?["title"]?.ToString() ?? "tool");
                            var rawOutput = FormatJsonOrString(update?["rawOutput"] ?? update?["output"] ?? update?["content"]);
                            var isError = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

                            var metadata = new Dictionary<string, object?>
                            {
                                [AgentActivityMetadataKeys.ToolName] = toolName
                            };
                            await BroadcastAsync(
                                activeSessionId,
                                context.ForgeTask.Id,
                                context.StageId,
                                AgentActivityKind.ToolCompleted,
                                $"{toolName}: {status}: {rawOutput}",
                                metadata,
                                cancellationToken);
                            await Recorder.RecordToolResultTurnAsync(
                                sessionId,
                                toolName,
                                rawOutput,
                                isError,
                                toolCallId,
                                durationMs: null,
                                cancellationToken);
                        }
                        else if (updateType is "turn_end" or "message_end")
                        {
                            await FlushTurnAsync();
                        }
                        continue;
                    }

                    // 5.2 Host-side callbacks (terminal/* and fs/*)
                    if (method != null && _hostRpcHandler.CanHandle(method))
                    {
                        if (_hostRpcHandler.EndsAssistantTurn(method))
                        {
                            await FlushTurnAsync();
                        }

                        var rpcResult = await _hostRpcHandler.HandleAsync(method, node["params"], hostContext, cancellationToken);
                        var rpcResp = rpcResult.IsError
                            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = JsonRpcInvalidParams, message = rpcResult.ErrorMessage } })
                            : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = rpcResult.Result });
                        await stdin.WriteLineAsync(rpcResp.AsMemory(), cancellationToken);
                        continue;
                    }

                    // 5.3 Permission Request (ReadOnly enforcement or Human-in-the-loop interruption)
                    if (method == "session/request_permission")
                    {
                        if (AcpProcessLauncher.IsReadOnlySession(context))
                        {
                            _logger.LogWarning("Rejected ACP session/request_permission in read-only session '{SessionId}'", activeSessionId);
                            var denyResp = JsonSerializer.Serialize(new
                            {
                                jsonrpc = "2.0",
                                id,
                                error = new { code = JsonRpcInvalidRequest, message = "Permission denied: mutating operations are blocked in read-only plan mode." }
                            });
                            await stdin.WriteLineAsync(denyResp.AsMemory(), cancellationToken);
                            continue;
                        }

                        await FlushTurnAsync();
                        var prompt = node["params"]?["prompt"]?.ToString();
                        var toolCall = node["params"]?["toolCall"]?.ToString();
                        var rationale = node["params"]?["rationale"]?.ToString();
                        var finalPrompt = prompt ?? toolCall ?? "Permission requested by ACP agent";

                        if (!string.IsNullOrWhiteSpace(rationale))
                        {
                            finalPrompt += $" ({rationale})";
                        }

                        string? optionsJson = null;
                        if (node["params"]?["options"] is JsonArray optsArr && optsArr.Count > 0)
                        {
                            finalPrompt += "\nOptions:\n" + string.Join("\n", optsArr.Select(o => $"- {o?["name"]?.ToString() ?? o?.ToString()}"));
                            optionsJson = JsonSerializer.Serialize(optsArr.Select(o => o?["name"]?.ToString() ?? o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList());
                        }

                        sw.Stop();
                        await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, finalPrompt, cancellationToken);

                        await SaveTranscriptAsync(
                            context,
                            localWorkspaceRoot,
                            promptText,
                            lastTurnConcludingText,
                            sw.ElapsedMilliseconds,
                            promptTokens,
                            completionTokens,
                            AgentRunStatus.WaitingForHuman,
                            cancellationToken,
                            sessionId,
                            cachedInputTokens);

                        var question = new Question
                        {
                            TaskId = context.ForgeTask.Id,
                            StageId = context.StageId,
                            Prompt = finalPrompt,
                            Rationale = rationale ?? string.Empty,
                            OptionsJson = optionsJson,
                            Status = QuestionStatus.Pending,
                            AskedAt = DateTime.UtcNow
                        };

                        return AgentRunResult.WaitingForHuman(
                            new[] { question },
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, totalTokens > 0 ? totalTokens : promptTokens + completionTokens, CachedInputTokens: cachedInputTokens),
                            updatedState: activeSessionId,
                            summary: "ACP agent paused awaiting developer permission");
                    }

                    // 5.4 Prompt Response Failure / Completion
                    if (string.Equals(id, PromptRequestId, StringComparison.Ordinal) && node["error"] is JsonNode promptErr)
                    {
                        await FlushTurnAsync();
                        var errMessage = promptErr["message"]?.ToString() ?? promptErr.ToJsonString();
                        if (IsAuthenticationError(promptErr))
                        {
                            throw new AcpAuthenticationException(
                                $"ACP prompt execution failed due to authentication error: {errMessage}. Authenticate Antigravity in Settings or run 'agy login' / 'wsl -e ~/.local/bin/agy login'.");
                        }

                        throw new InvalidOperationException($"ACP prompt execution failed: {errMessage}");
                    }

                    if (node["result"] != null && (id == PromptRequestId || node["result"]?["stopReason"] != null))
                    {
                        await FlushTurnAsync();
                        ExtractTokenUsage(node["result"]?["usage"], ref promptTokens, ref completionTokens, ref totalTokens, ref cachedInputTokens);
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json debug lines
                }
            }

            await FlushTurnAsync();
            sw.Stop();
            var extraction = StageSummaryExtractor.Extract(lastTurnConcludingText, fallbackSummary: "ACP agent completed successfully.");

            await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, extraction.Summary, cancellationToken);

            await SaveTranscriptAsync(
                context,
                localWorkspaceRoot,
                promptText,
                extraction.Summary,
                sw.ElapsedMilliseconds,
                promptTokens,
                completionTokens,
                AgentRunStatus.Completed,
                cancellationToken,
                sessionId,
                cachedInputTokens);

            var effectiveTotal = totalTokens > 0 ? totalTokens : promptTokens + completionTokens;
            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, effectiveTotal, CachedInputTokens: cachedInputTokens),
                updatedState: activeSessionId,
                summary: extraction.Summary,
                targetRouting: extraction.TargetRouting,
                reviewerFeedback: extraction.ReviewerFeedback);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            try
            {
                var cancelReq = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/cancel" });
                await stdin.WriteLineAsync(cancelReq.AsMemory());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send session/cancel to ACP process on cancellation");
            }

            await SaveTranscriptAsync(
                context,
                localWorkspaceRoot,
                string.Empty,
                "ACP agent execution cancelled.",
                sw.ElapsedMilliseconds,
                promptTokens,
                completionTokens,
                AgentRunStatus.Cancelled,
                CancellationToken.None,
                sessionId,
                cachedInputTokens);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, CancellationToken.None);
            await SaveTranscriptAsync(
                context,
                localWorkspaceRoot,
                string.Empty,
                ex.Message,
                sw.ElapsedMilliseconds,
                promptTokens,
                completionTokens,
                AgentRunStatus.Failed,
                CancellationToken.None,
                sessionId,
                cachedInputTokens);
            throw;
        }
        finally
        {
            _hostRpcHandler.EndSession(sessionId);
            if (!string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
            {
                _hostRpcHandler.EndSession(activeSessionId);
            }
        }
    }

    public static async Task<int> NegotiateProtocolAsync(StreamWriter stdin, StreamReader stdout, CancellationToken cancellationToken, ILogger? logger = null)
    {
        var negotiation = await NegotiateProtocolWithCapabilitiesAsync(stdin, stdout, cancellationToken, logger);
        return negotiation.ProtocolVersion;
    }

    public static async Task<AcpNegotiationResult> NegotiateProtocolWithCapabilitiesAsync(
        StreamWriter stdin,
        StreamReader stdout,
        CancellationToken cancellationToken,
        ILogger? logger = null)
    {
        var initReq = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = InitRequestId,
            method = "initialize",
            @params = new
            {
                protocolVersion = 1,
                clientCapabilities = new
                {
                    fs = new { readTextFile = true, writeTextFile = true },
                    terminal = true
                }
            }
        });
        await stdin.WriteLineAsync(initReq.AsMemory(), cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var responseLine = await stdout.ReadLineAsync(cancellationToken);
            if (responseLine == null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(responseLine);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Failed to parse ACP protocol version negotiation response: '{ResponseLine}'.", responseLine);
                continue;
            }

            if (node == null)
            {
                continue;
            }

            if (string.Equals(node["id"]?.ToString(), InitRequestId, StringComparison.Ordinal) && node["error"] is JsonNode initErr)
            {
                var errMessage = initErr["message"]?.ToString() ?? initErr.ToJsonString();
                if (IsAuthenticationError(initErr))
                {
                    throw new AcpAuthenticationException(
                        $"ACP initialize handshake failed due to authentication error: {errMessage}. Authenticate Antigravity in Settings or run 'agy login' / 'wsl -e ~/.local/bin/agy login'.");
                }

                throw new InvalidOperationException($"ACP initialize handshake failed: {errMessage}");
            }

            bool supportsImage = node["result"]?["agentCapabilities"]?["promptCapabilities"]?["image"] is JsonValue imgVal
                && imgVal.TryGetValue<bool>(out var imgBool)
                && imgBool;

            if (int.TryParse(node["result"]?["protocolVersion"]?.ToString(), out var version))
            {
                return new AcpNegotiationResult(version, supportsImage);
            }

            if (string.Equals(node["id"]?.ToString(), InitRequestId, StringComparison.Ordinal))
            {
                return new AcpNegotiationResult(1, supportsImage);
            }
        }

        return new AcpNegotiationResult(1, SupportsImagePrompt: false);
    }

    internal async Task<List<object>> BuildPromptContentBlocksAsync(
        AgentSessionContext context,
        string promptText,
        bool supportsImagePrompt,
        SandboxPathResolver? sandboxPathResolver,
        CancellationToken cancellationToken)
    {
        var blocks = new List<object>
        {
            new { type = "text", text = promptText }
        };

        var imageAttachments = CollectLinkedImageAttachments(context);
        if (imageAttachments.Count == 0)
        {
            return blocks;
        }

        if (!supportsImagePrompt)
        {
            throw new ModelVisionNotSupportedException(
                !string.IsNullOrWhiteSpace(context.Profile.ModelName) ? context.Profile.ModelName : HarnessId,
                imageAttachments[0].Title,
                $"ACP agent '{(!string.IsNullOrWhiteSpace(context.Profile.Name) ? context.Profile.Name : HarnessId)}' does not advertise agentCapabilities.promptCapabilities.image and cannot inspect image attachment '{imageAttachments[0].Title}'.");
        }

        foreach (var attachment in imageAttachments)
        {
            var relPath = (attachment.RelativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');
            var virtualPath = string.IsNullOrEmpty(relPath)
                ? AttachmentConstants.VirtualAttachmentsMountPath
                : $"{AttachmentConstants.VirtualAttachmentsMountPath}/{relPath}";

            var hostPath = ResolveAcpAttachmentHostPath(attachment, virtualPath, context, sandboxPathResolver);
            if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
            {
                throw new AttachmentTargetNotFoundException(
                    attachment.Id,
                    !string.IsNullOrWhiteSpace(attachment.SourceRef) ? attachment.SourceRef : virtualPath,
                    $"Attachment #{attachment.Id} ('{attachment.Title}') target does not exist or is unreadable.");
            }

            var normalized = await VisionImageNormalizer.LoadAndNormalizeAsync(hostPath, virtualPath, cancellationToken);
            blocks.Add(new
            {
                type = "image",
                data = Convert.ToBase64String(normalized.Bytes),
                mimeType = normalized.MediaType
            });
        }

        return blocks;
    }

    private static List<TaskAttachment> CollectLinkedImageAttachments(AgentSessionContext context)
    {
        if (string.Equals(context.StageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase) ||
            context.ForgeTask.Status == TaskOverallStatus.Triage)
        {
            return new List<TaskAttachment>();
        }

        var byId = new Dictionary<int, TaskAttachment>();
        if (context.TaskAttachments != null)
        {
            foreach (var att in context.TaskAttachments)
            {
                if (att != null && att.ContentKind == TaskAttachmentContentKind.Image)
                {
                    byId[att.Id] = att;
                }
            }
        }

        if (context.ForgeTask.AttachmentLinks != null)
        {
            foreach (var link in context.ForgeTask.AttachmentLinks)
            {
                if (link.TaskAttachment != null &&
                    link.TaskAttachment.ContentKind == TaskAttachmentContentKind.Image &&
                    !byId.ContainsKey(link.TaskAttachment.Id))
                {
                    byId[link.TaskAttachment.Id] = link.TaskAttachment;
                }
            }
        }

        return byId.Values.OrderBy(a => a.Id).ToList();
    }

    private string? ResolveAcpAttachmentHostPath(
        TaskAttachment attachment,
        string virtualPath,
        AgentSessionContext context,
        SandboxPathResolver? sandboxPathResolver)
    {
        if (sandboxPathResolver != null)
        {
            try
            {
                var resolved = sandboxPathResolver.ResolveHostPath(virtualPath, forWrite: false);
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                {
                    return resolved;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        int workspaceId = attachment.WorkspaceId > 0 ? attachment.WorkspaceId : (context.Workspace?.Id ?? 0);
        if (workspaceId > 0)
        {
            var rawAttachDir = PathResolver.GetAttachmentsPath(workspaceId);
            var hostAttachDir = !string.IsNullOrWhiteSpace(rawAttachDir) ? PathResolver.ToHostPath(rawAttachDir) : null;
            var effectiveDir = !string.IsNullOrWhiteSpace(hostAttachDir) ? hostAttachDir : rawAttachDir;
            if (!string.IsNullOrWhiteSpace(effectiveDir) && !string.IsNullOrWhiteSpace(attachment.RelativePath))
            {
                var rel = attachment.RelativePath.Trim();
                if (!Path.IsPathRooted(rel) && !rel.StartsWith('/') && !rel.StartsWith('\\'))
                {
                    var candidate = Path.GetFullPath(Path.Combine(effectiveDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (SandboxPathResolver.IsPathInsideDirectory(candidate, effectiveDir) && File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        if (attachment.IsSymlink && !string.IsNullOrWhiteSpace(attachment.SourceRef))
        {
            if (File.Exists(attachment.SourceRef))
            {
                return attachment.SourceRef;
            }

            var mapped = PathResolver.ToHostPath(attachment.SourceRef);
            if (!string.IsNullOrWhiteSpace(mapped) && File.Exists(mapped))
            {
                return mapped;
            }
        }

        return null;
    }

    private static async Task ApplySessionConfigurationAsync(
        StreamWriter stdin,
        JsonNode? sessionResult,
        string activeSessionId,
        AgentSessionContext context,
        CancellationToken cancellationToken)
    {
        if (sessionResult == null)
        {
            return;
        }

        var availableModels = sessionResult["models"]?["availableModels"] as JsonArray;
        var configOptions = sessionResult["configOptions"] as JsonArray;
        var hasAvailableModels = availableModels != null && availableModels.Count > 0;
        var hasConfigOptions = configOptions != null && configOptions.Count > 0;
        var isReadOnly = AcpProcessLauncher.IsReadOnlySession(context);

        if (!hasAvailableModels && !hasConfigOptions && !isReadOnly)
        {
            return;
        }

        var modelName = context.Profile.ModelName?.Trim();
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var mappedThoughtLevel = MapThinkingEffort(context.Profile.ThinkingEffort);
            if (TryFindExactAvailableModelId(availableModels, modelName, out var exactModelId))
            {
                await WriteSetModelRequestAsync(stdin, activeSessionId, exactModelId, cancellationToken);
            }
            else if (HasConfigOption(configOptions, "model"))
            {
                await WriteSetConfigOptionRequestAsync(stdin, "cfg-opt-1", activeSessionId, "model", modelName, cancellationToken);

                if (mappedThoughtLevel != null && HasConfigOption(configOptions, "thought_level"))
                {
                    var supportedThoughtLevels = GetConfigOptionValues(configOptions, "thought_level");
                    var effectiveThoughtLevel = ResolveSupportedThoughtLevel(mappedThoughtLevel, supportedThoughtLevels);
                    await WriteSetConfigOptionRequestAsync(stdin, "cfg-opt-2", activeSessionId, "thought_level", effectiveThoughtLevel, cancellationToken);
                }
            }
            else if (hasAvailableModels && TryResolveAvailableModelId(availableModels, modelName, mappedThoughtLevel, out var resolvedModelId))
            {
                await WriteSetModelRequestAsync(stdin, activeSessionId, resolvedModelId, cancellationToken);
            }
        }

        if (isReadOnly || HasConfigOption(configOptions, "mode"))
        {
            var targetMode = isReadOnly ? "plan" : "accept-edits";
            await WriteSetConfigOptionRequestAsync(stdin, "cfg-opt-mode", activeSessionId, "mode", targetMode, cancellationToken);
        }
    }

    private static Task WriteSetModelRequestAsync(
        StreamWriter stdin,
        string activeSessionId,
        string modelId,
        CancellationToken cancellationToken)
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "cfg-model-1",
            method = "session/set_model",
            @params = new
            {
                sessionId = activeSessionId,
                modelId
            }
        });
        return stdin.WriteLineAsync(req.AsMemory(), cancellationToken);
    }

    private static Task WriteSetConfigOptionRequestAsync(
        StreamWriter stdin,
        string requestId,
        string activeSessionId,
        string configId,
        string value,
        CancellationToken cancellationToken)
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "session/set_config_option",
            @params = new
            {
                sessionId = activeSessionId,
                configId,
                value
            }
        });
        return stdin.WriteLineAsync(req.AsMemory(), cancellationToken);
    }

    private string ResolveEffectiveSessionWorkingDirectory(AgentSessionContext context, string workspacePath)
        => context.Workspace?.ExecutionMode switch
        {
            ExecutionMode.ContainerSandbox => ResolveContainerSessionWorkingDirectory(context),
            ExecutionMode.WslDirect => ResolveWslDirectSessionWorkingDirectory(context),
            _ => workspacePath
        };

    private static string BuildNewSessionRequestJson(
        string requestId,
        string sessionWorkingDir,
        object mcpServers,
        string? systemPrompt)
    {
        object sessionParams = !string.IsNullOrWhiteSpace(systemPrompt)
            ? new
            {
                cwd = sessionWorkingDir,
                mcpServers,
                _meta = new
                {
                    antigravity = new
                    {
                        systemPrompt = new
                        {
                            append = systemPrompt
                        }
                    }
                }
            }
            : new
            {
                cwd = sessionWorkingDir,
                mcpServers
            };

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "session/new",
            @params = sessionParams
        });
    }

    private static bool IsAuthenticationError(JsonNode errNode)
    {
        if (int.TryParse(errNode["code"]?.ToString(), out var code) && code == JsonRpcAuthRequired)
        {
            return true;
        }

        var message = errNode["message"]?.ToString() ?? errNode.ToJsonString();
        return message.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonTerminalToolStatus(string? status) =>
        string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);

    private static string FormatJsonOrString(JsonNode? node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        if (node is JsonValue val)
        {
            return val.TryGetValue<string>(out var str) ? str : val.ToJsonString();
        }

        if (node is JsonObject obj && TryExtractTextContentBlock(obj, out var objText))
        {
            return objText!;
        }

        if (node is JsonArray arr && arr.Count > 0)
        {
            var extractedTexts = new List<string>(arr.Count);
            foreach (var item in arr)
            {
                if (item is JsonObject itemObj && TryExtractTextContentBlock(itemObj, out var itemText))
                {
                    extractedTexts.Add(itemText!);
                }
                else
                {
                    return arr.ToJsonString();
                }
            }

            return string.Join("\n", extractedTexts);
        }

        return node.ToJsonString();
    }

    private static bool TryExtractTextContentBlock(JsonObject obj, out string? text)
    {
        if (obj["text"] is JsonValue textVal &&
            textVal.TryGetValue<string>(out var directText) &&
            (obj.Count == 1 || (obj.Count == 2 && string.Equals(obj["type"]?.ToString(), "text", StringComparison.OrdinalIgnoreCase))))
        {
            text = directText;
            return true;
        }

        if (obj["content"] is JsonObject innerObj &&
            (obj.Count == 1 || (obj.Count == 2 && string.Equals(obj["type"]?.ToString(), "content", StringComparison.OrdinalIgnoreCase))) &&
            TryExtractTextContentBlock(innerObj, out var nestedText))
        {
            text = nestedText;
            return true;
        }

        text = null;
        return false;
    }

    private static string? ExtractAvailableModelId(JsonNode? item) =>
        item is JsonValue
            ? item.ToString()
            : (item?["modelId"]?.ToString() ?? item?["id"]?.ToString());

    private static bool TryFindExactAvailableModelId(JsonArray? availableModels, string modelName, out string modelId)
    {
        if (availableModels != null)
        {
            foreach (var item in availableModels)
            {
                var id = ExtractAvailableModelId(item);
                if (!string.IsNullOrWhiteSpace(id) && string.Equals(id, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    modelId = id;
                    return true;
                }
            }
        }

        modelId = string.Empty;
        return false;
    }

    private static bool TryResolveAvailableModelId(
        JsonArray? availableModels,
        string modelName,
        string? mappedThoughtLevel,
        out string modelId)
    {
        if (TryFindExactAvailableModelId(availableModels, modelName, out modelId))
        {
            return true;
        }

        if (availableModels != null)
        {
            var ids = availableModels
                .Select(ExtractAvailableModelId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToList();

            if (!string.IsNullOrWhiteSpace(mappedThoughtLevel))
            {
                var preferred = $"{modelName}-{mappedThoughtLevel}";
                var exactCombined = ids.FirstOrDefault(id => string.Equals(id, preferred, StringComparison.OrdinalIgnoreCase));
                if (exactCombined != null)
                {
                    modelId = exactCombined;
                    return true;
                }
            }

            var prefix = $"{modelName}-";
            var prefixedMatch = ids.FirstOrDefault(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (prefixedMatch != null)
            {
                modelId = prefixedMatch;
                return true;
            }
        }

        modelId = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> GetConfigOptionValues(JsonArray? configOptions, string configId)
    {
        if (configOptions == null)
        {
            return [];
        }

        var cfg = configOptions.FirstOrDefault(opt => string.Equals(opt?["id"]?.ToString(), configId, StringComparison.OrdinalIgnoreCase));
        var choices = cfg?["options"] as JsonArray ?? cfg?["values"] as JsonArray;
        if (choices == null)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var opt in choices)
        {
            var val = opt is JsonValue
                ? opt.ToString()
                : (opt?["value"]?.ToString() ?? opt?["id"]?.ToString());
            if (!string.IsNullOrWhiteSpace(val))
            {
                values.Add(val);
            }
        }

        return values;
    }

    private static string ResolveSupportedThoughtLevel(string mappedThoughtLevel, IReadOnlyList<string> supportedValues)
    {
        if (supportedValues.Count == 0)
        {
            return mappedThoughtLevel;
        }

        var match = supportedValues.FirstOrDefault(v => string.Equals(v, mappedThoughtLevel, StringComparison.OrdinalIgnoreCase));
        return match ?? supportedValues[0];
    }

    private static bool HasConfigOption(JsonArray? configOptions, string configId)
    {
        if (configOptions == null)
        {
            return false;
        }

        return configOptions.Any(opt => string.Equals(opt?["id"]?.ToString(), configId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MapThinkingEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High or ThinkingEffort.CustomBudget => "high",
        _ => null
    };

    private static void ExtractTokenUsage(
        JsonNode? usageNode,
        ref long promptTokens,
        ref long completionTokens,
        ref long totalTokens,
        ref int? cachedInputTokens)
    {
        if (usageNode == null)
        {
            return;
        }

        if (TryGetLong(usageNode["inputTokens"], out var input))
        {
            promptTokens = Math.Max(promptTokens, input);
        }

        if (TryGetLong(usageNode["cachedInputTokens"], out var cached))
        {
            cachedInputTokens = (int)Math.Min(int.MaxValue, cached);
        }

        long output = 0;
        if (TryGetLong(usageNode["outputTokens"], out var outVal))
        {
            output += outVal;
        }
        if (TryGetLong(usageNode["thoughtTokens"], out var thoughtVal))
        {
            output += thoughtVal;
        }
        if (output > 0)
        {
            completionTokens = Math.Max(completionTokens, output);
        }

        if (TryGetLong(usageNode["totalTokens"], out var total))
        {
            totalTokens = Math.Max(totalTokens, total);
        }
    }

    private static bool TryGetLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node == null)
        {
            return false;
        }

        return long.TryParse(node.ToString(), out value);
    }

    private static bool IsPromptPhaseMessage(JsonNode node, string? method, string? id)
    {
        if (string.Equals(id, PromptRequestId, StringComparison.Ordinal) ||
            node["result"]?["stopReason"] != null)
        {
            return true;
        }

        if (string.Equals(method, "session/request_permission", StringComparison.Ordinal))
        {
            return true;
        }

        if (method != null &&
            (method.StartsWith("terminal/", StringComparison.Ordinal) ||
             method.StartsWith("fs/", StringComparison.Ordinal)))
        {
            return true;
        }

        if (string.Equals(method, "session/update", StringComparison.Ordinal))
        {
            var updateType = node["params"]?["update"]?["sessionUpdate"]?.ToString();
            return updateType is "agent_thought_chunk" or "agent_message_chunk" or "tool_call" or "tool_call_update" or "turn_end" or "message_end";
        }

        return false;
    }

    private static bool TryParseJsonNode(string line, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(line);
            return node != null;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }
}
