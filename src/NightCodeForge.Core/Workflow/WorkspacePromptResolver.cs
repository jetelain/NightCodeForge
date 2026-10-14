namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Resolves repository-level prompt and persona overrides from .nightcodeforge/prompts/ markdown files.
/// </summary>
public static class WorkspacePromptResolver
{
    /// <summary>
    /// Discovers stage-specific directive overrides from .nightcodeforge/prompts/{stageId}.md or .nightcodeforge/{stageId}.md.
    /// </summary>
    public static string ResolveStageDirective(string? workspacePath, string stageId, string defaultDirective)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return defaultDirective;
        }

        var candidatePaths = new[]
        {
            Path.Combine(workspacePath, ".nightcodeforge", "prompts", $"{stageId}.md"),
            Path.Combine(workspacePath, ".nightcodeforge", "prompts", $"{stageId.Replace("-", "_")}.md"),
            Path.Combine(workspacePath, ".nightcodeforge", $"{stageId}.md"),
            Path.Combine(workspacePath, ".nightcodeforge", $"{stageId.Replace("-", "_")}.md")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }
                }
                catch { }
            }
        }

        return defaultDirective;
    }

    /// <summary>
    /// Discovers system prompt / persona overrides from .nightcodeforge/prompts/system.md or .nightcodeforge/system.md.
    /// </summary>
    public static string? ResolveSystemPrompt(string? workspacePath, string? defaultPrompt = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return defaultPrompt;
        }

        var candidatePaths = new[]
        {
            Path.Combine(workspacePath, ".nightcodeforge", "prompts", "system.md"),
            Path.Combine(workspacePath, ".nightcodeforge", "system.md")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }
                }
                catch { }
            }
        }

        return defaultPrompt;
    }

    /// <summary>
    /// Discovers initial bootstrap prompt template overrides from .nightcodeforge/prompts/{stageId}.bootstrap.md.
    /// </summary>
    public static string? ResolveBootstrapPromptOverride(string? workspacePath, string stageId)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        var candidatePaths = new[]
        {
            Path.Combine(workspacePath, ".nightcodeforge", "prompts", $"{stageId}.bootstrap.md"),
            Path.Combine(workspacePath, ".nightcodeforge", "prompts", $"{stageId.Replace("-", "_")}.bootstrap.md"),
            Path.Combine(workspacePath, ".nightcodeforge", $"{stageId}.bootstrap.md")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var content = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        return content;
                    }
                }
                catch { }
            }
        }

        return null;
    }
}
