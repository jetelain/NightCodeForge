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
    public const int DefaultKeepVerbatimCount = 4;
    public const int DefaultMaxResultLength = 200;
    public const int DefaultTruncateToLength = 150;

    private readonly LocalOptimizationFlags _flags;
    private readonly int _keepVerbatimCount;
    private readonly int _maxResultLength;
    private readonly int _truncateToLength;

    public ToolResultCompactor(
        LocalOptimizationFlags? flags = null,
        int keepVerbatimCount = DefaultKeepVerbatimCount,
        int maxResultLength = DefaultMaxResultLength,
        int truncateToLength = DefaultTruncateToLength)
    {
        _flags = flags ?? new LocalOptimizationFlags();
        _keepVerbatimCount = keepVerbatimCount;
        _maxResultLength = maxResultLength;
        _truncateToLength = truncateToLength;
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
        int keepVerbatimCount = DefaultKeepVerbatimCount,
        int maxResultLength = DefaultMaxResultLength,
        int truncateToLength = DefaultTruncateToLength)
    {
        if (flags != null && !flags.CompactOlderToolResults)
        {
            return; // Opted out
        }

        var toolIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.Tool || messages[i].Contents.Any(c => c is FunctionResultContent))
            {
                toolIndices.Add(i);
            }
        }

        if (toolIndices.Count <= keepVerbatimCount)
        {
            return;
        }

        var toCompact = toolIndices.Take(toolIndices.Count - keepVerbatimCount);
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
                    if (!string.IsNullOrEmpty(resultStr) && resultStr.Length > maxResultLength)
                    {
                        var prefixLen = Math.Min(truncateToLength, resultStr.Length);
                        var truncated = resultStr[..prefixLen] + $"\n... [Output truncated; {resultStr.Length} characters total]";
                        newContents.Add(new FunctionResultContent(frc.CallId, truncated));
                        modified = true;
                    }
                    else
                    {
                        newContents.Add(content);
                    }
                }
                else if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text) && tc.Text.Length > maxResultLength)
                {
                    var prefixLen = Math.Min(truncateToLength, tc.Text.Length);
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
