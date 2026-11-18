using System.Text;
using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Shared parsing, command matching, and diagnostic formatting helpers for CLI filters.
/// </summary>
public static partial class CliOutputFilterHelpers
{
    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"(?:&&|\|\||;|\r?\n)")]
    private static partial Regex ShellSegmentSplitterRegex();

    private static readonly HashSet<string> EnvironmentWrapperBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "env", "sudo", "timeout", "nice", "nohup", "time", "cd", "pushd", "export"
    };

    private static readonly HashSet<string> AllowedPipelinePassThroughCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "tee", "cat"
    };

    private static readonly HashSet<string> GlobalInformationalFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h", "-?", "--version", "--info", "--list-tests"
    };

    internal static string StripAnsi(string input) =>
        string.IsNullOrEmpty(input) ? string.Empty : AnsiEscapeRegex().Replace(input, string.Empty);

    internal static string[] SplitLines(string rawOutput) =>
        StripAnsi(rawOutput).Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

    /// <summary>
    /// Extracts executable and subcommand pairs across shell segments and safe pipelines.
    /// </summary>
    internal static IEnumerable<(string Executable, string? SubCommand, string? ThirdToken)> EnumerateCommands(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            yield break;
        }

        foreach (var rawSegment in ShellSegmentSplitterRegex().Split(command))
        {
            var segment = rawSegment.Trim();
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            var pipelineStages = TokenizePipelineStages(segment);
            for (int stageIdx = 0; stageIdx < pipelineStages.Count; stageIdx++)
            {
                if (!TryExtractStageCommand(pipelineStages[stageIdx], out var parsed))
                {
                    continue;
                }

                if (HasNonPassThroughDownstreamStage(pipelineStages, stageIdx))
                {
                    continue;
                }

                if (HasInformationalFlag(parsed.Executable, parsed.SubCommand, parsed.RemainingTokens))
                {
                    continue;
                }

                yield return (parsed.Executable, parsed.SubCommand, parsed.ThirdToken);
            }
        }
    }

    internal static void AddUnique(List<string> target, HashSet<string> seen, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
        {
            target.Add(value);
        }
    }

    /// <summary>
    /// Formats a simplified diagnostic block with warning suppression and outcome summary.
    /// </summary>
    internal static string FormatCompilerDiagnosticsOutput(
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string>? extraFailureLines,
        CommandFilterOptions options,
        string? explicitOutcomeSummary = null,
        bool? assumedSuccess = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CliOutputFilterConstants.SimplifiedOutputHeader);

        bool hasErrors = errors.Count > 0 || (extraFailureLines is { Count: > 0 });
        if (errors.Count > 0)
        {
            foreach (var err in errors)
            {
                sb.AppendLine(err);
            }
        }
        else if (extraFailureLines is { Count: > 0 })
        {
            foreach (var line in extraFailureLines)
            {
                sb.AppendLine(line);
            }
        }

        if (hasErrors)
        {
            if (options.SuppressWarningsOnErrors && warnings.Count > 0)
            {
                sb.AppendLine(CliOutputFilterConstants.FormatSuppressedWarningsNotice(warnings.Count, Math.Max(1, errors.Count)));
            }
            else if (warnings.Count > 0)
            {
                AppendCappedWarnings(sb, warnings, options.MaxWarnings);
            }

            sb.AppendLine(!string.IsNullOrWhiteSpace(explicitOutcomeSummary)
                ? explicitOutcomeSummary
                : CliOutputFilterConstants.BuildFailedSummary);
        }
        else
        {
            if (warnings.Count > 0)
            {
                AppendCappedWarnings(sb, warnings, options.MaxWarnings);
            }

            if (!string.IsNullOrWhiteSpace(explicitOutcomeSummary))
            {
                sb.AppendLine(explicitOutcomeSummary);
            }
            else
            {
                sb.AppendLine(assumedSuccess == false
                    ? CliOutputFilterConstants.BuildFailedSummary
                    : CliOutputFilterConstants.BuildSucceededSummary);
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static void AppendCappedWarnings(StringBuilder sb, IReadOnlyList<string> warnings, int maxWarnings)
    {
        int effectiveCap = Math.Max(0, maxWarnings);
        int emitCount = Math.Min(effectiveCap, warnings.Count);
        for (int i = 0; i < emitCount; i++)
        {
            sb.AppendLine(warnings[i]);
        }

        if (warnings.Count > emitCount)
        {
            sb.AppendLine(CliOutputFilterConstants.FormatOmittedWarningsNotice(warnings.Count - emitCount));
        }
    }

    /// <summary>
    /// Applies smart CLI filtering when eligible and enforces <paramref name="maxInlineChars"/>.
    /// </summary>
    public static (string Output, bool WasSmartFiltered, bool WasTruncated) FormatCommandOutput(
        ICliOutputFilterRegistry? registry,
        string originalCommand,
        string? translatedCommand,
        bool isSuccess,
        string prefix,
        string rawBody,
        string footer,
        string workspaceRoot,
        bool enableSmartFilter,
        int maxInlineChars = WorkflowDefaults.MaxToolInlineChars)
    {
        var filter = enableSmartFilter &&
            registry != null &&
            !rawBody.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, StringComparison.Ordinal)
            ? (registry.GetFilter(originalCommand) ??
               (!string.IsNullOrWhiteSpace(translatedCommand) ? registry.GetFilter(translatedCommand) : null))
            : null;

        if (filter != null)
        {
            var trimmedRawForCompare = rawBody.TrimEnd();
            var filteredBody = filter.FilterOutput(rawBody, workspaceRoot, new CommandFilterOptions()).TrimEnd();

            // Pass raw output through unchanged if the filter recognized no structured output.
            bool unchangedNonEmptyPassThrough = !string.IsNullOrWhiteSpace(trimmedRawForCompare) &&
                string.Equals(filteredBody, trimmedRawForCompare, StringComparison.Ordinal) &&
                !filteredBody.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, StringComparison.Ordinal);

            // Prevent invocation crashes (e.g., command not found) from being reported as success.
            bool isFalseSuccess = !isSuccess &&
                !rawBody.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase) &&
                !rawBody.Contains("Test Run Successful", StringComparison.OrdinalIgnoreCase) &&
                (filteredBody.EndsWith(CliOutputFilterConstants.BuildSucceededSummary, StringComparison.Ordinal) ||
                 filteredBody.EndsWith(CliOutputFilterConstants.TestRunPassedSummary, StringComparison.Ordinal));

            if (!isFalseSuccess && !unchangedNonEmptyPassThrough)
            {
                if (!filteredBody.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, StringComparison.Ordinal))
                {
                    filteredBody = $"{CliOutputFilterConstants.SimplifiedOutputHeader}\n{filteredBody}";
                }

                var candidate = string.IsNullOrEmpty(footer)
                    ? $"{prefix}{filteredBody}".TrimEnd()
                    : $"{prefix}{filteredBody}{footer}".TrimEnd();

                if (candidate.Length <= maxInlineChars)
                {
                    return (candidate, WasSmartFiltered: true, WasTruncated: false);
                }

                var truncatedFiltered = TruncateWithHeadAndFooter(prefix, filteredBody, footer, maxInlineChars);
                return (truncatedFiltered, WasSmartFiltered: true, WasTruncated: true);
            }
        }

        var trimmedRaw = rawBody.TrimEnd();
        var rawCandidate = $"{prefix}{trimmedRaw}";
        if (rawCandidate.Length <= maxInlineChars)
        {
            return (rawCandidate, WasSmartFiltered: false, WasTruncated: false);
        }

        var truncatedRaw = TruncateWithHeadAndFooter(prefix, trimmedRaw, footer, maxInlineChars);
        return (truncatedRaw, WasSmartFiltered: false, WasTruncated: true);
    }

    /// <summary>
    /// Applies smart CLI filtering and inline capping to deterministic priming output.
    /// </summary>
    public static string FilterPrimingOutput(
        ICliOutputFilterRegistry registry,
        string command,
        string rawOutput,
        bool isSuccess,
        string workspaceRoot,
        bool smartFilteringEnabled,
        int maxInlineChars = WorkflowDefaults.MaxToolInlineChars)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return rawOutput;
        }

        string exitPrefix = string.Empty;
        string body = rawOutput;
        if (body.StartsWith("Exit Code:", StringComparison.OrdinalIgnoreCase))
        {
            int newlineIdx = body.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                exitPrefix = body[..(newlineIdx + 1)];
                body = body[(newlineIdx + 1)..];
            }
        }

        var (formatted, wasSmartFiltered, wasTruncated) = FormatCommandOutput(
            registry,
            command,
            translatedCommand: null,
            isSuccess,
            exitPrefix,
            body,
            footer: string.Empty,
            workspaceRoot,
            enableSmartFilter: smartFilteringEnabled && !string.IsNullOrWhiteSpace(command),
            maxInlineChars);

        return wasSmartFiltered || wasTruncated ? formatted : rawOutput;
    }

    /// <summary>
    /// Head-truncates <paramref name="bodyText"/> with an omitted-char marker and footer.
    /// </summary>
    public static string TruncateWithHeadAndFooter(
        string prefix,
        string bodyText,
        string footer,
        int maxTotalChars)
    {
        if (maxTotalChars <= 0)
        {
            return string.Empty;
        }

        int fixedBase = prefix.Length + footer.Length;
        if (fixedBase >= maxTotalChars)
        {
            var combined = prefix + footer;
            return combined[..maxTotalChars];
        }

        int headLen = Math.Max(0, maxTotalChars - fixedBase - 64);
        for (int pass = 0; pass < 4; pass++)
        {
            int omitted = Math.Max(1, bodyText.Length - headLen);
            var marker = $"\n{CliOutputFilterConstants.FormatAdditionalCharsMarker(omitted)}";
            int available = Math.Max(0, maxTotalChars - fixedBase - marker.Length);
            if (available == headLen)
            {
                break;
            }

            headLen = Math.Min(bodyText.Length, available);
        }

        int finalOmitted = Math.Max(1, bodyText.Length - headLen);
        var finalMarker = $"\n{CliOutputFilterConstants.FormatAdditionalCharsMarker(finalOmitted)}";
        int finalAvailable = Math.Max(0, maxTotalChars - fixedBase - finalMarker.Length);
        headLen = Math.Min(headLen, finalAvailable);
        if (headLen > 0 && headLen < bodyText.Length && char.IsHighSurrogate(bodyText[headLen - 1]))
        {
            headLen--;
        }

        var candidate = $"{prefix}{bodyText[..headLen]}{finalMarker}{footer}";
        return candidate.Length <= maxTotalChars ? candidate : candidate[..maxTotalChars];
    }

    private static bool HasNonPassThroughDownstreamStage(List<List<string>> pipelineStages, int stageIdx)
    {
        for (int j = stageIdx + 1; j < pipelineStages.Count; j++)
        {
            if (!TryExtractStageCommand(pipelineStages[j], out var downstream) ||
                !AllowedPipelinePassThroughCommands.Contains(downstream.Executable))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInformationalFlag(
        string exe,
        string? sub,
        IReadOnlyList<string> remainingTokens)
    {
        bool isDotnetTest =
            (exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
             exe.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(sub, "test", StringComparison.OrdinalIgnoreCase);

        bool isTsc =
            exe.Equals("tsc", StringComparison.OrdinalIgnoreCase) ||
            exe.Equals("tsc.cmd", StringComparison.OrdinalIgnoreCase) ||
            (exe.Equals("npx", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(sub, "tsc", StringComparison.OrdinalIgnoreCase));

        foreach (var token in remainingTokens)
        {
            if (GlobalInformationalFlags.Contains(token))
            {
                return true;
            }

            if (isDotnetTest && token.Equals("-t", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (isTsc && token.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractStageCommand(
        List<string> tokens,
        out (string Executable, string? SubCommand, string? ThirdToken, IReadOnlyList<string> RemainingTokens) parsed)
    {
        int idx = 0;
        while (idx < tokens.Count)
        {
            var tok = tokens[idx];
            if (tok.Contains('=') && !tok.StartsWith('-'))
            {
                idx++;
                continue;
            }

            var fileName = Path.GetFileName(tok);
            if (EnvironmentWrapperBinaries.Contains(fileName))
            {
                idx = AdvancePastWrapper(fileName, tokens, idx + 1);
                continue;
            }

            break;
        }

        if (idx >= tokens.Count)
        {
            parsed = default;
            return false;
        }

        var exe = Path.GetFileName(tokens[idx]);
        var sub = idx + 1 < tokens.Count ? tokens[idx + 1] : null;
        var third = idx + 2 < tokens.Count ? tokens[idx + 2] : null;
        var remaining = idx + 1 < tokens.Count ? tokens.GetRange(idx + 1, tokens.Count - (idx + 1)) : [];
        parsed = (exe, sub, third, remaining);
        return true;
    }

    private static int AdvancePastWrapper(string wrapperName, List<string> tokens, int idx)
    {
        while (idx < tokens.Count && tokens[idx].StartsWith('-'))
        {
            var flag = tokens[idx];
            idx++;
            if (WrapperFlagTakesArgument(wrapperName, flag) && idx < tokens.Count)
            {
                idx++;
            }
        }

        if (wrapperName.Equals("cd", StringComparison.OrdinalIgnoreCase) ||
            wrapperName.Equals("pushd", StringComparison.OrdinalIgnoreCase))
        {
            if (idx < tokens.Count)
            {
                idx++;
            }
        }
        else if (wrapperName.Equals("timeout", StringComparison.OrdinalIgnoreCase) &&
                 idx < tokens.Count &&
                 tokens[idx].Length > 0 &&
                 char.IsDigit(tokens[idx][0]))
        {
            idx++;
        }

        return idx;
    }

    private static bool WrapperFlagTakesArgument(string wrapperName, string flag)
    {
        if (wrapperName.Equals("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return flag is "-k" or "-s" or "--kill-after" or "--signal";
        }

        if (wrapperName.Equals("nice", StringComparison.OrdinalIgnoreCase))
        {
            return flag is "-n" or "--adjustment";
        }

        if (wrapperName.Equals("sudo", StringComparison.OrdinalIgnoreCase) ||
            wrapperName.Equals("env", StringComparison.OrdinalIgnoreCase))
        {
            return flag is "-u" or "-g" or "-C" or "-S" or "-p" or "-r" or "-t";
        }

        return false;
    }

    private static List<List<string>> TokenizePipelineStages(string segment)
    {
        var stages = new List<List<string>>();
        var currentStage = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (char ch in segment)
        {
            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '\'' or '"')
            {
                quote = ch;
            }
            else if (ch == '|')
            {
                FlushToken(current, currentStage);
                if (currentStage.Count > 0)
                {
                    stages.Add(currentStage);
                    currentStage = [];
                }
            }
            else if (char.IsWhiteSpace(ch))
            {
                FlushToken(current, currentStage);
            }
            else
            {
                current.Append(ch);
            }
        }

        FlushToken(current, currentStage);
        if (currentStage.Count > 0)
        {
            stages.Add(currentStage);
        }

        return stages;
    }

    private static void FlushToken(StringBuilder current, List<string> target)
    {
        if (current.Length > 0)
        {
            target.Add(current.ToString());
            current.Clear();
        }
    }
}
