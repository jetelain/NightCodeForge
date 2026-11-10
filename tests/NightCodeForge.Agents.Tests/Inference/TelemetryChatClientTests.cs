using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class TelemetryChatClientTests
{
    private readonly Mock<IChatClient> _innerClient;
    private readonly Mock<IOperationsTelemetryService> _telemetryService;
    private readonly TelemetryChatClient _client;

    public TelemetryChatClientTests()
    {
        _innerClient = new Mock<IChatClient>();
        _telemetryService = new Mock<IOperationsTelemetryService>();
        _client = new TelemetryChatClient(_innerClient.Object, _telemetryService.Object, "qwen2.5-coder:32b", "Remote", NullLogger<TelemetryChatClient>.Instance, null, null, TimeProvider.System, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GetResponseAsync_OnSuccess_NotifiesStartAndCompletion()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Implement a function") };
        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Here is the implementation"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 20,
                OutputTokenCount = 35
            }
        };

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        LlmCallRecord? startedRecord = null;
        LlmCallRecord? completedRecord = null;

        _telemetryService
            .Setup(t => t.RecordLlmCallStarted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => startedRecord = r);

        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        var result = await _client.GetResponseAsync(messages);

        Assert.Same(expectedResponse, result);
        Assert.NotNull(startedRecord);
        Assert.Equal("qwen2.5-coder:32b", startedRecord.Model);
        Assert.Equal("Remote", startedRecord.TargetBackend);
        Assert.Equal(LlmCallStatus.Running, startedRecord.Status);
        Assert.Contains("Implement a function", startedRecord.PromptPreview);

        Assert.NotNull(completedRecord);
        Assert.Equal(startedRecord.CallId, completedRecord.CallId);
        Assert.Equal(LlmCallStatus.Completed, completedRecord.Status);
        Assert.Contains("Here is the implementation", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetResponseAsync_OnFailure_RecordsFailedStatus_AndRethrows()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Test failure") };

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Model timeout"));

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _client.GetResponseAsync(messages));
        Assert.Equal("Model timeout", ex.Message);

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Failed, completedRecord.Status);
        Assert.Equal("Model timeout", completedRecord.ErrorMessage);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UpdatesProgressAndCompletes()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Streaming test") };

        async IAsyncEnumerable<ChatResponseUpdate> FakeStream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Part 1 ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Part 2");
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(FakeStream());

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _client.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        _telemetryService.Verify(t => t.RecordLlmCallStarted(It.IsAny<LlmCallRecord>()), Times.Once);
        _telemetryService.Verify(t => t.UpdateLlmCallProgress(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()), Times.AtLeastOnce);
        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Completed, completedRecord.Status);
        Assert.Contains("Part 1 Part 2", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithTextReasoningContent_IncrementsTokensAndIncludesReasoningInSpeed()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Solve problem") };

        async IAsyncEnumerable<ChatResponseUpdate> FakeReasoningStream()
        {
            // Chunk 1: Thinking trace 1
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent("Deconstructing requirements into parts. ")]
            };
            await Task.Yield();

            // Chunk 2: Thinking trace 2
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent("Selecting optimal algorithm. ")]
            };
            await Task.Yield();

            // Chunk 3: Final answer text
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("Result: 42")]
            };
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(FakeReasoningStream());

        LlmCallRecord? completedRecord = null;
        var progressUpdates = new List<(int Tokens, double Duration, double Tps, int Reasoning)>();

        _telemetryService
            .Setup(t => t.UpdateLlmCallProgress(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Callback<string, int, double, double, int>((id, tokens, dur, tps, reasoning) =>
            {
                progressUpdates.Add((tokens, dur, tps, reasoning));
            });

        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _client.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        Assert.Equal(3, updates.Count);
        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Completed, completedRecord.Status);

        // Reasoning tokens must be positive and counted
        Assert.True(completedRecord.ReasoningTokens > 0, "ReasoningTokens should be > 0");
        // Total completion tokens must include reasoning tokens + text tokens
        Assert.True(completedRecord.CompletionTokens >= completedRecord.ReasoningTokens, "CompletionTokens must encompass reasoning tokens");
        // Speed must be calculated over total tokens including reasoning
        Assert.True(completedRecord.TokensPerSecond >= 0, "TokensPerSecond should be non-negative");

        // Inspect preview formatting
        Assert.Contains("[Reasoning]", completedRecord.CompletionPreview);
        Assert.Contains("Deconstructing requirements", completedRecord.CompletionPreview);
        Assert.Contains("[Response]", completedRecord.CompletionPreview);
        Assert.Contains("Result: 42", completedRecord.CompletionPreview);

        // Progress updates during streaming must track reasoning
        Assert.Contains(progressUpdates, u => u.Reasoning > 0);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithThinkTagsInText_ExtractsReasoningTokens()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "DeepSeek style prompt") };

        async IAsyncEnumerable<ChatResponseUpdate> FakeThinkTagStream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "<think>Carefully checking edge cases.");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, " Done thinking.</think>Here is the clean code.");
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(FakeThinkTagStream());

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await foreach (var _ in _client.GetStreamingResponseAsync(messages)) { }

        Assert.NotNull(completedRecord);
        Assert.True(completedRecord.ReasoningTokens > 0, "ReasoningTokens from <think> tags should be > 0");
        Assert.True(completedRecord.CompletionTokens > completedRecord.ReasoningTokens, "CompletionTokens must include both think and final text");
        Assert.Contains("[Reasoning]", completedRecord.CompletionPreview);
        Assert.Contains("Carefully checking edge cases", completedRecord.CompletionPreview);
        Assert.Contains("[Response]", completedRecord.CompletionPreview);
        Assert.Contains("Here is the clean code", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetResponseAsync_WithTextReasoningContent_IncludesReasoningInCompletionTokensAndSpeed()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Design architecture") };
        var assistantMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("Evaluating architectural patterns and blast radius."),
            new TextContent("Architecture diagram approved.")
        });

        var expectedResponse = new ChatResponse(assistantMsg);

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        var response = await _client.GetResponseAsync(messages);

        Assert.Same(expectedResponse, response);
        Assert.NotNull(completedRecord);
        Assert.True(completedRecord.ReasoningTokens > 0, "ReasoningTokens should be detected in non-streaming response");
        Assert.True(completedRecord.CompletionTokens >= completedRecord.ReasoningTokens, "CompletionTokens must include reasoning tokens");
        Assert.Contains("[Reasoning]", completedRecord.CompletionPreview);
        Assert.Contains("[Response]", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenConsumerBreaksEarly_StillRecordsCompletion()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Break early test") };

        async IAsyncEnumerable<ChatResponseUpdate> InfiniteOrMultiChunkStream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 1 ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 2 ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Chunk 3 ");
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(InfiniteOrMultiChunkStream());

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        // Consumer reads only 1 chunk and breaks early
        await foreach (var update in _client.GetStreamingResponseAsync(messages))
        {
            break;
        }

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Completed, completedRecord.Status);
        Assert.Contains("Chunk 1", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenCancelled_RecordsCancelledStatus()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Cancellation test") };
        using var cts = new CancellationTokenSource();

        async IAsyncEnumerable<ChatResponseUpdate> SlowStream()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Starting ");
            await Task.Yield();
            cts.Cancel();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Ending");
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(SlowStream());

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        try
        {
            await foreach (var _ in _client.GetStreamingResponseAsync(messages, cancellationToken: cts.Token))
            {
                if (cts.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation fires during MoveNextAsync.
        }

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Cancelled, completedRecord.Status);
    }

    [Fact]
    public void EstimateTotalContextTokens_CalculatesTokensAcrossAllMessageRolesAndToolPayloads()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a software engineer assistant."),
            new(ChatRole.User, "Please inspect file.cs and run tests."),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call_1", "run_command", new Dictionary<string, object?> { ["command"] = "dotnet test" })
            }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call_1", "Passed 50 tests successfully.")
            }),
            new(ChatRole.Assistant, "All tests passed cleanly.")
        };

        var estimated = TelemetryChatClient.EstimateTotalContextTokens(messages);

        // Framing overhead: 5 messages * 4 = 20
        // Plus content tokens of each text, function name, arguments, results
        Assert.True(estimated > 40, $"Estimated tokens ({estimated}) should account for all 5 turns and tool calls/results");
    }

    [Fact]
    public void EstimateTotalContextTokens_DoesNotDoubleCountTextContentAndMessageText()
    {
        // "12345678" has 8 chars => Pmad.AI HeuristicTokenEstimator.Default estimates ceil(8 / 3.8) = 3 tokens.
        // Framing overhead for 1 message is 4 tokens.
        // Total should be exactly 4 + 3 = 7 tokens, not 4 + 3 + 3 = 10 tokens.
        var message = new ChatMessage(ChatRole.User, "12345678");
        var estimated = TelemetryChatClient.EstimateTotalContextTokens([message]);

        Assert.Equal(7, estimated);
    }

    [Fact]
    public async Task GetResponseAsync_SetsInitialPromptTokensFromFullContext_AndPrefersAuthoritativeUsage()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Long system prompt instruction repeated. " + new string('X', 400)),
            new(ChatRole.User, "User prompt turn 1. " + new string('Y', 400)),
            new(ChatRole.Assistant, "Assistant turn 1. " + new string('Z', 400)),
            new(ChatRole.User, "Final query")
        };

        var expectedResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Final answer"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 385,
                OutputTokenCount = 15
            }
        };

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        LlmCallRecord? startedRecord = null;
        LlmCallRecord? completedRecord = null;

        _telemetryService
            .Setup(t => t.RecordLlmCallStarted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => startedRecord = r);

        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await _client.GetResponseAsync(messages);

        Assert.NotNull(startedRecord);
        // Initial PromptTokens in startedRecord should be calculated from ALL 4 messages (well above 500 chars / 125 tokens)
        Assert.True(startedRecord.PromptTokens > 250, $"Started PromptTokens should be > 250, got {startedRecord.PromptTokens}");

        Assert.NotNull(completedRecord);
        // Completed PromptTokens must prefer the authoritative response.Usage.InputTokenCount
        Assert.Equal(385, completedRecord.PromptTokens);
    }

    [Fact]
    public async Task GetResponseAsync_PopulatesEndpointAndThinkingEffort()
    {
        var client = new TelemetryChatClient(_innerClient.Object, _telemetryService.Object, "custom-model", "Auto", NullLogger<TelemetryChatClient>.Instance, "http://my-ollama:11434", null, TimeProvider.System, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = "high"
            }
        };

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "result")));

        LlmCallRecord? started = null;
        LlmCallRecord? completed = null;

        _telemetryService.Setup(t => t.RecordLlmCallStarted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => started = r);
        _telemetryService.Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completed = r);

        await client.GetResponseAsync(messages, options);

        Assert.NotNull(started);
        Assert.Equal("http://my-ollama:11434", started.Endpoint);
        Assert.Equal("high", started.ThinkingEffort);

        Assert.NotNull(completed);
        Assert.Equal("http://my-ollama:11434", completed.Endpoint);
        Assert.Equal("high", completed.ThinkingEffort);
    }

    [Theory]
    [InlineData(null, "unknown")]
    public void ResolveThinkingEffort_WhenOptionsNull_ReturnsUnknown(ChatOptions? options, string expected)
    {
        Assert.Equal(expected, TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Fact]
    public void ResolveThinkingEffort_WhenAdditionalPropertiesNull_ReturnsUnknown()
    {
        var options = new ChatOptions { AdditionalProperties = null };
        Assert.Equal("unknown", TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Fact]
    public void ResolveThinkingEffort_WhenThinkIsFalse_ReturnsNone()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false
            }
        };
        Assert.Equal("none", TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveThinkingEffort_WhenThinkIsTrue_AndReasoningEffortMissingOrEmpty_ReturnsDefault(string? reasoningEffort)
    {
        var props = new AdditionalPropertiesDictionary { ["think"] = true };
        if (reasoningEffort != null)
        {
            props["reasoning_effort"] = reasoningEffort;
        }

        var options = new ChatOptions { AdditionalProperties = props };
        Assert.Equal("default", TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Fact]
    public void ResolveThinkingEffort_WhenThinkMissingFromAdditionalProperties_ReturnsUnknown()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["other_key"] = "some_value"
            }
        };
        Assert.Equal("unknown", TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    public void ResolveThinkingEffort_WhenThinkIsTrue_WithReasoningEffort_ReturnsEffort(string effort)
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = effort
            }
        };
        Assert.Equal(effort, TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Fact]
    public async Task GetResponseAsync_WhenHttpClientTimeout_RecordsStatusAsFailedNotCancelled()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        var timeoutEx = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(timeoutEx);

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await Assert.ThrowsAsync<TaskCanceledException>(() => _client.GetResponseAsync(messages, cancellationToken: CancellationToken.None));

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Failed, completedRecord.Status);
        Assert.Contains("LLM inference request timed out after 900 seconds", completedRecord.ErrorMessage);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WhenHttpClientTimeout_RecordsStatusAsFailedNotCancelled()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        var timeoutEx = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

        async IAsyncEnumerable<ChatResponseUpdate> ThrowingStream()
        {
            await Task.Yield();
            if (bool.TryParse("false", out _))
            {
                yield return new ChatResponseUpdate();
            }
            throw timeoutEx;
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(ThrowingStream());

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await foreach (var _ in _client.GetStreamingResponseAsync(messages, cancellationToken: CancellationToken.None)) { }
        });

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Failed, completedRecord.Status);
        Assert.Contains("LLM inference request timed out after 900 seconds", completedRecord.ErrorMessage);
    }

    [Fact]
    public async Task GetResponseAsync_WhenCallerCancellationRequested_RecordsStatusAsCancelled()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "test") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.GetResponseAsync(messages, cancellationToken: cts.Token));

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Cancelled, completedRecord.Status);
    }

    [Theory]
    [InlineData(false, null, "none")]
    [InlineData(true, null, "default")]
    [InlineData(true, "medium", "medium")]
    public async Task GetResponseAsync_PopulatesExpectedThinkingEffort(bool? think, string? effort, string expectedEffort)
    {
        var client = new TelemetryChatClient(_innerClient.Object, _telemetryService.Object, "model", "Auto", NullLogger<TelemetryChatClient>.Instance, null, null, TimeProvider.System, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));
        var props = new AdditionalPropertiesDictionary();

        if (think == true)
        {
            props["think"] = (object?)effort ?? think;
        }
        else if (think == false)
        {
            props["think"] = false;
        }

        if (!think.HasValue && effort == null) props["other"] = 123;

        var options = new ChatOptions { AdditionalProperties = props };

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        LlmCallRecord? started = null;
        _telemetryService.Setup(t => t.RecordLlmCallStarted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => started = r);

        await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }, options);

        Assert.NotNull(started);
        Assert.Equal(expectedEffort, started.ThinkingEffort);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public void ResolveThinkingEffort_WhenReasoningEffortPresentWithoutThinkFlag_ReturnsReasoningEffort(string effort)
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_effort"] = effort
            }
        };

        Assert.Equal(effort, TelemetryChatClient.ResolveThinkingEffort(options));
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithFunctionCallContent_IncrementsTokensAndPopulatesPreview()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Write file") };

        async IAsyncEnumerable<ChatResponseUpdate> FakeToolStream()
        {
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextReasoningContent("Planning file update. ")]
            };
            await Task.Yield();

            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents =
                [
                    new FunctionCallContent(
                        "call_1",
                        "write_file",
                        new Dictionary<string, object?>
                        {
                            ["path"] = "src/Service.cs",
                            ["content"] = "public class Service { public void Execute() { } }"
                        })
                ]
            };
        }

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(FakeToolStream());

        LlmCallRecord? completedRecord = null;
        var progressUpdates = new List<(int Tokens, double Duration, double Tps, int Reasoning)>();

        _telemetryService
            .Setup(t => t.UpdateLlmCallProgress(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<int>()))
            .Callback<string, int, double, double, int>((id, tokens, dur, tps, reasoning) =>
            {
                progressUpdates.Add((tokens, dur, tps, reasoning));
            });

        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await foreach (var _ in _client.GetStreamingResponseAsync(messages)) { }

        Assert.Equal(2, progressUpdates.Count);
        Assert.True(progressUpdates[1].Tokens > progressUpdates[0].Tokens, "Tool call chunk must increment live token count beyond reasoning tokens");
        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Completed, completedRecord.Status);
        Assert.True(completedRecord.CompletionTokens > completedRecord.ReasoningTokens);
        Assert.Contains("[Tool Call: write_file]", completedRecord.CompletionPreview);
        Assert.Contains("path=src/Service.cs", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetResponseAsync_WithFunctionCallContent_IncludesToolTokensAndPreview()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Run tests") };
        var assistantMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("Running unit test suite."),
            new FunctionCallContent(
                "call_1",
                "run_command",
                new Dictionary<string, object?>
                {
                    ["command"] = "dotnet test --no-build"
                })
        });

        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(assistantMsg));

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await _client.GetResponseAsync(messages);

        Assert.NotNull(completedRecord);
        Assert.True(completedRecord.ReasoningTokens > 0);
        Assert.True(completedRecord.CompletionTokens > completedRecord.ReasoningTokens, "CompletionTokens must include FunctionCallContent tokens");
        Assert.Contains("[Reasoning]", completedRecord.CompletionPreview);
        Assert.Contains("[Tool Call: run_command]", completedRecord.CompletionPreview);
        Assert.Contains("command=dotnet test --no-build", completedRecord.CompletionPreview);
    }

    [Fact]
    public async Task GetResponseAsync_WithComplexFunctionCallArguments_FormatsJsonElementsAndCollectionsCleanly()
    {
        using var jsonDoc = System.Text.Json.JsonDocument.Parse("""{"target":"src/App.cs","flags":["--fast","--strict"]}""");
        var targetElem = jsonDoc.RootElement.GetProperty("target");
        var flagsElem = jsonDoc.RootElement.GetProperty("flags");

        var assistantCallMsg = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(
                "call_complex",
                "analyze_code",
                new Dictionary<string, object?>
                {
                    ["target"] = targetElem,
                    ["flags"] = flagsElem,
                    ["tags"] = new[] { "lint", "security" },
                    ["line"] = 42
                })
        });

        var promptMessages = new List<ChatMessage>
        {
            new(ChatRole.User, "Analyze App.cs"),
            assistantCallMsg
        };

        // 1. Verify ExtractPromptPreview formats complex tool call arguments cleanly without Type names
        var promptPreview = TelemetryChatClient.ExtractPromptPreview(promptMessages);
        Assert.Contains("[Tool Call: analyze_code]", promptPreview);
        Assert.Contains("target=src/App.cs", promptPreview);
        Assert.Contains("""flags=["--fast","--strict"]""", promptPreview);
        Assert.Contains("""tags=["lint","security"]""", promptPreview);
        Assert.Contains("line=42", promptPreview);
        Assert.DoesNotContain("System.String[]", promptPreview);

        // 2. Verify CompletionPreview in GetResponseAsync formats complex tool call arguments cleanly
        _innerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(assistantCallMsg));

        LlmCallRecord? completedRecord = null;
        _telemetryService
            .Setup(t => t.RecordLlmCallCompleted(It.IsAny<LlmCallRecord>()))
            .Callback<LlmCallRecord>(r => completedRecord = r);

        await _client.GetResponseAsync([new(ChatRole.User, "Analyze App.cs")]);

        Assert.NotNull(completedRecord);
        Assert.NotNull(completedRecord.CompletionPreview);
        Assert.Contains("[Tool Call: analyze_code]", completedRecord.CompletionPreview);
        Assert.Contains("target=src/App.cs", completedRecord.CompletionPreview);
        Assert.Contains("""flags=["--fast","--strict"]""", completedRecord.CompletionPreview);
        Assert.Contains("""tags=["lint","security"]""", completedRecord.CompletionPreview);
        Assert.Contains("line=42", completedRecord.CompletionPreview);
        Assert.DoesNotContain("System.String[]", completedRecord.CompletionPreview);
    }
}
