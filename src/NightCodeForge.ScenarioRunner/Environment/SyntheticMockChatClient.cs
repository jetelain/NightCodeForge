using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NightCodeForge.ScenarioRunner.Environment;

/// <summary>
/// Synthetic Mock implementation of IChatClient providing realistic agent responses and tool invocations
/// for rapid offline scenario validation without requiring live GPU inference.
/// </summary>
public class SyntheticMockChatClient : IChatClient
{
    private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse>? _responder;

    public ChatClientMetadata Metadata { get; } = new("SyntheticMock", null, "synthetic-mock:latest");

    public SyntheticMockChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse>? responder = null)
    {
        _responder = responder;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = GenerateResponse(chatMessages, options);
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = GenerateResponse(chatMessages, options);

        // Yield synthetic reasoning thinking block
        yield return new ChatResponseUpdate
        {
            Contents = [new TextContent("<think>Inspecting codebase structure and evaluating directives...</think>\n")]
        };

        await Task.Yield();

        // If there are tool calls
        var toolCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .ToList();

        if (toolCalls.Count > 0)
        {
            foreach (var call in toolCalls)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [call]
                };
            }
        }
        else
        {
            var text = response.Text ?? "Mock reasoning complete.";
            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent(text)]
            };
        }
    }

    private ChatResponse GenerateResponse(IEnumerable<ChatMessage> chatMessages, ChatOptions? options)
    {
        if (_responder != null)
        {
            return _responder(chatMessages, options);
        }

        var messages = chatMessages.ToList();
        var lastUserMsg = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var lastToolMsg = messages.LastOrDefault(m => m.Role == ChatRole.Tool);

        // If previous turn returned a tool result, produce final technical summary
        if (lastToolMsg != null)
        {
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "Codebase inspection verified. Calculator service is well-structured with Add and Subtract methods. All quality checks pass cleanly."));
        }

        // If tools are available and user wants file inspection, emit a read tool call
        if (options?.Tools != null && options.Tools.Any(t => t.Name.Equals("read", StringComparison.OrdinalIgnoreCase) || t.Name.Equals("file_search", StringComparison.OrdinalIgnoreCase)))
        {
            var call = new FunctionCallContent("call_inspect_1", "read", new Dictionary<string, object?>
            {
                ["path"] = "Calculator.cs"
            });

            var msg = new ChatMessage(ChatRole.Assistant, "Inspecting Calculator.cs to analyze method signatures and requirements.");
            msg.Contents.Add(call);
            return new ChatResponse(msg);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            $"Processed task: {lastUserMsg}. Requirements analyzed and validated successfully."));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }
        return null;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
