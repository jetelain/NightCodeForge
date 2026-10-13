using System.Text.Json;
using NightCodeForge.Agents.Mcp;

namespace NightCodeForge.Agents.Tests;

public class McpClientManagerTests
{
    [Fact]
    public async Task GetAcpMcpServersAsync_ParsesMcpConfigFile_AndMapsToAcpServerDescriptors()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_mcp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var configContent = @"{
  ""mcpServers"": {
    ""sqlite"": {
      ""command"": ""uvx"",
      ""args"": [""mcp-server-sqlite"", ""--db-path"", ""test.db""],
      ""env"": { ""DEBUG"": ""1"" }
    }
  }
}";
            await File.WriteAllTextAsync(Path.Combine(tempDir, "mcp_config.json"), configContent);

            var manager = new McpClientManager();
            var acpServers = await manager.GetAcpMcpServersAsync(1, tempDir);

            Assert.NotNull(acpServers);
            Assert.Single(acpServers);

            var server = acpServers[0];
            var json = JsonSerializer.Serialize(server);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("sqlite", root.GetProperty("name").GetString());
            Assert.Equal("uvx", root.GetProperty("command").GetString());
            Assert.Equal("mcp-server-sqlite", root.GetProperty("args")[0].GetString());
            Assert.Equal("1", root.GetProperty("env").GetProperty("DEBUG").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
