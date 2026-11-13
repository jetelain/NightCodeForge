using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

using NightCodeForge.Agents.Context;
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
/// Delegates host-side terminal and filesystem callbacks to <see cref="IAcpHostRpcHandler"/>,
/// streaming live activity to IAgentActivityStreamer and persisting session markdown transcripts.
/// </summary>
public class AcpAgentHarness : ExternalProcessAgentHarnessBase
{
    private const int JsonRpcInvalidParams = -32602;

    private readonly AcpHarnessOptions _options;
    private readonly IMcpClientManager _mcpManager;
    private readonly IAcpHostRpcHandler _hostRpcHandler;
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
        IHarnessActivityRecorder recorder,
        AgentsMdService agentsMdService,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        ILogger<AcpAgentHarness> logger)
        : base(recorder, agentsMdService, pathResolver, processRunner, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _hostRpcHandler = hostRpcHandler ?? throw new ArgumentNullException(nameof(hostRpcHandler));
        _logger = logger;
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
        var (workspacePath, localWorkspaceRoot) = ResolveWorkspacePaths(context);
        var sandboxPathResolver = context.SandboxScope != null
            ? new SandboxPathResolver(context.SandboxScope)
            : null;
        var hostContext = new AcpHostRpcContext(sessionId, context, workspacePath, localWorkspaceRoot, sandboxPathResolver);

        try
        {
            // 1. Handshake & Protocol Negotiation (Negotiates v2, falls back to v1)
            int negotiatedVersion = await NegotiateProtocolAsync(stdin, stdout, cancellationToken, _logger);
            _logger.LogInformation("Successfully negotiated ACP protocol version {Version}", negotiatedVersion);

            // 2. Initialize or Resume Session
            string activeSessionId = sessionId;
            bool isReconstructedHistory = ConversationHistorySerializer.IsSerializedHistory(context.SerializedStateJson);

            if (!string.IsNullOrEmpty(context.SerializedStateJson) && !isReconstructedHistory)
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
                        mcpServers = await _mcpManager.GetAcpMcpServersAsync(context.Workspace?.Id, localWorkspaceRoot, cancellationToken)
                    }
                });
                await stdin.WriteLineAsync(newSessionReq.AsMemory(), cancellationToken);
            }

            // 3. Dispatch Prompt Request (incorporating human answer if resuming from permission request)
            var promptText = BuildEnrichedPrompt(context, localWorkspaceRoot);

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

            var currentTurnText = new StringBuilder();
            string? lastTurnConcludingText = null;

            void FlushTurn()
            {
                if (currentTurnText.Length > 0)
                {
                    var text = currentTurnText.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastTurnConcludingText = text;
                    }
                    currentTurnText.Clear();
                }
            }

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
                            FlushTurn();
                            var title = update?["title"]?.ToString() ?? update?["toolCallId"]?.ToString() ?? "tool";
                            var rawInput = update?["rawInput"]?.ToString() ?? "";
                            await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{title}: {rawInput}", cancellationToken);
                        }
                        else if (updateType == "tool_call_update")
                        {
                            var status = update?["status"]?.ToString() ?? "completed";
                            var rawOutput = update?["rawOutput"]?.ToString() ?? "";
                            await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, $"{status}: {rawOutput}", cancellationToken);
                        }
                        else if (updateType is "turn_end" or "message_end")
                        {
                            FlushTurn();
                        }
                        continue;
                    }

                    // 4.2 Host-side callbacks (terminal/* and fs/*)
                    if (method != null && _hostRpcHandler.CanHandle(method))
                    {
                        if (_hostRpcHandler.EndsAssistantTurn(method))
                        {
                            FlushTurn();
                        }

                        var rpcResult = await _hostRpcHandler.HandleAsync(method, node?["params"], hostContext, cancellationToken);
                        var rpcResp = rpcResult.IsError
                            ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code = JsonRpcInvalidParams, message = rpcResult.ErrorMessage } })
                            : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = rpcResult.Result });
                        await stdin.WriteLineAsync(rpcResp.AsMemory(), cancellationToken);
                        continue;
                    }

                    // 4.3 Permission Request (Human-in-the-loop interruption)
                    if (method == "session/request_permission")
                    {
                        FlushTurn();
                        var prompt = node?["params"]?["prompt"]?.ToString();
                        var toolCall = node?["params"]?["toolCall"]?.ToString();
                        var rationale = node?["params"]?["rationale"]?.ToString();
                        var finalPrompt = prompt ?? toolCall ?? "Permission requested by ACP agent";

                        if (!string.IsNullOrWhiteSpace(rationale))
                        {
                            finalPrompt += $" ({rationale})";
                        }

                        string? optionsJson = null;
                        if (node?["params"]?["options"] is JsonArray optsArr && optsArr.Count > 0)
                        {
                            finalPrompt += "\nOptions:\n" + string.Join("\n", optsArr.Select(o => $"- {o?["name"]?.ToString() ?? o?.ToString()}"));
                            optionsJson = JsonSerializer.Serialize(optsArr.Select(o => o?["name"]?.ToString() ?? o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList());
                        }

                        sw.Stop();
                        await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, finalPrompt, cancellationToken);

                        await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, lastTurnConcludingText, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken);

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
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            updatedState: activeSessionId,
                            summary: "ACP agent paused awaiting developer permission");
                    }

                    // 4.4 Prompt Response Completion
                    if (node?["result"] != null && (id == "prompt-1" || node["result"]?["stopReason"] != null))
                    {
                        FlushTurn();
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json debug lines
                }
            }

            FlushTurn();
            sw.Stop();
            var extraction = StageSummaryExtractor.Extract(lastTurnConcludingText, fallbackSummary: "ACP agent completed successfully.");

            await BroadcastAsync(activeSessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, extraction.Summary, cancellationToken);

            await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, extraction.Summary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: activeSessionId,
                summary: extraction.Summary,
                targetRouting: extraction.TargetRouting,
                reviewerFeedback: extraction.ReviewerFeedback);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var cancelReq = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "session/cancel" });
                await stdin.WriteLineAsync(cancelReq.AsMemory());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send session/cancel to ACP process on cancellation");
            }
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
            await SaveTranscriptAsync(context, localWorkspaceRoot, "", ex.Message, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Failed, cancellationToken);
            throw;
        }
        finally
        {
            _hostRpcHandler.EndSession(sessionId);
        }
    }

    public static async Task<int> NegotiateProtocolAsync(StreamWriter stdin, StreamReader stdout, CancellationToken cancellationToken, ILogger? logger = null)
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
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to parse ACP protocol version negotiation response: '{ResponseLine}'. Defaulting to protocol v1.", responseLine);
            }
        }

        // Default negotiated to v1
        return 1;
    }
}
