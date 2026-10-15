using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Agents.Skills;

public record SkillDescriptor(
    string Name,
    string Description,
    string DirectoryPath,
    string SkillFilePath);

/// <summary>
/// Service integrating into Microsoft Agent Framework's modular skills system (Microsoft.Agents.AI.Skills).
/// Implements progressive disclosure: discovers skills via AgentFileSkillsSource and loads their content on demand. The lean skills catalog prompt and the single
/// load_skill tool replace MAF's AgentSkillsProvider (auxiliary resources and scripts go through read and bash).
/// </summary>
public partial class SkillsService
{
    private sealed class DummyChatClient : IChatClient
    {
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            AsyncEnumerableEmpty();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        private static async IAsyncEnumerable<ChatResponseUpdate> AsyncEnumerableEmpty()
        {
            await Task.Yield();
            yield break;
        }
    }

    /// <summary>
    /// Resolves existing skill directories according to the hierarchical resolution order:
    /// 1. Workspace: .nightcodeforge/skills, .agents/skills, skills
    /// 2. Global: ~/.nightcodeforge/skills
    /// </summary>
    public static IReadOnlyList<string> ResolveSkillPaths(string workspaceRoot, string? globalSkillsDirectory = null)
    {
        var paths = new List<string>();

        // 1. Workspace skill directories (override / take precedence)
        var workspaceCandidateDirs = new[]
        {
            Path.Combine(workspaceRoot, ".nightcodeforge", "skills"),
            Path.Combine(workspaceRoot, ".agents", "skills"),
            Path.Combine(workspaceRoot, "skills")
        };

        foreach (var dir in workspaceCandidateDirs)
        {
            if (Directory.Exists(dir))
            {
                paths.Add(dir);
            }
        }

        // 2. Global skills directory
        var globalDir = !string.IsNullOrWhiteSpace(globalSkillsDirectory)
            ? globalSkillsDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightcodeforge", "skills");

        if (Directory.Exists(globalDir))
        {
            paths.Add(globalDir);
        }

        return paths;
    }

    /// <summary>
    /// Discovers skills in the workspace using Microsoft Agent Framework's AgentFileSkillsSource.
    /// </summary>
    public async Task<IReadOnlyList<SkillDescriptor>> DiscoverSkillsAsync(
        string workspaceRoot,
        string? globalSkillsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolveSkillPaths(workspaceRoot, globalSkillsDirectory);
        if (paths.Count == 0)
        {
            return Array.Empty<SkillDescriptor>();
        }

        using var source = new AgentFileSkillsSource(paths);
        var dummyAgent = new ChatClientAgent(new DummyChatClient(), new ChatClientAgentOptions { Name = "NightCodeForgeSkills" });
        var context = new AgentSkillsSourceContext(dummyAgent, null);

        var agentSkills = await source.GetSkillsAsync(context, cancellationToken);
        var descriptors = new List<SkillDescriptor>();

        foreach (var s in agentSkills)
        {
            descriptors.Add(new SkillDescriptor(
                s.Frontmatter.Name,
                s.Frontmatter.Description ?? string.Empty,
                string.Empty,
                string.Empty));
        }

        return descriptors;
    }

    /// <summary>
    /// Loads the full content of a skill using Microsoft Agent Framework's AgentFileSkillsSource.
    /// </summary>
    public async Task<string> LoadSkillAsync(
        string workspaceRoot,
        string skillName,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolveSkillPaths(workspaceRoot);
        if (paths.Count == 0)
        {
            return $"Error: Skill '{skillName}' not found (no skills directory detected).";
        }

        using var source = new AgentFileSkillsSource(paths);
        var dummyAgent = new ChatClientAgent(new DummyChatClient(), new ChatClientAgentOptions { Name = "NightCodeForgeSkills" });
        var context = new AgentSkillsSourceContext(dummyAgent, null);

        var agentSkills = await source.GetSkillsAsync(context, cancellationToken);
        var skill = agentSkills.FirstOrDefault(s => string.Equals(s.Frontmatter.Name, skillName, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
        {
            return $"Error: Skill '{skillName}' not found. Available skills: {string.Join(", ", agentSkills.Select(s => s.Frontmatter.Name))}";
        }

        return await skill.GetContentAsync(cancellationToken);
    }

    /// <summary>
    /// Formats skills catalog prompt following the minimalist Pi approach to minimize token overhead (~25 tokens/skill).
    /// Matches PromptPrefixStabilizer's 4-layer prefix convention.
    /// </summary>
    public string FormatSkillsCatalogPrompt(IReadOnlyList<SkillDescriptor> skills)
    {
        if (skills == null || skills.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<skills>");
        sb.AppendLine("The following modular skills are available to assist with your tasks. Use the 'load_skill' tool to view full instructions when relevant:");
        foreach (var s in skills)
        {
            sb.AppendLine($"- {s.Name}: {s.Description}");
        }
        sb.AppendLine("</skills>");
        return sb.ToString().TrimEnd();
    }
}
