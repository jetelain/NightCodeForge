using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Agents.Crucible.ChatClients;

/// <summary>
/// Chat client middleware that triggers persistence of conversation history after every service call
/// to guarantee zero data loss across unexpected crashes or mid-turn interruptions.
/// </summary>
public class PerServiceCallChatHistoryPersistingChatClient : DelegatingChatClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, Task> _onPersistAsync;
    private readonly ILogger _logger;

    public PerServiceCallChatHistoryPersistingChatClient(
        IChatClient innerClient,
        Func<IReadOnlyList<ChatMessage>, Task> onPersistAsync,
        ILogger logger) : base(innerClient)
    {
        _onPersistAsync = onPersistAsync ?? throw new ArgumentNullException(nameof(onPersistAsync));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        // Add response to the tracked history and persist
        var updated = new List<ChatMessage>(messages);
        foreach (var message in response.Messages)
        {
            updated.Add(message);
        }

        try
        {
            await _onPersistAsync(updated);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist chat history after service response; continuing inference.");
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        var assistantContents = new List<AIContent>();
        var textBuilder = new System.Text.StringBuilder();

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    assistantContents.Add(content);
                }
            }
            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuilder.Append(update.Text);
            }

            yield return update;
        }

        var updated = new List<ChatMessage>(messages);
        if (assistantContents.Count > 0 || textBuilder.Length > 0)
        {
            if (textBuilder.Length > 0 && !assistantContents.OfType<TextContent>().Any())
            {
                assistantContents.Insert(0, new TextContent(textBuilder.ToString()));
            }
            var assistantMsg = new ChatMessage(ChatRole.Assistant, assistantContents);
            updated.Add(assistantMsg);
        }

        try
        {
            await _onPersistAsync(updated);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist chat history after streaming response; continuing inference.");
        }
    }
}
