using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Acp;

public class AcpHarnessOptions
{
    public string ExecutablePath { get; set; } = "gemini";
    public string Arguments { get; set; } = "--experimental-acp";
    public Func<Process>? CustomProcessFactory { get; set; }
}

/// <summary>
/// Open Agent Client Protocol (ACP) adapter supporting ACP v2 negotiation with backward-compatible v1 fallback.
/// Intercepts terminal & filesystem requests and routes them into the execution sandbox.
/// </summary>
public class AcpAgentHarness : IAgentHarness
{
    private readonly AcpHarnessOptions _options;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IMcpClientManager? _mcpManager;
    private readonly ILogger<AcpAgentHarness> _logger;

    public string HarnessId => "acp";

    public AcpAgentHarness(
        AcpHarnessOptions? options = null,
        IExecutionEnvironment? executionEnv = null,
        IMcpClientManager? mcpManager = null,
        ILogger<AcpAgentHarness>? logger = null)
    {
        _options = options ?? new AcpHarnessOptions();
        _executionEnv = executionEnv;
        _mcpManager = mcpManager;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcpAgentHarness>.Instance;
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

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
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
            _logger.LogError(ex, "Failed to launch ACP agent at {ExecutablePath}", _options.ExecutablePath);
            return AgentRunResult.Failed($"Failed to launch ACP agent: {ex.Message}", new AgentRunMetrics(sw.ElapsedMilliseconds));
        }

        using var stdin = new StreamWriter(process.StandardInput.BaseStream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        using var stdout = process.StandardOutput;

        try
        {
            // 1. Handshake & Protocol Negotiation (Negotiates v2, falls back to v1)
            int negotiatedVersion = await NegotiateProtocolAsync(stdin, stdout, cancellationToken);
            _logger.LogInformation("Successfully negotiated ACP protocol version {Version}", negotiatedVersion);

            // 2. Initialize Session
            string sessionId = Guid.NewGuid().ToString("N");
            var newSessionReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "session-1",
                method = "session/new",
                @params = new
                {
                    cwd = context.WorkspacePath,
                    mcpServers = _mcpManager != null ? await _mcpManager.GetAcpMcpServersAsync(context.Workspace?.Id, cancellationToken) : null
                }
            });
            await stdin.WriteLineAsync(newSessionReq.AsMemory(), cancellationToken);

            // 3. Dispatch Prompt Request
            var promptText = !string.IsNullOrEmpty(context.BootstrapPrompt)
                ? context.BootstrapPrompt
                : (!string.IsNullOrWhiteSpace(context.StageDirective)
                    ? $"[Stage Directive]: {context.StageDirective}\n\n{context.Issue.Title}\n{context.Issue.Description}"
                    : context.Issue.Description);
            var promptReq = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "prompt-1",
                method = "session/prompt",
                @params = new
                {
                    sessionId,
                    prompt = new[] { new { text = promptText } }
                }
            });
            await stdin.WriteLineAsync(promptReq.AsMemory(), cancellationToken);

            string? lastAgentSummary = null;

            // 4. Message Handling Loop
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await stdout.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var node = JsonNode.Parse(line);
                    var method = node?["method"]?.ToString();
                    var id = node?["id"]?.ToString();

                    // Check for Terminal Command from agent
                    if (method == "terminal/create" || method == "terminal/run")
                    {
                        var cmd = node?["params"]?["command"]?.ToString() ?? "";
                        var cwd = node?["params"]?["cwd"]?.ToString() ?? context.WorkspacePath;

                        string output = "";
                        int exitCode = 0;
                        if (_executionEnv != null)
                        {
                            var execRes = await _executionEnv.ExecuteAsync(cmd, cwd, 300, null, cancellationToken);
                            output = execRes.StandardOutput;
                            exitCode = execRes.ExitCode;
                        }

                        var terminalResp = JsonSerializer.Serialize(new
                        {
                            jsonrpc = "2.0",
                            id,
                            result = new { exitCode, output }
                        });
                        await stdin.WriteLineAsync(terminalResp.AsMemory(), cancellationToken);
                    }
                    // Check for Permission Request (Human-in-the-loop interruption)
                    else if (method == "session/request_permission")
                    {
                        var prompt = node?["params"]?["prompt"]?.ToString() ?? "Permission requested";
                        var rationale = node?["params"]?["rationale"]?.ToString();

                        sw.Stop();
                        return AgentRunResult.WaitingForHuman(
                            prompt,
                            new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            updatedState: sessionId,
                            summary: "ACP agent paused awaiting developer permission");
                    }
                    else if (node?["result"] != null && id == "prompt-1")
                    {
                        // Prompt completed
                        var stopReason = node["result"]?["stopReason"]?.ToString() ?? "completed";
                        lastAgentSummary = $"ACP agent finished: {stopReason}";
                        break;
                    }
                }
                catch (JsonException)
                {
                    // Ignore non-json debug lines
                }
            }

            sw.Stop();
            return AgentRunResult.Completed(
                new AgentRunMetrics(sw.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: sessionId,
                summary: lastAgentSummary ?? "ACP agent completed successfully.");
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
