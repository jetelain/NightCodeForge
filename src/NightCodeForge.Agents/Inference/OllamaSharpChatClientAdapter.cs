using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using OllamaChatRole = OllamaSharp.Models.Chat.ChatRole;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Direct <see cref="IChatClient"/> adapter over <see cref="OllamaApiClient.ChatAsync"/>
/// that preserves <see cref="TextReasoningContent"/> (<see cref="Message.Thinking"/>) on outgoing
/// chat requests as well as incoming responses.
/// </summary>
/// <remarks>
/// OllamaSharp 5.5.0's internal <c>AbstractionMapper.ToOllamaSharpMessages</c> maps incoming
/// <c>message.Thinking</c> to <see cref="TextReasoningContent"/>, but drops <see cref="TextReasoningContent"/>
/// when converting <see cref="ChatMessage"/> history back to <see cref="Message"/> on subsequent turns.
/// Dropping thinking from assistant tool-call turns corrupts the prompt template for reasoning models
/// (such as Qwen3/Qwen3.5), causing the model to lose track of the conversation after a tool call.
/// </remarks>
public sealed class OllamaSharpChatClientAdapter : IChatClient
{
    private static readonly AIJsonSchemaTransformCache SchemaTransformCache = new(new AIJsonSchemaTransformOptions
    {
        ConvertBooleanSchemas = true,
        DisallowAdditionalProperties = true
    });

    private readonly OllamaApiClient _client;
    private ChatClientMetadata? _metadata;

    public OllamaSharpChatClientAdapter(OllamaApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = ToOllamaSharpChatRequest(this, messages, options, stream: false, _client.OutgoingJsonSerializerOptions);
        var responseStream = await _client.ChatAsync(request, cancellationToken)
            .StreamToEndAsync()
            .ConfigureAwait(false);

        return ToChatResponse(responseStream, responseStream?.Model ?? request.Model ?? _client.SelectedModel)
            ?? new ChatResponse(new List<ChatMessage>());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var request = ToOllamaSharpChatRequest(this, messages, options, stream: true, _client.OutgoingJsonSerializerOptions);
        var responseId = Guid.NewGuid().ToString("N");

        await foreach (var item in _client.ChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return ToChatResponseUpdate(item, responseId);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata ??= new ChatClientMetadata("ollama", _client.Uri, _client.SelectedModel);
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType.IsInstanceOfType(_client))
        {
            return _client;
        }

        return null;
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public static ChatRequest ToOllamaSharpChatRequest(
        IChatClient? chatClient,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        JsonSerializerOptions serializerOptions)
    {
        object? format = null;
        if (options?.ResponseFormat is ChatResponseFormatJson jsonFormat)
        {
            format = jsonFormat.Schema.HasValue ? (object)jsonFormat.Schema.Value : "json";
        }

        var mappedTools = ToOllamaSharpTools(options?.Tools);
        var mappedMessages = ToOllamaSharpMessages(messages, options, serializerOptions);

        var request = (chatClient is not null && options?.RawRepresentationFactory?.Invoke(chatClient) is ChatRequest rawRequest)
            ? rawRequest
            : new ChatRequest();
        request.Format ??= format;
        request.Stream = stream;
        request.Model ??= options?.ModelId ?? string.Empty;
        request.Options ??= new RequestOptions();
        request.Options.FrequencyPenalty ??= options?.FrequencyPenalty;
        request.Options.PresencePenalty ??= options?.PresencePenalty;
        request.Options.Seed ??= (int?)options?.Seed;
        request.Options.Stop ??= options?.StopSequences?.ToArray();
        request.Options.Temperature ??= options?.Temperature;
        request.Options.TopP ??= options?.TopP;
        request.Options.TopK ??= options?.TopK;
        request.Options.NumPredict = options?.MaxOutputTokens;

        if (options?.Reasoning is { } reasoning && !request.Think.HasValue)
        {
            request.Think = reasoning.Effort switch
            {
                null => (ThinkValue?)null,
                ReasoningEffort.None => false,
                ReasoningEffort.Low => new ThinkValue("low"),
                ReasoningEffort.Medium => new ThinkValue("medium"),
                _ => new ThinkValue("high")
            };
        }

        if (request.Messages is not null)
        {
            if (mappedMessages is not null)
            {
                var combinedMessages = new List<Message>(request.Messages);
                combinedMessages.AddRange(mappedMessages);
                request.Messages = combinedMessages;
            }
        }
        else
        {
            request.Messages = mappedMessages;
        }

        if (request.Tools is not null)
        {
            if (mappedTools is not null)
            {
                var combinedTools = new List<object>(request.Tools);
                combinedTools.AddRange(mappedTools);
                request.Tools = combinedTools;
            }
        }
        else
        {
            request.Tools = mappedTools;
        }

        if (options?.AdditionalProperties?.Any() != true)
        {
            return request;
        }

        TryAddOllamaOption(options, OllamaOption.F16kv, v => request.Options.F16kv = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.FrequencyPenalty, v => request.Options.FrequencyPenalty = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.LogitsAll, v => request.Options.LogitsAll = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.LowVram, v => request.Options.LowVram = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.MainGpu, v => request.Options.MainGpu = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.MinP, v => request.Options.MinP = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.MiroStat, v => request.Options.MiroStat = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.MiroStatEta, v => request.Options.MiroStatEta = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.MiroStatTau, v => request.Options.MiroStatTau = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.Numa, v => request.Options.Numa = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.NumBatch, v => request.Options.NumBatch = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumCtx, v => request.Options.NumCtx = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumGpu, v => request.Options.NumGpu = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumGqa, v => request.Options.NumGqa = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumKeep, v => request.Options.NumKeep = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumPredict, v => request.Options.NumPredict = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.MaxOutputTokens, v => request.Options.NumPredict = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.NumThread, v => request.Options.NumThread = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.PenalizeNewline, v => request.Options.PenalizeNewline = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.PresencePenalty, v => request.Options.PresencePenalty = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.RepeatLastN, v => request.Options.RepeatLastN = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.RepeatPenalty, v => request.Options.RepeatPenalty = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.Seed, v => request.Options.Seed = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.Stop, v => request.Options.Stop = (v as IEnumerable<string>)?.ToArray());
        TryAddOllamaOption(options, OllamaOption.Temperature, v => request.Options.Temperature = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.TfsZ, v => request.Options.TfsZ = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.TopK, v => request.Options.TopK = Convert.ToInt32(v));
        TryAddOllamaOption(options, OllamaOption.TopP, v => request.Options.TopP = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.TypicalP, v => request.Options.TypicalP = Convert.ToSingle(v));
        TryAddOllamaOption(options, OllamaOption.UseMlock, v => request.Options.UseMlock = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.UseMmap, v => request.Options.UseMmap = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.VocabOnly, v => request.Options.VocabOnly = (bool?)v);
        TryAddOllamaOption(options, OllamaOption.Think, v => request.Think = new ThinkValue(v));
        TryAddOption(options, "keep_alive", v => request.KeepAlive = (string?)v);

        return request;
    }

    private static void TryAddOllamaOption(ChatOptions? microsoftChatOptions, OllamaOption option, Action<object?> optionSetter)
    {
        TryAddOption(microsoftChatOptions, option.Name, optionSetter);
    }

    private static void TryAddOption(ChatOptions? microsoftChatOptions, string option, Action<object?> optionSetter)
    {
        if (microsoftChatOptions?.AdditionalProperties is { } props &&
            props.TryGetValue(option, out var value) &&
            value is not null)
        {
            optionSetter(value);
        }
    }

    private static IEnumerable<object>? ToOllamaSharpTools(IEnumerable<AITool>? tools)
    {
        return tools?.Select(ToOllamaSharpTool).Where(t => t is not null).Cast<object>();
    }

    private static object? ToOllamaSharpTool(AITool tool)
    {
        if (tool is AIFunctionDeclaration function)
        {
            return ToOllamaSharpTool(function);
        }

        return null;
    }

    private static JsonNode? ToOllamaSharpTool(AIFunctionDeclaration function)
    {
        var transformedSchema = SchemaTransformCache.GetOrCreateTransformedSchema(function);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = function.Name,
                ["description"] = function.Description,
                ["parameters"] = JsonNode.Parse(transformedSchema.GetRawText())
            }
        };
    }

    private static IEnumerable<Message> ToOllamaSharpMessages(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        JsonSerializerOptions serializerOptions)
    {
        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            yield return new Message
            {
                Content = options.Instructions,
                Role = OllamaChatRole.System
            };
        }

        foreach (var cm in messages)
        {
            var images = cm.Contents
                .OfType<DataContent>()
                .Where(dc => dc.HasTopLevelMediaType("image"))
                .Select(dc => dc.Base64Data.ToString())
                .ToArray();

            var toolCalls = cm.Contents
                .OfType<FunctionCallContent>()
                .Select(ToOllamaSharpToolCall)
                .ToArray();

            var thinkingParts = cm.Contents
                .OfType<TextReasoningContent>()
                .Select(rc => rc.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            var thinking = thinkingParts.Count > 0 ? string.Concat(thinkingParts) : null;
            var text = cm.Text;

            if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(thinking) || images.Length > 0 || toolCalls.Length > 0)
            {
                yield return new Message
                {
                    Content = text,
                    Thinking = thinking,
                    Images = images.Length > 0 ? images : null,
                    Role = ToOllamaSharpRole(cm.Role),
                    ToolCalls = toolCalls.Length > 0 ? toolCalls : null
                };
            }

            foreach (var item in cm.Contents.OfType<FunctionResultContent>())
            {
                var result = JsonSerializer.SerializeToElement(item.Result, serializerOptions);
                yield return new Message
                {
                    Content = JsonSerializer.Serialize(new OllamaToolResultPayload
                    {
                        CallId = item.CallId,
                        Result = result
                    }, serializerOptions),
                    Role = OllamaChatRole.Tool
                };
            }
        }
    }

    private static Message.ToolCall ToOllamaSharpToolCall(FunctionCallContent functionCall)
    {
        return new Message.ToolCall
        {
            Function = new Message.Function
            {
                Arguments = functionCall.Arguments,
                Name = functionCall.Name
            }
        };
    }

    private static OllamaChatRole ToOllamaSharpRole(Microsoft.Extensions.AI.ChatRole role)
    {
        return role.Value switch
        {
            "assistant" => OllamaChatRole.Assistant,
            "system" => OllamaChatRole.System,
            "user" => OllamaChatRole.User,
            "tool" => OllamaChatRole.Tool,
            _ => new OllamaChatRole(role.Value)
        };
    }

    private static Microsoft.Extensions.AI.ChatRole ToAbstractionRole(OllamaChatRole? role)
    {
        if (!role.HasValue)
        {
            return new Microsoft.Extensions.AI.ChatRole("unknown");
        }

        var roleText = role.Value.ToString();
        return roleText switch
        {
            "assistant" => Microsoft.Extensions.AI.ChatRole.Assistant,
            "system" => Microsoft.Extensions.AI.ChatRole.System,
            "user" => Microsoft.Extensions.AI.ChatRole.User,
            "tool" => Microsoft.Extensions.AI.ChatRole.Tool,
            _ => new Microsoft.Extensions.AI.ChatRole(roleText ?? "unknown")
        };
    }

    public static ChatResponse? ToChatResponse(ChatDoneResponseStream? stream, string? usedModel)
    {
        if (stream is null)
        {
            return null;
        }

        var responseId = stream.CreatedAtString ?? Guid.NewGuid().ToString("N");
        var chatMessage = ToChatMessage(stream.Message);
        chatMessage.CreatedAt = stream.CreatedAt;
        chatMessage.MessageId = responseId;

        return new ChatResponse(chatMessage)
        {
            FinishReason = ToFinishReason(stream.DoneReason),
            AdditionalProperties = ParseOllamaChatResponseProps(stream),
            CreatedAt = stream.CreatedAt,
            ModelId = usedModel ?? stream.Model,
            RawRepresentation = stream,
            ResponseId = responseId,
            Usage = ParseOllamaChatResponseUsage(stream)
        };
    }

    public static ChatResponseUpdate ToChatResponseUpdate(ChatResponseStream? response, string responseId)
    {
        var contents = response?.Message is not null
            ? GetAIContentsFromMessage(response.Message)
            : [new TextContent(string.Empty)];

        if (response is ChatDoneResponseStream doneStream)
        {
            var usage = ParseOllamaChatResponseUsage(doneStream);
            if (usage is not null)
            {
                contents.Add(new UsageContent(usage));
            }

            return new ChatResponseUpdate(ToAbstractionRole(doneStream.Message.Role), contents)
            {
                AdditionalProperties = ParseOllamaChatResponseProps(doneStream),
                CreatedAt = doneStream.CreatedAt,
                FinishReason = doneStream.DoneReason is null ? null : new ChatFinishReason(doneStream.DoneReason),
                MessageId = responseId,
                RawRepresentation = response,
                ResponseId = responseId,
                ModelId = doneStream.Model
            };
        }

        return new ChatResponseUpdate(ToAbstractionRole(response?.Message?.Role), contents)
        {
            CreatedAt = response?.CreatedAt,
            FinishReason = response is { Done: true } ? ChatFinishReason.Stop : null,
            MessageId = responseId,
            RawRepresentation = response,
            ResponseId = responseId,
            ModelId = response?.Model
        };
    }

    public static ChatMessage ToChatMessage(Message message)
    {
        return new ChatMessage(ToAbstractionRole(message.Role), GetAIContentsFromMessage(message))
        {
            RawRepresentation = message
        };
    }

    private static List<AIContent> GetAIContentsFromMessage(Message? message)
    {
        var contents = new List<AIContent>();
        if (message is null)
        {
            return contents;
        }

        if (message.ToolCalls is { } toolCalls)
        {
            foreach (var toolCall in toolCalls)
            {
                if (toolCall.Function is { } function)
                {
                    var callId = Guid.NewGuid().ToString()[..8];
                    contents.Add(new FunctionCallContent(callId, function.Name ?? "n/a", function.Arguments)
                    {
                        RawRepresentation = toolCall
                    });
                }
            }
        }

        if (!string.IsNullOrEmpty(message.Thinking))
        {
            contents.Insert(0, new TextReasoningContent(message.Thinking));
        }

        if (!string.IsNullOrEmpty(message.Content) || contents.Count == 0)
        {
            contents.Insert(0, new TextContent(message.Content));
        }

        return contents;
    }

    private static AdditionalPropertiesDictionary ParseOllamaChatResponseProps(ChatDoneResponseStream response)
    {
        return new AdditionalPropertiesDictionary
        {
            ["load_duration"] = TimeSpan.FromMilliseconds(response.LoadDuration / 1_000_000.0),
            ["total_duration"] = TimeSpan.FromMilliseconds(response.TotalDuration / 1_000_000.0),
            ["prompt_eval_duration"] = TimeSpan.FromMilliseconds(response.PromptEvalDuration / 1_000_000.0),
            ["eval_duration"] = TimeSpan.FromMilliseconds(response.EvalDuration / 1_000_000.0)
        };
    }

    private static ChatFinishReason? ToFinishReason(string? ollamaDoneReason)
    {
        return ollamaDoneReason switch
        {
            null => null,
            "length" => ChatFinishReason.Length,
            "stop" => ChatFinishReason.Stop,
            _ => new ChatFinishReason(ollamaDoneReason)
        };
    }

    [return: NotNullIfNotNull(nameof(response))]
    private static UsageDetails? ParseOllamaChatResponseUsage(ChatDoneResponseStream? response)
    {
        if (response is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = response.PromptEvalCount,
            OutputTokenCount = response.EvalCount,
            TotalTokenCount = response.PromptEvalCount + response.EvalCount
        };
    }

    private sealed class OllamaToolResultPayload
    {
        public string? CallId { get; set; }
        public JsonElement Result { get; set; }
    }
}

