using System.Globalization;
using System.Text;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Transcripts;

public record TranscriptEntry(
    DateTime Timestamp,
    string Role,
    string Content,
    string? ToolName = null,
    string? ToolArguments = null,
    string? ToolResult = null,
    string? CallId = null);

public record TranscriptCompactionMetadata(
    string Strategy,
    int ContextWindowTokens,
    int ReserveTokens,
    int TriggerThresholdTokens,
    int KeepRecentTokens,
    string ResolutionSource);

public record TranscriptMetadata(
    int TaskId,
    string StageId,
    string AgentProfile,
    string ModelName,
    long DurationMs,
    AgentRunMetrics Metrics,
    LocalOptimizationFlags Optimizations,
    AgentRunStatus Status,
    double? KvCacheHitRate = null,
    string? SystemPrompt = null,
    IReadOnlyList<string>? AvailableTools = null,
    IReadOnlyList<string>? ActiveSkills = null,
    string? MafConfiguration = null,
    IReadOnlyList<ModelCallSnapshot>? ModelCalls = null,
    int AttemptIndex = 1,
    TranscriptCompactionMetadata? Compaction = null);

/// <summary>
/// Saves turn-by-turn markdown execution logs embedding active optimization flags in YAML frontmatter,
/// full-fidelity environment diagnostics (system prompt in 4-tick fence, available tools catalog),
/// and ordered conversation steps with safe truncation and icon tags.
/// </summary>
public class SessionTranscriptService
{
    public const int MaxEntryChars = 65536;

    private readonly string _transcriptsDirectory;

    public SessionTranscriptService()
        : this(Path.Combine(Directory.GetCurrentDirectory(), "docs", "transcripts"))
    {
    }

    public SessionTranscriptService(string transcriptsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptsDirectory);
        _transcriptsDirectory = transcriptsDirectory;
    }

    public async Task<string> SaveTranscriptAsync(
        TranscriptMetadata metadata,
        IReadOnlyList<TranscriptEntry> entries,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var targetDir = !string.IsNullOrWhiteSpace(outputDirectory)
            ? outputDirectory
            : _transcriptsDirectory;

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var statusStr = metadata.Status.ToString().ToLowerInvariant();
        var runSuffix = metadata.AttemptIndex > 1 ? $"-run{metadata.AttemptIndex}" : "";
        var fileName = $"{dateStr}-task-{metadata.TaskId}-{metadata.StageId}{runSuffix}-{statusStr}.md";
        var filePath = Path.Combine(targetDir, fileName);

        var sb = new StringBuilder();

        // YAML Frontmatter
        sb.AppendLine("---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"task_id: {metadata.TaskId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"stage_id: {metadata.StageId}");
        sb.AppendLine($"agent_profile: {metadata.AgentProfile}");
        sb.AppendLine($"model: {metadata.ModelName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"duration_ms: {metadata.DurationMs}");
        sb.AppendLine("tokens:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  prompt: {metadata.Metrics.PromptTokens}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  completion: {metadata.Metrics.CompletionTokens}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  total: {metadata.Metrics.TotalTokens}");
        sb.AppendLine("optimizations:");
        sb.AppendLine($"  guided_grammar_decoding: {metadata.Optimizations.GuidedGrammarDecoding.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  deterministic_symbol_map: {metadata.Optimizations.DeterministicSymbolMap.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  structured_diagnostic_extraction: {metadata.Optimizations.StructuredDiagnosticExtraction.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  prefix_stabilization: {metadata.Optimizations.PrefixStabilization.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  jit_model_preload: {metadata.Optimizations.JitModelPreload.ToString().ToLowerInvariant()}");
        if (metadata.KvCacheHitRate.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"kv_cache_hit_rate: {metadata.KvCacheHitRate.Value:F2}");
        }
        if (metadata.Compaction != null)
        {
            sb.AppendLine("compaction:");
            sb.AppendLine($"  strategy: {metadata.Compaction.Strategy}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  context_window_tokens: {metadata.Compaction.ContextWindowTokens}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  reserve_tokens: {metadata.Compaction.ReserveTokens}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  trigger_threshold_tokens: {metadata.Compaction.TriggerThresholdTokens}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  keep_recent_tokens: {metadata.Compaction.KeepRecentTokens}");
            sb.AppendLine($"  resolution_source: \"{metadata.Compaction.ResolutionSource}\"");
        }
        sb.AppendLine($"status: {metadata.Status}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# Execution Transcript: ForgeTask #{metadata.TaskId} ({metadata.StageId})");
        sb.AppendLine();

        // Environment section
        bool hasEnv = !string.IsNullOrWhiteSpace(metadata.SystemPrompt)
            || (metadata.AvailableTools != null && metadata.AvailableTools.Count > 0)
            || (metadata.ActiveSkills != null && metadata.ActiveSkills.Count > 0)
            || metadata.Compaction != null;
        if (hasEnv)
        {
            sb.AppendLine("### Environment");
            sb.AppendLine();
            if (metadata.Compaction != null)
            {
                sb.AppendLine("**Context Compaction Budget**");
                sb.AppendLine($"- Strategy: `{metadata.Compaction.Strategy}`");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Context Window: {metadata.Compaction.ContextWindowTokens:N0} tokens (Resolution Source: `{metadata.Compaction.ResolutionSource}`)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Trigger Threshold: {metadata.Compaction.TriggerThresholdTokens:N0} tokens | Reserve Headroom: {metadata.Compaction.ReserveTokens:N0} tokens");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Keep Recent Budget: {metadata.Compaction.KeepRecentTokens:N0} tokens");
                sb.AppendLine();
            }

            if (metadata.AvailableTools != null && metadata.AvailableTools.Count > 0)
            {
                sb.AppendLine("**Available tools**");
                foreach (var tool in metadata.AvailableTools)
                {
                    sb.AppendLine($"- {tool}");
                }
                sb.AppendLine();
            }

            if (metadata.ActiveSkills != null && metadata.ActiveSkills.Count > 0)
            {
                sb.AppendLine("**Active skills**");
                foreach (var skill in metadata.ActiveSkills)
                {
                    sb.AppendLine($"- {skill}");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(metadata.SystemPrompt))
            {
                sb.AppendLine("**System prompt**");
                sb.AppendLine("````");
                sb.AppendLine(TruncateContent(metadata.SystemPrompt));
                sb.AppendLine("````");
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.MafConfiguration))
        {
            sb.AppendLine("### MAF Pipeline Configuration");
            sb.AppendLine(metadata.MafConfiguration);
            sb.AppendLine();
        }

        // Ordered conversation steps (stamped with wall-clock offset from the first entry)
        var origin = entries.Count > 0 ? entries.Min(e => e.Timestamp) : DateTime.UtcNow;
        foreach (var entry in entries)
        {
            string roleIcon = entry.Role.ToUpperInvariant() switch
            {
                "USER" => "🧑 USER",
                "ASSISTANT" => "🤖 ASSISTANT",
                "THINKING" => "💭 THINKING",
                "TOOL" => "🔧 TOOL",
                "TOOL_RESULT" => "↳ TOOL_RESULT",
                "TOOL_ERROR" => "❌ TOOL_ERROR",
                "COMPACTION" => "✂️ COMPACTION",
                _ => entry.Role.ToUpperInvariant()
            };

            sb.AppendLine($"### {FormatOffset(entry.Timestamp - origin)} {roleIcon}");
            bool isToolOutputEntry = entry.Role.Equals("TOOL_RESULT", StringComparison.OrdinalIgnoreCase)
                || entry.Role.Equals("TOOL_ERROR", StringComparison.OrdinalIgnoreCase);
            if (isToolOutputEntry)
            {
                if (!string.IsNullOrEmpty(entry.ToolName))
                {
                    sb.AppendLine($"**Tool:** `{entry.ToolName}`");
                }
                if (!string.IsNullOrEmpty(entry.Content))
                {
                    sb.AppendLine("````");
                    sb.AppendLine(TruncateContent(entry.Content));
                    sb.AppendLine("````");
                }
            }
            else if (!string.IsNullOrEmpty(entry.Content))
            {
                sb.AppendLine(TruncateContent(entry.Content));
            }
            if (!string.IsNullOrEmpty(entry.ToolName))
            {
                sb.AppendLine($"**Tool Invocation:** `{entry.ToolName}`");
                if (!string.IsNullOrEmpty(entry.ToolArguments))
                {
                    sb.AppendLine("````json");
                    sb.AppendLine(TruncateContent(entry.ToolArguments));
                    sb.AppendLine("````");
                }
            }
            if (!string.IsNullOrEmpty(entry.ToolResult))
            {
                sb.AppendLine("**Tool Output:**");
                sb.AppendLine("````");
                sb.AppendLine(TruncateContent(entry.ToolResult));
                sb.AppendLine("````");
            }
            sb.AppendLine();
        }

        if (metadata.ModelCalls != null && metadata.ModelCalls.Count > 0)
        {
            AppendModelCalls(sb, metadata.ModelCalls, origin);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString().Replace("\r\n", "\n"), cancellationToken);
        return filePath;
    }

    /// <summary>
    /// Renders every request sent to the model (what the agent framework actually assembled): options, instructions,
    /// tool declarations and the messages added or rewritten since the previous call. Unchanged parts are summarized.
    /// </summary>
    private static void AppendModelCalls(StringBuilder sb, IReadOnlyList<ModelCallSnapshot> calls, DateTime origin)
    {
        sb.AppendLine("### Model calls (requests as sent to the model)");
        sb.AppendLine();
        sb.AppendLine("Each call lists only what changed since the previous request: messages added by the framework or the loop, rewritten history (e.g. compaction), and changed instructions or tools.");
        sb.AppendLine();

        ModelCallSnapshot? previous = null;
        foreach (var call in calls)
        {
            if (previous != null && call.Index > previous.Index + 1)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"_… calls {previous.Index + 1}–{call.Index - 1} omitted to bound the transcript size …_");
                sb.AppendLine();
            }

            var offset = call.Timestamp > origin ? call.Timestamp - origin : TimeSpan.Zero;
            sb.AppendLine(CultureInfo.InvariantCulture, $"#### {FormatOffset(offset)} Call {call.Index}");

            var newCount = call.Messages.Count;
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Messages:** {call.TotalMessages} total, {call.UnchangedPrefixCount} unchanged, {newCount} {(call.HistoryRewritten ? "rewritten/new" : "new")}");
            if (call.HistoryRewritten)
            {
                sb.AppendLine("⚠ **History rewritten since the previous call** (compaction or message mutation); the messages below replace the previous tail.");
            }
            sb.AppendLine();

            if (previous == null || !string.Equals(call.OptionsSummary, previous.OptionsSummary, StringComparison.Ordinal))
            {
                sb.AppendLine("**Options**");
                sb.AppendLine("````");
                sb.AppendLine(TruncateContent(call.OptionsSummary));
                sb.AppendLine("````");
                sb.AppendLine();
            }

            if (call.InstructionsChanged)
            {
                sb.AppendLine(call.Index == 1 || previous == null ? "**Instructions**" : "**Instructions (changed)**");
                if (string.IsNullOrEmpty(call.Instructions))
                {
                    sb.AppendLine("_(none)_");
                }
                else
                {
                    sb.AppendLine("````");
                    sb.AppendLine(TruncateContent(call.Instructions));
                    sb.AppendLine("````");
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("**Instructions:** unchanged");
                sb.AppendLine();
            }

            if (call.ToolsChanged)
            {
                sb.AppendLine(call.Index == 1 || previous == null
                    ? $"**Tools ({call.ToolCount})**"
                    : $"**Tools (changed, {call.ToolCount})**");
                foreach (var tool in call.Tools ?? Array.Empty<ToolSchemaSnapshot>())
                {
                    sb.AppendLine($"- `{tool.Name}` — {tool.Description}");
                    if (!string.IsNullOrEmpty(tool.JsonSchema))
                    {
                        sb.AppendLine("````json");
                        sb.AppendLine(TruncateContent(tool.JsonSchema));
                        sb.AppendLine("````");
                    }
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Tools:** unchanged ({call.ToolCount})");
                sb.AppendLine();
            }

            foreach (var message in call.Messages)
            {
                sb.AppendLine($"**{message.Role}**");
                sb.AppendLine("````");
                sb.AppendLine(TruncateContent(message.Text));
                sb.AppendLine("````");
                sb.AppendLine();
            }

            previous = call;
        }
    }

    /// <summary>
    /// Formats an elapsed offset as <c>[ mm:ss ]</c> (minutes may exceed 59 for long runs).
    /// </summary>
    public static string FormatOffset(TimeSpan offset)
    {
        if (offset < TimeSpan.Zero) offset = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"[ {(int)offset.TotalMinutes:D2}:{offset.Seconds:D2} ]");
    }

    public static string TruncateContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        if (content.Length <= MaxEntryChars) return content;
        return string.Create(CultureInfo.InvariantCulture, $"{content.Substring(0, MaxEntryChars)}\n... [Output truncated; {content.Length:N0} chars total; showing first {MaxEntryChars:N0} characters]");
    }
}
