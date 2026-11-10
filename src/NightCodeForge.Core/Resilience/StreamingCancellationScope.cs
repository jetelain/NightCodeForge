namespace NightCodeForge.Core.Resilience;

using System;
using System.Threading;

/// <summary>
/// Mutable state tracked across asynchronous HTTP handler boundaries for an active streaming LLM call.
/// </summary>
public sealed class StreamingCallState
{
    private TaskCompletionSource<bool> _headersReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StreamingCallState(CancellationToken token, long initialStartTimestamp = 0)
    {
        Token = token;
        CurrentAttemptStartTimestamp = initialStartTimestamp;
    }

    public CancellationToken Token { get; }

    /// <summary>
    /// Gets the 1-based attempt number started by <see cref="TransientHttpErrorRetryHandler"/> (0 if no handler ran).
    /// </summary>
    public int AttemptNumber { get; private set; }

    /// <summary>
    /// Gets whether the current HTTP attempt has received its HTTP response headers.
    /// </summary>
    public bool ResponseHeadersReceived { get; private set; }

    /// <summary>
    /// Gets whether <see cref="TransientHttpErrorRetryHandler"/> is currently waiting in retry backoff between attempts.
    /// </summary>
    public bool IsInRetryBackoff { get; private set; }

    /// <summary>
    /// Gets the <see cref="TimeProvider"/> timestamp when the current attempt started.
    /// </summary>
    public long CurrentAttemptStartTimestamp { get; private set; }

    /// <summary>
    /// Gets whether <see cref="TransientHttpErrorRetryHandler"/> is enforcing its own per-attempt pre-header timeout.
    /// </summary>
    public bool HasHandlerAttemptTimeout { get; private set; }

    public void RecordHandlerEntered(bool hasHandlerAttemptTimeout)
    {
        HasHandlerAttemptTimeout = hasHandlerAttemptTimeout;
    }

    public void RecordAttemptStarted(long timestamp, bool hasHandlerAttemptTimeout)
    {
        AttemptNumber++;
        CurrentAttemptStartTimestamp = timestamp;
        ResponseHeadersReceived = false;
        IsInRetryBackoff = false;
        HasHandlerAttemptTimeout = hasHandlerAttemptTimeout;
        if (_headersReceivedTcs.Task.IsCompleted)
        {
            _headersReceivedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void RecordHeadersReceived()
    {
        ResponseHeadersReceived = true;
        IsInRetryBackoff = false;
        _headersReceivedTcs.TrySetResult(true);
    }

    public void RecordRetryBackoffStarted()
    {
        IsInRetryBackoff = true;
        ResponseHeadersReceived = false;
    }

    /// <summary>
    /// Waits asynchronously until <see cref="RecordHeadersReceived"/> is signaled or <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    public Task WaitForHeadersAsync(CancellationToken cancellationToken)
    {
        if (ResponseHeadersReceived)
        {
            return Task.CompletedTask;
        }

        return _headersReceivedTcs.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// Ambient execution context scope providing the stream-level <see cref="CancellationToken"/>
/// and attempt lifecycle state across asynchronous HTTP handler boundaries during streaming LLM invocations.
/// </summary>
/// <remarks>
/// TEMPORARY WORKAROUND: This ambient scope and <see cref="CancellationAwareStream"/> exist because
/// <c>OllamaSharp.OllamaApiClient.ProcessStreamedChatResponseAsync</c> calls <c>StreamReader.ReadLineAsync()</c>
/// without passing the method's <see cref="CancellationToken"/>, and <see cref="System.Net.Http.HttpClient.SendAsync(System.Net.Http.HttpRequestMessage, System.Threading.CancellationToken)"/>
/// disposes its internal linked <see cref="CancellationTokenSource"/> as soon as response headers arrive.
/// Once upstream PR <see href="https://github.com/awaescher/OllamaSharp/pull/400"/> is released and referenced
/// (see <c>docs/fixes/STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md</c>), the token-propagation portion of this scope
/// and <see cref="CancellationAwareHttpContent"/> can be retired.
/// </remarks>
public static class StreamingCancellationScope
{
    private static readonly AsyncLocal<StreamingCallState?> s_currentState = new();

    /// <summary>
    /// Gets the current ambient streaming cancellation token, or <see cref="CancellationToken.None"/> if no scope is active.
    /// </summary>
    public static CancellationToken Current => s_currentState.Value?.Token ?? CancellationToken.None;

    /// <summary>
    /// Gets the current ambient streaming call state, or <c>null</c> if no scope is active.
    /// </summary>
    public static StreamingCallState? CurrentState => s_currentState.Value;

    /// <summary>
    /// Enters a new ambient streaming cancellation scope associated with the given <paramref name="token"/>.
    /// Restores the previous scope when the returned disposable is disposed.
    /// </summary>
    public static IDisposable Enter(CancellationToken token) => Enter(token, out _);

    /// <summary>
    /// Enters a new ambient streaming cancellation scope and outputs the created <see cref="StreamingCallState"/>.
    /// Restores the previous scope when the returned disposable is disposed.
    /// </summary>
    public static IDisposable Enter(CancellationToken token, out StreamingCallState state) =>
        Enter(token, initialStartTimestamp: 0, out state);

    /// <summary>
    /// Enters a new ambient streaming cancellation scope with an initial start timestamp and outputs the created <see cref="StreamingCallState"/>.
    /// Restores the previous scope when the returned disposable is disposed.
    /// </summary>
    public static IDisposable Enter(CancellationToken token, long initialStartTimestamp, out StreamingCallState state)
    {
        var previous = s_currentState.Value;
        state = new StreamingCallState(token, initialStartTimestamp);
        s_currentState.Value = state;
        return new ScopeDisposable(previous);
    }

    private sealed class ScopeDisposable(StreamingCallState? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                s_currentState.Value = previous;
                _disposed = true;
            }
        }
    }
}


