using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Data;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Default implementation of ITranscriptBrowserService providing transcript discovery,
/// YAML frontmatter extraction, interactive turn parsing, hybrid AI diagnostics,
/// and one-click remediation dispatch.
/// </summary>
public partial class TranscriptBrowserService : ITranscriptBrowserService
{
    private readonly IGlobalOptimizationSettingsService _globalOptService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOllamaChatClientFactory _chatClientFactory;
    private readonly ILogger<TranscriptBrowserService> _logger;

    [GeneratedRegex(@"^---\r?\n(?<frontmatter>[\s\S]*?)\r?\n---\r?\n?", RegexOptions.Multiline)]
    private static partial Regex FrontmatterRegex();

    [GeneratedRegex(@"^###\s+\[\s*(?<offset>[0-9:]+)\s*\]\s+(?:(?<icon>[^\s\w]+)\s+)?(?<role>[A-Za-z0-9_]+)", RegexOptions.Multiline)]
    private static partial Regex TurnHeaderRegex();

    [GeneratedRegex(@"(?:\d{4}-\d{2}-\d{2}-)?task-(?<taskId>\d+)-(?<stage>[A-Za-z0-9_\-]+)-(?<status>[A-Za-z0-9_\-]+)\.md", RegexOptions.IgnoreCase)]
    private static partial Regex FilenameRegex();

    [GeneratedRegex(@"(?s)<think>(?<thought>.*?)</think>")]
    private static partial Regex ThinkTagRegex();

    [GeneratedRegex(@"\*\*Tool Invocation:\*\*\s*`(?<name>[^`]+)`")]
    private static partial Regex ToolInvocationRegex();

    [GeneratedRegex(@"\*\*Tool:\*\*\s*`(?<name>[^`]+)`")]
    private static partial Regex ToolResultHeaderRegex();

    public TranscriptBrowserService(
        IGlobalOptimizationSettingsService globalOptService,
        IServiceScopeFactory scopeFactory,
        IOllamaChatClientFactory chatClientFactory,
        ILogger<TranscriptBrowserService> logger)
    {
        _globalOptService = globalOptService ?? throw new ArgumentNullException(nameof(globalOptService));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TranscriptSummary>> GetTranscriptsAsync(string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var directoriesToSearch = GetCandidateDirectories(workspacePath);
        var summaries = new List<TranscriptSummary>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directoriesToSearch)
        {
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (seenFiles.Contains(fileName)) continue;
                seenFiles.Add(fileName);

                try
                {
                    var summary = await ParseSummaryFromFileAsync(file, cancellationToken);
                    if (summary != null)
                    {
                        summaries.Add(summary);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to parse transcript summary for {FilePath}", file);
                }
            }
        }

        return summaries
            .OrderByDescending(s => s.Timestamp)
            .ThenByDescending(s => s.TaskId)
            .ToList();
    }

    public async Task<ParsedTranscriptDetail?> GetTranscriptDetailAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;

        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var summary = ParseSummaryFromContent(filePath, content);
        if (summary == null) return null;

        return ParseDetail(summary, content);
    }

    public async Task<ParsedTranscriptDetail?> GetTranscriptDetailByNameAsync(string fileName, string? workspacePath = null, CancellationToken cancellationToken = default)
    {
        var directories = GetCandidateDirectories(workspacePath);
        foreach (var dir in directories)
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                return await GetTranscriptDetailAsync(path, cancellationToken);
            }
        }

        return null;
    }

    public async Task<DiagnosticReport> RunDiagnosticsAsync(ParsedTranscriptDetail detail, string mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);

        // 1. Attempt live LLM diagnosis if a client is available
        try
        {
            var target = string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase)
                ? OllamaRoutingTarget.Cloud
                : OllamaRoutingTarget.Auto; // Dynamic routing across on-premise targets (Host GPU or LAN Server)

            var client = _chatClientFactory.CreateClient(null, target);
            if (client != null)
            {
                var diagService = new TranscriptDiagnosticService(client);
                var diagnosis = await diagService.DiagnoseTranscriptAsync(detail.RawMarkdown, cancellationToken: cancellationToken);

                if (!string.IsNullOrWhiteSpace(diagnosis.DiagnosisSummary) && diagnosis.DiagnosisSummary != "Transcript is empty.")
                {
                    return ConvertDiagnosisToReport(diagnosis, detail);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Live LLM diagnostic run failed or timed out. Falling back to heuristic analysis.");
        }

        // 2. Intelligent Heuristic Diagnostic Engine fallback
        return GenerateHeuristicReport(detail, mode);
    }

    public async Task ApplyRemediationAsync(RemediationSuggestion suggestion, int? workspaceId = null, string? profileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        if (string.Equals(suggestion.ActionType, "ToggleOptimizationFlag", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(suggestion.TargetKey))
        {
            var targetKey = suggestion.TargetKey.Trim();
            var targetValue = suggestion.TargetValue is bool b ? b : bool.TryParse(suggestion.TargetValue?.ToString(), out var parsedB) && parsedB;

            // 1. Update Global Optimization Settings Service
            var currentFlags = _globalOptService.GetFlags();
            var updatedFlags = CloneAndSetFlag(currentFlags, targetKey, targetValue);
            await _globalOptService.UpdateFlagsAsync(updatedFlags, cancellationToken);

            // 2. Update Database Agent Profiles
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var profilesQuery = db.AgentProfiles.AsQueryable();
            if (workspaceId.HasValue)
            {
                profilesQuery = profilesQuery.Where(p => p.WorkspaceId == workspaceId.Value);
            }
            else
            {
                profilesQuery = profilesQuery.Where(p => p.WorkspaceId == null);
            }

            if (!string.IsNullOrWhiteSpace(profileName))
            {
                profilesQuery = profilesQuery.Where(p => p.Name == profileName);
            }

            var profiles = await profilesQuery.ToListAsync(cancellationToken);
            foreach (var profile in profiles)
            {
                profile.OptimizationFlags = CloneAndSetFlag(profile.OptimizationFlags, targetKey, targetValue);
            }

            if (profiles.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static LocalOptimizationFlags CloneAndSetFlag(LocalOptimizationFlags? flags, string key, bool value)
    {
        var copy = flags?.Clone() ?? new LocalOptimizationFlags();

        switch (key.ToLowerInvariant())
        {
            case "guidedgrammardecoding":
            case "guided_grammar_decoding":
                copy.GuidedGrammarDecoding = value;
                break;
            case "deterministicsymbolmap":
            case "deterministic_symbol_map":
                copy.DeterministicSymbolMap = value;
                break;
            case "structureddiagnosticextraction":
            case "structured_diagnostic_extraction":
                copy.StructuredDiagnosticExtraction = value;
                break;
            case "jitmodelpreload":
            case "jit_model_preload":
                copy.JitModelPreload = value;
                break;
        }

        return copy;
    }

    private DiagnosticReport ConvertDiagnosisToReport(TranscriptDiagnosis diagnosis, ParsedTranscriptDetail detail)
    {
        var critiquePoints = new List<string>();
        var actions = new List<RemediationSuggestion>();

        var summaryText = diagnosis.DiagnosisSummary.Trim();
        var lines = summaryText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var bulletPoints = lines
            .Where(l => {
                var trimmed = l.TrimStart();
                return trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || Regex.IsMatch(trimmed, @"^\d+\.\s+");
            })
            .Select(l => l.Trim().TrimStart('-', '*', ' '))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (bulletPoints.Count > 0)
        {
            critiquePoints.AddRange(bulletPoints);
        }
        else
        {
            foreach (var rec in diagnosis.RecommendedActions)
            {
                critiquePoints.Add(rec);
            }
        }

        // Map category to concrete remediation suggestion
        if (diagnosis.RootCauseCategory.Contains("Compilat", StringComparison.OrdinalIgnoreCase) ||
            diagnosis.RootCauseCategory.Contains("Syntax", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(new RemediationSuggestion(
                Title: "Enable Deterministic Symbol Map",
                Description: "Provides Roslyn symbol index lookup to prevent hallucinated type definitions.",
                ActionType: "ToggleOptimizationFlag",
                TargetKey: "DeterministicSymbolMap",
                TargetValue: true));
        }
        else
        {
            actions.Add(new RemediationSuggestion(
                Title: "Enable JIT Model Preload",
                Description: "Pre-loads model weights into GPU VRAM before stage initiation.",
                ActionType: "ToggleOptimizationFlag",
                TargetKey: "JitModelPreload",
                TargetValue: true));
        }

        return new DiagnosticReport(
            RootCause: diagnosis.DiagnosisSummary,
            FailureCategory: diagnosis.RootCauseCategory,
            CritiquePoints: critiquePoints,
            SuggestedActions: actions,
            FullMarkdownReport: diagnosis.DiagnosisSummary);
    }

    private static DiagnosticReport GenerateHeuristicReport(ParsedTranscriptDetail detail, string mode)
    {
        var raw = detail.RawMarkdown;
        var turns = detail.Turns;

        // 1. Detect Compiler / Syntax Failures
        if (raw.Contains("error CS", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Compilation error", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("The type or namespace name", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticReport(
                RootCause: "Compiler error encountered during stage execution. Code generation introduced undefined symbols or invalid member accesses.",
                FailureCategory: "Compilation Failure / Type Drift",
                CritiquePoints: new[]
                {
                    "Deterministic symbol map was not leveraged to provide accurate type signatures to the model.",
                    "Compiler diagnostics lacked structured span isolation, causing the model to attempt ungrounded edits."
                },
                SuggestedActions: new[]
                {
                    new RemediationSuggestion(
                        Title: "Enable Deterministic Symbol Map",
                        Description: "Injects Roslyn symbol definitions into context to prevent hallucinated member names.",
                        ActionType: "ToggleOptimizationFlag",
                        TargetKey: "DeterministicSymbolMap",
                        TargetValue: true),
                    new RemediationSuggestion(
                        Title: "Enable Structured Diagnostic Extraction",
                        Description: "Parses compiler diagnostics into structured error spans for precise fixes.",
                        ActionType: "ToggleOptimizationFlag",
                        TargetKey: "StructuredDiagnosticExtraction",
                        TargetValue: true)
                });
        }

        // 2. Default / Success / Waiting
        if (string.Equals(detail.Metadata.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            return new DiagnosticReport(
                RootCause: "Session executed without anomalies and satisfied all pipeline verification gates.",
                FailureCategory: "Clean Execution",
                CritiquePoints: new[]
                {
                    "Prompt instructions and tool selection were well-aligned.",
                    "All compiler and unit test verifications succeeded on the first pass."
                },
                SuggestedActions: new[]
                {
                    new RemediationSuggestion(
                        Title: "Enable JIT Model Preload",
                        Description: "Pre-loads model weights into GPU VRAM before stage initiation.",
                        ActionType: "ToggleOptimizationFlag",
                        TargetKey: "JitModelPreload",
                        TargetValue: true)
                });
        }

        return new DiagnosticReport(
            RootCause: $"Stage {detail.Metadata.StageId} halted with status '{detail.Metadata.Status}'. Agent required manual intervention or tool call steering.",
            FailureCategory: "Guardrail / Intervention",
            CritiquePoints: new[]
            {
                "Task description or stage acceptance criteria may have lacked explicit boundary conditions.",
                "Review session transcript turns to verify tool parameter precision."
            },
            SuggestedActions: new[]
            {
                new RemediationSuggestion(
                    Title: "Enable Guided Grammar Decoding",
                    Description: "Enforces structured JSON output schema to prevent malformed responses.",
                    ActionType: "ToggleOptimizationFlag",
                    TargetKey: "GuidedGrammarDecoding",
                    TargetValue: true)
            });
    }

    private IReadOnlyList<string> GetCandidateDirectories(string? workspacePath)
    {
        var dirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            dirs.Add(Path.Combine(workspacePath, "docs", "transcripts"));
            dirs.Add(Path.Combine(workspacePath, ".nightcodeforge", "transcripts"));

            // Check any active or existing worktrees under .nightcodeforge/worktrees
            var worktreesRoot = Path.Combine(workspacePath, ".nightcodeforge", "worktrees");
            if (Directory.Exists(worktreesRoot))
            {
                try
                {
                    foreach (var wt in Directory.GetDirectories(worktreesRoot))
                    {
                        var wtTranscripts = Path.Combine(wt, "docs", "transcripts");
                        if (Directory.Exists(wtTranscripts))
                        {
                            dirs.Add(wtTranscripts);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to scan worktrees for transcripts under {WorktreesRoot}", worktreesRoot);
                }
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        dirs.Add(Path.Combine(cwd, "docs", "transcripts"));

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        dirs.Add(Path.Combine(baseDir, "docs", "transcripts"));

        // If running in src/NightCodeForge.Web, also check solution root docs/transcripts
        try
        {
            var parent = Directory.GetParent(cwd)?.Parent?.FullName;
            if (parent != null)
            {
                dirs.Add(Path.Combine(parent, "docs", "transcripts"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve solution root docs/transcripts from CWD '{Cwd}'", cwd);
        }

        return dirs.Distinct().ToList();
    }

    private async Task<TranscriptSummary?> ParseSummaryFromFileAsync(string filePath, CancellationToken ct)
    {
        // Read file header or full file
        var content = await File.ReadAllTextAsync(filePath, ct);
        return ParseSummaryFromContent(filePath, content);
    }

    public static TranscriptSummary? ParseSummaryFromContent(string filePath, string content)
    {
        var fileName = Path.GetFileName(filePath);
        var fileInfo = new FileInfo(filePath);
        var timestamp = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.UtcNow;

        int taskId = 0;
        string stageId = "general";
        string agentProfile = "DefaultAgent";
        string modelName = "unknown";
        int durationMs = 0;
        int promptTokens = 0;
        int completionTokens = 0;
        string status = "Unknown";
        double? kvCacheHitRate = null;
        var optimizations = new LocalOptimizationFlags();
        bool hasValidTranscriptData = false;

        var fmMatch = FrontmatterRegex().Match(content);
        if (fmMatch.Success)
        {
            var yaml = fmMatch.Groups["frontmatter"].Value;
            var lines = yaml.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            string? currentSection = null;
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                bool isIndented = rawLine.StartsWith(' ') || rawLine.StartsWith('\t');
                if (!isIndented && !line.EndsWith(':'))
                {
                    currentSection = null;
                }

                if (line.EndsWith(':'))
                {
                    currentSection = line.TrimEnd(':').Trim().ToLowerInvariant();
                    continue;
                }

                var colonIdx = line.IndexOf(':');
                if (colonIdx > 0)
                {
                    var key = line.Substring(0, colonIdx).Trim().ToLowerInvariant();
                    var val = line.Substring(colonIdx + 1).Trim();

                    if (currentSection == "tokens")
                    {
                        if (key == "prompt" && int.TryParse(val, out var pt)) promptTokens = pt;
                        else if (key == "completion" && int.TryParse(val, out var ct)) completionTokens = ct;
                        else if (key == "total" && int.TryParse(val, out var tt) && promptTokens == 0) promptTokens = tt;
                    }
                    else if (currentSection == "optimizations")
                    {
                        var boolVal = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                        switch (key)
                        {
                            case "guided_grammar_decoding": optimizations.GuidedGrammarDecoding = boolVal; break;
                            case "deterministic_symbol_map": optimizations.DeterministicSymbolMap = boolVal; break;
                            case "structured_diagnostic_extraction": optimizations.StructuredDiagnosticExtraction = boolVal; break;
                            case "jit_model_preload": optimizations.JitModelPreload = boolVal; break;
                        }
                    }
                    else
                    {
                        switch (key)
                        {
                            case "task_id":
                                if (int.TryParse(val, out var tid)) { taskId = tid; hasValidTranscriptData = true; }
                                break;
                            case "stage_id":
                                stageId = val;
                                hasValidTranscriptData = true;
                                break;
                            case "agent_profile":
                                agentProfile = val;
                                hasValidTranscriptData = true;
                                break;
                            case "model":
                                modelName = val;
                                break;
                            case "duration_ms":
                                if (int.TryParse(val, out var dms)) durationMs = dms;
                                break;
                            case "status":
                                status = val;
                                hasValidTranscriptData = true;
                                break;
                            case "kv_cache_hit_rate":
                                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var kv)) kvCacheHitRate = kv;
                                break;
                        }
                    }
                }
            }
        }
        else
        {
            // Fallback: parse filename
            var fnMatch = FilenameRegex().Match(fileName);
            if (fnMatch.Success)
            {
                if (int.TryParse(fnMatch.Groups["taskId"].Value, out var tid)) taskId = tid;
                stageId = fnMatch.Groups["stage"].Value;
                status = fnMatch.Groups["status"].Value;
                hasValidTranscriptData = true;
            }
        }

        if (!hasValidTranscriptData)
        {
            return null;
        }

        // Try extracting timestamp from filename (e.g. 2026-10-15-task...)
        if (fileName.Length >= 10 && DateTime.TryParseExact(fileName.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fnDate))
        {
            timestamp = fnDate;
        }

        return new TranscriptSummary(
            FileName: fileName,
            FilePath: filePath,
            TaskId: taskId,
            StageId: stageId,
            ModelName: modelName,
            Timestamp: timestamp,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            DurationMs: durationMs,
            Status: status,
            AgentProfile: agentProfile,
            KvCacheHitRate: kvCacheHitRate,
            Optimizations: optimizations);
    }

    public static ParsedTranscriptDetail ParseDetail(TranscriptSummary summary, string content)
    {
        var body = content;
        var fmMatch = FrontmatterRegex().Match(content);
        if (fmMatch.Success)
        {
            body = content.Substring(fmMatch.Length);
        }

        string? systemPrompt = null;
        var availableTools = new List<string>();
        var activeSkills = new List<string>();
        string? modelCallsMarkdown = null;

        // Check for Model Calls section
        var modelCallsIdx = body.IndexOf("### Model calls", StringComparison.OrdinalIgnoreCase);
        if (modelCallsIdx >= 0)
        {
            modelCallsMarkdown = body.Substring(modelCallsIdx).Trim();
            body = body.Substring(0, modelCallsIdx);
        }

        // Check for Environment section
        var envIdx = body.IndexOf("### Environment", StringComparison.OrdinalIgnoreCase);
        if (envIdx >= 0)
        {
            var nextSectionIdx = body.IndexOf("### [", envIdx, StringComparison.OrdinalIgnoreCase);
            var envText = nextSectionIdx >= 0
                ? body.Substring(envIdx, nextSectionIdx - envIdx)
                : body.Substring(envIdx);

            // Extract tools
            var toolsIdx = envText.IndexOf("**Available tools**", StringComparison.OrdinalIgnoreCase);
            if (toolsIdx >= 0)
            {
                var toolsText = envText.Substring(toolsIdx + 19);
                var endTools = toolsText.IndexOf("**", StringComparison.OrdinalIgnoreCase);
                if (endTools >= 0) toolsText = toolsText.Substring(0, endTools);

                foreach (var line in toolsText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var t = line.Trim().TrimStart('-', '*', ' ');
                    if (!string.IsNullOrWhiteSpace(t)) availableTools.Add(t);
                }
            }

            // Extract skills
            var skillsIdx = envText.IndexOf("**Active skills**", StringComparison.OrdinalIgnoreCase);
            if (skillsIdx >= 0)
            {
                var skillsText = envText.Substring(skillsIdx + 17);
                var endSkills = skillsText.IndexOf("**", StringComparison.OrdinalIgnoreCase);
                if (endSkills >= 0) skillsText = skillsText.Substring(0, endSkills);

                foreach (var line in skillsText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var s = line.Trim().TrimStart('-', '*', ' ');
                    if (!string.IsNullOrWhiteSpace(s)) activeSkills.Add(s);
                }
            }

            // Extract system prompt
            var promptIdx = envText.IndexOf("**System prompt**", StringComparison.OrdinalIgnoreCase);
            if (promptIdx >= 0)
            {
                var promptSnippet = envText.Substring(promptIdx + 17);
                var fenceStart = promptSnippet.IndexOf("````", StringComparison.OrdinalIgnoreCase);
                if (fenceStart >= 0)
                {
                    var afterStart = promptSnippet.Substring(fenceStart + 4);
                    var fenceEnd = afterStart.IndexOf("````", StringComparison.OrdinalIgnoreCase);
                    if (fenceEnd >= 0)
                    {
                        systemPrompt = afterStart.Substring(0, fenceEnd).Trim();
                    }
                }
            }

            if (nextSectionIdx >= 0)
            {
                body = body.Substring(nextSectionIdx);
            }
        }

        // Parse turns
        var turns = new List<ParsedTranscriptTurn>();
        var matches = TurnHeaderRegex().Matches(body);

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var offset = match.Groups["offset"].Value;
            var role = match.Groups["role"].Value;

            var startIndex = match.Index + match.Length;
            var endIndex = (i + 1 < matches.Count) ? matches[i + 1].Index : body.Length;
            var turnContent = body.Substring(startIndex, endIndex - startIndex).Trim();

            string? thinking = null;
            string? toolName = null;
            string? toolArgs = null;
            string? toolResult = null;
            bool isTool = false;
            bool isError = role.Equals("TOOL_ERROR", StringComparison.OrdinalIgnoreCase);

            if (role.Equals("THINKING", StringComparison.OrdinalIgnoreCase))
            {
                thinking = turnContent;
            }
            else
            {
                var thinkMatch = ThinkTagRegex().Match(turnContent);
                if (thinkMatch.Success)
                {
                    thinking = thinkMatch.Groups["thought"].Value.Trim();
                    turnContent = turnContent.Replace(thinkMatch.Value, "").Trim();
                }
            }

            // Check tool invocation
            var toolInvocMatch = ToolInvocationRegex().Match(turnContent);
            if (toolInvocMatch.Success)
            {
                isTool = true;
                toolName = toolInvocMatch.Groups["name"].Value;

                var jsonFence = turnContent.IndexOf("````json", StringComparison.OrdinalIgnoreCase);
                if (jsonFence >= 0)
                {
                    var afterFence = turnContent.Substring(jsonFence + 8);
                    var endFence = afterFence.IndexOf("````", StringComparison.OrdinalIgnoreCase);
                    if (endFence >= 0)
                    {
                        toolArgs = afterFence.Substring(0, endFence).Trim();
                    }
                }

                var outIdx = turnContent.IndexOf("**Tool Output:**", StringComparison.OrdinalIgnoreCase);
                if (outIdx >= 0)
                {
                    var afterOut = turnContent.Substring(outIdx + 16);
                    var fenceOut = afterOut.IndexOf("````", StringComparison.OrdinalIgnoreCase);
                    if (fenceOut >= 0)
                    {
                        var fenceOutEnd = afterOut.IndexOf("````", fenceOut + 4, StringComparison.OrdinalIgnoreCase);
                        if (fenceOutEnd >= 0)
                        {
                            toolResult = afterOut.Substring(fenceOut + 4, fenceOutEnd - (fenceOut + 4)).Trim();
                        }
                    }
                }
            }

            var toolResMatch = ToolResultHeaderRegex().Match(turnContent);
            if (toolResMatch.Success)
            {
                isTool = true;
                toolName = toolResMatch.Groups["name"].Value;
                var fenceStart = turnContent.IndexOf("````", StringComparison.OrdinalIgnoreCase);
                if (fenceStart >= 0)
                {
                    var fenceEnd = turnContent.IndexOf("````", fenceStart + 4, StringComparison.OrdinalIgnoreCase);
                    if (fenceEnd >= 0)
                    {
                        toolResult = turnContent.Substring(fenceStart + 4, fenceEnd - (fenceStart + 4)).Trim();
                    }
                }
            }

            if (role.StartsWith("TOOL", StringComparison.OrdinalIgnoreCase))
            {
                isTool = true;
            }

            turns.Add(new ParsedTranscriptTurn(
                Offset: offset,
                Role: role,
                Content: turnContent,
                ThinkingTrace: thinking,
                ToolName: toolName,
                ToolArguments: toolArgs,
                ToolResult: toolResult,
                IsTool: isTool,
                IsError: isError));
        }

        return new ParsedTranscriptDetail(
            Metadata: summary,
            RawMarkdown: content,
            SystemPrompt: systemPrompt,
            AvailableTools: availableTools,
            ActiveSkills: activeSkills,
            Turns: turns,
            ModelCallsMarkdown: modelCallsMarkdown);
    }
}
