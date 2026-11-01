using System.ComponentModel;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Resilience;
using OllamaSharp;
using OllamaSharp.Constants;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using OllamaChatRole = OllamaSharp.Models.Chat.ChatRole;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Comprehensive unit tests for <see cref="OllamaSharpChatClientAdapter"/>, including all chat mapping
/// test cases ported from OllamaSharp's <c>AbstractionMapperTests</c> plus reasoning/thinking preservation
/// and <see cref="IChatClient"/> contract tests.
/// </summary>
public class OllamaSharpChatClientAdapterTests
{
    [Fact]
    public void Maps_Partial_Options_Class()
    {
        var messages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "A"),
            new(Microsoft.Extensions.AI.ChatRole.User, "B"),
        };

        var options = new ChatOptions { Temperature = 0.5f };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            messages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.NotNull(request.Options);
        Assert.Null(request.Options!.F16kv);
        Assert.Null(request.Options.FrequencyPenalty);
        Assert.Null(request.Options.LogitsAll);
        Assert.Null(request.Options.LowVram);
        Assert.Null(request.Options.MainGpu);
        Assert.Null(request.Options.MinP);
        Assert.Null(request.Options.MiroStat);
        Assert.Null(request.Options.MiroStatEta);
        Assert.Null(request.Options.MiroStatTau);
        Assert.Null(request.Options.Numa);
        Assert.Null(request.Options.NumBatch);
        Assert.Null(request.Options.NumCtx);
        Assert.Null(request.Options.NumGpu);
        Assert.Null(request.Options.NumGqa);
        Assert.Null(request.Options.NumKeep);
        Assert.Null(request.Options.NumPredict);
        Assert.Null(request.Options.NumThread);
        Assert.Null(request.Options.PenalizeNewline);
        Assert.Null(request.Options.PresencePenalty);
        Assert.Null(request.Options.RepeatLastN);
        Assert.Null(request.Options.RepeatPenalty);
        Assert.Null(request.Options.Seed);
        Assert.Null(request.Options.Stop);
        Assert.Equal(0.5f, request.Options.Temperature);
        Assert.Null(request.Options.TfsZ);
        Assert.Null(request.Options.TopK);
        Assert.Null(request.Options.TopP);
        Assert.Null(request.Options.TypicalP);
        Assert.Null(request.Options.UseMlock);
        Assert.Null(request.Options.UseMmap);
        Assert.Null(request.Options.VocabOnly);
    }

    [Fact]
    public void Maps_Messages()
    {
        var chatMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "Hi there.")
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                Contents = [new TextContent("Hi there.")],
                RawRepresentation = null,
            },
            new(Microsoft.Extensions.AI.ChatRole.User, "What is 3 + 4?")
            {
                AdditionalProperties = [],
                AuthorName = "a2",
                Contents = [new TextContent("What is 3 + 4?")],
                RawRepresentation = null,
            },
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "3 + 4 is 7")
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                Contents = [new TextContent("3 + 4 is 7")],
                RawRepresentation = null,
            },
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            null,
            stream: true,
            JsonSerializerOptions.Default);

        var mapped = chatRequest.Messages!.ToList();
        Assert.Equal(3, mapped.Count);

        Assert.Equal("Hi there.", mapped[0].Content);
        Assert.Equal(OllamaChatRole.Assistant, mapped[0].Role);

        Assert.Equal("What is 3 + 4?", mapped[1].Content);
        Assert.Equal(OllamaChatRole.User, mapped[1].Role);

        Assert.Equal("3 + 4 is 7", mapped[2].Content);
        Assert.Equal(OllamaChatRole.Assistant, mapped[2].Role);
    }

    [Fact]
    public void Maps_Base64_Images()
    {
        const string transparentPixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/wcAAgEBAYkFNgAAAAAASUVORK5CYII=";
        const string transparentPixelWithBase64Meta = "data:image/png;base64," + transparentPixel;

        var chatMessages = new List<ChatMessage>
        {
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                Contents =
                [
                    new TextContent("Make me an image like this, but with beer."),
                    new DataContent(transparentPixelWithBase64Meta)
                ],
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.User
            },
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a2",
                Contents =
                [
                    new TextContent("Interesting idea, here we go:"),
                    new DataContent(transparentPixelWithBase64Meta),
                    new DataContent(transparentPixelWithBase64Meta),
                    new DataContent(transparentPixelWithBase64Meta),
                    new DataContent(transparentPixelWithBase64Meta)
                ],
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.Assistant
            },
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            null,
            stream: true,
            JsonSerializerOptions.Default);

        var mapped = chatRequest.Messages!.ToList();
        Assert.Equal(2, mapped.Count);

        Assert.Equal(OllamaChatRole.User, mapped[0].Role);
        Assert.Equal(transparentPixel, Assert.Single(mapped[0].Images!));

        Assert.Equal(OllamaChatRole.Assistant, mapped[1].Role);
        Assert.Equal(4, mapped[1].Images!.Length);
    }

    [Fact]
    public void Maps_Byte_Array_Images()
    {
        var bytes = Encoding.ASCII.GetBytes("ABC");

        var chatMessages = new List<ChatMessage>
        {
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                Contents =
                [
                    new TextContent("Make me an image like this, but with beer."),
                    new DataContent(bytes, "image/png")
                ],
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.User
            }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            null,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal("QUJD", Assert.Single(Assert.Single(request.Messages!).Images!));
    }

    [Fact]
    public void Ignores_UriContent()
    {
        var chatMessages = new List<ChatMessage>
        {
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                Contents =
                [
                    new TextContent("Make me an image like this, but with beer."),
                    new UriContent("https://unsplash.com/sunset.png", "image/png")
                ],
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.User
            }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            null,
            stream: true,
            JsonSerializerOptions.Default);

        var message = Assert.Single(request.Messages!);
        Assert.Null(message.Images);
    }

    [Fact]
    public void Maps_Messages_With_Tools()
    {
        var chatMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "What's the weather in Honolulu?")
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                RawRepresentation = null,
            }
        };

        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    ([Description("The city to get the weather for")] string city,
                     [Description("The unit to calculate the current temperature to")] string unit = "celsius") => "sunny",
                    "get_weather",
                    "Gets the current weather for a current location")
            ],
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        var toolJson = JsonSerializer.SerializeToElement(Assert.Single(chatRequest.Tools!));
        Assert.Equal("function", toolJson.GetProperty("type").GetString());
        Assert.Equal("Gets the current weather for a current location", toolJson.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal("get_weather", toolJson.GetProperty("function").GetProperty("name").GetString());

        var parameters = toolJson.GetProperty("function").GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Equal(new[] { "city" }, parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());

        var properties = parameters.GetProperty("properties");
        Assert.Equal("The city to get the weather for", properties.GetProperty("city").GetProperty("description").GetString());
        Assert.Equal("string", properties.GetProperty("city").GetProperty("type").GetString());
        Assert.Equal("The unit to calculate the current temperature to", properties.GetProperty("unit").GetProperty("description").GetString());
        Assert.Equal("string", properties.GetProperty("unit").GetProperty("type").GetString());
    }

    [Fact]
    public void Maps_Messages_With_Tools_Nullable_Parameters()
    {
        var chatMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "Filter files")
        };

        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(
                    ([Description("The filter to apply")] string? filenameFilter) => "done",
                    "filter_files",
                    "Filters files by name")
            ],
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        var toolJson = JsonSerializer.SerializeToElement(Assert.Single(chatRequest.Tools!));
        Assert.Equal("function", toolJson.GetProperty("type").GetString());
        Assert.Equal("filter_files", toolJson.GetProperty("function").GetProperty("name").GetString());

        var parameters = toolJson.GetProperty("function").GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());

        var filenameFilter = parameters.GetProperty("properties").GetProperty("filenameFilter");
        Assert.Equal("The filter to apply", filenameFilter.GetProperty("description").GetString());
    }

    [Fact]
    public void Maps_KeepAliveAll_From_AdditionalProperties()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["keep_alive"] = "60m"
            }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: false,
            JsonSerializerOptions.Default);

        Assert.Equal("60m", request.KeepAlive);
    }

    [Fact]
    public void Maps_All_Options_With_AdditionalProperties()
    {
        List<ChatMessage> chatMessages = [];

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // Boolean options
                [OllamaOption.F16kv.Name] = true,
                [OllamaOption.LogitsAll.Name] = true,
                [OllamaOption.LowVram.Name] = true,
                [OllamaOption.Numa.Name] = true,
                [OllamaOption.PenalizeNewline.Name] = true,
                [OllamaOption.UseMlock.Name] = true,
                [OllamaOption.UseMmap.Name] = true,
                [OllamaOption.VocabOnly.Name] = true,

                // Float options
                [OllamaOption.FrequencyPenalty.Name] = 0.5f,
                [OllamaOption.MinP.Name] = 0.1f,
                [OllamaOption.MiroStatEta.Name] = 0.1f,
                [OllamaOption.MiroStatTau.Name] = 0.2f,
                [OllamaOption.PresencePenalty.Name] = 0.3f,
                [OllamaOption.RepeatPenalty.Name] = 0.4f,
                [OllamaOption.Temperature.Name] = 0.7f,
                [OllamaOption.TfsZ.Name] = 0.8f,
                [OllamaOption.TopP.Name] = 0.9f,
                [OllamaOption.TypicalP.Name] = 0.95f,

                // Integer options
                [OllamaOption.MainGpu.Name] = 0,
                [OllamaOption.MiroStat.Name] = 1,
                [OllamaOption.NumBatch.Name] = 512,
                [OllamaOption.NumCtx.Name] = 4096,
                [OllamaOption.NumGpu.Name] = 1,
                [OllamaOption.NumGqa.Name] = 8,
                [OllamaOption.NumKeep.Name] = 64,
                [OllamaOption.NumPredict.Name] = 1024,
                [OllamaOption.MaxOutputTokens.Name] = 2048,
                [OllamaOption.NumThread.Name] = 8,
                [OllamaOption.RepeatLastN.Name] = 64,
                [OllamaOption.Seed.Name] = 42,
                [OllamaOption.TopK.Name] = 40,

                // String array options
                [OllamaOption.Stop.Name] = new[] { "stop1", "stop2" }
            }
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.NotNull(chatRequest.Options);

        // Boolean assertions
        Assert.Equal(true, chatRequest.Options!.F16kv);
        Assert.Equal(true, chatRequest.Options.LogitsAll);
        Assert.Equal(true, chatRequest.Options.LowVram);
        Assert.Equal(true, chatRequest.Options.Numa);
        Assert.Equal(true, chatRequest.Options.PenalizeNewline);
        Assert.Equal(true, chatRequest.Options.UseMlock);
        Assert.Equal(true, chatRequest.Options.UseMmap);
        Assert.Equal(true, chatRequest.Options.VocabOnly);

        // Float assertions
        Assert.Equal(0.5f, chatRequest.Options.FrequencyPenalty);
        Assert.Equal(0.1f, chatRequest.Options.MinP);
        Assert.Equal(0.1f, chatRequest.Options.MiroStatEta);
        Assert.Equal(0.2f, chatRequest.Options.MiroStatTau);
        Assert.Equal(0.3f, chatRequest.Options.PresencePenalty);
        Assert.Equal(0.4f, chatRequest.Options.RepeatPenalty);
        Assert.Equal(0.7f, chatRequest.Options.Temperature);
        Assert.Equal(0.8f, chatRequest.Options.TfsZ);
        Assert.Equal(0.9f, chatRequest.Options.TopP);
        Assert.Equal(0.95f, chatRequest.Options.TypicalP);

        // Integer assertions
        Assert.Equal(0, chatRequest.Options.MainGpu);
        Assert.Equal(1, chatRequest.Options.MiroStat);
        Assert.Equal(512, chatRequest.Options.NumBatch);
        Assert.Equal(4096, chatRequest.Options.NumCtx);
        Assert.Equal(1, chatRequest.Options.NumGpu);
        Assert.Equal(8, chatRequest.Options.NumGqa);
        Assert.Equal(64, chatRequest.Options.NumKeep);
        Assert.Equal(2048, chatRequest.Options.NumPredict);
        Assert.Equal(8, chatRequest.Options.NumThread);
        Assert.Equal(64, chatRequest.Options.RepeatLastN);
        Assert.Equal(42, chatRequest.Options.Seed);
        Assert.Equal(40, chatRequest.Options.TopK);

        // String array assertions
        Assert.NotNull(chatRequest.Options.Stop);
        Assert.Equal(["stop1", "stop2"], chatRequest.Options.Stop);
    }

    public static IEnumerable<object?[]> StopSequencesTestData
    {
        get
        {
            yield return [JsonSerializer.Deserialize<JsonElement>("[\"stop1\", \"stop2\"]").EnumerateArray().Select(e => e.GetString())];
            yield return [null];
            yield return [new List<string> { "stop1", "stop2", "stop3", "stop4" }];
            yield return [new string[] { "stop1", "stop2", "stop3" }];
            yield return [new HashSet<string> { "stop1", "stop2" }];
            yield return [new Stack<string>(["stop1"])];
            yield return [new Queue<string>(["stop1"])];
        }
    }

    [Theory]
    [MemberData(nameof(StopSequencesTestData))]
    public void Maps_Messages_With_IEnumerable_StopSequences(object? enumerable)
    {
        var chatMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "What's the weather in Honolulu?")
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                RawRepresentation = null,
            }
        };

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["stop"] = enumerable }
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        var stopSequences = chatRequest.Options!.Stop;
        var typedEnumerable = (IEnumerable<string?>?)enumerable;

        if (typedEnumerable is null)
        {
            Assert.Null(stopSequences);
            return;
        }

        Assert.NotNull(stopSequences);
        Assert.Equal(typedEnumerable.Count(), stopSequences!.Length);
    }

    [Fact]
    public void Maps_Messages_With_ToolResponse()
    {
        var chatMessages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.Tool, "The weather in Honolulu is 25°C.")
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                RawRepresentation = null,
            }
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            new ChatOptions(),
            stream: true,
            JsonSerializerOptions.Default);

        var tool = Assert.Single(chatRequest.Messages!);
        Assert.Contains("The weather in Honolulu is 25°C.", tool.Content);
        Assert.Equal(OllamaChatRole.Tool, tool.Role);
    }

    [Fact]
    public void Maps_Messages_With_MultipleToolResponse()
    {
        var aiChatMessages = new List<ChatMessage>
        {
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.User,
                Contents =
                [
                    new TextContent("I have found those 2 results"),
                    new FunctionResultContent(callId: "123", result: new { Temperature = 40 }),
                    new FunctionResultContent(callId: "456", result: new { Summary = "This is a tool result test" }),
                ]
            }
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            aiChatMessages,
            new ChatOptions(),
            stream: true,
            JsonSerializerOptions.Default);
        var chatMessages = chatRequest.Messages!.ToList();

        Assert.Equal(3, chatMessages.Count);

        var user = chatMessages[0];
        var tool1 = chatMessages[1];
        var tool2 = chatMessages[2];

        Assert.Contains("\"Temperature\":40", tool1.Content);
        Assert.Contains("\"CallId\":\"123\"", tool1.Content);
        Assert.Equal(OllamaChatRole.Tool, tool1.Role);

        Assert.Contains("\"Summary\":\"This is a tool result test\"", tool2.Content);
        Assert.Contains("\"CallId\":\"456\"", tool2.Content);
        Assert.Equal(OllamaChatRole.Tool, tool2.Role);

        Assert.Contains("I have found those 2 results", user.Content);
        Assert.Equal(OllamaChatRole.User, user.Role);
    }

    [Fact]
    public void Maps_Messages_WithoutContent_MultipleToolResponse()
    {
        var aiChatMessages = new List<ChatMessage>
        {
            new()
            {
                AdditionalProperties = [],
                AuthorName = "a1",
                RawRepresentation = null,
                Role = Microsoft.Extensions.AI.ChatRole.User,
                Contents =
                [
                    new FunctionResultContent(callId: "123", result: new { Temperature = 40 }),
                    new FunctionResultContent(callId: "456", result: new { Summary = "This is a tool result test" }),
                ]
            }
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            aiChatMessages,
            new ChatOptions(),
            stream: true,
            JsonSerializerOptions.Default);
        var chatMessages = chatRequest.Messages!.ToList();

        Assert.Equal(2, chatMessages.Count);

        var tool1 = chatMessages[0];
        var tool2 = chatMessages[1];
        Assert.Contains("\"Temperature\":40", tool1.Content);
        Assert.Contains("\"CallId\":\"123\"", tool1.Content);
        Assert.Equal(OllamaChatRole.Tool, tool1.Role);

        Assert.Contains("\"Summary\":\"This is a tool result test\"", tool2.Content);
        Assert.Contains("\"CallId\":\"456\"", tool2.Content);
        Assert.Equal(OllamaChatRole.Tool, tool2.Role);
    }

    [Fact]
    public void Maps_Options()
    {
        var chatMessages = new List<ChatMessage>();

        var options = new ChatOptions
        {
            FrequencyPenalty = 0.5f,
            MaxOutputTokens = 1000,
            ModelId = "qwen3.5:35b-a3b",
            PresencePenalty = 0.3f,
            ResponseFormat = ChatResponseFormat.Json,
            Seed = 11,
            StopSequences = ["stop1", "stop2", "stop3"],
            Temperature = 0.1f,
            TopP = 10.1f
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            chatMessages,
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal("json", chatRequest.Format);
        Assert.Equal("qwen3.5:35b-a3b", chatRequest.Model);
        Assert.Equal(0.5f, chatRequest.Options!.FrequencyPenalty);
        Assert.Equal(0.3f, chatRequest.Options.PresencePenalty);
        Assert.Equal(new[] { "stop1", "stop2", "stop3" }, chatRequest.Options.Stop!);
        Assert.Equal(0.1f, chatRequest.Options.Temperature);
        Assert.Equal(10.1f, chatRequest.Options.TopP);
        Assert.Equal(11, chatRequest.Options.Seed);
        Assert.True(chatRequest.Stream);
        Assert.Null(chatRequest.Template);
        Assert.Equal(1000, chatRequest.Options.NumPredict);

        Assert.Empty(chatRequest.CustomHeaders);
        Assert.Null(chatRequest.KeepAlive);
        Assert.Null(chatRequest.Options.F16kv);
        Assert.Null(chatRequest.Options.LogitsAll);
        Assert.Null(chatRequest.Options.LowVram);
        Assert.Null(chatRequest.Options.MainGpu);
        Assert.Null(chatRequest.Options.MinP);
        Assert.Null(chatRequest.Options.MiroStat);
        Assert.Null(chatRequest.Options.MiroStatEta);
        Assert.Null(chatRequest.Options.MiroStatTau);
        Assert.Null(chatRequest.Options.Numa);
        Assert.Null(chatRequest.Options.NumBatch);
        Assert.Null(chatRequest.Options.NumCtx);
        Assert.Null(chatRequest.Options.NumGpu);
        Assert.Null(chatRequest.Options.NumGqa);
        Assert.Null(chatRequest.Options.NumKeep);
        Assert.Null(chatRequest.Options.NumThread);
        Assert.Null(chatRequest.Options.PenalizeNewline);
        Assert.Null(chatRequest.Options.RepeatLastN);
        Assert.Null(chatRequest.Options.RepeatPenalty);
        Assert.Null(chatRequest.Options.TfsZ);
        Assert.Null(chatRequest.Options.TopK);
        Assert.Null(chatRequest.Options.TypicalP);
        Assert.Null(chatRequest.Options.UseMlock);
        Assert.Null(chatRequest.Options.UseMmap);
        Assert.Null(chatRequest.Options.VocabOnly);
    }

    [Fact]
    public void Maps_JsonWithoutSchema()
    {
        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.Json
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal("json", chatRequest.Format);
    }

    [Fact]
    public void Maps_JsonWithSchema()
    {
        var schemaElement = AIJsonUtilities.CreateJsonSchema(type: typeof(Sword));

        var options = new ChatOptions
        {
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schemaElement)
        };

        var chatRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal(schemaElement, chatRequest.Format);
    }

    private sealed class Sword
    {
        public required string Name { get; set; }
        public required int Damage { get; set; }
    }

    [Fact]
    public void Maps_Ollama_Options()
    {
        var options = new ChatOptions()
            .AddOllamaOption(OllamaOption.F16kv, true)
            .AddOllamaOption(OllamaOption.FrequencyPenalty, 0.11f)
            .AddOllamaOption(OllamaOption.LogitsAll, false)
            .AddOllamaOption(OllamaOption.LowVram, true)
            .AddOllamaOption(OllamaOption.MainGpu, 1)
            .AddOllamaOption(OllamaOption.MinP, 0.22f)
            .AddOllamaOption(OllamaOption.MiroStat, 2)
            .AddOllamaOption(OllamaOption.MiroStatEta, 0.33f)
            .AddOllamaOption(OllamaOption.MiroStatTau, 0.44f)
            .AddOllamaOption(OllamaOption.Numa, false)
            .AddOllamaOption(OllamaOption.NumBatch, 3)
            .AddOllamaOption(OllamaOption.NumCtx, 4)
            .AddOllamaOption(OllamaOption.NumGpu, 5)
            .AddOllamaOption(OllamaOption.NumGqa, 6)
            .AddOllamaOption(OllamaOption.NumKeep, 7)
            .AddOllamaOption(OllamaOption.NumPredict, 8)
            .AddOllamaOption(OllamaOption.NumThread, 9)
            .AddOllamaOption(OllamaOption.PenalizeNewline, true)
            .AddOllamaOption(OllamaOption.PresencePenalty, 0.55f)
            .AddOllamaOption(OllamaOption.RepeatLastN, 10)
            .AddOllamaOption(OllamaOption.RepeatPenalty, 0.66f)
            .AddOllamaOption(OllamaOption.Seed, 11)
            .AddOllamaOption(OllamaOption.Stop, new string[] { "stop", "quit", "exit" })
            .AddOllamaOption(OllamaOption.Temperature, 0.77f)
            .AddOllamaOption(OllamaOption.TfsZ, 0.88f)
            .AddOllamaOption(OllamaOption.TopK, 12)
            .AddOllamaOption(OllamaOption.TopP, 0.99f)
            .AddOllamaOption(OllamaOption.TypicalP, 1.01f)
            .AddOllamaOption(OllamaOption.UseMlock, false)
            .AddOllamaOption(OllamaOption.UseMmap, true)
            .AddOllamaOption(OllamaOption.VocabOnly, false)
            .AddOllamaOption(OllamaOption.Think, false);

        var ollamaRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal(true, ollamaRequest.Options!.F16kv);
        Assert.Equal(0.11f, ollamaRequest.Options.FrequencyPenalty);
        Assert.Equal(false, ollamaRequest.Options.LogitsAll);
        Assert.Equal(true, ollamaRequest.Options.LowVram);
        Assert.Equal(1, ollamaRequest.Options.MainGpu);
        Assert.Equal(0.22f, ollamaRequest.Options.MinP);
        Assert.Equal(2, ollamaRequest.Options.MiroStat);
        Assert.Equal(0.33f, ollamaRequest.Options.MiroStatEta);
        Assert.Equal(0.44f, ollamaRequest.Options.MiroStatTau);
        Assert.Equal(false, ollamaRequest.Options.Numa);
        Assert.Equal(3, ollamaRequest.Options.NumBatch);
        Assert.Equal(4, ollamaRequest.Options.NumCtx);
        Assert.Equal(5, ollamaRequest.Options.NumGpu);
        Assert.Equal(6, ollamaRequest.Options.NumGqa);
        Assert.Equal(7, ollamaRequest.Options.NumKeep);
        Assert.Equal(8, ollamaRequest.Options.NumPredict);
        Assert.Equal(9, ollamaRequest.Options.NumThread);
        Assert.Equal(true, ollamaRequest.Options.PenalizeNewline);
        Assert.Equal(0.55f, ollamaRequest.Options.PresencePenalty);
        Assert.Equal(10, ollamaRequest.Options.RepeatLastN);
        Assert.Equal(0.66f, ollamaRequest.Options.RepeatPenalty);
        Assert.Equal(11, ollamaRequest.Options.Seed);
        Assert.Equal(new[] { "stop", "quit", "exit" }, ollamaRequest.Options.Stop!);
        Assert.Equal(0.77f, ollamaRequest.Options.Temperature);
        Assert.Equal(0.88f, ollamaRequest.Options.TfsZ);
        Assert.Equal(12, ollamaRequest.Options.TopK);
        Assert.Equal(0.99f, ollamaRequest.Options.TopP);
        Assert.Equal(1.01f, ollamaRequest.Options.TypicalP);
        Assert.Equal(false, ollamaRequest.Options.UseMlock);
        Assert.Equal(true, ollamaRequest.Options.UseMmap);
        Assert.Equal(false, ollamaRequest.Options.VocabOnly);
        Assert.Equal((ThinkValue)false, ollamaRequest.Think);
    }

    [Fact]
    public void Maps_Ollama_Options_With_ThinkValue_Boolean()
    {
        var options = new ChatOptions()
            .AddOllamaOption(OllamaOption.Think, true);

        var ollamaRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal((ThinkValue)true, ollamaRequest.Think);
    }

    [Fact]
    public void Maps_Ollama_Options_With_ThinkValue_String()
    {
        var options = new ChatOptions()
            .AddOllamaOption(OllamaOption.Think, "high");

        var ollamaRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal((ThinkValue)"high", ollamaRequest.Think);
    }

    [Fact]
    public void Maps_UsesRawRepresentation()
    {
        ChatOptions options = new()
        {
            Temperature = 0.7f,
            RawRepresentationFactory = _ => new ChatRequest
            {
                Think = true,
                Options = new RequestOptions { VocabOnly = true },
            },
        };

        var ollamaRequest = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            new MockChatClient(),
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal(0.7f, ollamaRequest.Options!.Temperature);
        Assert.True(ollamaRequest.Think.HasValue);
        Assert.Equal((ThinkValue)true, ollamaRequest.Think!.Value);
        Assert.True(ollamaRequest.Options.VocabOnly.HasValue);
        Assert.True(ollamaRequest.Options.VocabOnly!.Value);
    }

    [Fact]
    public void Maps_Reasoning_Output_None_Disables_Think()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Output = ReasoningOutput.None }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Null(request.Think);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "high")]
    public void Maps_Reasoning_Effort_To_Think_Level(ReasoningEffort effort, string expectedThink)
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = effort }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal((ThinkValue)expectedThink, request.Think);
    }

    [Fact]
    public void Maps_Reasoning_Effort_None_To_False()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal((ThinkValue)false, request.Think);
    }

    [Fact]
    public void Maps_Reasoning_Without_Effort_Leaves_Think_Null()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Output = ReasoningOutput.Full }
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Null(request.Think);
    }

    [Fact]
    public void Maps_Reasoning_Does_Not_Override_RawRepresentation()
    {
        var options = new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            RawRepresentationFactory = _ => new ChatRequest { Think = false },
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            new MockChatClient(),
            [],
            options,
            stream: true,
            JsonSerializerOptions.Default);

        Assert.Equal((ThinkValue)false, request.Think);
    }

    [Fact]
    public void Maps_Instructions_To_Leading_System_Message()
    {
        var options = new ChatOptions
        {
            Instructions = "Always respond concisely."
        };
        var messages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "Hello")
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            messages,
            options,
            stream: false,
            JsonSerializerOptions.Default);

        var mapped = request.Messages!.ToList();
        Assert.Equal(2, mapped.Count);
        Assert.Equal(OllamaChatRole.System, mapped[0].Role);
        Assert.Equal("Always respond concisely.", mapped[0].Content);
        Assert.Equal(OllamaChatRole.User, mapped[1].Role);
        Assert.Equal("Hello", mapped[1].Content);
    }

    [Fact]
    public void Maps_Thinking_Content_On_Outgoing_Messages()
    {
        var messages = new List<ChatMessage>
        {
            // 1. Assistant message with only TextReasoningContent (no text, no tool calls)
            new(Microsoft.Extensions.AI.ChatRole.Assistant, [new TextReasoningContent("Standalone reasoning.")]),
            // 2. Assistant message with multiple TextReasoningContent chunks + tool call + empty text
            new(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new TextContent(string.Empty),
                    new TextReasoningContent("Step 1. "),
                    new TextReasoningContent("Step 2."),
                    new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" })
                ]),
            // 3. Assistant message with both reasoning and final text
            new(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new TextReasoningContent("Thought before answer."),
                    new TextContent("Final answer.")
                ])
        };

        var request = OllamaSharpChatClientAdapter.ToOllamaSharpChatRequest(
            null,
            messages,
            null,
            stream: false,
            JsonSerializerOptions.Default);

        var mapped = request.Messages!.ToList();
        Assert.Equal(3, mapped.Count);

        Assert.Equal("Standalone reasoning.", mapped[0].Thinking);
        Assert.Equal(string.Empty, mapped[0].Content);
        Assert.Null(mapped[0].ToolCalls);

        Assert.Equal("Step 1. Step 2.", mapped[1].Thinking);
        Assert.Equal(string.Empty, mapped[1].Content);
        Assert.Single(mapped[1].ToolCalls!);

        Assert.Equal("Thought before answer.", mapped[2].Thinking);
        Assert.Equal("Final answer.", mapped[2].Content);
    }

    [Fact]
    public void ToChatResponse_Maps_Known_Properties()
    {
        var ollamaCreatedStamp = "2023-08-04T08:52:19.385406-07:00";

        var stream = new ChatDoneResponseStream
        {
            CreatedAtString = ollamaCreatedStamp,
            Done = true,
            DoneReason = "stop",
            EvalCount = 111,
            EvalDuration = 2222222222,
            LoadDuration = 3333333333,
            Message = new Message { Role = OllamaChatRole.Assistant, Content = "Hi." },
            Model = "qwen3.5:35b-a3b",
            PromptEvalCount = 411,
            PromptEvalDuration = 5555555555,
            TotalDuration = 6666666666
        };

        var response = OllamaSharpChatClientAdapter.ToChatResponse(stream, usedModel: null);

        Assert.NotNull(response);
        Assert.NotNull(response!.AdditionalProperties);
        Assert.Equal(TimeSpan.FromSeconds(2.222222222), response.AdditionalProperties!["eval_duration"]);
        Assert.Equal(TimeSpan.FromSeconds(3.333333333), response.AdditionalProperties["load_duration"]);
        Assert.Equal(TimeSpan.FromSeconds(6.666666666), response.AdditionalProperties["total_duration"]);
        Assert.Equal(TimeSpan.FromSeconds(5.555555555), response.AdditionalProperties["prompt_eval_duration"]);
        Assert.Equal(new DateTimeOffset(2023, 08, 04, 08, 52, 19, 385, 406, TimeSpan.FromHours(-7)), response.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Null(response.Messages[0].AuthorName);
        Assert.Equal(ollamaCreatedStamp, response.Messages[0].MessageId);
        Assert.Same(stream.Message, response.Messages[0].RawRepresentation);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, response.Messages[0].Role);
        Assert.Equal("Hi.", response.Messages[0].Text);
        Assert.Single(response.Messages[0].Contents);
        Assert.Equal("Hi.", ((TextContent)response.Messages[0].Contents[0]).Text);
        Assert.Equal("qwen3.5:35b-a3b", response.ModelId);
        Assert.Same(stream, response.RawRepresentation);
        Assert.Equal(ollamaCreatedStamp, response.ResponseId);
        Assert.NotNull(response.Usage);
        Assert.Equal(411, response.Usage!.InputTokenCount);
        Assert.Equal(111, response.Usage.OutputTokenCount);
        Assert.Equal(111 + 411, response.Usage.TotalTokenCount);
    }

    [Fact]
    public void ToChatResponse_NullStream_ReturnsNull()
    {
        Assert.Null(OllamaSharpChatClientAdapter.ToChatResponse(null, "qwen3.5:cloud"));
    }

    [Fact]
    public void ToChatResponseUpdate_Maps_Known_Properties()
    {
        var ollamaCreated = new DateTimeOffset(2023, 08, 04, 08, 52, 19, 385, 406, TimeSpan.FromHours(-7));

        var stream = new ChatResponseStream
        {
            CreatedAt = ollamaCreated,
            Done = true,
            Message = new Message { Role = OllamaChatRole.Assistant, Content = "Hi." },
            Model = "qwen3.5:35b-a3b"
        };

        var streamingChatCompletion = OllamaSharpChatClientAdapter.ToChatResponseUpdate(stream, "12345");

        Assert.Null(streamingChatCompletion.AdditionalProperties);
        Assert.Null(streamingChatCompletion.AuthorName);
        Assert.Single(streamingChatCompletion.Contents);
        Assert.Equal("Hi.", ((TextContent)streamingChatCompletion.Contents[0]).Text);
        Assert.Equal(ollamaCreated, streamingChatCompletion.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, streamingChatCompletion.FinishReason);
        Assert.Equal("12345", streamingChatCompletion.MessageId);
        Assert.Same(stream, streamingChatCompletion.RawRepresentation);
        Assert.Equal("12345", streamingChatCompletion.ResponseId);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, streamingChatCompletion.Role);
        Assert.Equal("Hi.", streamingChatCompletion.Text);
    }

    [Fact]
    public void ToChatResponseUpdate_Maps_Thinking_Tokens()
    {
        var ollamaCreated = new DateTimeOffset(2023, 08, 04, 08, 52, 19, 385, 406, TimeSpan.FromHours(-7));

        var stream = new ChatResponseStream
        {
            CreatedAt = ollamaCreated,
            Done = true,
            Message = new Message { Role = OllamaChatRole.Assistant, Content = "", Thinking = "Beer." },
            Model = "qwen3.5:35b-a3b"
        };

        var streamingChatCompletion = OllamaSharpChatClientAdapter.ToChatResponseUpdate(stream, "12345");

        Assert.Null(streamingChatCompletion.AdditionalProperties);
        Assert.Null(streamingChatCompletion.AuthorName);
        Assert.Single(streamingChatCompletion.Contents);
        Assert.Equal("Beer.", ((TextReasoningContent)streamingChatCompletion.Contents[0]).Text);
        Assert.Equal(ollamaCreated, streamingChatCompletion.CreatedAt);
        Assert.Equal(ChatFinishReason.Stop, streamingChatCompletion.FinishReason);
        Assert.Equal("12345", streamingChatCompletion.MessageId);
        Assert.Same(stream, streamingChatCompletion.RawRepresentation);
        Assert.Equal("12345", streamingChatCompletion.ResponseId);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, streamingChatCompletion.Role);
        Assert.Empty(streamingChatCompletion.Text);
    }

    [Fact]
    public void ToChatResponseUpdate_Maps_ToolCalls()
    {
        var ollamaCreated = new DateTimeOffset(2023, 08, 04, 08, 52, 19, 385, 406, TimeSpan.FromHours(-7));

        var message = new Message
        {
            Role = OllamaChatRole.Assistant,
            Content = "It seems the sun will be out all day.",
            ToolCalls =
            [
                new Message.ToolCall
                {
                    Function = new Message.Function
                    {
                        Arguments = new Dictionary<string, object?>
                        {
                            ["city"] = "Honolulu",
                            ["unit"] = "celsius"
                        },
                        Name = "get_weather"
                    }
                }
            ]
        };

        var stream = new ChatResponseStream
        {
            CreatedAt = ollamaCreated,
            Done = true,
            Message = message,
            Model = "qwen3.5:35b-a3b"
        };

        var chatMessage = OllamaSharpChatClientAdapter.ToChatResponseUpdate(stream, "12345");

        Assert.Null(chatMessage.AdditionalProperties);
        Assert.Null(chatMessage.AuthorName);
        Assert.Equal(2, chatMessage.Contents.Count);
        var textContent = Assert.IsType<TextContent>(chatMessage.Contents.First());
        Assert.Equal("It seems the sun will be out all day.", textContent.Text);

        var toolCall = Assert.IsType<FunctionCallContent>(chatMessage.Contents.Last());
        Assert.Null(toolCall.AdditionalProperties);
        Assert.Equal(2, toolCall.Arguments!.Count);
        Assert.Equal("Honolulu", toolCall.Arguments["city"]);
        Assert.Equal("celsius", toolCall.Arguments["unit"]);
        Assert.Equal(8, toolCall.CallId.Length);
        Assert.Null(toolCall.Exception);
        Assert.Equal("get_weather", toolCall.Name);
        Assert.Same(message.ToolCalls.Single(), toolCall.RawRepresentation);
        Assert.Same(stream, chatMessage.RawRepresentation);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, chatMessage.Role);
        Assert.Equal("It seems the sun will be out all day.", chatMessage.Text);
    }

    [Fact]
    public void ToChatResponseUpdate_Separates_Updates_With_Different_MessageIds()
    {
        var updates = new[]
        {
            OllamaSharpChatClientAdapter.ToChatResponseUpdate(new ChatResponseStream
            {
                Message = new Message { Role = OllamaChatRole.Assistant, Content = "First" }
            }, "response-1"),
            OllamaSharpChatClientAdapter.ToChatResponseUpdate(new ChatResponseStream
            {
                Message = new Message { Role = OllamaChatRole.Assistant, Content = "Second" }
            }, "response-2")
        };

        var response = updates.ToChatResponse();

        Assert.Equal(2, response.Messages.Count);
        Assert.Equal("response-1", response.Messages[0].MessageId);
        Assert.Equal("First", response.Messages[0].Text);
        Assert.Equal("response-2", response.Messages[1].MessageId);
        Assert.Equal("Second", response.Messages[1].Text);
    }

    [Fact]
    public void ToChatResponseUpdate_Combines_Updates_With_The_Same_MessageId()
    {
        var updates = new[]
        {
            OllamaSharpChatClientAdapter.ToChatResponseUpdate(new ChatResponseStream
            {
                Message = new Message { Role = OllamaChatRole.Assistant, Content = "Hel" }
            }, "response-1"),
            OllamaSharpChatClientAdapter.ToChatResponseUpdate(new ChatResponseStream
            {
                Message = new Message { Role = OllamaChatRole.Assistant, Content = "lo" }
            }, "response-1")
        };

        var response = updates.ToChatResponse();

        Assert.Single(response.Messages);
        Assert.Equal("response-1", response.Messages[0].MessageId);
        Assert.Equal("Hello", response.Messages[0].Text);
    }

    [Fact]
    public void ToChatResponseUpdate_Maps_Performance_Metrics_To_AdditionalProperties()
    {
        var ollamaCreated = new DateTimeOffset(2023, 08, 04, 08, 52, 19, 385, 406, TimeSpan.FromHours(-7));
        var responseId = "test-response-67890";

        var stream = new ChatDoneResponseStream
        {
            CreatedAt = ollamaCreated,
            Done = true,
            Message = new Message
            {
                Role = OllamaChatRole.Assistant,
                Content = "Hello, world!",
                Thinking = ""
            },
            Model = "qwen3.5:35b-a3b",
            TotalDuration = 1_234_567_890,
            LoadDuration = 123_456_789,
            PromptEvalCount = 42,
            PromptEvalDuration = 98_765_432,
            EvalCount = 15,
            EvalDuration = 876_543_210,
            DoneReason = "stop"
        };

        var streamingChatCompletion = OllamaSharpChatClientAdapter.ToChatResponseUpdate(stream, responseId);

        const double nanosecondsPerMillisecond = 1_000_000;

        Assert.Equal(responseId, streamingChatCompletion.MessageId);
        Assert.NotNull(streamingChatCompletion.AdditionalProperties);
        Assert.Equal(TimeSpan.FromMilliseconds(123_456_789 / nanosecondsPerMillisecond), streamingChatCompletion.AdditionalProperties!["load_duration"]);
        Assert.Equal(TimeSpan.FromMilliseconds(1_234_567_890 / nanosecondsPerMillisecond), streamingChatCompletion.AdditionalProperties["total_duration"]);
        Assert.Equal(TimeSpan.FromMilliseconds(98_765_432 / nanosecondsPerMillisecond), streamingChatCompletion.AdditionalProperties["prompt_eval_duration"]);
        Assert.Equal(TimeSpan.FromMilliseconds(876_543_210 / nanosecondsPerMillisecond), streamingChatCompletion.AdditionalProperties["eval_duration"]);

        var usageContent = Assert.Single(streamingChatCompletion.Contents.OfType<UsageContent>());
        Assert.NotNull(usageContent.Details);
        Assert.Equal(42, usageContent.Details.InputTokenCount);
        Assert.Equal(15, usageContent.Details.OutputTokenCount);
        Assert.Equal(42 + 15, usageContent.Details.TotalTokenCount);

        var combinedResponse = new[] { streamingChatCompletion }.ToChatResponse();
        Assert.NotNull(combinedResponse.Messages.Single().AdditionalProperties);
        Assert.True(combinedResponse.Messages.Single().AdditionalProperties!.ContainsKey("total_duration"));
        Assert.NotNull(combinedResponse.Usage);
        Assert.Equal(42, combinedResponse.Usage!.InputTokenCount);
        Assert.Equal(15, combinedResponse.Usage.OutputTokenCount);
    }

    [Fact]
    public void ToChatMessage_Maps_ToolCalls()
    {
        var message = new Message
        {
            Role = OllamaChatRole.Assistant,
            Content = "It seems the sun will be out all day.",
            ToolCalls =
            [
                new Message.ToolCall
                {
                    Function = new Message.Function
                    {
                        Arguments = new Dictionary<string, object?>
                        {
                            ["city"] = "Honolulu",
                            ["unit"] = "celsius"
                        },
                        Name = "get_weather"
                    }
                }
            ]
        };

        var chatMessage = OllamaSharpChatClientAdapter.ToChatMessage(message);

        Assert.Null(chatMessage.AdditionalProperties);
        Assert.Null(chatMessage.AuthorName);
        Assert.Equal(2, chatMessage.Contents.Count);
        var textContent = Assert.IsType<TextContent>(chatMessage.Contents.First());
        Assert.Equal("It seems the sun will be out all day.", textContent.Text);

        var toolCall = Assert.IsType<FunctionCallContent>(chatMessage.Contents.Last());
        Assert.Null(toolCall.AdditionalProperties);
        Assert.Equal(2, toolCall.Arguments!.Count);
        Assert.Equal("Honolulu", toolCall.Arguments["city"]);
        Assert.Equal("celsius", toolCall.Arguments["unit"]);
        Assert.Equal(8, toolCall.CallId.Length);
        Assert.Null(toolCall.Exception);
        Assert.Equal("get_weather", toolCall.Name);
        Assert.Same(message.ToolCalls.Single(), toolCall.RawRepresentation);
        Assert.Same(message, chatMessage.RawRepresentation);
        Assert.Equal(Microsoft.Extensions.AI.ChatRole.Assistant, chatMessage.Role);
        Assert.Equal("It seems the sun will be out all day.", chatMessage.Text);
    }

    [Fact]
    public void GetService_Returns_Metadata_UnderlyingClient_And_Self()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:11434") };
        var innerClient = new OllamaApiClient(httpClient, "qwen3.5:cloud", null);
        using var adapter = new OllamaSharpChatClientAdapter(innerClient);

        var metadata = adapter.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("ollama", metadata!.ProviderName);
        Assert.Equal(new Uri("http://localhost:11434"), metadata.ProviderUri);
        Assert.Equal("qwen3.5:cloud", metadata.DefaultModelId);

        Assert.Same(innerClient, adapter.GetService<OllamaApiClient>());
        Assert.Same(innerClient, adapter.GetService<IOllamaApiClient>());
        Assert.Same(adapter, adapter.GetService<OllamaSharpChatClientAdapter>());
        Assert.Same(adapter, adapter.GetService<IChatClient>());
        Assert.Null(adapter.GetService(typeof(ChatClientMetadata), serviceKey: "keyed"));
        Assert.Null(adapter.GetService<string>());
    }

    [Fact]
    public async Task Adapter_RoundTrips_Thinking_And_ToolCalls_In_Streaming_And_NonStreaming()
    {
        var capturedPayloads = new List<string>();
        var handler = new DelegateHttpMessageHandler(async request =>
        {
            capturedPayloads.Add(await request.Content!.ReadAsStringAsync());
            const string responseJson = """
                {"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:00Z","message":{"role":"assistant","content":"Done.","thinking":"Final verification."},"done":true,"done_reason":"stop","prompt_eval_count":80,"eval_count":15}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson + "\n", Encoding.UTF8, "application/json")
            };
        });

        using var client = OllamaChatClientFactory.CreateOllama(
            new LlmProviderOptions { Endpoint = "http://localhost:11434", DefaultModelId = "qwen3.5:cloud" },
            modelId: "qwen3.5:cloud",
            NullLogger.Instance,
            TimeProvider.System,
            new RetryPolicyOptions(),
            OllamaRoutingTarget.Auto,
            handler,
            _ => { });

        var messages = new List<ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.User, "Inspect task 24."),
            new(
                Microsoft.Extensions.AI.ChatRole.Assistant,
                [
                    new TextContent(string.Empty),
                    new TextReasoningContent("I need to read the task markdown first."),
                    new FunctionCallContent("call_abc", "read_file", new Dictionary<string, object?> { ["path"] = "docs/tasks/24.md" })
                ]),
            new(
                Microsoft.Extensions.AI.ChatRole.Tool,
                [
                    new FunctionResultContent("call_abc", "file contents")
                ])
        };

        var nonStreamingResponse = await client.GetResponseAsync(messages);
        Assert.Equal("Done.", nonStreamingResponse.Text);
        Assert.Equal("Final verification.", Assert.Single(nonStreamingResponse.Messages[0].Contents.OfType<TextReasoningContent>()).Text);

        var streamingUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            streamingUpdates.Add(update);
        }
        var aggregated = streamingUpdates.ToChatResponse();
        Assert.Equal("Done.", aggregated.Text);
        Assert.Equal("Final verification.", Assert.Single(aggregated.Messages[0].Contents.OfType<TextReasoningContent>()).Text);

        Assert.Equal(2, capturedPayloads.Count);
        foreach (var payload in capturedPayloads)
        {
            using var doc = JsonDocument.Parse(payload);
            var assistantMsg = doc.RootElement.GetProperty("messages")[1];
            Assert.Equal("assistant", assistantMsg.GetProperty("role").GetString());
            Assert.Equal("I need to read the task markdown first.", assistantMsg.GetProperty("thinking").GetString());
            Assert.Equal(1, assistantMsg.GetProperty("tool_calls").GetArrayLength());
        }
    }

    private sealed class MockChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responder(request);
    }
}
