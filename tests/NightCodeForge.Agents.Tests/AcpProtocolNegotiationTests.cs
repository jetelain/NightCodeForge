using System.Text;
using NightCodeForge.Agents.Acp;

namespace NightCodeForge.Agents.Tests;

public class AcpProtocolNegotiationTests
{
    [Fact]
    public async Task NegotiateProtocolAsync_WhenAgentReturnsV2_NegotiatesV2()
    {
        var input = "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}\n";
        using var streamIn = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var reader = new StreamReader(streamIn);

        using var streamOut = new MemoryStream();
        using var writer = new StreamWriter(streamOut, Encoding.UTF8) { AutoFlush = true };

        int version = await AcpAgentHarness.NegotiateProtocolAsync(writer, reader, CancellationToken.None);

        Assert.Equal(2, version);
        var sent = Encoding.UTF8.GetString(streamOut.ToArray());
        Assert.Contains("\"protocolVersion\":2", sent);
    }

    [Fact]
    public async Task NegotiateProtocolAsync_WhenAgentReturnsV1_FallsBackToV1()
    {
        var input = "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":1}}\n";
        using var streamIn = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var reader = new StreamReader(streamIn);

        using var streamOut = new MemoryStream();
        using var writer = new StreamWriter(streamOut, Encoding.UTF8) { AutoFlush = true };

        int version = await AcpAgentHarness.NegotiateProtocolAsync(writer, reader, CancellationToken.None);

        Assert.Equal(1, version);
    }
}
