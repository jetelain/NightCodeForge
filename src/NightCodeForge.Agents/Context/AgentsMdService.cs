using System.Text;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Context;

/// <summary>
/// Service to discover and ingest repository rules from AGENTS.md / CLAUDE.md / GEMINI.md
/// and format execution limits and timeout policies for agent prompts.
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

    public string FormatProjectContextSection(
        string? agentsMdRules,
        WorkspaceProjectConfig? projectConfig = null,
        Workspace? workspace = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(agentsMdRules))
        {
            sb.AppendLine("<project_context>");
            sb.AppendLine("Project-specific instructions and guidelines from AGENTS.md:");
            sb.AppendLine(agentsMdRules);
            sb.AppendLine("</project_context>");
        }

        if (projectConfig != null || workspace != null)
        {
            var guidelines = FormatExecutionGuidelines(projectConfig, workspace);
            if (!string.IsNullOrWhiteSpace(guidelines))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(guidelines);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string FormatExecutionGuidelines(WorkspaceProjectConfig? projectConfig, Workspace? workspace = null)
    {
        var buildCmd = projectConfig?.BuildCommand ?? "dotnet build";
        var testCmd = projectConfig?.TestCommand ?? "dotnet test --no-build";
        var buildTimeout = workspace?.BuildTimeoutSeconds ?? projectConfig?.BuildTimeoutSeconds ?? 120;
        var testTimeout = workspace?.TestTimeoutSeconds ?? projectConfig?.TestTimeoutSeconds ?? 120;

        return $@"<execution_guidelines>
Active repository execution commands and timeout limits:
- Build Command: `{buildCmd}` (Timeout: {buildTimeout}s)
- Test Command: `{testCmd}` (Timeout: {testTimeout}s)
- Automated Quality Gate Enforcement: Build and test runners will terminate and fail if execution exceeds these timeouts.
- CRITICAL RESOURCE PROTECTION: Never write or leave hanging tests, infinite loops, deadlocks, or unmocked blocking network calls. All tests and builds must run quickly and finish strictly within their timeout limits.
</execution_guidelines>";
    }
}
