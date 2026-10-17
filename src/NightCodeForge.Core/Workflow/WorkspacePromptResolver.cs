using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Resolves repository-level prompt and persona overrides from .nightcodeforge/prompts/ markdown files.
/// </summary>
public static class WorkspacePromptResolver
{
    /// <summary>
    /// Discovers stage-specific directive overrides from .nightcodeforge/prompts/{stageId}.md or .nightcodeforge/{stageId}.md.
    /// </summary>
    public static string ResolveStageDirective(string? workspacePath, string stageId, string defaultDirective, ILogger? logger = null)
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
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to read stage prompt override file '{Path}'", path);
                    throw new InvalidOperationException($"Failed to read prompt override file '{path}'.", ex);
                }
            }
        }

        return defaultDirective;
    }

    /// <summary>
    /// Discovers system prompt / persona overrides from .nightcodeforge/prompts/system.md or .nightcodeforge/system.md.
    /// </summary>
    public static string? ResolveSystemPrompt(string? workspacePath, string? defaultPrompt = null, ILogger? logger = null)
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
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to read system prompt override file '{Path}'", path);
                    throw new InvalidOperationException($"Failed to read prompt override file '{path}'.", ex);
                }
            }
        }

        return defaultPrompt;
    }

    /// <summary>
    /// Discovers initial bootstrap prompt template overrides from .nightcodeforge/prompts/{stageId}.bootstrap.md.
    /// </summary>
    public static string? ResolveBootstrapPromptOverride(string? workspacePath, string stageId, ILogger? logger = null)
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
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to read bootstrap prompt override file '{Path}'", path);
                    throw new InvalidOperationException($"Failed to read prompt override file '{path}'.", ex);
                }
            }
        }

        return null;
    }
}
