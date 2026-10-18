using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Resolves repository-level prompt and persona overrides from .nightcodeforge/prompts/ markdown files.
/// </summary>
public static class WorkspacePromptResolver
{
    private static readonly Regex StageIdValidator = new(@"^[a-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates that a stage ID contains only safe alphanumeric characters, underscores, and hyphens to prevent path traversal attacks.
    /// </summary>
    /// <param name="stageId">The stage identifier to validate.</param>
    public static void ValidateStageId(string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        if (!StageIdValidator.IsMatch(stageId))
        {
            throw new ArgumentException($"Invalid stage ID '{stageId}'. Stage IDs must contain only alphanumeric characters, underscores, and hyphens.", nameof(stageId));
        }
    }

    /// <summary>
    /// Discovers stage-specific directive overrides from .nightcodeforge/prompts/{stageId}.md or .nightcodeforge/{stageId}.md.
    /// </summary>
    public static string ResolveStageDirective(string? workspacePath, string stageId, string defaultDirective, ILogger? logger = null)
    {
        ValidateStageId(stageId);

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return defaultDirective;
        }

        var candidatePaths = new[]
        {
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, NightCodeForgeDirectories.Prompts, $"{stageId}.md"),
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, $"{stageId}.md")
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
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, NightCodeForgeDirectories.Prompts, "system.md"),
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, "system.md")
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
        ValidateStageId(stageId);

        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return null;
        }

        var candidatePaths = new[]
        {
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, NightCodeForgeDirectories.Prompts, $"{stageId}.bootstrap.md"),
            Path.Combine(workspacePath, NightCodeForgeDirectories.Root, $"{stageId}.bootstrap.md")
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
