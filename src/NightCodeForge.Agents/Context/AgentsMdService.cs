namespace NightCodeForge.Agents.Context;

/// <summary>
/// Service to discover and ingest repository rules from AGENTS.md / CLAUDE.md / GEMINI.md.
/// </summary>
public class AgentsMdService
{
    private static readonly string[] RuleCandidatePaths =
    [
        "AGENTS.md",
        ".agents/AGENTS.md",
        "CLAUDE.md",
        "GEMINI.md"
    ];

    public async Task<string?> IngestRulesAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return null;
        }

        foreach (var relativePath in RuleCandidatePaths)
        {
            var fullPath = Path.Combine(workspaceRoot, relativePath);
            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
                return content.Trim();
            }
        }

        return null;
    }

    public string FormatProjectContextSection(string? agentsMdRules)
    {
        if (string.IsNullOrWhiteSpace(agentsMdRules))
        {
            return string.Empty;
        }

        return $"<project_context>\nProject-specific instructions and guidelines from AGENTS.md:\n{agentsMdRules}\n</project_context>";
    }
}
