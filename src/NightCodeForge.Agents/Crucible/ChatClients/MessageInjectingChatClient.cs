using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Crucible.ChatClients;

/// <summary>
/// Chat client middleware that injects synthetic messages, developer answers to 'ask',
/// or deterministic quality gate feedback into the chat stream on resumption.
/// </summary>
public class MessageInjectingChatClient : DelegatingChatClient
{
    private readonly List<ChatMessage> _messagesToInject = new();
    private readonly object _lock = new();

    public MessageInjectingChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    public void InjectMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _messagesToInject.Add(message);
        }
    }

    public void InjectUserAnswer(string answer)
    {
        InjectMessage(new ChatMessage(ChatRole.User, answer));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = DrainInjectedMessages(chatMessages);
        return await base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = DrainInjectedMessages(chatMessages);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
    }

    private List<ChatMessage> DrainInjectedMessages(IEnumerable<ChatMessage> currentMessages)
    {
        lock (_lock)
        {
            var list = currentMessages.ToList();
            if (_messagesToInject.Count > 0)
            {
                list.AddRange(_messagesToInject);
                _messagesToInject.Clear();
            }
            return list;
        }
    }
}
