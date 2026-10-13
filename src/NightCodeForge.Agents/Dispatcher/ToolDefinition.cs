using System.Text.Json;

namespace NightCodeForge.Agents.Dispatcher;

/// <summary>
/// Extensible tool definition supporting aliases, parameter mappings, and dynamic execution handlers.
/// </summary>
public class ToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string> ParameterAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<Dictionary<string, object?>, CancellationToken, Task<string>>? Handler { get; set; }
    public JsonElement? ParameterSchema { get; set; }

    public ToolDefinition() { }

    public ToolDefinition(
        string name,
        string description,
        Func<Dictionary<string, object?>, CancellationToken, Task<string>> handler,
        IEnumerable<string>? aliases = null,
        IDictionary<string, string>? parameterAliases = null)
    {
        Name = name;
        Description = description;
        Handler = handler;
        if (aliases != null) Aliases.AddRange(aliases);
        if (parameterAliases != null)
        {
            foreach (var (k, v) in parameterAliases)
            {
                ParameterAliases[k] = v;
            }
        }
    }
}

/// <summary>
/// Rule translating a legacy or external tool invocation into another canonical tool call.
/// </summary>
public class ToolTranslationRule
{
    public string SourceTool { get; set; } = string.Empty;
    public string TargetTool { get; set; } = string.Empty;
    public Func<Dictionary<string, object?>, Dictionary<string, object?>> TransformArguments { get; set; } = args => args;

    public ToolTranslationRule() { }

    public ToolTranslationRule(string sourceTool, string targetTool, Func<Dictionary<string, object?>, Dictionary<string, object?>> transform)
    {
        SourceTool = sourceTool;
        TargetTool = targetTool;
        TransformArguments = transform;
    }
}
