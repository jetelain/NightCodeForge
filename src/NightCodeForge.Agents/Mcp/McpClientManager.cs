using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightCodeForge.Agents.Mcp;

public class McpServerDefinition
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();

    [JsonPropertyName("env")]
    public Dictionary<string, string> Env { get; set; } = new();

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public class McpConfigurationFile
{
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, McpServerDefinition> McpServers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IMcpClientManager
{
    Task<IReadOnlyDictionary<string, McpServerDefinition>> GetConfiguredServersAsync(string workspaceRoot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<object>> GetAcpMcpServersAsync(int? workspaceId, string? workspaceRoot = null, CancellationToken cancellationToken = default);
}

public class McpClientManager : IMcpClientManager
{
    public async Task<IReadOnlyDictionary<string, McpServerDefinition>> GetConfiguredServersAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "mcp_config.json"),
            Path.Combine(workspaceRoot, ".nightcodeforge", "mcp_config.json"),
            Path.Combine(workspaceRoot, ".agents", "mcp_config.json")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path, cancellationToken);
                    var config = JsonSerializer.Deserialize<McpConfigurationFile>(json);
                    if (config?.McpServers != null)
                    {
                        return config.McpServers;
                    }
                }
                catch
                {
                    // Ignore parse error and proceed
                }
            }
        }

        return new Dictionary<string, McpServerDefinition>();
    }

    public async Task<IReadOnlyList<object>> GetAcpMcpServersAsync(int? workspaceId, string? workspaceRoot = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return Array.Empty<object>();
        }

        var servers = await GetConfiguredServersAsync(workspaceRoot, cancellationToken);
        var result = new List<object>();

        foreach (var (name, def) in servers)
        {
            result.Add(new
            {
                name = name,
                command = def.Command,
                args = def.Args,
                env = def.Env,
                url = def.Url
            });
        }

        return result;
    }
}
