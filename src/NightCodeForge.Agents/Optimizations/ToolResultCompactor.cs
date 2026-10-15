using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

/// <summary>
/// Conversation history optimizer that compacts older, bulky tool outputs
/// outside a protected recent window into concise summary stubs to prevent quadratic token inflation.
/// Enabled by default via <see cref="LocalOptimizationFlags.CompactOlderToolResults"/> (opt-out).
/// </summary>
public class ToolResultCompactor
{
    public const int DefaultKeepVerbatimCount = 100;
    public const int DefaultMaxResultLength = 2000;
    public const int DefaultTruncateToLength = 1750;

    private readonly LocalOptimizationFlags _flags;
    private readonly int _keepVerbatimCount;
    private readonly int _maxResultLength;
    private readonly int _truncateToLength;

    public ToolResultCompactor(
        LocalOptimizationFlags? flags = null,
        int? keepVerbatimCount = null,
        int? maxResultLength = null,
        int? truncateToLength = null)
    {
        _flags = flags ?? new LocalOptimizationFlags();
        _keepVerbatimCount = keepVerbatimCount ?? _flags.ToolKeepVerbatimCount;
        _maxResultLength = maxResultLength ?? _flags.ToolMaxResultLength;
        _truncateToLength = truncateToLength ?? _flags.ToolTruncateToLength;
    }

    /// <summary>
    /// Compacts older tool results in-place if optimization is enabled.
    /// </summary>
    public void Compact(IList<ChatMessage> messages, int? keepVerbatimCount = null)
    {
        Compact(
            messages,
            _flags,
            keepVerbatimCount ?? _keepVerbatimCount,
            _maxResultLength,
            _truncateToLength);
    }

    /// <summary>
    /// Static compaction utility method with opt-out support via <see cref="LocalOptimizationFlags.CompactOlderToolResults"/>.
    /// </summary>
    public static void Compact(
        IList<ChatMessage> messages,
        LocalOptimizationFlags? flags = null,
        int? keepVerbatimCount = null,
        int? maxResultLength = null,
        int? truncateToLength = null)
    {
        if (flags != null && !flags.CompactOlderToolResults)
        {
            return; // Opted out
        }

        var effectiveKeep = keepVerbatimCount ?? flags?.ToolKeepVerbatimCount ?? DefaultKeepVerbatimCount;
        var effectiveMaxLen = maxResultLength ?? flags?.ToolMaxResultLength ?? DefaultMaxResultLength;
        var effectiveTruncate = truncateToLength ?? flags?.ToolTruncateToLength ?? DefaultTruncateToLength;

        var toolIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.Tool || messages[i].Contents.Any(c => c is FunctionResultContent))
            {
                toolIndices.Add(i);
            }
        }

        if (toolIndices.Count <= effectiveKeep)
        {
            return;
        }

        var toCompact = toolIndices.Take(toolIndices.Count - effectiveKeep);
        foreach (var idx in toCompact)
        {
            var msg = messages[idx];
            bool modified = false;
            var newContents = new List<AIContent>(msg.Contents.Count);

            foreach (var content in msg.Contents)
            {
                if (content is FunctionResultContent frc)
                {
                    var resultStr = frc.Result as string ?? frc.Result?.ToString();
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > effectiveMaxLen)
                    {
                        var prefixLen = Math.Min(effectiveTruncate, resultStr.Length);
                        var truncated = resultStr[..prefixLen] + $"\n... [Output truncated; {resultStr.Length} characters total]";
                        newContents.Add(new FunctionResultContent(frc.CallId, truncated));
                        modified = true;
                    }
                    else
                    {
                        newContents.Add(content);
                    }
                }
                else if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text) && tc.Text.Length > effectiveMaxLen)
                {
                    var prefixLen = Math.Min(effectiveTruncate, tc.Text.Length);
                    var truncated = tc.Text[..prefixLen] + $"\n... [Output truncated; {tc.Text.Length} characters total]";
                    newContents.Add(new TextContent(truncated));
                    modified = true;
                }
                else
                {
                    newContents.Add(content);
                }
            }

            if (modified)
            {
                messages[idx] = new ChatMessage(msg.Role, newContents);
            }
        }
    }
}
