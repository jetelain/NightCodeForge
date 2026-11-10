using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class StreamingIdleWatchdogIntegrationTests
{
    private sealed class ChunkedStreamingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public ChunkedStreamingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _chunkOffset;

        public void PushChunk(string ndjsonLine)
        {
            var bytes = Encoding.UTF8.GetBytes(ndjsonLine + "\n");
            _channel.Writer.TryWrite(bytes);
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentChunk == null || _chunkOffset >= _currentChunk.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0; // EOF
                }

                if (_channel.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _chunkOffset = 0;
                }
            }

            var available = _currentChunk.Length - _chunkOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _currentChunk.AsSpan(_chunkOffset, toCopy).CopyTo(buffer.Span);
            _chunkOffset += toCopy;
            return toCopy;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _channel.Writer.TryComplete(new ObjectDisposedException(nameof(ChannelStream)));
            }
            base.Dispose(disposing);
        }
    }

    private static (IChatClient Client, List<string> CapturedRequests) CreateTestPipeline(
        HttpMessageHandler innerHandler,
        TimeSpan firstTokenTimeout,
        TimeSpan streamIdleTimeout,
        TimeProvider? timeProvider = null)
    {
        var capturedRequests = new List<string>();
        var effectiveTimeProvider = timeProvider ?? TimeProvider.System;

        var delegatingHandler = new ChunkedStreamingHandler(async (request, ct) =>
        {
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync(ct);
                capturedRequests.Add(body);
            }
            return await ((Task<HttpResponseMessage>)((HttpMessageHandler)innerHandler)
                .GetType()
                .GetMethod("SendAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(innerHandler, [request, ct])!)
                ?? throw new InvalidOperationException("Failed to invoke inner handler");
        });

        var options = new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            DefaultModelId = "test-model",
            MaxOutputTokens = 16384
        };

        var retryOptions = new RetryPolicyOptions
        {
            MaxRetries = 0,
            MaxTimeoutRetries = 0,
            PerAttemptTimeout = firstTokenTimeout
        };

        var rawClient = OllamaChatClientFactory.CreateOllama(
            options,
            "test-model",
            NullLogger.Instance,
            effectiveTimeProvider,
            retryOptions,
            OllamaRoutingTarget.Auto,
            delegatingHandler,
            _ => { });

        var telemetryService = new Mock<IOperationsTelemetryService>();
        var telemetryClient = new TelemetryChatClient(
            rawClient,
            telemetryService.Object,
            "test-model",
            "Auto",
            NullLogger<TelemetryChatClient>.Instance,
            options.Endpoint,
            sessionId: null,
            effectiveTimeProvider,
            firstTokenTimeout,
            streamIdleTimeout);

        return (telemetryClient, capturedRequests);
    }

    [Fact]
    public async Task ScenarioA_SlowFirstTokenWithinPrefillTimeout_ThenActiveStreamBeyondPrefillTimeout_Succeeds()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(400);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(150);
        var stream = new ChannelStream();

        // Background producer: 220ms initial wait (< 400ms prefill, > 150ms idle), then 10 tokens 40ms apart (total ~620ms > 400ms)
        _ = Task.Run(async () =>
        {
            await Task.Delay(220);
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"token0"},"done":false}""");

            for (int i = 1; i <= 10; i++)
            {
                await Task.Delay(40);
                stream.PushChunk($$"""{"model":"test-model","message":{"role":"assistant","content":"token{{i}}"},"done":false}""");
            }

            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":""},"done":true}""");
            stream.Complete();
        });

        var innerHandler = new ChunkedStreamingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        });

        var (client, _) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var tokens = new List<string>();
        var chatOptions = new ChatOptions { MaxOutputTokens = 16384 };

        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")], chatOptions))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                tokens.Add(update.Text);
            }
        }

        Assert.Equal(11, tokens.Count);
        Assert.Equal("token0", tokens[0]);
        Assert.Equal("token10", tokens[10]);
    }

    [Fact]
    public async Task ScenarioB_FirstTokenExceedsPrefillTimeout_AbortsWithPrefillTimeout_AndUnblocksReadLineAsync()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(300);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(120);
        var stream = new ChannelStream();

        // Producer delays 600ms (> 300ms prefill) before emitting any chunk
        _ = Task.Run(async () =>
        {
            await Task.Delay(600);
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"too_late"},"done":false}""");
            stream.Complete();
        });

        var innerHandler = new ChunkedStreamingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        });

        var (client, _) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")])) { }
        });

        Assert.True(TransientErrorDetector.IsHttpTimeout(ex));
        Assert.Equal(StreamTimeoutPhase.FirstTokenPrefill, ex.Diagnostics.Phase);
        Assert.Equal(0, ex.Diagnostics.GeneratedTokens);
        Assert.Contains("prefill timeout (0 tokens received", ex.Message);
    }

    [Fact]
    public async Task ScenarioC_MidStreamPauseWithinIdleTimeout_ResumesCleanly()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(400);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(200);
        var stream = new ChannelStream();

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t1"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t2"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t3"},"done":false}""");

            // Pause within idle timeout (100ms < 200ms)
            await Task.Delay(100);

            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t4"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t5"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":""},"done":true}""");
            stream.Complete();
        });

        var innerHandler = new ChunkedStreamingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        });

        var (client, _) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var tokens = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                tokens.Add(update.Text);
            }
        }

        Assert.Equal(5, tokens.Count);
        Assert.Equal("t1", tokens[0]);
        Assert.Equal("t5", tokens[4]);
    }

    [Fact]
    public async Task ScenarioD_MidStreamPauseExceedsIdleTimeout_UnblocksReadLineAsync_AndThrowsEnrichedTimeout()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(400);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(150);
        var stream = new ChannelStream();

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t1"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t2"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"t3"},"done":false}""");
            // Do NOT complete or push any more chunks: holds stream open indefinitely
        });

        var innerHandler = new ChunkedStreamingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        });

        var (client, _) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")])) { }
        });

        Assert.True(TransientErrorDetector.IsHttpTimeout(ex));
        Assert.Equal(StreamTimeoutPhase.MidStreamIdleStall, ex.Diagnostics.Phase);
        Assert.Equal(6, ex.Diagnostics.GeneratedTokens);
        Assert.Contains("stream stalled", ex.Message);
        Assert.Contains("6 tokens generated", ex.Message);
    }

    [Fact]
    public async Task OutboundRequestBody_ContainsNumPredict_Ceiling()
    {
        var firstTokenTimeout = TimeSpan.FromSeconds(5);
        var streamIdleTimeout = TimeSpan.FromSeconds(5);
        var stream = new ChannelStream();
        stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"done"},"done":true}""");
        stream.Complete();

        var innerHandler = new ChunkedStreamingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        });

        var (client, capturedRequests) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var chatOptions = new ChatOptions { MaxOutputTokens = 16384 };
        await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hello")], chatOptions)) { }

        Assert.NotEmpty(capturedRequests);
        var json = capturedRequests[0];
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("options", out var optionsProp));
        Assert.True(optionsProp.TryGetProperty("num_predict", out var numPredictProp));
        Assert.Equal(16384, numPredictProp.GetInt32());
    }

    [Fact]
    public async Task DeterministicWatchdog_WithDefaultCeilings_UsingManualTimeProvider()
    {
        var timeProvider = new ManualControllableTimeProvider();
        var innerClient = new Mock<IChatClient>();

        var streamStartedTcs = new TaskCompletionSource<bool>();
        var streamTcs1 = new TaskCompletionSource<bool>();
        var streamTcs2 = new TaskCompletionSource<bool>();
        var streamTcs3 = new TaskCompletionSource<bool>();

        async IAsyncEnumerable<ChatResponseUpdate> ControlledStream([EnumeratorCancellation] CancellationToken ct)
        {
            streamStartedTcs.TrySetResult(true);
            using var reg1 = ct.Register(() => streamTcs1.TrySetCanceled(ct));
            await streamTcs1.Task;
            yield return new ChatResponseUpdate { Contents = [new TextContent("token1")] };

            using var reg2 = ct.Register(() => streamTcs2.TrySetCanceled(ct));
            await streamTcs2.Task;
            yield return new ChatResponseUpdate { Contents = [new TextContent("token2")] };

            using var reg3 = ct.Register(() => streamTcs3.TrySetCanceled(ct));
            await streamTcs3.Task;
            yield return new ChatResponseUpdate { Contents = [new TextContent("token3")] };

            // Wait indefinitely for 4th token until watchdog fires
            var tcsIndefinite = new TaskCompletionSource<bool>();
            using var reg4 = ct.Register(() => tcsIndefinite.TrySetCanceled(ct));
            await tcsIndefinite.Task;
            yield return new ChatResponseUpdate { Contents = [new TextContent("token4")] };
        }

        innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct) => ControlledStream(ct));

        var telemetryService = new Mock<IOperationsTelemetryService>();
        var client = new TelemetryChatClient(
            innerClient.Object,
            telemetryService.Object,
            "test-model",
            "Auto",
            NullLogger<TelemetryChatClient>.Instance,
            "http://localhost:11434",
            sessionId: null,
            timeProvider,
            firstTokenTimeout: TimeSpan.FromMinutes(10),
            streamIdleTimeout: TimeSpan.FromMinutes(5));

        var receivedTokens = new List<string>();

        var enumerationTask = Task.Run(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    receivedTokens.Add(update.Text);
                }
            }
        });

        await streamStartedTcs.Task;
        await Task.Delay(25);

        // 1. Advance time by 9.5 minutes (< 10m prefill budget) -> emit token 1
        timeProvider.Advance(TimeSpan.FromMinutes(9.5));
        streamTcs1.TrySetResult(true);

        // Allow token 1 to be yielded
        await Task.Delay(50);
        Assert.Single(receivedTokens);

        // 2. Advance time by 4.5 minutes (< 5m idle budget) -> emit token 2
        timeProvider.Advance(TimeSpan.FromMinutes(4.5));
        streamTcs2.TrySetResult(true);

        await Task.Delay(50);
        Assert.Equal(2, receivedTokens.Count);

        // 3. Advance time by 4.5 minutes (< 5m idle budget) -> emit token 3 (Total elapsed > 18.5 minutes!)
        timeProvider.Advance(TimeSpan.FromMinutes(4.5));
        streamTcs3.TrySetResult(true);

        await Task.Delay(50);
        Assert.Equal(3, receivedTokens.Count);

        // 4. Now pause for 5.1 minutes (> 5m idle watchdog) without emitting token 4 -> watchdog fires!
        timeProvider.Advance(TimeSpan.FromMinutes(5.1));

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(() => enumerationTask);
        Assert.Equal(StreamTimeoutPhase.MidStreamIdleStall, ex.Diagnostics.Phase);
        Assert.True(ex.Diagnostics.GeneratedTokens > 0);
        // Verify ElapsedSeconds uses the injected TimeProvider (9.5m + 4.5m + 4.5m + 5.1m = 23.6m = 1416s), not wall-clock Stopwatch
        Assert.True(ex.Diagnostics.ElapsedSeconds >= 1400.0, $"Expected TimeProvider elapsed >= 1400s, got {ex.Diagnostics.ElapsedSeconds}s");
        Assert.Equal(3, receivedTokens.Count);
    }

    [Fact]
    public async Task ScenarioB_PreHeaderStall_ExceedsPrefillTimeout_ClassifiesAsPreHeaderPrefill()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(250);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(120);

        // Handler stalls before returning HTTP 200 OK headers
        var innerHandler = new ChunkedStreamingHandler(async (req, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var (client, _) = CreateTestPipeline(innerHandler, firstTokenTimeout, streamIdleTimeout);

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")])) { }
        });

        Assert.True(TransientErrorDetector.IsHttpTimeout(ex));
        Assert.Equal(StreamTimeoutPhase.PreHeaderPrefill, ex.Diagnostics.Phase);
        Assert.Equal(0, ex.Diagnostics.GeneratedTokens);
        Assert.Contains("prefill timeout (0 tokens received", ex.Message);
    }

    [Fact]
    public async Task EmptyInitialChunk_BeforePrefillCompletes_KeepsFirstTokenTimeoutBudgetActive()
    {
        var timeProvider = new ManualControllableTimeProvider();
        var innerClient = new Mock<IChatClient>();

        var emptyChunkTcs = new TaskCompletionSource<bool>();
        var firstRealTokenTcs = new TaskCompletionSource<bool>();

        async IAsyncEnumerable<ChatResponseUpdate> StreamWithEmptyRoleHeader([EnumeratorCancellation] CancellationToken ct)
        {
            using var reg1 = ct.Register(() => emptyChunkTcs.TrySetCanceled(ct));
            await emptyChunkTcs.Task;
            // Role-only / empty delta chunk emitted immediately upon HTTP 200 OK before prefill finishes
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [] };

            using var reg2 = ct.Register(() => firstRealTokenTcs.TrySetCanceled(ct));
            await firstRealTokenTcs.Task;
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("real_token")] };
        }

        innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct) => StreamWithEmptyRoleHeader(ct));

        var telemetryService = new Mock<IOperationsTelemetryService>();
        var client = new TelemetryChatClient(
            innerClient.Object,
            telemetryService.Object,
            "test-model",
            "Auto",
            NullLogger<TelemetryChatClient>.Instance,
            "http://localhost:11434",
            sessionId: null,
            timeProvider,
            firstTokenTimeout: TimeSpan.FromMinutes(10),
            streamIdleTimeout: TimeSpan.FromMinutes(5));

        var receivedTokens = new List<string>();
        var enumerationTask = Task.Run(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    receivedTokens.Add(update.Text);
                }
            }
        });

        // Emit the empty role chunk immediately at t=1s
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        emptyChunkTcs.TrySetResult(true);
        await Task.Delay(50);

        // Now advance by 8 minutes (> 5m StreamIdleTimeout, but < 10m FirstTokenTimeout) while prefill completes
        timeProvider.Advance(TimeSpan.FromMinutes(8));
        firstRealTokenTcs.TrySetResult(true);

        await enumerationTask;
        Assert.Single(receivedTokens);
        Assert.Equal("real_token", receivedTokens[0]);
    }

    [Fact]
    public async Task LlmChatClientFactoryBase_HonorsProviderStreamIdleTimeoutMinutes_WhenRetryOptionsUseDefault()
    {
        var timeProvider = new ManualControllableTimeProvider();
        var providerOptions = Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            DefaultModelId = "test-model",
            RequestTimeoutMinutes = 10,
            StreamIdleTimeoutMinutes = 2 // Custom 2-minute idle timeout on LlmProviderOptions
        });
        // Default LlmRetryPolicyOptions (StreamIdleTimeout not explicitly overridden)
        var retryOptions = Options.Create(new LlmRetryPolicyOptions());
        var telemetryService = new Mock<IOperationsTelemetryService>();
        var controlService = new Mock<IOperationsControlService>();
        var streamer = new Mock<NightCodeForge.Core.Streaming.IAgentActivityStreamer>();

        var firstTokenTcs = new TaskCompletionSource<bool>();
        var secondTokenTcs = new TaskCompletionSource<bool>();

        async IAsyncEnumerable<ChatResponseUpdate> TestStream([EnumeratorCancellation] CancellationToken ct)
        {
            firstTokenTcs.TrySetResult(true);
            yield return new ChatResponseUpdate { Contents = [new TextContent("tok1")] };

            using var reg = ct.Register(() => secondTokenTcs.TrySetCanceled(ct));
            await secondTokenTcs.Task;
            yield return new ChatResponseUpdate { Contents = [new TextContent("tok2")] };
        }

        var rawClient = new Mock<IChatClient>();
        rawClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct) => TestStream(ct));

        using var factory = new StubChatClientFactory(
            rawClient.Object,
            providerOptions,
            NullLoggerFactory.Instance,
            telemetryService.Object,
            controlService.Object,
            timeProvider,
            retryOptions,
            streamer.Object);

        var client = factory.CreateClient("test-model");

        var enumTask = Task.Run(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")])) { }
        });

        await firstTokenTcs.Task;
        await Task.Delay(50);

        // Advance by 125 seconds (> 2m / 120s configured on LlmProviderOptions, < 5m / 300s default on LlmRetryPolicyOptions)
        timeProvider.Advance(TimeSpan.FromSeconds(125));

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(() => enumTask);
        Assert.Equal(StreamTimeoutPhase.MidStreamIdleStall, ex.Diagnostics.Phase);
        Assert.Equal(120.0, ex.Diagnostics.ConfiguredTimeoutSeconds);
    }

    [Fact]
    public async Task PreHeaderTimeout_WhenHandlerHasMaxTimeoutRetries_AllowsHandlerToRetryAndSucceedOnSecondAttempt()
    {
        var firstTokenTimeout = TimeSpan.FromMilliseconds(200);
        var streamIdleTimeout = TimeSpan.FromMilliseconds(150);
        int handlerAttempts = 0;

        var innerHandler = new ChunkedStreamingHandler(async (req, ct) =>
        {
            var currentAttempt = Interlocked.Increment(ref handlerAttempts);
            if (currentAttempt == 1)
            {
                // Attempt 1 stalls before HTTP 200 OK headers -> triggers TransientHttpErrorRetryHandler PerAttemptTimeout
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            var stream = new ChannelStream();
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":"recovered_after_retry"},"done":false}""");
            stream.PushChunk("""{"model":"test-model","message":{"role":"assistant","content":""},"done":true}""");
            stream.Complete();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return response;
        });

        var options = new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            DefaultModelId = "test-model",
            MaxOutputTokens = 16384
        };

        var retryOptions = new RetryPolicyOptions
        {
            MaxRetries = 3,
            MaxTimeoutRetries = 2,
            BaseDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDuration = TimeSpan.FromSeconds(10),
            PerAttemptTimeout = firstTokenTimeout
        };

        var rawClient = OllamaChatClientFactory.CreateOllama(
            options,
            "test-model",
            NullLogger.Instance,
            TimeProvider.System,
            retryOptions,
            OllamaRoutingTarget.Auto,
            innerHandler,
            _ => { });

        var telemetryService = new Mock<IOperationsTelemetryService>();
        var client = new TelemetryChatClient(
            rawClient,
            telemetryService.Object,
            "test-model",
            "Auto",
            NullLogger<TelemetryChatClient>.Instance,
            options.Endpoint,
            sessionId: null,
            TimeProvider.System,
            firstTokenTimeout,
            streamIdleTimeout);

        var received = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")]))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                received.Add(update.Text);
            }
        }

        Assert.Equal(2, handlerAttempts);
        Assert.Single(received);
        Assert.Equal("recovered_after_retry", received[0]);
    }

    [Fact]
    public async Task EmptyInitialChunk_WhenCumulativePrefillExceedsFirstTokenTimeout_AbortsAtCumulativePrefillDeadline()
    {
        var timeProvider = new ManualControllableTimeProvider();
        var innerClient = new Mock<IChatClient>();

        var emptyChunkTcs = new TaskCompletionSource<bool>();
        var stalledTokenTcs = new TaskCompletionSource<bool>();

        async IAsyncEnumerable<ChatResponseUpdate> StreamWithEmptyChunkThenStall([EnumeratorCancellation] CancellationToken ct)
        {
            using var reg1 = ct.Register(() => emptyChunkTcs.TrySetCanceled(ct));
            await emptyChunkTcs.Task;
            // Empty role chunk emitted after 4 minutes of prefill
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [] };

            using var reg2 = ct.Register(() => stalledTokenTcs.TrySetCanceled(ct));
            await stalledTokenTcs.Task;
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("never_arrives")] };
        }

        innerClient
            .Setup(c => c.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> msgs, ChatOptions? opts, CancellationToken ct) => StreamWithEmptyChunkThenStall(ct));

        var telemetryService = new Mock<IOperationsTelemetryService>();
        var client = new TelemetryChatClient(
            innerClient.Object,
            telemetryService.Object,
            "test-model",
            "Auto",
            NullLogger<TelemetryChatClient>.Instance,
            "http://localhost:11434",
            sessionId: null,
            timeProvider,
            firstTokenTimeout: TimeSpan.FromMinutes(10),
            streamIdleTimeout: TimeSpan.FromMinutes(5));

        var enumerationTask = Task.Run(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hi")])) { }
        });

        await Task.Delay(25);

        // 1. Advance 4 minutes and emit empty role chunk (6 minutes of the 10-minute prefill budget remain)
        timeProvider.Advance(TimeSpan.FromMinutes(4));
        emptyChunkTcs.TrySetResult(true);
        await Task.Delay(50);

        // 2. Advance 6.5 minutes (total 10.5m > 10m FirstTokenTimeout, even though 6.5m < 10m since the empty chunk)
        timeProvider.Advance(TimeSpan.FromMinutes(6.5));

        var ex = await Assert.ThrowsAsync<LlmStreamTimeoutException>(() => enumerationTask);
        Assert.Equal(StreamTimeoutPhase.FirstTokenPrefill, ex.Diagnostics.Phase);
        Assert.Equal(0, ex.Diagnostics.GeneratedTokens);
    }

    [Fact]
    public void CancellationAwareStream_SynchronousRead_WhenTokenCancelled_DisposesInnerStreamAndThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var disposed = false;
        var innerStream = new BlockingSyncStream(() => disposed = true);
        using var awareStream = new CancellationAwareStream(innerStream, cts.Token);

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            cts.Cancel();
        });

        var buffer = new byte[16];
        Assert.ThrowsAny<OperationCanceledException>(() => awareStream.Read(buffer, 0, buffer.Length));
        Assert.True(disposed);
    }

    private sealed class BlockingSyncStream(Action onDispose) : Stream
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _gate.Wait();
            throw new ObjectDisposedException(nameof(BlockingSyncStream));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
                _gate.Set();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StubChatClientFactory : LlmChatClientFactoryBase
    {
        private readonly IChatClient _rawClient;

        public StubChatClientFactory(
            IChatClient rawClient,
            IOptions<LlmProviderOptions> options,
            Microsoft.Extensions.Logging.ILoggerFactory loggerFactory,
            IOperationsTelemetryService telemetryService,
            IOperationsControlService operationsControlService,
            TimeProvider timeProvider,
            IOptions<LlmRetryPolicyOptions> retryOptions,
            NightCodeForge.Core.Streaming.IAgentActivityStreamer activityStreamer)
            : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
        {
            _rawClient = rawClient;
        }

        protected override IChatClient CreateRawChatClient(
            LlmProviderOptions options,
            string effectiveModel,
            OllamaRoutingTarget routingTarget,
            string? sessionId,
            SocketsHttpHandler socketsHandler) => _rawClient;
    }

    private sealed class ManualControllableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        private long _timestampTicks;
        private readonly List<ControllableTimer> _timers = new();

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestampTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan delta)
        {
            if (delta <= TimeSpan.Zero) return;
            _utcNow = _utcNow.Add(delta);
            _timestampTicks += delta.Ticks;

            var pending = _timers.ToList();
            foreach (var timer in pending)
            {
                if (timer.DueTicks <= _timestampTicks && !timer.IsDisposed)
                {
                    timer.Fire();
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.Zero)
            {
                callback(state);
                return new NoOpTimer();
            }

            var timer = new ControllableTimer(this, callback, state, dueTime);
            _timers.Add(timer);
            return timer;
        }

        private sealed class ControllableTimer : ITimer
        {
            private readonly ManualControllableTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            public long DueTicks { get; private set; }
            public bool IsDisposed { get; private set; }

            public ControllableTimer(ManualControllableTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                DueTicks = dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan
                    ? owner._timestampTicks + dueTime.Ticks
                    : long.MaxValue;
            }

            public void Fire()
            {
                if (!IsDisposed)
                {
                    _callback(_state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueTicks = dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan
                    ? _owner._timestampTicks + dueTime.Ticks
                    : (dueTime == TimeSpan.Zero ? _owner._timestampTicks : long.MaxValue);
                if (dueTime == TimeSpan.Zero && !IsDisposed)
                {
                    Fire();
                }
                return true;
            }

            public void Dispose()
            {
                IsDisposed = true;
                _owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class NoOpTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
