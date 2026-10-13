using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Interactive Ideation and Discovery Agent conversing with developers, exploring codebase structure,
/// and proposing backlog issues with cross-dependencies.
/// Relies on an AgentProfile (not scheduled as a background pipeline workload).
/// Provides live feedback via IAgentActivityStreamer and streaming updates.
/// </summary>
public partial class IdeationService : IIdeationService
{
    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex IssueJsonArrayRegex();

    private readonly IChatClient? _defaultChatClient;
    private readonly IOllamaChatClientFactory? _chatClientFactory;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly ILogger<IdeationService> _logger;

    public IdeationService(
        IChatClient? defaultChatClient = null,
        IOllamaChatClientFactory? chatClientFactory = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        ILogger<IdeationService>? logger = null)
    {
        if (defaultChatClient == null && chatClientFactory == null)
        {
            throw new ArgumentNullException(nameof(defaultChatClient), "Either defaultChatClient or chatClientFactory must be provided.");
        }

        _defaultChatClient = defaultChatClient;
        _chatClientFactory = chatClientFactory;
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdeationService>.Instance;
    }

    /// <summary>
    /// Resolves the effective AgentProfile for ideation, prioritizing explicit profile,
    /// then workspace profile named/id 'ideation', or falling back to CreateDefaultIdeationProfile.
    /// </summary>
    public static AgentProfile ResolveIdeationProfile(Workspace? workspace, AgentProfile? explicitProfile = null)
    {
        if (explicitProfile != null) return explicitProfile;

        var profileFromWorkspace = workspace?.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, "ideation", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("ideation", StringComparison.OrdinalIgnoreCase));

        return profileFromWorkspace ?? AgentProfile.CreateDefaultIdeationProfile(workspace?.Id);
    }

    public Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        IdeationResult? finalResult = null;
        await foreach (var update in ConverseStreamingAsync(
            workspace,
            userPrompt,
            conversationHistory,
            agentProfile,
            customSystemPrompt,
            cancellationToken).ConfigureAwait(false))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed && update.FinalResult != null)
            {
                finalResult = update.FinalResult;
            }
        }

        return finalResult ?? new IdeationResult(string.Empty, Array.Empty<IdeationProposal>());
    }

    public IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseStreamingAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var profile = ResolveIdeationProfile(workspace, agentProfile);

        // Resolve client dynamically for the agent profile
        var client = _chatClientFactory != null
            ? _chatClientFactory.CreateClient(profile)
            : (_defaultChatClient ?? throw new InvalidOperationException("No IChatClient or IOllamaChatClientFactory configured."));

        var sessionId = $"ideation-{workspace.Id}-{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace.Id,
            ["agentProfile"] = profile.Id,
            ["model"] = profile.ModelName
        };

        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.StageStarted, "Starting ideation discovery", cancellationToken, metadata);

        var repoPath = !string.IsNullOrEmpty(workspace.RepositoryPath) ? workspace.RepositoryPath : Directory.GetCurrentDirectory();
        var agentsRules = await _agentsMdService.IngestRulesAsync(repoPath, cancellationToken);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules);

        var basePersona = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : (!string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt
                : "You are the NightCodeForge Ideation & Codebase Discovery Agent.\nYour objective is to explore the codebase, discuss architectural features, and help the developer plan new issues.");

        var systemPrompt = $@"{basePersona}
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

        var chatOptions = new ChatOptions
        {
            ModelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : null,
            Temperature = (float?)profile.Temperature ?? 0.3f
        };

        var fullTextBuilder = new StringBuilder();
        var inThinking = false;
        bool streamingFailed = false;

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            var stream = client.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);
            enumerator = stream.GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            streamingFailed = true;
        }

        if (!streamingFailed && enumerator != null)
        {
            try
            {
                while (true)
                {
                    ChatResponseUpdate? update = null;
                    bool hasMore = false;
                    try
                    {
                        hasMore = await enumerator.MoveNextAsync();
                        if (hasMore)
                        {
                            update = enumerator.Current;
                        }
                    }
                    catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                    {
                        streamingFailed = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                        throw;
                    }

                    if (!hasMore || update == null) break;

                    var text = update.Text;
                    if (string.IsNullOrEmpty(text)) continue;

                    fullTextBuilder.Append(text);

                    foreach (var (isThinkingDelta, piece) in ParseStreamDeltas(text, ref inThinking))
                    {
                        if (string.IsNullOrEmpty(piece)) continue;

                        var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                        await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                        var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                        yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        if (streamingFailed)
        {
            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(messages, chatOptions, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                throw;
            }

            var text = response.Text ?? string.Empty;
            fullTextBuilder.Clear();
            fullTextBuilder.Append(text);

            inThinking = false;
            foreach (var (isThinkingDelta, piece) in ParseStreamDeltas(text, ref inThinking))
            {
                if (string.IsNullOrEmpty(piece)) continue;

                var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
            }
        }

        var fullText = fullTextBuilder.ToString();
        var proposals = ParseProposals(fullText);

        foreach (var proposal in proposals)
        {
            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: proposal);
        }

        var finalResult = new IdeationResult(fullText, proposals);
        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.StageCompleted, $"Ideation completed with {proposals.Count} proposal(s).", cancellationToken, metadata);

        yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.Completed, FinalResult: finalResult);
    }

    private static List<(bool IsThinking, string Text)> ParseStreamDeltas(string text, ref bool inThinking)
    {
        var results = new List<(bool IsThinking, string Text)>();
        var remaining = text;
        while (!string.IsNullOrEmpty(remaining))
        {
            if (!inThinking)
            {
                var thinkIndex = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (thinkIndex < 0)
                {
                    results.Add((false, remaining));
                    break;
                }

                if (thinkIndex > 0)
                {
                    results.Add((false, remaining[..thinkIndex]));
                }

                inThinking = true;
                remaining = remaining[(thinkIndex + 7)..];
            }
            else
            {
                var endThinkIndex = remaining.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (endThinkIndex < 0)
                {
                    results.Add((true, remaining));
                    break;
                }

                if (endThinkIndex > 0)
                {
                    results.Add((true, remaining[..endThinkIndex]));
                }

                inThinking = false;
                remaining = remaining[(endThinkIndex + 8)..];
            }
        }

        return results;
    }

    private List<IdeationProposal> ParseProposals(string text)
    {
        var proposals = new List<IdeationProposal>();
        var match = IssueJsonArrayRegex().Match(text);
        if (!match.Success) return proposals;

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

        return proposals;
    }

    private async Task BroadcastAsync(
        string sessionId,
        int issueId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, issueId, stageId, kind, content, DateTime.UtcNow, metadata);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
    }

    private class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; }
        public List<string>? DependsOnTitles { get; set; }
    }
}


