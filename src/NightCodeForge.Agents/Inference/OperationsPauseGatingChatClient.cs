using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Delegating chat client that intercepts inference requests and rejects them if global operations are paused.
/// </summary>
public class OperationsPauseGatingChatClient : DelegatingChatClient
{
    private readonly IOperationsControlService _operationsControlService;

    public OperationsPauseGatingChatClient(
        IChatClient innerClient,
        IOperationsControlService operationsControlService)
        : base(innerClient)
    {
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_operationsControlService.IsPaused)
        {
            throw new OperationsPausedException("Inference request rejected: global operations are paused.");
        }

        return await base.GetResponseAsync(chatMessages, options, cancellationToken);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_operationsControlService.IsPaused)
        {
            throw new OperationsPausedException("Streaming inference request rejected: global operations are paused.");
        }

        await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            yield return update;
        }
    }
}
