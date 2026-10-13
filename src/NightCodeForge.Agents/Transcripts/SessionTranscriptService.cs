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
    string? ToolResult = null);

public record TranscriptMetadata(
    int IssueId,
    string StageId,
    string AgentProfile,
    string ModelName,
    long DurationMs,
    AgentRunMetrics Metrics,
    LocalOptimizationFlags Optimizations,
    AgentRunStatus Status,
    double? KvCacheHitRate = null);

/// <summary>
/// Saves turn-by-turn markdown execution logs embedding active optimization flags in YAML frontmatter.
/// </summary>
public class SessionTranscriptService
{
    private readonly string _transcriptsDirectory;

    public SessionTranscriptService(string? transcriptsDirectory = null)
    {
        _transcriptsDirectory = transcriptsDirectory ?? Path.Combine(Directory.GetCurrentDirectory(), "docs", "transcripts");
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
        var fileName = $"{dateStr}-issue-{metadata.IssueId}-{metadata.StageId}-{statusStr}.md";
        var filePath = Path.Combine(targetDir, fileName);

        var sb = new StringBuilder();

        // YAML Frontmatter
        sb.AppendLine("---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"issue_id: {metadata.IssueId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"stage_id: {metadata.StageId}");
        sb.AppendLine($"agent_profile: {metadata.AgentProfile}");
        sb.AppendLine($"model: {metadata.ModelName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"duration_ms: {metadata.DurationMs}");
        sb.AppendLine("tokens:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  prompt: {metadata.Metrics.PromptTokens}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  completion: {metadata.Metrics.CompletionTokens}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  total: {metadata.Metrics.TotalTokens}");
        sb.AppendLine("optimizations:");
        sb.AppendLine($"  thinking_trace_compaction: {metadata.Optimizations.ThinkingTraceCompaction.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  loop_circuit_breaker: {metadata.Optimizations.LoopCircuitBreaker.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  guided_grammar_decoding: {metadata.Optimizations.GuidedGrammarDecoding.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  deterministic_symbol_map: {metadata.Optimizations.DeterministicSymbolMap.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  structured_diagnostic_extraction: {metadata.Optimizations.StructuredDiagnosticExtraction.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  prefix_stabilization: {metadata.Optimizations.PrefixStabilization.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  jit_model_preload: {metadata.Optimizations.JitModelPreload.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  compact_older_tool_results: {metadata.Optimizations.CompactOlderToolResults.ToString().ToLowerInvariant()}");
        if (metadata.KvCacheHitRate.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"kv_cache_hit_rate: {metadata.KvCacheHitRate.Value:F2}");
        }
        sb.AppendLine($"status: {metadata.Status}");
        sb.AppendLine("---");
        sb.AppendLine();

        // Turn-by-turn log
        sb.AppendLine($"# Execution Transcript: Issue #{metadata.IssueId} ({metadata.StageId})");
        sb.AppendLine();

        foreach (var entry in entries)
        {
            sb.AppendLine($"### [{entry.Timestamp:HH:mm:ss}] {entry.Role.ToUpperInvariant()}");
            if (!string.IsNullOrEmpty(entry.Content))
            {
                sb.AppendLine(entry.Content);
            }
            if (!string.IsNullOrEmpty(entry.ToolName))
            {
                sb.AppendLine($"**Tool Invocation:** `{entry.ToolName}`");
                if (!string.IsNullOrEmpty(entry.ToolArguments))
                {
                    sb.AppendLine("```json");
                    sb.AppendLine(entry.ToolArguments);
                    sb.AppendLine("```");
                }
            }
            if (!string.IsNullOrEmpty(entry.ToolResult))
            {
                sb.AppendLine("**Tool Output:**");
                sb.AppendLine("```");
                sb.AppendLine(entry.ToolResult);
                sb.AppendLine("```");
            }
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
        return filePath;
    }
}
