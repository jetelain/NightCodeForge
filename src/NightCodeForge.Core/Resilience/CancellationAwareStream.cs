namespace NightCodeForge.Core.Resilience;

using System.IO;
using System.Net;
using System.Net.Http;

/// <summary>
/// HttpContent decorator that wraps the underlying content's stream with a <see cref="CancellationAwareStream"/>
/// linking the request's cancellation token across all read operations.
/// </summary>
/// <remarks>
/// TEMPORARY WORKAROUND until upstream PR <see href="https://github.com/awaescher/OllamaSharp/pull/400"/> is released and referenced
/// (see <c>docs/fixes/STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md</c>).
/// </remarks>
public sealed class CancellationAwareHttpContent : HttpContent
{
    private readonly HttpContent _innerContent;
    private readonly CancellationToken _requestCancellationToken;

    public CancellationAwareHttpContent(HttpContent innerContent, CancellationToken requestCancellationToken)
    {
        _innerContent = innerContent ?? throw new ArgumentNullException(nameof(innerContent));
        _requestCancellationToken = requestCancellationToken;

        foreach (var header in innerContent.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return _innerContent.CopyToAsync(stream, context, _requestCancellationToken);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestCancellationToken);
        await _innerContent.CopyToAsync(stream, context, linked.Token);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var innerStream = await _innerContent.ReadAsStreamAsync(_requestCancellationToken);
        return new CancellationAwareStream(innerStream, _requestCancellationToken);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestCancellationToken);
        var innerStream = await _innerContent.ReadAsStreamAsync(linked.Token);
        return new CancellationAwareStream(innerStream, _requestCancellationToken);
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestCancellationToken);
        var innerStream = _innerContent.ReadAsStream(linked.Token);
        return new CancellationAwareStream(innerStream, _requestCancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _innerContent.Headers.ContentLength ?? -1;
        return length >= 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerContent.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Stream decorator that enforces a request-level cancellation token on all read operations.
/// When the request cancellation token fires, pending and subsequent reads fail immediately with
/// <see cref="OperationCanceledException"/> and the underlying inner stream is closed/disposed,
/// preventing uncancelable reads such as <see cref="StreamReader.ReadLineAsync()"/> from hanging.
/// </summary>
public sealed class CancellationAwareStream : Stream
{
    private readonly Stream _innerStream;
    private readonly CancellationToken _requestCancellationToken;

    public CancellationAwareStream(Stream innerStream, CancellationToken requestCancellationToken)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _requestCancellationToken = requestCancellationToken;
    }

    public Stream InnerStream => _innerStream;

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _innerStream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        _innerStream.Seek(offset, origin);

    public override void SetLength(long value) =>
        _innerStream.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_requestCancellationToken.IsCancellationRequested)
        {
            _innerStream.Dispose();
            _requestCancellationToken.ThrowIfCancellationRequested();
        }

        using var reg = _requestCancellationToken.CanBeCanceled
            ? _requestCancellationToken.UnsafeRegister(static state =>
              {
                  try
                  {
                      ((Stream)state!).Dispose();
                  }
                  catch (Exception)
                  {
                      // Justification: Suppress socket/stream disposal exceptions during cancellation abort so OperationCanceledException propagates cleanly.
                  }
              }, _innerStream)
            : default;

        try
        {
            return _innerStream.Read(buffer);
        }
        catch (Exception ex) when (_requestCancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            try
            {
                _innerStream.Dispose();
            }
            catch (Exception)
            {
                // Justification: Suppress secondary disposal exceptions after read was already aborted by cancellation.
            }
            throw new OperationCanceledException("Stream read was canceled.", ex, _requestCancellationToken);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_requestCancellationToken.IsCancellationRequested)
        {
            _innerStream.Dispose();
            _requestCancellationToken.ThrowIfCancellationRequested();
        }

        if (!cancellationToken.CanBeCanceled && !_requestCancellationToken.CanBeCanceled)
        {
            return await _innerStream.ReadAsync(buffer, CancellationToken.None);
        }

        using var linkedCts = cancellationToken.CanBeCanceled && _requestCancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestCancellationToken)
            : null;

        var effectiveToken = linkedCts?.Token
            ?? (cancellationToken.CanBeCanceled ? cancellationToken : _requestCancellationToken);

        using var reg = effectiveToken.CanBeCanceled
            ? effectiveToken.UnsafeRegister(static state =>
              {
                  try
                  {
                      ((Stream)state!).Dispose();
                  }
                  catch (Exception)
                  {
                      // Justification: Suppress socket/stream disposal exceptions during cancellation abort so OperationCanceledException propagates cleanly.
                  }
              }, _innerStream)
            : default;

        try
        {
            return await _innerStream.ReadAsync(buffer, effectiveToken);
        }
        catch (Exception ex) when (effectiveToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            try
            {
                _innerStream.Dispose();
            }
            catch (Exception)
            {
                // Justification: Suppress secondary disposal exceptions after read was already aborted by cancellation.
            }
            throw new OperationCanceledException("Stream read was canceled.", ex, effectiveToken);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        _innerStream.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _innerStream.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _innerStream.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync();
        await base.DisposeAsync();
    }
}
