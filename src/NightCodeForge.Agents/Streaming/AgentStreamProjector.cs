using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Streaming;

/// <summary>
/// Projects Microsoft Agent Framework (MAF) AgentResponseUpdate items into domain events
/// (IdeationStreamUpdate) and broadcasts telemetry to IAgentActivityStreamer.
/// Taps TextReasoningContent, FunctionCallContent, FunctionResultContent, and TextContent.
/// </summary>
public class AgentStreamProjector
{
    private readonly Dictionary<string, (string ToolName, string Arguments, Stopwatch Sw)> _activeToolCalls = new();

    public IEnumerable<IdeationStreamUpdate> ProjectUpdate(AgentResponseUpdate update)
    {
        // 1. Process structured reasoning tokens & tool calls
        if (update.Contents != null)
        {
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                {
                    yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: reasoning.Text);
                }
                else if (content is FunctionCallContent fc)
                {
                    var callId = fc.CallId ?? Guid.NewGuid().ToString("N");
                    var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                    var toolInfo = new IdeationToolCallInfo(callId, fc.Name, argsJson);
                    var sw = Stopwatch.StartNew();
                    _activeToolCalls[callId] = (fc.Name, argsJson, sw);
                    yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: toolInfo);
                }
                else if (content is FunctionResultContent frc)
                {
                    var callId = frc.CallId;
                    var resultStr = frc.Result is Exception ex ? $"Exception: {ex.GetType().Name}: {ex.Message} -> {ex.InnerException?.Message}" : frc.Result?.ToString() ?? string.Empty;
                    bool isError = frc.Result is Exception || resultStr.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
                    long durationMs = 0;
                    string toolName = "tool";
                    string argsJson = "{}";

                    if (!string.IsNullOrEmpty(callId) && _activeToolCalls.TryGetValue(callId, out var callState))
                    {
                        callState.Sw.Stop();
                        durationMs = callState.Sw.ElapsedMilliseconds;
                        toolName = callState.ToolName;
                        argsJson = callState.Arguments;
                        _activeToolCalls.Remove(callId);
                    }

                    if (isError && !resultStr.Contains("Error executing tool", StringComparison.OrdinalIgnoreCase))
                    {
                        resultStr = $"Error executing tool '{toolName}': {resultStr}";
                    }

                    var toolInfo = new IdeationToolCallInfo(callId ?? Guid.NewGuid().ToString("N"), toolName, argsJson, resultStr, isError, durationMs);
                    yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: toolInfo);
                }
            }
        }

        // 2. Process text tokens
        if (!string.IsNullOrEmpty(update.Text))
        {
            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.MessageDelta, TextDelta: update.Text);
        }
    }

    /// <summary>Retained for call-site symmetry; no buffered state remains to flush.</summary>
    public IEnumerable<IdeationStreamUpdate> Flush() => Array.Empty<IdeationStreamUpdate>();
}
