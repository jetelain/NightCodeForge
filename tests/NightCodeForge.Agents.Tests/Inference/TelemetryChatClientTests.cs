using Microsoft.Extensions.AI;
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
        _client = new TelemetryChatClient(_innerClient.Object, _telemetryService.Object, "qwen2.5-coder:32b", "Remote");
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

        await foreach (var _ in _client.GetStreamingResponseAsync(messages, cancellationToken: cts.Token))
        {
            if (cts.IsCancellationRequested) break;
        }

        Assert.NotNull(completedRecord);
        Assert.Equal(LlmCallStatus.Cancelled, completedRecord.Status);
    }
}
