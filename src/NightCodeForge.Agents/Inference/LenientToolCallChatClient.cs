using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Chat client middleware that inspects model responses for text-based tool calls
/// (e.g. &lt;tool_call&gt;{...}&lt;/tool_call&gt;) emitted by models without native tool token support,
/// and converts them into native Microsoft.Extensions.AI.FunctionCallContent items so that
/// downstream agents (such as MAF HarnessAgent) can execute them automatically.
/// </summary>
public partial class LenientToolCallChatClient : DelegatingChatClient
{
    [GeneratedRegex(@"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();

    public LenientToolCallChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(chatMessages, options, cancellationToken);
        if (response?.Messages != null)
        {
            foreach (var msg in response.Messages)
            {
                if (msg.Contents.OfType<FunctionCallContent>().Any()) continue;
                if (!string.IsNullOrEmpty(msg.Text))
                {
                    var fc = TryExtractTextToolCall(msg.Text);
                    if (fc != null)
                    {
                        msg.Contents.Add(fc);
                    }
                }
            }
        }
        return response!;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var textBuffer = new StringBuilder();
        bool hasNativeFunctionCall = false;

        await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            if (update.Contents != null && update.Contents.OfType<FunctionCallContent>().Any())
            {
                hasNativeFunctionCall = true;
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                textBuffer.Append(update.Text);
            }

            yield return update;
        }

        if (!hasNativeFunctionCall && textBuffer.Length > 0)
        {
            var fallbackCall = TryExtractTextToolCall(textBuffer.ToString());
            if (fallbackCall != null)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = { fallbackCall }
                };
            }
        }
    }

    public static FunctionCallContent? TryExtractTextToolCall(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = ToolCallTagRegex().Match(text);
        if (!match.Success) return null;

        try
        {
            var node = JsonNode.Parse(match.Groups[1].Value);
            var name = node?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) return null;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (node?["arguments"] is JsonObject args)
            {
                foreach (var (k, v) in args)
                {
                    dict[k] = ParseJsonNodeValue(v);
                }
            }
            else if (node?["arguments"] is JsonArray arrArgs)
            {
                dict["tasks"] = arrArgs.Select(ParseJsonNodeValue).ToList();
            }

            return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, dict);
        }
        catch
        {
            return null;
        }
    }

    private static object? ParseJsonNodeValue(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<string>(out var s)) return s;
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            return val.ToString();
        }
        if (node is JsonArray arr)
        {
            return arr.Select(ParseJsonNodeValue).ToList();
        }
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in obj)
            {
                dict[k] = ParseJsonNodeValue(v);
            }
            return dict;
        }
        return node.ToString();
    }
}
