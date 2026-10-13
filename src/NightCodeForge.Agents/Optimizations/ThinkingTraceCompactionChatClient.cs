using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

/// <summary>
/// IChatClient middleware that prunes or compacts <think>...</think> monologue blocks
/// from historical assistant turns to save 60%-80% prompt tokens on local reasoning models.
/// </summary>
public partial class ThinkingTraceCompactionChatClient : DelegatingChatClient
{
    [GeneratedRegex(@"<think>(.*?)</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    private readonly LocalOptimizationFlags _flags;

    public ThinkingTraceCompactionChatClient(IChatClient innerClient, LocalOptimizationFlags? flags = null)
        : base(innerClient)
    {
        _flags = flags ?? new LocalOptimizationFlags();
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!_flags.ThinkingTraceCompaction)
        {
            return await base.GetResponseAsync(chatMessages, options, cancellationToken);
        }

        var messageList = chatMessages.ToList();
        var compactedMessages = CompactHistoricalAssistantMessages(messageList);

        return await base.GetResponseAsync(compactedMessages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_flags.ThinkingTraceCompaction)
        {
            await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
            {
                yield return update;
            }
            yield break;
        }

        var messageList = chatMessages.ToList();
        var compactedMessages = CompactHistoricalAssistantMessages(messageList);

        await foreach (var update in base.GetStreamingResponseAsync(compactedMessages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public static List<ChatMessage> CompactHistoricalAssistantMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            // Only compact past assistant turns; if this is the very last assistant turn, we can keep or compact
            if (msg.Role == ChatRole.Assistant && !string.IsNullOrEmpty(msg.Text))
            {
                var stripped = CompactThinkBlock(msg.Text);
                var newMsg = new ChatMessage(msg.Role, stripped);
                foreach (var content in msg.Contents)
                {
                    if (content is not TextContent)
                    {
                        newMsg.Contents.Add(content);
                    }
                }
                result.Add(newMsg);
            }
            else
            {
                result.Add(msg);
            }
        }

        return result;
    }

    public static string CompactThinkBlock(string text)
    {
        if (string.IsNullOrEmpty(text) || !ThinkBlockRegex().IsMatch(text))
        {
            return text;
        }

        return ThinkBlockRegex().Replace(text, match =>
        {
            var thoughts = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(thoughts))
            {
                return string.Empty;
            }

            // Summarize into a concise single-line stub
            var firstLine = thoughts.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            if (firstLine.Length > 80)
            {
                firstLine = firstLine.Substring(0, 77) + "...";
            }
            return $"[Reasoned: {firstLine}]";
        }).Trim();
    }
}
