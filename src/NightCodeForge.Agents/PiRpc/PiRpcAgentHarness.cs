using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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
/// </summary>
public class PiRpcAgentHarness : IAgentHarness
{
    private readonly PiRpcHarnessOptions _options;
    private readonly ILogger<PiRpcAgentHarness> _logger;

    public string HarnessId => "pi";

    public PiRpcAgentHarness(
        PiRpcHarnessOptions? options = null,
        ILogger<PiRpcAgentHarness>? logger = null)
    {
        _options = options ?? new PiRpcHarnessOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PiRpcAgentHarness>.Instance;
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        long promptTokens = 0;
        long completionTokens = 0;

        Process process;
        if (_options.CustomProcessFactory != null)
        {
            process = _options.CustomProcessFactory();
        }
        else
        {
            var psi = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                Arguments = _options.Arguments,
                WorkingDirectory = !string.IsNullOrEmpty(context.WorkspacePath) ? context.WorkspacePath : Directory.GetCurrentDirectory(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process = new Process { StartInfo = psi };
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Pi RPC daemon at {ExecutablePath}", _options.ExecutablePath);
            return AgentRunResult.Failed($"Failed to launch Pi daemon: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
        }

        using var stdin = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        using var stdout = process.StandardOutput;

        try
        {
            // 1. Configure Model if specified
            if (!string.IsNullOrWhiteSpace(context.Profile.ModelName))
            {
                var setModelCmd = JsonSerializer.Serialize(new
                {
                    id = $"req-{Guid.NewGuid():N}",
                    type = "set_model",
                    provider = "ollama",
                    modelId = context.Profile.ModelName
                });
                await stdin.WriteLineAsync(setModelCmd.AsMemory(), cancellationToken);
            }

            // 2. Dispatch prompt command
            var promptText = !string.IsNullOrEmpty(context.BootstrapPrompt) ? context.BootstrapPrompt : context.Issue.Description;
            var promptCmd = JsonSerializer.Serialize(new
            {
                id = $"req-{Guid.NewGuid():N}",
                type = "prompt",
                message = promptText
            });
            await stdin.WriteLineAsync(promptCmd.AsMemory(), cancellationToken);

            string? lastAgentSummary = null;
            string? pendingQuestion = null;

            // 3. Read JSONL events from stdout
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    var type = node?["type"]?.ToString();

                    if (type == "extension_ui_request")
                    {
                        // Intercept human-in-the-loop interruption from Pi
                        var prompt = node?["message"]?.ToString() ?? "Human input requested by Pi";
                        var rationale = node?["rationale"]?.ToString();
                        pendingQuestion = prompt;

                        sw.Stop();
                        return AgentRunResult.WaitingForHuman(
                            prompt,
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            summary: "Pi daemon paused awaiting developer answer");
                    }
                    else if (type == "message_update")
                    {
                        var text = node?["delta"]?.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            lastAgentSummary = (lastAgentSummary ?? "") + text;
                        }
                    }
                    else if (type == "usage")
                    {
                        if (long.TryParse(node?["prompt_tokens"]?.ToString(), out var pt)) promptTokens += pt;
                        if (long.TryParse(node?["completion_tokens"]?.ToString(), out var ct)) completionTokens += ct;
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
            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                summary: lastAgentSummary ?? "Pi agent completed successfully.");
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
}
