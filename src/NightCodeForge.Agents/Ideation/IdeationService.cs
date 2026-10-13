using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Interactive Ideation and Discovery Agent conversing with developers, exploring codebase structure,
/// and proposing backlog issues with cross-dependencies.
/// </summary>
public partial class IdeationService : IIdeationService
{
    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex IssueJsonArrayRegex();

    private readonly IChatClient _chatClient;
    private readonly AgentsMdService _agentsMdService;
    private readonly ILogger<IdeationService> _logger;

    public IdeationService(
        IChatClient chatClient,
        AgentsMdService? agentsMdService = null,
        ILogger<IdeationService>? logger = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdeationService>.Instance;
    }

    public async Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var repoPath = !string.IsNullOrEmpty(workspace.RepositoryPath) ? workspace.RepositoryPath : Directory.GetCurrentDirectory();
        var agentsRules = await _agentsMdService.IngestRulesAsync(repoPath, cancellationToken);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules);

        var systemPrompt = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : $@"You are the NightCodeForge Ideation & Codebase Discovery Agent.
Your objective is to explore the codebase, discuss architectural features, and help the developer plan new issues.
{projectContext}

When you recommend one or more implementation tasks, format them as a structured JSON array at the end of your response:
```json
[
  {{
    ""title"": ""Descriptive Issue Title"",
    ""description"": ""Detailed technical specification and acceptance criteria"",
    ""priority"": 1,
    ""dependsOnTitles"": []
  }}
]
```";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        if (conversationHistory != null)
        {
            foreach (var (role, content) in conversationHistory)
            {
                var chatRole = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant;
                messages.Add(new ChatMessage(chatRole, content));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, userPrompt));

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var text = response.Text ?? string.Empty;

        // Parse any proposed issues from markdown JSON code blocks
        var proposals = new List<IdeationProposal>();
        var match = IssueJsonArrayRegex().Match(text);
        if (match.Success)
        {
            var rawJson = match.Value.Trim();
            if (rawJson.StartsWith("```"))
            {
                var firstLine = rawJson.IndexOf('\n');
                var lastTicks = rawJson.LastIndexOf("```");
                if (firstLine > 0 && lastTicks > firstLine)
                {
                    rawJson = rawJson.Substring(firstLine + 1, lastTicks - firstLine - 1).Trim();
                }
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<List<IdeationProposalDto>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null)
                {
                    proposals.AddRange(parsed.Select(p => new IdeationProposal(
                        p.Title ?? "Untitled Proposal",
                        p.Description ?? string.Empty,
                        p.Priority <= 0 ? 1 : p.Priority,
                        p.DependsOnTitles)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse proposed issues JSON from ideation response.");
            }
        }

        return new IdeationResult(text, proposals);
    }

    private class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; }
        public List<string>? DependsOnTitles { get; set; }
    }
}
