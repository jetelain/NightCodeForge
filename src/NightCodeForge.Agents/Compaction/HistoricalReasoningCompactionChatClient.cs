using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Compaction;

/// <summary>
/// Chat client middleware that prunes historical reasoning traces (TextReasoningContent)
/// on older assistant turns before sending messages to the model.
/// The most recent assistant turn retains its reasoning trace, while older turns are stripped
/// to prevent reasoning token bloat across multi-turn interactions.
/// </summary>
public sealed class HistoricalReasoningCompactionChatClient : DelegatingChatClient
{
    public HistoricalReasoningCompactionChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    private static IEnumerable<ChatMessage> PruneHistoricalReasoning(IEnumerable<ChatMessage> chatMessages)
    {
        var list = chatMessages as IList<ChatMessage> ?? chatMessages.ToList();
        if (list.Count == 0) return list;

        // Locate the index of the most recent assistant message
        int lastAssistantIndex = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.Assistant)
            {
                lastAssistantIndex = i;
                break;
            }
        }

        var prunedList = new List<ChatMessage>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var msg = list[i];
            if (msg.Role == ChatRole.Assistant && i != lastAssistantIndex && msg.Contents.OfType<TextReasoningContent>().Any())
            {
                var filteredContents = msg.Contents.Where(c => c is not TextReasoningContent).ToList();
                if (filteredContents.Count == 0)
                {
                    // A reasoning-only turn carries no information once pruned; sending an empty assistant message is rejected by some providers.
                    continue;
                }

                var clone = new ChatMessage(msg.Role, filteredContents)
                {
                    AuthorName = msg.AuthorName,
                    MessageId = msg.MessageId,
                    CreatedAt = msg.CreatedAt,
                    RawRepresentation = msg.RawRepresentation
                };
                if (msg.AdditionalProperties != null)
                {
                    clone.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    foreach (var prop in msg.AdditionalProperties)
                    {
                        clone.AdditionalProperties[prop.Key] = prop.Value;
                    }
                }
                prunedList.Add(clone);
            }
            else
            {
                prunedList.Add(msg);
            }
        }

        return prunedList;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(PruneHistoricalReasoning(chatMessages), options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(PruneHistoricalReasoning(chatMessages), options, cancellationToken);
    }
}
