using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents;

/// <summary>
/// Result of extracting concluding summary, reviewer feedback, and target routing
/// from an agent's concluding message.
/// </summary>
public sealed record StageSummaryExtractionResult(
    string Summary,
    string? ReviewerFeedback = null,
    string? TargetRouting = null,
    bool IsApproved = false);

/// <summary>
/// Shared summary extractor across Crucible, ACP, and Pi RPC harnesses.
/// Parses routing tags (request-changes, approved) for routing and feedback,
/// strips recognized tags from the human-facing summary, preserves code/generics,
/// and selects concluding text from conversation history.
/// </summary>
public static class StageSummaryExtractor
{
    public const string DefaultCompletedSummary = "Stage completed successfully.";
    public const string RequestChangesTagName = "request-changes";
    public const string RequestChangesAltTagName = "request_changes";
    public const string ApprovedTagName = "approved";

    public static readonly Regex RequestChangesRegex = new(
        @"<request[-_]changes\s*>(.*?)</request[-_]changes\s*>|<request[-_]changes\s*/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static readonly Regex ApprovedRegex = new(
        @"<approved\s*>(.*?)</approved\s*>|<approved\s*/>|<approved\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Selects the text of the last assistant message with non-empty text from the history.
    /// If none is found, returns null.
    /// </summary>
    public static string? GetConcludingText(IEnumerable<ChatMessage>? messages)
    {
        if (messages == null) return null;

        return messages
            .LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            ?.Text;
    }

    /// <summary>
    /// Extracts concluding summary, reviewer feedback, and target routing from raw concluding text.
    /// Removes only recognized routing tags (&lt;request-changes&gt;, &lt;request_changes&gt;, &lt;approved&gt;),
    /// preserving other angle brackets such as generics and code snippets.
    /// Falls back to <paramref name="fallbackSummary"/> (or <see cref="DefaultCompletedSummary"/>) if empty.
    /// </summary>
    public static StageSummaryExtractionResult Extract(
        string? rawConcludingText,
        string? fallbackSummary = DefaultCompletedSummary,
        string? explicitTargetRouting = null)
    {
        var effectiveFallback = !string.IsNullOrWhiteSpace(fallbackSummary)
            ? fallbackSummary
            : DefaultCompletedSummary;

        if (string.IsNullOrWhiteSpace(rawConcludingText))
        {
            return new StageSummaryExtractionResult(effectiveFallback, TargetRouting: explicitTargetRouting);
        }

        string text = rawConcludingText;
        string? reviewerFeedback = null;
        string? targetRouting = explicitTargetRouting;
        bool isApproved = false;

        // 1. Parse & strip request-changes tag
        var reqMatch = RequestChangesRegex.Match(text);
        if (reqMatch.Success)
        {
            var feedback = reqMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(feedback))
            {
                reviewerFeedback = feedback;
            }
            targetRouting = StandardStageIds.Implementation;
            text = RequestChangesRegex.Replace(text, string.Empty);
        }

        // 2. Parse & strip approved tag
        var appMatch = ApprovedRegex.Match(text);
        if (appMatch.Success)
        {
            isApproved = true;
            text = ApprovedRegex.Replace(text, string.Empty);
        }

        // 3. Normalize whitespace and blank lines
        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = effectiveFallback;
        }

        return new StageSummaryExtractionResult(
            Summary: cleaned,
            ReviewerFeedback: reviewerFeedback,
            TargetRouting: targetRouting,
            IsApproved: isApproved);
    }
}
