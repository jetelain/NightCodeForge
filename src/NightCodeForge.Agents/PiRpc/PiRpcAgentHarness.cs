using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Transcripts;
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
public class PiRpcAgentHarness : IAgentHarness
{
    private readonly PiRpcHarnessOptions _options;
    private readonly Microsoft.Extensions.Options.IOptions<Inference.LlmProviderOptions>? _llmOptions;
    private readonly Context.AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly SessionTranscriptService? _transcriptService;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<PiRpcAgentHarness> _logger;

    public string HarnessId => HarnessType.PiRpc;

    public PiRpcAgentHarness(
        PiRpcHarnessOptions? options = null,
        Microsoft.Extensions.Options.IOptions<Inference.LlmProviderOptions>? llmOptions = null,
        Context.AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        SessionTranscriptService? transcriptService = null,
        IWorkspacePathResolver? pathResolver = null,
        IProcessRunner? processRunner = null,
        ILogger<PiRpcAgentHarness>? logger = null)
    {
        _options = options ?? new PiRpcHarnessOptions();
        _llmOptions = llmOptions;
        _agentsMdService = agentsMdService ?? new Context.AgentsMdService();
        _activityStreamer = activityStreamer;
        _transcriptService = transcriptService;
        _pathResolver = pathResolver;
        _processRunner = processRunner ?? new SystemProcessRunner();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PiRpcAgentHarness>.Instance;
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
                _logger.LogError(ex, "Failed to launch Pi RPC daemon via custom factory");
                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
                return AgentRunResult.Failed($"Failed to launch Pi daemon: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
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
            _logger.LogError(ex, "Failed to launch Pi RPC daemon at {ExecutablePath}", _options.ExecutablePath);
            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
            return AgentRunResult.Failed($"Failed to launch Pi daemon: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
        }

        await using (session.ConfigureAwait(false))
        {
            return await RunSessionStreamAsync(session.StandardInput, session.StandardOutput, context, sessionId, sw, cancellationToken);
        }
    }

    /// <summary>
    /// Executes the Pi JSONL RPC message exchange against input and output streams.
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

        try
        {
            // 1. Configure Model if specified or fallback to default from appsettings
            var effectiveModel = !string.IsNullOrWhiteSpace(context.Profile.ModelName)
                ? context.Profile.ModelName
                : _llmOptions?.Value?.ModelId;

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

            var promptCmd = JsonSerializer.Serialize(new
            {
                id = $"req-{Guid.NewGuid():N}",
                type = "prompt",
                message = promptText
            });
            await stdin.WriteLineAsync(promptCmd.AsMemory(), cancellationToken);

            string? lastAgentSummary = null;

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
                        var title = node?["title"]?.ToString();
                        var msg = node?["message"]?.ToString();
                        var prompt = !string.IsNullOrWhiteSpace(title)
                            ? (string.IsNullOrWhiteSpace(msg) ? title : $"{title}\n{msg}")
                            : (msg ?? "Human input requested by Pi");

                        if (node?["options"] is JsonArray optsArray && optsArray.Count > 0)
                        {
                            prompt += "\nOptions:\n" + string.Join("\n", optsArray.Select(o => $"- {o}"));
                        }

                        sw.Stop();
                        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Interrupted, prompt, cancellationToken);

                        await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, lastAgentSummary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken);

                        return AgentRunResult.WaitingForHuman(
                            prompt,
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
                                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ThinkingDelta, thinkingText, cancellationToken);
                            }
                        }
                        else if (ameType == "text_delta")
                        {
                            var text = ame?["delta"]?.ToString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                lastAgentSummary = (lastAgentSummary ?? "") + text;
                                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.MessageDelta, text, cancellationToken);
                            }
                        }
                        else if (ameType == "toolcall_start")
                        {
                            var toolName = ame?["toolName"]?.ToString() ?? ame?["id"]?.ToString() ?? "tool";
                            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolStarted, toolName, cancellationToken);
                        }
                        else if (ameType == "toolcall_end")
                        {
                            var callId = ame?["id"]?.ToString() ?? "tool";
                            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, callId, cancellationToken);
                        }
                        else
                        {
                            // Flat delta fallback
                            var delta = node?["delta"]?.ToString();
                            if (!string.IsNullOrEmpty(delta))
                            {
                                lastAgentSummary = (lastAgentSummary ?? "") + delta;
                                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.MessageDelta, delta, cancellationToken);
                            }
                        }
                    }
                    else if (type == "tool_execution_start")
                    {
                        var tool = node?["toolName"]?.ToString() ?? node?["tool"]?.ToString() ?? "tool";
                        var args = node?["args"]?.ToString() ?? node?["arguments"]?.ToString() ?? "";
                        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolStarted, $"{tool}: {args}", cancellationToken);
                    }
                    else if (type == "tool_execution_end")
                    {
                        var tool = node?["toolName"]?.ToString() ?? node?["tool"]?.ToString() ?? "tool";
                        var result = node?["result"]?.ToString() ?? node?["output"]?.ToString() ?? "";
                        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, $"{tool}: {result}", cancellationToken);
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
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Non-JSON diagnostic output from Pi
                }
            }

            sw.Stop();
            var finalSummary = lastAgentSummary ?? "Pi agent completed successfully.";
            await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.StageCompleted, finalSummary, cancellationToken);

            await SaveTranscriptAsync(context, localWorkspaceRoot, promptText, finalSummary, sw.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: sessionId,
                summary: finalSummary);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var abortCmd = JsonSerializer.Serialize(new { id = $"abort-{Guid.NewGuid():N}", type = "abort" });
                await stdin.WriteLineAsync(abortCmd.AsMemory());
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
}

