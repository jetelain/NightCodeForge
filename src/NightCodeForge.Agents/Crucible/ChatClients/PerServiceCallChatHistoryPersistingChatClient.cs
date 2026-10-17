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
    private readonly ILogger? _logger;

    public PerServiceCallChatHistoryPersistingChatClient(
        IChatClient innerClient,
        Func<IReadOnlyList<ChatMessage>, Task> onPersistAsync,
        ILogger? logger = null) : base(innerClient)
    {
        _onPersistAsync = onPersistAsync ?? throw new ArgumentNullException(nameof(onPersistAsync));
        _logger = logger;
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
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }

        try
        {
            await _onPersistAsync(messages);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist chat history after streaming response; continuing inference.");
        }
    }
}
