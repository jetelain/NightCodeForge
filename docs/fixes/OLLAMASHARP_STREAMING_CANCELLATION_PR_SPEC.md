# Upstream Fix Specification: OllamaSharp Streaming CancellationToken Propagation

> **Target Repository:** [awaescher/OllamaSharp](https://github.com/awaescher/OllamaSharp)  
> **Upstream PR:** [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400) (scoped to `.NET 7+`)  
> **Type:** Bug Fix / PR Specification  
> **Affects:** Streaming endpoints (`ChatAsync`, `GenerateAsync`, and `Microsoft.Extensions.AI` integration)

---

## 1. Summary

In `OllamaSharp`, streaming response processors (such as `ProcessStreamedChatResponseAsync`) read NDJSON lines from the HTTP response stream via `StreamReader.ReadLineAsync()` **without passing the method's `CancellationToken`**.

Consequently, `StreamReader` invokes `Stream.ReadAsync(Memory<byte>, CancellationToken.None)`. If the network stalls, the remote Ollama instance pauses generation mid-stream, or a client watchdog triggers cancellation, the background read blocks indefinitely on the underlying socket. Checking `!cancellationToken.IsCancellationRequested` only occurs *between* lines, leaving in-flight line reads uncancelable.

Furthermore, when consumed via the `Microsoft.Extensions.AI.IChatClient` interface (`GetStreamingResponseAsync`), calling `await enumerator.DisposeAsync()` while `MoveNextAsync()` is suspended inside `ReadLineAsync()` triggers `System.NotSupportedException` from the compiler-generated `IAsyncEnumerable` state machine.

---

## 2. Root Cause Analysis

In `OllamaApiClient.cs`:

```csharp
private async IAsyncEnumerable<ChatResponseStream?> ProcessStreamedChatResponseAsync(
    HttpResponseMessage response, 
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(continueOnCapturedContext: false);
    using StreamReader reader = new StreamReader(stream);
    while (!cancellationToken.IsCancellationRequested)
    {
        // BUG: ReadLineAsync() is called with no CancellationToken!
        string text = await reader.ReadLineAsync().ConfigureAwait(continueOnCapturedContext: false);
        if (text != null)
        {
            ChatResponseStream chatResponseStream = JsonSerializer.Deserialize<ChatResponseStream>(text, IncomingJsonSerializerOptions);
            yield return (chatResponseStream != null && chatResponseStream.Done) 
                ? JsonSerializer.Deserialize<ChatDoneResponseStream>(text, IncomingJsonSerializerOptions) 
                : chatResponseStream;
            continue;
        }
        break;
    }
}
```

### Affected Streaming Methods in `OllamaSharp`:
1. `ProcessStreamedChatResponseAsync(HttpResponseMessage, CancellationToken)`
2. `ProcessStreamedGenerateResponseAsync(HttpResponseMessage, CancellationToken)` (if present)
3. Any other streaming helpers reading NDJSON lines with `StreamReader.ReadLineAsync()`.

---

## 3. Proposed Fix (Non-Breaking / Backward-Compatible, Scoped to `.NET 7+`)

> [!NOTE]
> Submitted upstream in [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400), scoped to `.NET 7+` (`NET7_0_OR_GREATER`), which covers `NightCodeForge` (`.NET 10`).

Notice that `OllamaSharp`'s existing loop condition is `while (!cancellationToken.IsCancellationRequested)`: when cancellation occurs *between* lines, `OllamaSharp` exits the stream gracefully (`MoveNextAsync()` returns `false`) rather than throwing `OperationCanceledException`.

To preserve 100% backward compatibility for existing `OllamaSharp` consumers who cancel a token to stop streaming gracefully—while still unblocking in-flight `ReadLineAsync` calls immediately for timeout watchdogs—pass `cancellationToken` on `.NET 7+` and catch `OperationCanceledException when (cancellationToken.IsCancellationRequested)` around `ReadLineAsync` to `break` out of the loop cleanly:

```csharp
string? text;
try
{
#if NET7_0_OR_GREATER
    text = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
#else
    text = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    break;
}
```

---

## 4. Suggested Pull Request Details

### Title
`fix(streaming): pass CancellationToken to StreamReader.ReadLineAsync to unblock stalled stream reads`

### Description
> When streaming responses in `ProcessStreamedChatResponseAsync`, `reader.ReadLineAsync()` is currently invoked without a `CancellationToken`.
> 
> **Impact:**
> - If an upstream Ollama instance or reverse proxy stalls mid-stream, or if the client attempts to cancel a long-running generation while waiting for the next token, the stream read blocks indefinitely on the underlying socket.
> - When used via `Microsoft.Extensions.AI.IChatClient.GetStreamingResponseAsync`, attempting to dispose an in-flight enumeration while `ReadLineAsync()` is suspended causes the C# async iterator state machine to throw `NotSupportedException`.
> 
> **Solution (Non-Breaking, `.NET 7+`):**
> - On .NET 7+ (`NET7_0_OR_GREATER`), pass `cancellationToken` directly to `reader.ReadLineAsync(cancellationToken)`.
> - Catch `OperationCanceledException when (cancellationToken.IsCancellationRequested)` and `break` out of the `while (!cancellationToken.IsCancellationRequested)` loop so existing callers who cancel a token to stop streaming continue to see a clean end-of-stream (`MoveNextAsync() == false`) rather than an unhandled exception, while `using` blocks (`StreamReader`, `Stream`, `HttpResponseMessage`) dispose deterministically.

### Test Case for PR
```csharp
[Fact]
public async Task ProcessStreamedChatResponseAsync_WhenCancellationTokenFiresMidRead_UnblocksAndCompletesGracefully()
{
    // Arrange: stream that yields headers and 1 token, then hangs indefinitely
    using var cts = new CancellationTokenSource();
    var stream = new BlockingTestStream();
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    // Act: enumerate first token then schedule cancellation while second ReadLineAsync is blocked
    var chatClient = new OllamaApiClient(new HttpClient(new EchoHandler(response)), "test-model");
    var received = new List<ChatResponseStream?>();

    await foreach (var update in chatClient.ChatAsync(new ChatRequest { Stream = true }, cts.Token))
    {
        received.Add(update);
        cts.CancelAfter(50); // Fires while awaiting the second ReadLineAsync()
    }

    Assert.Single(received);
}
```

