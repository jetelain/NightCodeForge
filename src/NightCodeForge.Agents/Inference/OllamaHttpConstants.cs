using System.Net.Sockets;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Connection tuning constants and transport helpers for LLM HTTP communication.
/// </summary>
public static class OllamaHttpConstants
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultConnectionLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan DefaultConnectionIdleTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultKeepAlivePingDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultKeepAlivePingTimeout = TimeSpan.FromSeconds(15);

    public const int DefaultTcpKeepAliveTimeSeconds = 15;
    public const int DefaultTcpKeepAliveIntervalSeconds = 5;
    public const int DefaultTcpKeepAliveRetryCount = 3;

    /// <summary>
    /// Creates a hardened <see cref="SocketsHttpHandler"/> configured with fast connect timeouts,
    /// connection pooling limits, and OS-level TCP keep-alive probes.
    /// </summary>
    public static SocketsHttpHandler CreateSocketsHttpHandler() => new()
    {
        ConnectTimeout = DefaultConnectTimeout,
        PooledConnectionLifetime = DefaultConnectionLifetime,
        PooledConnectionIdleTimeout = DefaultConnectionIdleTimeout,
        KeepAlivePingDelay = DefaultKeepAlivePingDelay,
        KeepAlivePingTimeout = DefaultKeepAlivePingTimeout,
        ConnectCallback = ConnectKeepAliveSocketAsync
    };

    /// <summary>
    /// Connect callback establishing an OS-level TCP keep-alive socket for HTTP/1.1 connections.
    /// Detects dead peers within ~30s rather than hanging on half-open sockets for 15 minutes.
    /// </summary>
    public static async ValueTask<Stream> ConnectKeepAliveSocketAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, DefaultTcpKeepAliveTimeSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, DefaultTcpKeepAliveIntervalSeconds);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, DefaultTcpKeepAliveRetryCount);
            }
            catch (SocketException)
            {
                // In case the underlying OS/network stack does not support specific TCP keepalive sub-options
            }

            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
