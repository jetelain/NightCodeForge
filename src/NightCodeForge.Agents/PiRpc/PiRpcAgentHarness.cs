using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.PiRpc;

public class PiRpcHarnessOptions
{
    public string ExecutablePath { get; set; } = "node";
    public string Arguments { get; set; } = "./packages/coding-agent/dist/rpc-entry.js --mode rpc";
    public Func<Process>? CustomProcessFactory { get; set; }
}

/// <summary>
/// Bridge harness communicating with Pi coding-agent daemon over line-delimited JSON (JSONL).
/// Supports live streaming via IAgentActivityStreamer and session markdown transcripts.
/// </summary>
public class PiRpcAgentHarness : ExternalProcessAgentHarnessBase
{
    private readonly PiRpcHarnessOptions _options;
    private readonly Inference.LlmProviderOptions _llmOptions;
    private readonly ILogger<PiRpcAgentHarness> _logger;

    public override string HarnessId => StandardHarnessIds.PiRpc;

    protected override string AgentDisplayName => "Pi daemon";
    protected override string DefaultExecutablePath => _options.ExecutablePath;
    protected override string DefaultArguments => _options.Arguments;
    protected override Func<Process>? CustomProcessFactory => _options.CustomProcessFactory;

    public PiRpcAgentHarness(
        Microsoft.Extensions.Options.IOptions<PiRpcHarnessOptions> options,
        Microsoft.Extensions.Options.IOptions<Inference.LlmProviderOptions> llmOptions,
        IHarnessActivityRecorder recorder,
        Context.AgentsMdService agentsMdService,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        ILogger<PiRpcAgentHarness> logger)
        : base(recorder, agentsMdService, pathResolver, processRunner, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(llmOptions);
        _llmOptions = llmOptions.Value ?? throw new ArgumentNullException(nameof(llmOptions));
        _logger = logger;
    }

    /// <summary>
    /// Executes the Pi JSONL RPC message exchange against input and output streams.
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
        var (_, localWorkspaceRoot) = ResolveWorkspacePaths(context);

        try
        {
            // 1. Configure Model if specified or fallback to default from appsettings
            var effectiveModel = !string.IsNullOrWhiteSpace(context.Profile.ModelName)
                ? context.Profile.ModelName
                : _llmOptions.ModelId;

            if (!string.IsNullOrWhiteSpace(effectiveModel))
            {
                var setModelCmd = JsonSerializer.Serialize(new
                {
                    id = $"req-{Guid.NewGuid():N}",
                    type = "set_model",
                    provider = "ollama",
                    modelId = effectiveModel
                });
                await stdin.WriteLineAsync(setModelCmd.AsMemory(), cancellationToken);
            }

            // 2. Dispatch prompt command (including pending human answer if resuming)
            var promptText = BuildEnrichedPrompt(context, localWorkspaceRoot);


            var promptCmd = JsonSerializer.Serialize(new
            {
                id = $"req-{Guid.NewGuid():N}",
                type = "prompt",
                message = promptText
            });
            await stdin.WriteLineAsync(promptCmd.AsMemory(), cancellationToken);

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

            // 3. Read JSONL events from stdout
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    var type = node?["type"]?.ToString();

                    // Intercept human-in-the-loop interruption from Pi
                    if (type == "extension_ui_request")
                    {
                        FlushTurn();
                        var title = node?["title"]?.ToString();
                        var msg = node?["message"]?.ToString();
                        var prompt = !string.IsNullOrWhiteSpace(title)
                            ? (string.IsNullOrWhiteSpace(msg) ? title : $"{title}\n{msg}")
                            : (msg ?? "Human input requested by Pi");

                        string? optionsJson = null;
                        if (node?["options"] is JsonArray optsArray && optsArray.Count > 0)
                        {
                            prompt += "\nOptions:\n" + string.Join("\n", optsArray.Select(o => $"- {o}"));
                            optionsJson = JsonSerializer.Serialize(optsArray.Select(o => o?.ToString()).Where(s => !string.IsNullOrEmpty(s)).ToList());
                        }

                        sw.Stop();
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, prompt, cancellationToken);

                        await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, lastTurnConcludingText, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken);

                        var question = new Question
                        {
                            TaskId = context.ForgeTask.Id,
                            StageId = context.StageId,
                            Prompt = prompt,
                            OptionsJson = optionsJson,
                            Status = QuestionStatus.Pending,
                            AskedAt = DateTime.UtcNow
                        };

                        return AgentRunResult.WaitingForHuman(
                            new[] { question },
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            updatedState: sessionId,
                            summary: "Pi daemon paused awaiting developer answer");
                    }
                    else if (type == "message_update")
                    {
                        // Check usage block
                        if (node?["usage"] is JsonObject usageObj)
                        {
                            if (long.TryParse(usageObj["prompt_tokens"]?.ToString(), out var pt)) promptTokens = Math.Max(promptTokens, pt);
                            if (long.TryParse(usageObj["completion_tokens"]?.ToString(), out var ct)) completionTokens = Math.Max(completionTokens, ct);
                        }

                        // Check assistantMessageEvent
                        var ame = node?["assistantMessageEvent"];
                        var ameType = ame?["type"]?.ToString();

                        if (ameType == "thinking_delta")
                        {
                            var thinkingText = ame?["delta"]?.ToString();
                            if (!string.IsNullOrEmpty(thinkingText))
                            {
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, thinkingText, cancellationToken);
                            }
                        }
                        else if (ameType == "text_delta")
                        {
                            var text = ame?["delta"]?.ToString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                currentTurnText.Append(text);
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, text, cancellationToken);
                            }
                        }
                        else if (ameType == "toolcall_start")
                        {
                            FlushTurn();
                            var toolName = ame?["toolName"]?.ToString() ?? ame?["id"]?.ToString() ?? "tool";
                            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, toolName, cancellationToken);
                        }
                        else if (ameType == "toolcall_end")
                        {
                            var callId = ame?["id"]?.ToString() ?? "tool";
                            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, callId, cancellationToken);
                        }
                        else if (ameType is "turn_end" or "message_end")
                        {
                            FlushTurn();
                        }
                        else
                        {
                            // Flat delta fallback
                            var delta = node?["delta"]?.ToString();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                currentTurnText.Append(delta);
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, delta, cancellationToken);
                            }
                        }
                    }
                    else if (type == "tool_execution_start")
                    {
                        FlushTurn();
                        var tool = node?["toolName"]?.ToString() ?? node?["tool"]?.ToString() ?? "tool";
                        var args = node?["args"]?.ToString() ?? node?["arguments"]?.ToString() ?? "";
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{tool}: {args}", cancellationToken);
                    }
                    else if (type == "tool_execution_end")
                    {
                        var tool = node?["toolName"]?.ToString() ?? node?["tool"]?.ToString() ?? "tool";
                        var result = node?["result"]?.ToString() ?? node?["output"]?.ToString() ?? "";
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, $"{tool}: {result}", cancellationToken);
                    }
                    else if (type is "turn_end" or "message_end")
                    {
                        FlushTurn();
                    }
                    else if (type == "usage")
                    {
                        if (long.TryParse(node?["prompt_tokens"]?.ToString(), out var pt)) promptTokens = Math.Max(promptTokens, pt);
                        if (long.TryParse(node?["completion_tokens"]?.ToString(), out var ct)) completionTokens = Math.Max(completionTokens, ct);
                    }
                    else if (type == "response" && node?["command"]?.ToString() == "prompt" && node?["success"]?.GetValue<bool>() == false)
                    {
                        var err = node?["error"]?.ToString() ?? "Pi prompt command failed";
                        throw new InvalidOperationException(err);
                    }
                    else if (type == "agent_settled" || type == "completed")
                    {
                        FlushTurn();
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON diagnostic output from Pi
                }
            }

            FlushTurn();
            sw.Stop();
            var extraction = StageSummaryExtractor.Extract(lastTurnConcludingText, fallbackSummary: "Pi agent completed successfully.");

            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, extraction.Summary, cancellationToken);

            await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, extraction.Summary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: sessionId,
                summary: extraction.Summary,
                targetRouting: extraction.TargetRouting,
                reviewerFeedback: extraction.ReviewerFeedback);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var abortCmd = JsonSerializer.Serialize(new { id = $"abort-{Guid.NewGuid():N}", type = "abort" });
                await stdin.WriteLineAsync(abortCmd.AsMemory());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send abort command to PiRpc process on cancellation");
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
    }
}
