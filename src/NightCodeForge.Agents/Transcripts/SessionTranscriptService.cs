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
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_transcriptsDirectory))
        {
            Directory.CreateDirectory(_transcriptsDirectory);
        }

        var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var statusStr = metadata.Status.ToString().ToLowerInvariant();
        var fileName = $"{dateStr}-issue-{metadata.IssueId}-{metadata.StageId}-{statusStr}.md";
        var filePath = Path.Combine(_transcriptsDirectory, fileName);

        var sb = new StringBuilder();

        // YAML Frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"issue_id: {metadata.IssueId}");
        sb.AppendLine($"stage_id: {metadata.StageId}");
        sb.AppendLine($"agent_profile: {metadata.AgentProfile}");
        sb.AppendLine($"model: {metadata.ModelName}");
        sb.AppendLine($"duration_ms: {metadata.DurationMs}");
        sb.AppendLine("tokens:");
        sb.AppendLine($"  prompt: {metadata.Metrics.PromptTokens}");
        sb.AppendLine($"  completion: {metadata.Metrics.CompletionTokens}");
        sb.AppendLine($"  total: {metadata.Metrics.TotalTokens}");
        sb.AppendLine("optimizations:");
        sb.AppendLine($"  thinking_trace_compaction: {metadata.Optimizations.ThinkingTraceCompaction.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  loop_circuit_breaker: {metadata.Optimizations.LoopCircuitBreaker.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  guided_grammar_decoding: {metadata.Optimizations.GuidedGrammarDecoding.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  deterministic_symbol_map: {metadata.Optimizations.DeterministicSymbolMap.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  structured_diagnostic_extraction: {metadata.Optimizations.StructuredDiagnosticExtraction.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  prefix_stabilization: {metadata.Optimizations.PrefixStabilization.ToString().ToLowerInvariant()}");
        sb.AppendLine($"  jit_model_preload: {metadata.Optimizations.JitModelPreload.ToString().ToLowerInvariant()}");
        if (metadata.KvCacheHitRate.HasValue)
        {
            sb.AppendLine($"kv_cache_hit_rate: {metadata.KvCacheHitRate.Value:F2}");
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
            if (!string.IsNullOrEmpty(entry.ToolName))
            {
                sb.AppendLine($"**Tool Invocation:** `{entry.ToolName}`");
                if (!string.IsNullOrEmpty(entry.ToolArguments))
                {
                    sb.AppendLine("```json");
                    sb.AppendLine(entry.ToolArguments);
                    sb.AppendLine("```");
                }
                if (!string.IsNullOrEmpty(entry.ToolResult))
                {
                    sb.AppendLine("**Tool Output:**");
                    sb.AppendLine("```");
                    sb.AppendLine(entry.ToolResult);
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.AppendLine(entry.Content);
            }
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
        return filePath;
    }
}
