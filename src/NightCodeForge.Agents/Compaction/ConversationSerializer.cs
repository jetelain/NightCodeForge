using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// Serializes chat messages into structured plain text transcripts for summarization,
/// preventing the model from treating history as an ongoing conversation to continue.
/// Clamps individual tool results to bounded character limits and omits raw thinking traces.
/// </summary>
public static class ConversationSerializer
{
    public const int DefaultToolResultMaxChars = CrucibleCompactionOptions.DefaultToolResultMaxChars;
    public const int DefaultMaxConversationTokens =
        CrucibleCompactionOptions.DefaultMaxSummarizerInputTokens - CrucibleCompactionOptions.SummarizerPromptOverheadReserveTokens;

    private const int MediumToolResultMaxChars = 1000;
    private const int SmallToolResultMaxChars = 500;
    private const int MinimumToolResultMaxChars = 250;
    private const int UserMessageCapMultiplier = 4;
    private const int MinUserMessageMaxChars = 4000;
    private const int AssistantAndSystemCapMultiplier = 2;
    private const int MinAssistantAndSystemMaxChars = 2000;
    private const int ElisionMarkerFramingTokens = 8;
    private const int PartFramingTokens = ModelCalibratedTokenEstimator.PerMessageFramingTokens;
    private const int MinAvailableElisionTokens = 64;
    private const int MinSectionBudgetTokens = 32;
    private const int HeadBudgetDivisor = 4;
    private const int CharsPerTokenEstimate = 2;
    private const int MinTruncatedPartChars = 200;
    private const int MinFinalShrinkChars = 100;
    private const double FinalShrinkFactor = 0.8;
    private const string ElisionMarker = "[... older middle conversation turns elided to stay within summarizer context ceiling ...]";

    private static readonly int[] ProgressiveToolCharCaps =
    [
        DefaultToolResultMaxChars,
        MediumToolResultMaxChars,
        SmallToolResultMaxChars,
        MinimumToolResultMaxChars
    ];

    /// <summary>
    /// Serializes a collection of ChatMessages into a formatted transcript bounded within the target token budget.
    /// </summary>
    public static string SerializeConversation(
        IEnumerable<ChatMessage> messages,
        int maxToolResultChars = DefaultToolResultMaxChars,
        int maxTokens = DefaultMaxConversationTokens,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var estimator = ModelCalibratedTokenEstimator.Resolve(modelId);

        int effectiveInitialCap = maxToolResultChars > 0 ? maxToolResultChars : DefaultToolResultMaxChars;
        var candidateCaps = ProgressiveToolCharCaps
            .Where(c => c < effectiveInitialCap)
            .Prepend(effectiveInitialCap)
            .Distinct()
            .ToArray();

        string transcript = string.Empty;
        foreach (var cap in candidateCaps)
        {
            var parts = SerializeParts(messageList, cap);
            transcript = string.Join("\n\n", parts);
            if (maxTokens <= 0 || ModelCalibratedTokenEstimator.EstimateTextTokens(transcript, estimator) <= maxTokens)
            {
                return transcript;
            }
        }

        // Still over maxTokens at minimum tool char cap (250): elide oldest middle parts while preserving
        // initial user goal/checkpoint entries and newest conversational turns.
        var clampedParts = SerializeParts(messageList, candidateCaps[^1]);
        return ElideMiddlePartsToFitTokenBudget(clampedParts, maxTokens, estimator);
    }

    private static List<string> SerializeParts(IReadOnlyList<ChatMessage> messages, int maxToolResultChars)
    {
        var parts = new List<string>();
        int maxUserChars = Math.Max(maxToolResultChars * UserMessageCapMultiplier, MinUserMessageMaxChars);
        int maxAssistantOrSystemChars = Math.Max(maxToolResultChars * AssistantAndSystemCapMultiplier, MinAssistantAndSystemMaxChars);

        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.User)
            {
                var text = ExtractTextContent(msg);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.StartsWith(ConversationHistorySerializer.CompactionCheckpointHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add($"[Summary Checkpoint]: {TruncateForSummary(text, maxUserChars)}");
                    }
                    else
                    {
                        parts.Add($"[User]: {TruncateForSummary(text, maxUserChars)}");
                    }
                }
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                var text = ExtractTextContent(msg);
                var toolCalls = new List<string>();

                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fcc)
                    {
                        var argsStr = FormatArguments(fcc.Arguments, maxToolResultChars);
                        toolCalls.Add($"{fcc.Name}({argsStr})");
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add($"[Assistant]: {TruncateForSummary(text, maxAssistantOrSystemChars)}");
                }

                if (toolCalls.Count > 0)
                {
                    parts.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
                }
            }
            else if (msg.Role == ChatRole.Tool)
            {
                var resultText = ExtractToolResultText(msg);
                if (!string.IsNullOrWhiteSpace(resultText))
                {
                    parts.Add($"[Tool result]: {TruncateForSummary(resultText, maxToolResultChars)}");
                }
            }
            else if (msg.Role == ChatRole.System)
            {
                var text = ExtractTextContent(msg);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add($"[System]: {TruncateForSummary(text, maxAssistantOrSystemChars)}");
                }
            }
        }

        return parts;
    }

    internal static string ElideMiddlePartsToFitTokenBudget(
        IReadOnlyList<string> parts,
        int maxTokens,
        Pmad.AI.Tokens.HeuristicTokenEstimator estimator)
    {
        if (parts.Count == 0 || maxTokens <= 0)
        {
            return string.Join("\n\n", parts);
        }

        var full = string.Join("\n\n", parts);
        if (ModelCalibratedTokenEstimator.EstimateTextTokens(full, estimator) <= maxTokens)
        {
            return full;
        }

        int markerTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(ElisionMarker, estimator) + ElisionMarkerFramingTokens;
        int availableTokens = Math.Max(MinAvailableElisionTokens, maxTokens - markerTokens);

        int headBudget = Math.Max(MinSectionBudgetTokens, availableTokens / HeadBudgetDivisor);
        var headParts = new List<string>();
        int usedTokens = 0;
        int headEnd = 0;

        for (int i = 0; i < parts.Count; i++)
        {
            int partTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(parts[i], estimator) + PartFramingTokens;
            if (i == 0 && partTokens > headBudget)
            {
                int maxHeadChars = Math.Max(MinTruncatedPartChars, headBudget * CharsPerTokenEstimate);
                var truncatedFirst = TruncateForSummary(parts[i], maxHeadChars);
                headParts.Add(truncatedFirst);
                usedTokens += ModelCalibratedTokenEstimator.EstimateTextTokens(truncatedFirst, estimator) + PartFramingTokens;
                headEnd = 1;
                break;
            }

            if (usedTokens + partTokens > headBudget)
            {
                break;
            }

            headParts.Add(parts[i]);
            usedTokens += partTokens;
            headEnd = i + 1;
        }

        var tailParts = new List<string>();
        for (int i = parts.Count - 1; i >= headEnd; i--)
        {
            int partTokens = ModelCalibratedTokenEstimator.EstimateTextTokens(parts[i], estimator) + PartFramingTokens;
            if (usedTokens + partTokens > availableTokens)
            {
                if (tailParts.Count == 0)
                {
                    int remainingBudget = Math.Max(MinSectionBudgetTokens, availableTokens - usedTokens);
                    int maxTailChars = Math.Max(MinTruncatedPartChars, remainingBudget * CharsPerTokenEstimate);
                    var truncatedLast = TruncateForSummary(parts[i], maxTailChars);
                    if (usedTokens + ModelCalibratedTokenEstimator.EstimateTextTokens(truncatedLast, estimator) <= availableTokens)
                    {
                        tailParts.Add(truncatedLast);
                    }
                }
                break;
            }

            tailParts.Add(parts[i]);
            usedTokens += partTokens;
        }

        tailParts.Reverse();

        var combined = new List<string>(headParts.Count + 1 + tailParts.Count);
        combined.AddRange(headParts);
        combined.Add(ElisionMarker);
        combined.AddRange(tailParts);

        var result = string.Join("\n\n", combined);
        var originalResult = result;
        int currentMaxChars = originalResult.Length;
        while (ModelCalibratedTokenEstimator.EstimateTextTokens(result, estimator) > maxTokens && currentMaxChars > MinFinalShrinkChars)
        {
            currentMaxChars = Math.Max(MinFinalShrinkChars, (int)(currentMaxChars * FinalShrinkFactor));
            result = originalResult[..Math.Min(currentMaxChars, originalResult.Length)];
            if (currentMaxChars <= MinFinalShrinkChars)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Truncates tool result text to a maximum length, appending a human-readable truncation marker.
    /// </summary>
    public static string TruncateForSummary(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        int truncatedChars = text.Length - maxChars;
        return $"{text[..maxChars]}\n\n[... {truncatedChars} more characters truncated]";
    }

    private static string ExtractTextContent(ChatMessage msg)
    {
        var sb = new StringBuilder();
        foreach (var content in msg.Contents)
        {
            // Omit TextReasoningContent per Pi compaction specification
            if (content is TextContent tc && content is not TextReasoningContent)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractToolResultText(ChatMessage msg)
    {
        var sb = new StringBuilder();
        foreach (var content in msg.Contents)
        {
            if (content is FunctionResultContent frc)
            {
                var str = frc.Result?.ToString();
                if (!string.IsNullOrEmpty(str))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(str);
                }
            }
            else if (content is TextContent tc && content is not TextReasoningContent)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(tc.Text);
            }
        }

        return sb.ToString().Trim();
    }

    private static string FormatArguments(IDictionary<string, object?>? arguments, int maxValueChars)
    {
        if (arguments == null || arguments.Count == 0) return string.Empty;
        var parts = new List<string>();
        foreach (var kvp in arguments)
        {
            // Tool arguments (e.g. full file contents for write tools) can be arbitrarily large: clamp them like tool results.
            string serialized;
            if (kvp.Value is string s)
            {
                serialized = $"\"{TruncateForSummary(s, maxValueChars)}\"";
            }
            else if (kvp.Value is JsonElement je && je.ValueKind == JsonValueKind.String)
            {
                serialized = $"\"{TruncateForSummary(je.GetString() ?? string.Empty, maxValueChars)}\"";
            }
            else
            {
                serialized = TruncateForSummary(JsonSerializer.Serialize(kvp.Value), maxValueChars);
            }

            parts.Add($"{kvp.Key}={serialized}");
        }
        return string.Join(", ", parts);
    }
}
