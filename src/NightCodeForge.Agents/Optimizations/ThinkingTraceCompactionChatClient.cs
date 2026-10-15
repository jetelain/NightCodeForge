using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

/// <summary>
/// IChatClient middleware that prunes or compacts structured reasoning (TextReasoningContent)
/// from older historical assistant turns outside a protected recent window (default: last 10 turns)
/// to save 60%-80% prompt tokens on local reasoning models without discarding recent chain of thought.
/// </summary>
public class ThinkingTraceCompactionChatClient : DelegatingChatClient
{
    public const int DefaultKeepVerbatimCount = 100;

    private readonly LocalOptimizationFlags _flags;
    private readonly int _keepVerbatimCount;

    public ThinkingTraceCompactionChatClient(
        IChatClient innerClient,
        LocalOptimizationFlags? flags = null,
        int? keepVerbatimCount = null)
        : base(innerClient)
    {
        _flags = flags ?? new LocalOptimizationFlags();
        _keepVerbatimCount = keepVerbatimCount ?? _flags.ThinkingKeepVerbatimCount;
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
        var compactedMessages = CompactHistoricalAssistantMessages(messageList, _keepVerbatimCount);

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
        var compactedMessages = CompactHistoricalAssistantMessages(messageList, _keepVerbatimCount);

        await foreach (var update in base.GetStreamingResponseAsync(compactedMessages, options, cancellationToken))
        {
            yield return update;
        }
    }

    public static List<ChatMessage> CompactHistoricalAssistantMessages(
        IReadOnlyList<ChatMessage> messages,
        int keepVerbatimCount = DefaultKeepVerbatimCount)
    {
        var thinkIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == ChatRole.Assistant && msg.Contents.OfType<TextReasoningContent>().Any())
            {
                thinkIndices.Add(i);
            }
        }

        if (thinkIndices.Count <= keepVerbatimCount)
        {
            return messages.ToList();
        }

        var indicesToCompact = new HashSet<int>(thinkIndices.Take(thinkIndices.Count - keepVerbatimCount));
        var result = new List<ChatMessage>(messages.Count);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (indicesToCompact.Contains(i))
            {
                var newMsg = new ChatMessage(msg.Role, msg.Text ?? string.Empty);

                var reasoningContents = msg.Contents.OfType<TextReasoningContent>().ToList();
                if (reasoningContents.Count > 0)
                {
                    var combinedReasoning = string.Concat(reasoningContents.Select(r => r.Text)).Trim();
                    var firstLine = combinedReasoning.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
                    if (firstLine.Length > 80)
                    {
                        firstLine = firstLine.Substring(0, 77) + "...";
                    }
                    var summary = string.IsNullOrWhiteSpace(firstLine) ? "[Reasoned]" : $"[Reasoned: {firstLine}]";
                    newMsg.Contents.Add(new TextReasoningContent(summary));
                }

                foreach (var content in msg.Contents)
                {
                    if (content is not TextContent && content is not TextReasoningContent)
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
}
