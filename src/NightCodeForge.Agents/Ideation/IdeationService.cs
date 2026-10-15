using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Interactive Ideation and Discovery Agent conversing with developers, exploring codebase structure,
/// and proposing backlog tasks with cross-dependencies.
/// Relies on an AgentProfile (not scheduled as a background pipeline workload).
/// Provides live feedback via IAgentActivityStreamer and streaming updates.
/// </summary>
public partial class IdeationService : IIdeationService
{
    /// <summary>
    /// Default maximum turns for ideation agent exploration and tool loop.
    /// </summary>
    public const int DefaultMaxTurns = AgentProfile.DefaultMaxTurns;

    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex TaskJsonArrayRegex();

    [GeneratedRegex(@"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();

    private readonly IChatClient? _defaultChatClient;
    private readonly IOllamaChatClientFactory? _chatClientFactory;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IWebSearchService _webSearchService;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly SessionTranscriptService? _transcriptService;
    private readonly IGlobalOptimizationSettingsService? _globalOptimizationSettings;
    private readonly ILogger<IdeationService> _logger;

    public IdeationService(
        IWorkspacePathResolver pathResolver,
        IChatClient? defaultChatClient = null,
        IOllamaChatClientFactory? chatClientFactory = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IExecutionEnvironment? executionEnv = null,
        IWebSearchService? webSearchService = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        SessionTranscriptService? transcriptService = null,
        IGlobalOptimizationSettingsService? globalOptimizationSettings = null,
        ILogger<IdeationService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        if (defaultChatClient == null && chatClientFactory == null)
        {
            throw new ArgumentNullException(nameof(defaultChatClient), "Either defaultChatClient or chatClientFactory must be provided.");
        }

        _pathResolver = pathResolver;
        _defaultChatClient = defaultChatClient;
        _chatClientFactory = chatClientFactory;
        _envProvider = envProvider;
        _executionEnv = executionEnv;
        _webSearchService = webSearchService ?? new MockWebSearchService();
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _transcriptService = transcriptService;
        _globalOptimizationSettings = globalOptimizationSettings;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdeationService>.Instance;
    }

    public IdeationService(
        IChatClient defaultChatClient,
        IWorkspacePathResolver pathResolver)
        : this(pathResolver, defaultChatClient: defaultChatClient)
    {
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
        var optFlags = profile.OptimizationFlags ?? _globalOptimizationSettings?.GetFlags() ?? new LocalOptimizationFlags();

        // Resolve client dynamically for the agent profile
        var rawClient = _chatClientFactory != null
            ? _chatClientFactory.CreateClient(profile)
            : (_defaultChatClient ?? throw new InvalidOperationException("No IChatClient or IOllamaChatClientFactory configured."));
        var client = new ThinkingTraceCompactionChatClient(rawClient, optFlags);

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
        var projectConfig = CodeStackPresets.ResolveConfig(repoPath, null);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules, projectConfig, workspace);

        // Determine agent's effective working directory based on execution mode and workspace paths
        string agentWorkingDir;
        if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox)
        {
            bool hasClone = !string.IsNullOrEmpty(workspace.Ext4MirrorPath) ||
                            Directory.Exists(_pathResolver.ResolveCruciblePath(workspace.Id, string.Empty));
            agentWorkingDir = hasClone ? "/workspace/clone" : "/workspace";
        }
        else if (workspace.ExecutionMode == ExecutionMode.WslDirect)
        {
            agentWorkingDir = !string.IsNullOrEmpty(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : _pathResolver.GetAgentWorkingClonePath(workspace.Id);
        }
        else
        {
            agentWorkingDir = repoPath;
        }

        string? topLevelSummary = null;
        try
        {
            var hostDir = _pathResolver.ResolveCruciblePath(workspace.Id, string.Empty);
            if (string.IsNullOrEmpty(hostDir) || !Directory.Exists(hostDir))
            {
                hostDir = repoPath;
            }
            if (!string.IsNullOrEmpty(hostDir) && Directory.Exists(hostDir))
            {
                var entries = Directory.GetFileSystemEntries(hostDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n) &&
                                !n.StartsWith(".git") &&
                                !n.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                                !n.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                                !n.Equals(".vs", StringComparison.OrdinalIgnoreCase))
                    .Take(20)
                    .ToList();
                if (entries.Count > 0)
                {
                    topLevelSummary = string.Join(", ", entries);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not discover top-level entries for workspace {WorkspaceId}", workspace.Id);
        }

        var environmentSection = new StringBuilder();
        environmentSection.AppendLine("<environment>");
        environmentSection.AppendLine($"- Current Working Directory (PWD): {agentWorkingDir}");
        environmentSection.AppendLine($"- Repository Root: {agentWorkingDir}");
        environmentSection.AppendLine($"- Execution Mode: {workspace.ExecutionMode}");
        environmentSection.AppendLine($"- All relative paths, file operations (read, edit, write), and bash commands execute in this working directory ({agentWorkingDir}).");
        if (!string.IsNullOrEmpty(topLevelSummary))
        {
            environmentSection.AppendLine($"- Top-level files and directories in repository root: {topLevelSummary}");
        }
        environmentSection.AppendLine($"- When exploring the codebase, begin by inspecting the directory structure or key files in '{agentWorkingDir}'.");
        environmentSection.AppendLine("</environment>");

        var basePersona = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : (!string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt
                : "You are the NightCodeForge Ideation & Codebase Discovery Agent.\nYour objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.");

        var permittedTools = profile.PermittedTools?.Count > 0
            ? profile.PermittedTools
            : DefaultAgentTools.GetTools(profile.Mode);

        var toolsSection = new StringBuilder();
        toolsSection.AppendLine("<tools>");
        foreach (var tool in permittedTools)
        {
            var desc = tool.ToLowerInvariant() switch
            {
                "read" => "- read: Read file contents with line slicing (path, offset, limit)",
                "bash" => "- bash: Execute shell commands, builds, and file discovery (command, timeout, working_directory)",
                "web_search" or "search" => "- web_search: Search technical web documentation for APIs, errors, or libraries (query, max_results)",
                "propose_tasks" => "- propose_tasks: Propose structured backlog items (tasks: [{ title, description, priority, dependsOnTitles }])",
                _ => $"- {tool}: Execute tool '{tool}'"
            };
            toolsSection.AppendLine(desc);
        }
        toolsSection.AppendLine("</tools>");

        var taskGuidance = @"When you recommend one or more implementation tasks, do NOT output markdown JSON codeblocks in your response. Instead, invoke the 'propose_tasks' tool with your proposed tasks (each having title, description, and optional priority). If multiple tasks are proposed, they will be scheduled sequentially with dependencies established automatically. After proposing tasks, explain your proposal and architectural rationale clearly to the developer.";

        var systemPrompt = $@"{basePersona}
{environmentSection.ToString().TrimEnd()}
{projectContext}
{toolsSection.ToString().TrimEnd()}

Before executing tools or recommending changes, think step-by-step about what files to inspect or how to answer inside <think>...</think> tags.

{taskGuidance}";

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

        var sessionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var transcriptEntries = new List<TranscriptEntry>
        {
            new(DateTime.UtcNow, "USER", userPrompt)
        };

        var effectiveEnv = (workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(workspace, profile.SecurityTier)
            : _executionEnv;

        var buildTimeout = workspace?.BuildTimeoutSeconds ?? projectConfig.BuildTimeoutSeconds;
        var testTimeout = workspace?.TestTimeoutSeconds ?? projectConfig.TestTimeoutSeconds;

        var canonicalTools = new CanonicalTools(
            repoPath,
            _pathResolver,
            effectiveEnv,
            webSearchService: _webSearchService,
            buildTimeoutSeconds: buildTimeout,
            testTimeoutSeconds: testTimeout,
            workspaceId: workspace?.Id);

        var sessionProposals = new List<IdeationProposal>();

        var dispatcher = new LenientToolDispatcher(canonicalTools);
        dispatcher.RegisterTool(new ToolDefinition(
            CanonicalToolNames.ProposeTasks,
            "Propose structured backlog items (tasks: [{ title, description, priority, dependsOnTitles }])",
            (args, _) =>
            {
                var argsStr = JsonSerializer.Serialize(args);
                var (resultText, _) = ExecuteProposeTasks(argsStr, sessionProposals, canonicalTools);
                return Task.FromResult(resultText);
            },
            aliases: ["propose_task", "proposetasks"]));

        var aiTools = BuildAIFunctionList(permittedTools);

        var chatOptions = new ChatOptions
        {
            ModelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : null,
            Temperature = (float?)profile.Temperature ?? 0.3f,
            Tools = aiTools
        };

        var toolResultCompactor = new ToolResultCompactor(optFlags);
        var fullTextBuilder = new StringBuilder();
        int maxTurns = profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0 ? profile.MaxTurns.Value : DefaultMaxTurns;
        int currentTurn = 0;

        var allToolCalls = new List<IdeationToolCallInfo>();

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;
            toolResultCompactor.Compact(messages);
            var tagFilter = new StreamingTagFilter();
            bool streamingFailed = false;
            var functionCalls = new List<FunctionCallContent>();
            var turnTextBuilder = new StringBuilder();

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

                        if (update.Contents != null)
                        {
                            foreach (var c in update.Contents)
                            {
                                if (c is FunctionCallContent fc) functionCalls.Add(fc);
                            }
                        }

                        var text = update.Text;
                        if (string.IsNullOrEmpty(text)) continue;

                        turnTextBuilder.Append(text);

                        foreach (var (isThinkingDelta, piece) in tagFilter.ProcessDelta(text))
                        {
                            if (string.IsNullOrEmpty(piece)) continue;

                            transcriptEntries.Add(new TranscriptEntry(
                                DateTime.UtcNow,
                                isThinkingDelta ? "THINKING" : "ASSISTANT",
                                piece));

                            var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                            await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                            var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                            yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                        }
                    }

                    foreach (var (isThinkingDelta, piece) in tagFilter.Flush())
                    {
                        if (string.IsNullOrEmpty(piece)) continue;

                        transcriptEntries.Add(new TranscriptEntry(
                            DateTime.UtcNow,
                            isThinkingDelta ? "THINKING" : "ASSISTANT",
                            piece));

                        var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                        await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                        var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                        yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
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

                if (response.Messages != null)
                {
                    foreach (var msg in response.Messages)
                    {
                        if (msg.Contents != null)
                        {
                            foreach (var c in msg.Contents)
                            {
                                if (c is FunctionCallContent fc) functionCalls.Add(fc);
                            }
                        }
                    }
                }

                var text = response.Text ?? string.Empty;
                turnTextBuilder.Clear();
                turnTextBuilder.Append(text);

                foreach (var (isThinkingDelta, piece) in tagFilter.ProcessDelta(text))
                {
                    if (string.IsNullOrEmpty(piece)) continue;

                    transcriptEntries.Add(new TranscriptEntry(
                        DateTime.UtcNow,
                        isThinkingDelta ? "THINKING" : "ASSISTANT",
                        piece));

                    var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                    await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                    var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                    yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                }

                foreach (var (isThinkingDelta, piece) in tagFilter.Flush())
                {
                    if (string.IsNullOrEmpty(piece)) continue;

                    transcriptEntries.Add(new TranscriptEntry(
                        DateTime.UtcNow,
                        isThinkingDelta ? "THINKING" : "ASSISTANT",
                        piece));

                    var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                    await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                    var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                    yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                }
            }

            var turnText = turnTextBuilder.ToString();
            fullTextBuilder.Append(turnText);

            var assistantMsg = new ChatMessage(ChatRole.Assistant, turnText);
            foreach (var fc in functionCalls)
            {
                assistantMsg.Contents.Add(fc);
            }
            messages.Add(assistantMsg);

            // Also check for raw text fallback tool call emitted by models without native tool tokens
            if (functionCalls.Count == 0 && !string.IsNullOrEmpty(turnText))
            {
                var fallbackCall = TryExtractTextToolCall(turnText);
                if (fallbackCall != null)
                {
                    functionCalls.Add(fallbackCall);
                }
            }

            if (functionCalls.Count == 0)
            {
                break;
            }

            // Process tool calls
            foreach (var call in functionCalls)
            {
                var canonicalName = dispatcher.ResolveToolName(call.Name);
                var argsJson = call.Arguments != null ? JsonSerializer.Serialize(call.Arguments) : "{}";
                var callId = call.CallId ?? Guid.NewGuid().ToString("N");

                var toolCallInfo = new IdeationToolCallInfo(callId, canonicalName, argsJson);
                allToolCalls.Add(toolCallInfo);

                // Live stream tool call started to Blazor UI
                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: toolCallInfo);

                await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken, metadata);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string toolResult;
                bool isError = false;
                List<IdeationProposal>? newlyDiscoveredProposals = null;
                try
                {
                    if (string.Equals(canonicalName, CanonicalToolNames.ProposeTasks, StringComparison.OrdinalIgnoreCase))
                    {
                        var (execResult, extractedProposals) = ExecuteProposeTasks(argsJson, sessionProposals, canonicalTools);
                        toolResult = execResult;
                        newlyDiscoveredProposals = extractedProposals;
                    }
                    else
                    {
                        toolResult = await dispatcher.DispatchAsync(canonicalName, argsJson, cancellationToken);
                        if (toolResult.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                            toolResult.StartsWith("[READ-ONLY VIOLATION]", StringComparison.OrdinalIgnoreCase) ||
                            toolResult.StartsWith("[TIMEOUT]", StringComparison.OrdinalIgnoreCase) ||
                            toolResult.StartsWith("File not found", StringComparison.OrdinalIgnoreCase) ||
                            (toolResult.StartsWith("Exit Code: ", StringComparison.OrdinalIgnoreCase) &&
                             !toolResult.StartsWith("Exit Code: 0\n", StringComparison.OrdinalIgnoreCase) &&
                             !toolResult.StartsWith("Exit Code: 0\r\n", StringComparison.OrdinalIgnoreCase)))
                        {
                            isError = true;
                            _logger.LogWarning("Ideation tool '{ToolName}' returned error or non-zero exit code. Args: {ToolArgs}. Result: {ToolResult}",
                                canonicalName, argsJson, toolResult);
                        }
                    }
                }
                catch (Exception ex)
                {
                    isError = true;
                    toolResult = $"Error executing tool '{canonicalName}': {ex.Message}";
                    _logger.LogError(ex, "Ideation tool '{ToolName}' failed with exception. Args: {ToolArgs}",
                        canonicalName, argsJson);
                }
                sw.Stop();

                var completedToolInfo = toolCallInfo with
                {
                    Result = toolResult,
                    IsError = isError,
                    DurationMs = sw.ElapsedMilliseconds
                };

                var idx = allToolCalls.FindIndex(t => t.ToolCallId == callId);
                if (idx >= 0) allToolCalls[idx] = completedToolInfo;

                // Live stream tool call completed to Blazor UI
                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: completedToolInfo);

                // Live stream newly discovered proposals immediately to Blazor UI
                if (newlyDiscoveredProposals != null && newlyDiscoveredProposals.Count > 0)
                {
                    foreach (var proposal in newlyDiscoveredProposals)
                    {
                        yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: proposal);
                        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.ToolCompleted, $"Proposed task: {proposal.Title}", cancellationToken, metadata);
                    }
                }

                transcriptEntries.Add(new TranscriptEntry(
                    DateTime.UtcNow,
                    "TOOL",
                    $"Tool {canonicalName} ({sw.ElapsedMilliseconds}ms)",
                    ToolName: canonicalName,
                    ToolArguments: argsJson,
                    ToolResult: toolResult));

                messages.Add(new ChatMessage(ChatRole.Tool, toolResult)
                {
                    Contents = { new FunctionResultContent(callId, toolResult) }
                });
                await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.ToolCompleted, toolResult, cancellationToken, metadata);
            }

            // If we reached max turns and the agent called tools on the final turn,
            // execute one final synthesis turn with Tools = null so the agent answers the user rather than cutting off abruptly.
            if (currentTurn >= maxTurns && !cancellationToken.IsCancellationRequested)
            {
                var synthesisFilter = new StreamingTagFilter();
                var synthesisTurnBuilder = new StringBuilder();
                var synthesisOptions = new ChatOptions
                {
                    ModelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : null,
                    Temperature = (float?)profile.Temperature ?? 0.3f,
                    Tools = null
                };

                IAsyncEnumerator<ChatResponseUpdate>? synthesisEnumerator = null;
                try
                {
                    var stream = client.GetStreamingResponseAsync(messages, synthesisOptions, cancellationToken);
                    synthesisEnumerator = stream.GetAsyncEnumerator(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Synthesis turn failed to initialize streaming.");
                }

                if (synthesisEnumerator != null)
                {
                    try
                    {
                        while (await synthesisEnumerator.MoveNextAsync())
                        {
                            var update = synthesisEnumerator.Current;
                            var text = update.Text;
                            if (string.IsNullOrEmpty(text)) continue;

                            synthesisTurnBuilder.Append(text);

                            foreach (var (isThinkingDelta, piece) in synthesisFilter.ProcessDelta(text))
                            {
                                if (string.IsNullOrEmpty(piece)) continue;

                                transcriptEntries.Add(new TranscriptEntry(
                                    DateTime.UtcNow,
                                    isThinkingDelta ? "THINKING" : "ASSISTANT",
                                    piece));

                                var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                                await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                                var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                                yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                            }
                        }

                        foreach (var (isThinkingDelta, piece) in synthesisFilter.Flush())
                        {
                            if (string.IsNullOrEmpty(piece)) continue;

                            transcriptEntries.Add(new TranscriptEntry(
                                DateTime.UtcNow,
                                isThinkingDelta ? "THINKING" : "ASSISTANT",
                                piece));

                            var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                            yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                        }
                    }
                    finally
                    {
                        await synthesisEnumerator.DisposeAsync();
                    }
                }

                var synthesisText = synthesisTurnBuilder.ToString();
                fullTextBuilder.Append(synthesisText);
                messages.Add(new ChatMessage(ChatRole.Assistant, synthesisText));
                break;
            }
        }

        sessionStopwatch.Stop();
        string? transcriptPath = null;
        string? fullTranscriptMarkdown = null;

        if (_transcriptService != null)
        {
            try
            {
                var targetDir = Path.Combine(repoPath, "docs", "transcripts");
                var metadataRecord = new TranscriptMetadata(
                    TaskId: workspace?.Id ?? 0,
                    StageId: "ideation",
                    AgentProfile: profile.Id,
                    ModelName: profile.ModelName ?? "default",
                    DurationMs: sessionStopwatch.ElapsedMilliseconds,
                    Metrics: new AgentRunMetrics(sessionStopwatch.ElapsedMilliseconds, 0, 0, 0),
                    Optimizations: profile.OptimizationFlags ?? new LocalOptimizationFlags(),
                    Status: AgentRunStatus.Completed);

                transcriptPath = await _transcriptService.SaveTranscriptAsync(metadataRecord, transcriptEntries, targetDir, cancellationToken);
                if (File.Exists(transcriptPath))
                {
                    fullTranscriptMarkdown = await File.ReadAllTextAsync(transcriptPath, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save ideation session transcript.");
            }
        }

        var fullText = fullTextBuilder.ToString();
        var fallbackProposals = ParseProposals(fullText);

        foreach (var p in fallbackProposals)
        {
            if (!sessionProposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
            {
                sessionProposals.Add(p);
                yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: p);
            }
        }

        var finalResult = new IdeationResult(fullText, sessionProposals, allToolCalls, transcriptPath, fullTranscriptMarkdown);
        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.StageCompleted, $"Ideation completed with {sessionProposals.Count} proposal(s).", cancellationToken, metadata);

        yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.Completed, FinalResult: finalResult);
    }

    private static List<(bool IsThinking, string Text)> ParseStreamDeltas(string text, ref bool inThinking)
    {
        var filter = new StreamingTagFilter();
        var results = new List<(bool IsThinking, string Text)>();
        results.AddRange(filter.ProcessDelta(text));
        results.AddRange(filter.Flush());
        inThinking = filter.IsInThinking;
        return results;
    }

    private sealed class StreamingTagFilter
    {
        private readonly StringBuilder _buffer = new();
        private bool _inThinking;

        public bool IsInThinking => _inThinking;

        public IEnumerable<(bool IsThinking, string Text)> ProcessDelta(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) yield break;

            _buffer.Append(chunk);

            while (_buffer.Length > 0)
            {
                if (!_inThinking)
                {
                    var current = _buffer.ToString();
                    var thinkIdx = IndexOfAnyTag(current, ["<think>", "<thought>"], out var matchedTag);

                    if (thinkIdx >= 0)
                    {
                        if (thinkIdx > 0)
                        {
                            yield return (false, current[..thinkIdx]);
                        }
                        _inThinking = true;
                        _buffer.Remove(0, thinkIdx + matchedTag!.Length);
                    }
                    else
                    {
                        var prefixLen = GetPotentialPrefixLength(current, ["<think>", "<thought>"]);
                        if (prefixLen > 0)
                        {
                            var emitLen = current.Length - prefixLen;
                            if (emitLen > 0)
                            {
                                yield return (false, current[..emitLen]);
                                _buffer.Remove(0, emitLen);
                            }
                            break;
                        }
                        else
                        {
                            yield return (false, current);
                            _buffer.Clear();
                            break;
                        }
                    }
                }
                else
                {
                    var current = _buffer.ToString();
                    var endIdx = IndexOfAnyTag(current, ["</think>", "</thought>"], out var matchedTag);

                    if (endIdx >= 0)
                    {
                        if (endIdx > 0)
                        {
                            yield return (true, current[..endIdx]);
                        }
                        _inThinking = false;
                        _buffer.Remove(0, endIdx + matchedTag!.Length);
                    }
                    else
                    {
                        var prefixLen = GetPotentialPrefixLength(current, ["</think>", "</thought>"]);
                        if (prefixLen > 0)
                        {
                            var emitLen = current.Length - prefixLen;
                            if (emitLen > 0)
                            {
                                yield return (true, current[..emitLen]);
                                _buffer.Remove(0, emitLen);
                            }
                            break;
                        }
                        else
                        {
                            yield return (true, current);
                            _buffer.Clear();
                            break;
                        }
                    }
                }
            }
        }

        public IEnumerable<(bool IsThinking, string Text)> Flush()
        {
            if (_buffer.Length > 0)
            {
                yield return (_inThinking, _buffer.ToString());
                _buffer.Clear();
            }
        }

        private static int IndexOfAnyTag(string text, string[] tags, out string? matchedTag)
        {
            matchedTag = null;
            int lowestIdx = -1;
            foreach (var tag in tags)
            {
                var idx = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (lowestIdx < 0 || idx < lowestIdx))
                {
                    lowestIdx = idx;
                    matchedTag = tag;
                }
            }
            return lowestIdx;
        }

        private static int GetPotentialPrefixLength(string text, string[] tags)
        {
            int maxLen = 0;
            foreach (var tag in tags)
            {
                for (int len = 1; len < tag.Length && len <= text.Length; len++)
                {
                    var sub = text[^len..];
                    if (tag.StartsWith(sub, StringComparison.OrdinalIgnoreCase))
                    {
                        if (len > maxLen) maxLen = len;
                    }
                }
            }
            return maxLen;
        }
    }

    private List<IdeationProposal> ParseProposals(string text)
    {
        var proposals = new List<IdeationProposal>();
        var match = TaskJsonArrayRegex().Match(text);
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
            if (parsed != null && parsed.Count > 0)
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    var p = parsed[i];
                    var deps = new List<string>();
                    if (p.DependsOnTitles != null && p.DependsOnTitles.Count > 0)
                    {
                        deps.AddRange(p.DependsOnTitles.Where(d => !string.IsNullOrWhiteSpace(d)));
                    }

                    // Enforce sequential implicit dependencies if multiple tasks are parsed:
                    // Task i (for i > 0) depends on Task i - 1.
                    if (parsed.Count > 1 && i > 0)
                    {
                        var prevTitle = parsed[i - 1].Title?.Trim();
                        if (!string.IsNullOrWhiteSpace(prevTitle) &&
                            !deps.Any(d => string.Equals(d, prevTitle, StringComparison.OrdinalIgnoreCase)))
                        {
                            deps.Add(prevTitle);
                        }
                    }

                    proposals.Add(new IdeationProposal(
                        p.Title ?? "Untitled Proposal",
                        p.Description ?? string.Empty,
                        p.Priority <= 0 ? 1 : p.Priority,
                        deps));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse proposed tasks JSON from ideation response.");
        }

        return proposals;
    }

    private static (string ResultText, List<IdeationProposal> Proposals) ExecuteProposeTasks(
        string argsJson,
        List<IdeationProposal> sessionProposals,
        CanonicalTools canonicalTools)
    {
        var rawTasks = ExtractRawProposals(argsJson);
        if (rawTasks.Count == 0)
        {
            return ("No valid task proposals could be extracted from the provided arguments. Ensure tasks have at least a title and description.", new List<IdeationProposal>());
        }

        var newProposals = new List<IdeationProposal>();
        for (int i = 0; i < rawTasks.Count; i++)
        {
            var raw = rawTasks[i];
            var deps = new List<string>();
            if (raw.DependsOnTitles != null && raw.DependsOnTitles.Count > 0)
            {
                deps.AddRange(raw.DependsOnTitles.Where(d => !string.IsNullOrWhiteSpace(d)));
            }

            // If multiple tasks are proposed, establish sequential implicit dependencies:
            // Task i (for i > 0) depends on Task i - 1.
            if (rawTasks.Count > 1 && i > 0)
            {
                var prevTitle = rawTasks[i - 1].Title.Trim();
                if (!string.IsNullOrWhiteSpace(prevTitle) &&
                    !deps.Any(d => string.Equals(d, prevTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    deps.Add(prevTitle);
                }
            }

            var proposal = new IdeationProposal(
                raw.Title.Trim(),
                raw.Description.Trim(),
                raw.Priority <= 0 ? 1 : Math.Clamp(raw.Priority, 1, 5),
                deps);

            var existingIndex = sessionProposals.FindIndex(p => string.Equals(p.Title, proposal.Title, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                sessionProposals[existingIndex] = proposal;
            }
            else
            {
                sessionProposals.Add(proposal);
            }

            newProposals.Add(proposal);

            canonicalTools.PendingProposals.Add(new TaskProposal(
                proposal.Title,
                proposal.Description,
                "feature",
                proposal.DependsOnTitles));
        }

        var titles = string.Join(", ", newProposals.Select(p => $"'{p.Title}'"));
        var resultText = $"Recorded {newProposals.Count} task proposal(s) with sequential dependencies in the ideation workspace for user review: {titles}. The developer can now review, refine, and add them to the backlog in the UI.";
        return (resultText, newProposals);
    }

    private record RawTaskProposal(
        string Title,
        string Description,
        int Priority,
        IReadOnlyList<string>? DependsOnTitles);

    private static List<RawTaskProposal> ExtractRawProposals(string argsJson)
    {
        var results = new List<RawTaskProposal>();
        if (string.IsNullOrWhiteSpace(argsJson)) return results;

        var text = argsJson.Trim();
        if (text.StartsWith("```"))
        {
            var firstLine = text.IndexOf('\n');
            var lastTicks = text.LastIndexOf("```");
            if (firstLine > 0 && lastTicks > firstLine)
            {
                text = text.Substring(firstLine + 1, lastTicks - firstLine - 1).Trim();
            }
        }

        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonArray rootArray)
            {
                foreach (var item in rootArray)
                {
                    var p = ParseSingleRawTask(item);
                    if (p != null) results.Add(p);
                }
            }
            else if (node is JsonObject rootObj)
            {
                if (rootObj.TryGetPropertyValue("tasks", out var tasksNode) && tasksNode != null)
                {
                    if (tasksNode is JsonArray taskArr)
                    {
                        foreach (var item in taskArr)
                        {
                            var p = ParseSingleRawTask(item);
                            if (p != null) results.Add(p);
                        }
                    }
                    else if (tasksNode is JsonValue val && val.TryGetValue<string>(out var strVal))
                    {
                        if (strVal.TrimStart().StartsWith("["))
                        {
                            var innerNode = JsonNode.Parse(strVal);
                            if (innerNode is JsonArray innerArr)
                            {
                                foreach (var item in innerArr)
                                {
                                    var p = ParseSingleRawTask(item);
                                    if (p != null) results.Add(p);
                                }
                            }
                        }
                    }
                    else if (tasksNode is JsonObject singleInTasks)
                    {
                        var p = ParseSingleRawTask(singleInTasks);
                        if (p != null) results.Add(p);
                    }
                }
                else
                {
                    var p = ParseSingleRawTask(rootObj);
                    if (p != null) results.Add(p);
                }
            }
        }
        catch
        {
            // If JsonNode parsing fails, return empty list
        }

        return results;
    }

    private static RawTaskProposal? ParseSingleRawTask(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        string title = string.Empty;
        if (obj.TryGetPropertyValue("title", out var t) ||
            obj.TryGetPropertyValue("name", out t) ||
            obj.TryGetPropertyValue("task_title", out t) ||
            obj.TryGetPropertyValue("taskTitle", out t))
        {
            title = t?.ToString() ?? string.Empty;
        }

        string desc = string.Empty;
        if (obj.TryGetPropertyValue("description", out var d) ||
            obj.TryGetPropertyValue("desc", out d) ||
            obj.TryGetPropertyValue("scope", out d) ||
            obj.TryGetPropertyValue("details", out d) ||
            obj.TryGetPropertyValue("summary", out d))
        {
            desc = d?.ToString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc))
        {
            return null;
        }

        int priority = 1;
        if (obj.TryGetPropertyValue("priority", out var pr) ||
            obj.TryGetPropertyValue("prio", out pr))
        {
            if (pr is JsonValue jv && jv.TryGetValue<int>(out var intVal))
            {
                priority = intVal;
            }
            else if (int.TryParse(pr?.ToString(), out var parsedInt))
            {
                priority = parsedInt;
            }
        }

        var deps = new List<string>();
        if (obj.TryGetPropertyValue("dependsOnTitles", out var depNode) ||
            obj.TryGetPropertyValue("depends_on_titles", out depNode) ||
            obj.TryGetPropertyValue("dependsOn", out depNode) ||
            obj.TryGetPropertyValue("dependencies", out depNode) ||
            obj.TryGetPropertyValue("deps", out depNode))
        {
            if (depNode is JsonArray depArr)
            {
                foreach (var item in depArr)
                {
                    var s = item?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) deps.Add(s);
                }
            }
            else if (depNode is JsonValue depVal && depVal.TryGetValue<string>(out var depStr))
            {
                if (!string.IsNullOrWhiteSpace(depStr))
                {
                    deps.AddRange(depStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }

        return new RawTaskProposal(title, desc, priority, deps);
    }

    private async Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
    }

    private static IList<AITool> BuildAIFunctionList(IReadOnlyList<string> permittedTools)
    {
        var tools = new List<AITool>();
        foreach (var tool in permittedTools)
        {
            if (string.Equals(tool, CanonicalToolNames.ProposeTasks, StringComparison.OrdinalIgnoreCase))
            {
                tools.Add(AIFunctionFactory.Create(
                    ([System.ComponentModel.Description("List of task proposals")] IReadOnlyList<IdeationProposalDto> tasks) => "ok",
                    CanonicalToolNames.ProposeTasks,
                    "Propose structured backlog items for the workspace"));
            }
            else
            {
                tools.Add(AIFunctionFactory.Create(() => "ok", tool));
            }
        }
        return tools;
    }

    private static FunctionCallContent? TryExtractTextToolCall(string text)
    {
        var match = ToolCallTagRegex().Match(text);
        if (!match.Success) return null;

        try
        {
            var node = JsonNode.Parse(match.Groups[1].Value);
            var name = node?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) return null;

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (node?["arguments"] is JsonObject args)
            {
                foreach (var (k, v) in args)
                {
                    dict[k] = ParseJsonNodeValue(v);
                }
            }
            else if (node?["arguments"] is JsonArray arrArgs)
            {
                dict["tasks"] = arrArgs.Select(ParseJsonNodeValue).ToList();
            }

            return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, dict);
        }
        catch
        {
            return null;
        }
    }

    private static object? ParseJsonNodeValue(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<string>(out var s)) return s;
            return val.ToString();
        }
        if (node is JsonArray arr)
        {
            return arr.Select(ParseJsonNodeValue).ToList();
        }
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in obj)
            {
                dict[k] = ParseJsonNodeValue(v);
            }
            return dict;
        }
        return node.ToString();
    }

    public class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; } = 1;
        public List<string>? DependsOnTitles { get; set; }
    }
}


