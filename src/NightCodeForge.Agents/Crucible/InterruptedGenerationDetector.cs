using System.Text;
using Microsoft.Extensions.AI;

namespace NightCodeForge.Agents.Crucible;

/// <summary>
/// Detects assistant output that was cut off mid-thought (e.g. when the inference server fails to parse an
/// unterminated tool call and returns the partial text), so the harness can ask the model to resume
/// instead of treating the fragment as the final stage report.
/// </summary>
public static class InterruptedGenerationDetector
{
    private const string TripleBacktick = "```";
    private const char Backtick = '`';

    private static readonly (string Open, string Close)[] ToolTagPairs =
    [
        ("<tool_call>", "</tool_call>"),
        ("<action>", "</action>"),
        ("<function=", "</function>")
    ];

    private static readonly char[] DanglingTrailingChars = [':', ',', '\\', '-', '(', '[', '{', '=', '+', '&'];

    private static readonly string[] DanglingTrailingWords = ["will", "to", "the", "a", "an", "and", "with", "of", "then", "next", "i'll", "by"];

    /// <summary>Evaluates the last assistant message of a pass that ended without any tool call.</summary>
    public static bool IsTruncated(ChatMessage? lastAssistantMessage)
    {
        if (lastAssistantMessage == null)
        {
            return false;
        }

        if (lastAssistantMessage.Contents.OfType<FunctionCallContent>().Any())
        {
            return false;
        }

        var text = lastAssistantMessage.Text;
        var reasoning = string.Join(
            string.Empty,
            lastAssistantMessage.Contents.OfType<TextReasoningContent>().Select(r => r.Text));

        if (string.IsNullOrWhiteSpace(text))
        {
            return !string.IsNullOrWhiteSpace(reasoning);
        }

        return IsTruncated(text);
    }

    /// <summary>Evaluates raw assistant text for signs of an interrupted generation.</summary>
    public static bool IsTruncated(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (CountOccurrences(text, TripleBacktick) % 2 != 0)
        {
            return true;
        }

        foreach (var (open, close) in ToolTagPairs)
        {
            var openIndex = text.LastIndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (openIndex >= 0 && text.IndexOf(close, openIndex, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return true;
            }
        }

        var trimmed = text.TrimEnd();
        var withoutFences = StripClosedCodeFences(text);
        if (CountOccurrences(withoutFences, Backtick) % 2 != 0)
        {
            return true;
        }

        if (Array.IndexOf(DanglingTrailingChars, trimmed[^1]) >= 0)
        {
            return true;
        }

        var lastLine = trimmed[(trimmed.LastIndexOf('\n') + 1)..].Trim();
        if (IsBareListMarker(lastLine))
        {
            return true;
        }

        var lastWord = lastLine[(lastLine.LastIndexOf(' ') + 1)..].ToLowerInvariant();
        return Array.IndexOf(DanglingTrailingWords, lastWord) >= 0;
    }

    private static bool IsBareListMarker(string line)
    {
        if (line is "-" or "*" or "+")
        {
            return true;
        }

        var digits = line.TakeWhile(char.IsDigit).Count();
        return digits > 0 && digits == line.Length - 1 && line[^1] is '.' or ')';
    }

    private static string StripClosedCodeFences(string text)
    {
        var sb = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf(TripleBacktick, index, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(text, index, text.Length - index);
                break;
            }

            sb.Append(text, index, open - index);
            var close = text.IndexOf(TripleBacktick, open + TripleBacktick.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            index = close + TripleBacktick.Length;
        }

        return sb.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static int CountOccurrences(string text, char value) => text.Count(c => c == value);
}
