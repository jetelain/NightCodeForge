using System.Text;
using NightCodeForge.Agents.Memory;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Memory;

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
            if (File.Exists(fullPath) && !AgentMemoryFileSystem.HasReparsePointInChain(workspaceRoot, fullPath))
            {
                var content = await AgentMemoryFileSystem.ReadAllTextSharedAsync(fullPath, cancellationToken);
                return content.Trim();
            }
        }

        return null;
    }

    public const string PinnedMemoryTruncationNotice =
        "[MEMORY.md truncated: keep it concise and move detailed notes into topic files under /workspace/memory/<topic>.md]";

    private const string FrontmatterDelimiter = "---";
    private const string FrontmatterDescriptionKey = "description:";
    private const string CatalogSharedLabel = "Shared/Git";
    private const string CatalogLocalLabel = "Local";
    private const string Ellipsis = "…";
    private const int MaxCatalogPreviewChars = 4096;

    /// <summary>
    /// Reads the pinned local <c>MEMORY.md</c> (symlink-checked) and wraps it, bounded by
    /// <see cref="AgentMemoryConstants.MaxPinnedMemoryLines"/> / <see cref="AgentMemoryConstants.MaxPinnedMemoryBytes"/>,
    /// in <c>&lt;workspace_memory&gt;</c> tags. Returns an empty string when the file is missing or blank.
    /// </summary>
    public async Task<string> BuildWorkspaceMemorySectionAsync(string? localMemoryDir, CancellationToken cancellationToken = default)
    {
        if (!TryGetLocalMemoryRoot(localMemoryDir, out var wksRoot) || !Directory.Exists(localMemoryDir))
        {
            return string.Empty;
        }

        var pinned = Path.Combine(localMemoryDir!, AgentMemoryConstants.PinnedMemoryFileName);
        if (!File.Exists(pinned) || AgentMemoryFileSystem.HasReparsePointInChain(wksRoot, pinned))
        {
            return string.Empty;
        }

        var content = await AgentMemoryFileSystem.ReadAllTextSharedAsync(pinned, cancellationToken);
        return FormatWorkspaceMemorySection(content);
    }

    public string FormatWorkspaceMemorySection(string? memoryContent)
    {
        if (string.IsNullOrWhiteSpace(memoryContent))
        {
            return string.Empty;
        }

        return $"<workspace_memory>\n{TruncatePinnedMemory(memoryContent)}\n</workspace_memory>";
    }

    /// <summary>Truncates cleanly at a line boundary when the line or byte cap is exceeded and appends an explicit notice.</summary>
    public static string TruncatePinnedMemory(string content)
    {
        var lines = content.Replace("\r\n", "\n").TrimEnd().Split('\n');
        var kept = new List<string>();
        var bytes = 0;
        foreach (var line in lines)
        {
            var separatorBytes = kept.Count > 0 ? 1 : 0;
            var lineBytes = Encoding.UTF8.GetByteCount(line) + separatorBytes;
            if (kept.Count >= AgentMemoryConstants.MaxPinnedMemoryLines ||
                bytes + lineBytes > AgentMemoryConstants.MaxPinnedMemoryBytes)
            {
                return kept.Count == 0
                    ? PinnedMemoryTruncationNotice
                    : string.Join("\n", kept) + "\n" + PinnedMemoryTruncationNotice;
            }

            kept.Add(line);
            bytes += lineBytes;
        }

        return string.Join("\n", kept);
    }

    /// <summary>
    /// Builds the progressive-disclosure <c>&lt;memory_catalog&gt;</c> of <c>.agents/memory/*.md</c> (Tier 1) and
    /// local topic files (Tier 2, excluding <c>MEMORY.md</c>). Returns an empty string when no topic file exists.
    /// </summary>
    public async Task<string> BuildMemoryCatalogAsync(string? repoPath, string? localMemoryDir, CancellationToken cancellationToken = default)
    {
        var entries = new List<string>();

        if (!string.IsNullOrWhiteSpace(repoPath) && Directory.Exists(repoPath))
        {
            var sharedDir = Path.Combine(repoPath, AgentMemoryConstants.SharedMemoryDirectory.Replace('/', Path.DirectorySeparatorChar));
            foreach (var file in AgentMemoryFileSystem.ListMarkdownFiles(repoPath, sharedDir).Take(AgentMemoryConstants.MaxCatalogEntries))
            {
                var path = $"{AgentMemoryConstants.SharedMemoryDirectory}/{Path.GetFileName(file)}";
                entries.Add(await FormatCatalogEntryAsync(CatalogSharedLabel, path, file, cancellationToken));
            }
        }

        if (TryGetLocalMemoryRoot(localMemoryDir, out var wksRoot))
        {
            var localFiles = AgentMemoryFileSystem.ListMarkdownFiles(wksRoot, localMemoryDir!)
                .Where(f => !string.Equals(Path.GetFileName(f), AgentMemoryConstants.PinnedMemoryFileName, StringComparison.OrdinalIgnoreCase))
                .Take(AgentMemoryConstants.MaxCatalogEntries);
            foreach (var file in localFiles)
            {
                var path = $"{AgentMemoryConstants.VirtualLocalMemoryPath}/{Path.GetFileName(file)}";
                entries.Add(await FormatCatalogEntryAsync(CatalogLocalLabel, path, file, cancellationToken));
            }
        }

        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<memory_catalog>");
        sb.AppendLine("Topic memory files available to read on demand via `read`:");
        foreach (var entry in entries)
        {
            sb.AppendLine(entry);
        }
        sb.Append("</memory_catalog>");
        return sb.ToString();
    }

    /// <summary>Extracts a one-line description: YAML frontmatter <c>description:</c>, else first heading, else first content line.</summary>
    public static string ExtractCatalogDescription(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        string? frontmatterDescription = null;
        if (lines.Length > 0 && lines[0].Trim() == FrontmatterDelimiter)
        {
            var closingIndex = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == FrontmatterDelimiter)
                {
                    closingIndex = i;
                    break;
                }
            }

            if (closingIndex > 0)
            {
                for (var i = 1; i < closingIndex; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (frontmatterDescription == null && trimmed.StartsWith(FrontmatterDescriptionKey, StringComparison.OrdinalIgnoreCase))
                    {
                        frontmatterDescription = trimmed[FrontmatterDescriptionKey.Length..].Trim().Trim('"', '\'');
                    }
                }

                index = closingIndex + 1;
            }
            else
            {
                // Leading '---' without a closing delimiter is a horizontal rule, not frontmatter.
                index = 1;
            }
        }

        string? description = string.IsNullOrWhiteSpace(frontmatterDescription) ? null : frontmatterDescription;
        if (description == null)
        {
            string? firstContent = null;
            for (var i = index; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed == FrontmatterDelimiter)
                {
                    continue;
                }

                if (IsMarkdownHeading(trimmed, out var headingText))
                {
                    description = headingText;
                    break;
                }

                firstContent ??= trimmed;
            }

            description ??= firstContent ?? string.Empty;
        }

        description = description.Replace('\n', ' ').Trim();
        return description.Length <= AgentMemoryConstants.MaxCatalogDescriptionLength
            ? description
            : description[..(AgentMemoryConstants.MaxCatalogDescriptionLength - Ellipsis.Length)].TrimEnd() + Ellipsis;
    }

    private static bool IsMarkdownHeading(string trimmedLine, out string headingText)
    {
        headingText = string.Empty;
        if (!trimmedLine.StartsWith('#'))
        {
            return false;
        }

        var hashes = 0;
        while (hashes < trimmedLine.Length && trimmedLine[hashes] == '#')
        {
            hashes++;
        }

        if (hashes == 0 || hashes > 6 || (hashes < trimmedLine.Length && !char.IsWhiteSpace(trimmedLine[hashes])))
        {
            return false;
        }

        headingText = trimmedLine[hashes..].Trim();
        return headingText.Length > 0;
    }

    private static async Task<string> FormatCatalogEntryAsync(string label, string displayPath, string file, CancellationToken cancellationToken)
    {
        var prefix = await AgentMemoryFileSystem.ReadPrefixSharedAsync(file, MaxCatalogPreviewChars, cancellationToken);
        var description = ExtractCatalogDescription(prefix);
        return string.IsNullOrEmpty(description)
            ? $"- [{label}] {displayPath}"
            : $"- [{label}] {displayPath} — {description}";
    }

    private static bool TryGetLocalMemoryRoot(string? localMemoryDir, out string wksRoot)
    {
        wksRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(localMemoryDir) || !Directory.Exists(localMemoryDir))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(localMemoryDir)));
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        wksRoot = parent;
        return true;
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
- Incremental Compilation Invariant: Whenever source code or test files are edited or added, execute Build Command (`{buildCmd}`) before running Test Command (`{testCmd}`). Never run test commands with `--no-build` after source modifications without building first.
- Automated Quality Gate Enforcement: Build and test runners will terminate and fail if execution exceeds these timeouts.
- CRITICAL RESOURCE PROTECTION: Never write or leave hanging tests, infinite loops, deadlocks, or unmocked blocking network calls. All tests and builds must run quickly and finish strictly within their timeout limits.
</execution_guidelines>";
    }
}
