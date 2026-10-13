using System.Text.Json.Serialization;

namespace NightCodeForge.Agents.Acp;

public class InitializeRequest
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 2;

    [JsonPropertyName("clientCapabilities")]
    public ClientCapabilities ClientCapabilities { get; set; } = new();
}

public class ClientCapabilities
{
    [JsonPropertyName("fs")]
    public FileSystemCapability Fs { get; set; } = new();

    [JsonPropertyName("terminal")]
    public TerminalCapability Terminal { get; set; } = new();
}

public class FileSystemCapability
{
    [JsonPropertyName("readTextFile")]
    public bool ReadTextFile { get; set; } = true;

    [JsonPropertyName("writeTextFile")]
    public bool WriteTextFile { get; set; } = true;
}

public class TerminalCapability
{
    [JsonPropertyName("create")]
    public bool Create { get; set; } = true;

    [JsonPropertyName("output")]
    public bool Output { get; set; } = true;
}

public class InitializeResponse
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyName("agentCapabilities")]
    public object? AgentCapabilities { get; set; }
}

public class NewSessionRequest
{
    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    [JsonPropertyName("mcpServers")]
    public IReadOnlyList<object>? McpServers { get; set; }
}

public class NewSessionResponse
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;
}

public class PromptRequest
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public List<TextContentBlock> Prompt { get; set; } = new();
}

public class TextContentBlock
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public class PromptResponse
{
    [JsonPropertyName("stopReason")]
    public string StopReason { get; set; } = "completed";
}

public class RequestPermissionRequest
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("rationale")]
    public string? Rationale { get; set; }
}

public class RequestPermissionResponse
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "approved"; // approved, denied, answered
}
