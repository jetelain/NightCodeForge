namespace NightCodeForge.Agents.Crucible;

public class CustomToolConfig
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string> ParameterAliases { get; set; } = new();
    public string? McpServerName { get; set; }
}

/// <summary>
/// Extensible configuration for Crucible stored in AgentProfile.HarnessConfigurationJson.
/// Allows database-driven customization of tools, directives, descriptions, and rules.
/// </summary>
public class CrucibleHarnessConfig
{
    public List<CustomToolConfig> CustomTools { get; set; } = new();
    public Dictionary<string, string> ToolDirectives { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> StageDirectives { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string>? AllowedTools { get; set; }
    public int MaxTurns { get; set; } = 20;
    public string? CustomSystemRules { get; set; }
}
