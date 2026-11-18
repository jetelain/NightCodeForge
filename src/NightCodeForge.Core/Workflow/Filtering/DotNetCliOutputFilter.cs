using System.Text;
using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Semantic CLI output filter for <c>dotnet</c> and <c>msbuild</c> build/test commands.
/// </summary>
public sealed partial class DotNetCliOutputFilter : ICliOutputFilter
{
    private const int MaxStackFramesPerFailure = 12;
    private const int MaxErrorMessageLinesPerFailure = 20;

    private static readonly HashSet<string> SupportedDotNetSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "test", "msbuild", "publish", "pack", "restore", "clean", "vstest"
    };

    [GeneratedRegex(@"(?:^|\s|:)error\s+(?<code>[A-Za-z]+\d+)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerOrSdkErrorRegex();

    [GeneratedRegex(@"(?:^|\s|:)warning\s+(?<code>[A-Za-z]+\d+)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerOrSdkWarningRegex();

    [GeneratedRegex(@"^\s*(?:Failed\s+(?<test>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_<>`+]+)+(?:\([^\r\n]*\))?(?:\s+\[[^\]]+\])?)|\[xUnit\.net[^\]]*\]\s+(?<xunitTest>[^\r\n]+?)\s+\[FAIL\])\s*$")]
    private static partial Regex FailedTestHeaderRegex();

    [GeneratedRegex(@"\s+\[[^\]]+\]\s*$")]
    private static partial Regex TrailingDurationBracketRegex();

    [GeneratedRegex(@"^\[xUnit\.net[^\]]*\]\s*")]
    private static partial Regex XunitNetPrefixRegex();

    [GeneratedRegex(@"^\s*(?:(?:Passed|Skipped)\s+[A-Za-z0-9_]+(?:\.[A-Za-z0-9_<>`+]+)+|\[xUnit\.net[^\]]*\]\s+[^\r\n]+?\s+\[(?:SKIP|PASS)\])", RegexOptions.IgnoreCase)]
    private static partial Regex PassingOrSkippedTestLineRegex();

    [GeneratedRegex(@"^\s*(?:Failed!|Passed!)\s+-\s+Failed:\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex VsTestOutcomeSummaryRegex();

    [GeneratedRegex(@"^\s*(?:Test Run Failed\.|Test Run Successful\.|Total tests:\s*\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex VsTestRunBannerSummaryRegex();

    [GeneratedRegex(@"^\s*(?:Determining projects to restore\.\.\.|All projects are up-to-date for restore\.|Restored\s+.+?\(in\s+.+?\)\.?|MSBuild version\s+|VSTest version\s+|Microsoft \(R\) Build Engine|Microsoft \(R\) Test Execution|Copyright \(c\) Microsoft|Build started\s+|Time Elapsed\s+|\d+\s+Warning\(s\)|\d+\s+Error\(s\)|Starting test execution|A total of\s+\d+\s+test files matched|Test run for\s+|Workload updates are available|Run `dotnet workload update`|\[xUnit\.net[^\]]*\]\s+(?:xUnit\.net VSTest Adapter|Starting:|Finished:|Discovering:|Discovered:))", RegexOptions.IgnoreCase)]
    private static partial Regex BoilerplateNoiseRegex();

    [GeneratedRegex(@"^\s*[^\s].*?\s+->\s+[^\r\n]+(?:\.dll|\.exe)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildOutputArrowRegex();

    [GeneratedRegex(@"(?:Microsoft\.NET\.Sdk|Microsoft\.Common\.CurrentVersion\.targets|\\dotnet\\sdk\\|/dotnet/sdk/)", RegexOptions.IgnoreCase)]
    private static partial Regex SdkInternalLocationRegex();

    [GeneratedRegex(@"^\s*at\s+(?:System\.RuntimeMethodHandle\.|System\.Reflection\.|InvokeStub_)", RegexOptions.IgnoreCase)]
    private static partial Regex InternalReflectionStackFrameRegex();

    [GeneratedRegex(@"^\s*Build\s+(?<status>succeeded|FAILED)\.", RegexOptions.IgnoreCase)]
    private static partial Regex BuildStatusLineRegex();

    public bool CanHandle(string command)
    {
        foreach (var (exe, sub, _) in CliOutputFilterHelpers.EnumerateCommands(command))
        {
            if (exe.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("msbuild.exe", StringComparison.OrdinalIgnoreCase) ||
                exe.Equals("vstest.console.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if ((exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                 exe.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) &&
                sub != null &&
                SupportedDotNetSubcommands.Contains(sub))
            {
                return true;
            }
        }

        return false;
    }

    public string FilterOutput(string rawOutput, string workspaceRoot, CommandFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return $"{CliOutputFilterConstants.SimplifiedOutputHeader}\n{CliOutputFilterConstants.BuildSucceededSummary}";
        }

        var state = new ParseState();
        var lines = CliOutputFilterHelpers.SplitLines(rawOutput);

        for (int i = 0; i < lines.Length; i++)
        {
            ProcessLine(lines, ref i, workspaceRoot, options, state);
        }

        if (!state.SawRecognizedSignal)
        {
            // Keep unrecognized non-MSBuild output verbatim rather than faking success.
            return rawOutput.TrimEnd();
        }

        return FormatDotNetOutcome(state, options);
    }

    private static void ProcessLine(
        string[] lines,
        ref int index,
        string workspaceRoot,
        CommandFilterOptions options,
        ParseState state)
    {
        var trimmed = lines[index].Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        if (trimmed.Equals("[STDERR]", StringComparison.Ordinal))
        {
            state.InStderrSection = true;
            return;
        }

        var buildStatusMatch = BuildStatusLineRegex().Match(trimmed);
        if (buildStatusMatch.Success)
        {
            state.SawRecognizedSignal = true;
            if (buildStatusMatch.Groups["status"].Value.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                state.SawBuildFailed = true;
            }
            else
            {
                state.SawBuildSucceeded = true;
            }
            return;
        }

        var failedHeaderMatch = FailedTestHeaderRegex().Match(trimmed);
        if (failedHeaderMatch.Success)
        {
            state.SawRecognizedSignal = true;
            RecordFailedTestBlock(lines, ref index, failedHeaderMatch, workspaceRoot, options.RelativizePaths, state);
            return;
        }

        if (VsTestOutcomeSummaryRegex().IsMatch(trimmed) || VsTestRunBannerSummaryRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            var summaryLine = options.RelativizePaths
                ? CliOutputPathRelativizer.RelativizeLinePaths(trimmed, workspaceRoot)
                : trimmed;
            CliOutputFilterHelpers.AddUnique(state.TestSummaries, state.SeenTestSummaries, summaryLine);
            return;
        }

        if (PassingOrSkippedTestLineRegex().IsMatch(trimmed) ||
            BoilerplateNoiseRegex().IsMatch(trimmed) ||
            BuildOutputArrowRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            return;
        }

        if (CompilerOrSdkErrorRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            var normalized = CliOutputPathRelativizer.NormalizeMsBuildDiagnosticLine(trimmed, workspaceRoot, options.RelativizePaths);
            if (options.StripSdkInternalLocations && SdkInternalLocationRegex().IsMatch(trimmed))
            {
                CliOutputFilterHelpers.AddUnique(state.SdkOnlyErrors, state.SeenSdkErrors, normalized);
            }
            else
            {
                CliOutputFilterHelpers.AddUnique(state.Errors, state.SeenErrors, normalized);
            }
            return;
        }

        if (CompilerOrSdkWarningRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            if (options.StripSdkInternalLocations && SdkInternalLocationRegex().IsMatch(trimmed))
            {
                return;
            }

            var normalized = CliOutputPathRelativizer.NormalizeMsBuildDiagnosticLine(trimmed, workspaceRoot, options.RelativizePaths);
            CliOutputFilterHelpers.AddUnique(state.Warnings, state.SeenWarnings, normalized);
            return;
        }

        if (options.StripSdkInternalLocations && SdkInternalLocationRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            return;
        }

        if (state.InStderrSection ||
            trimmed.StartsWith("Fatal error", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("MSBUILD : error", StringComparison.OrdinalIgnoreCase))
        {
            state.SawRecognizedSignal = true;
            var normalized = CliOutputPathRelativizer.NormalizeMsBuildDiagnosticLine(trimmed, workspaceRoot, options.RelativizePaths);
            CliOutputFilterHelpers.AddUnique(state.FallbackErrorLines, state.SeenFallbackErrors, normalized);
        }
    }

    private static void RecordFailedTestBlock(
        string[] lines,
        ref int index,
        Match failedHeaderMatch,
        string workspaceRoot,
        bool relativizePaths,
        ParseState state)
    {
        var rawKey = failedHeaderMatch.Groups["test"].Success
            ? failedHeaderMatch.Groups["test"].Value
            : failedHeaderMatch.Groups["xunitTest"].Value;
        var testKey = TrailingDurationBracketRegex().Replace(rawKey.Trim(), string.Empty).Trim();
        var block = ParseTestFailureBlock(lines, ref index, workspaceRoot, relativizePaths);

        if (!state.TestFailureIndices.TryGetValue(testKey, out int existingIdx))
        {
            state.TestFailureIndices[testKey] = state.TestFailures.Count;
            state.TestFailures.Add(block);
            return;
        }

        var existing = state.TestFailures[existingIdx];
        int existingScore = existing.ErrorMessageLines.Count + existing.StackLines.Count;
        int newScore = block.ErrorMessageLines.Count + block.StackLines.Count;
        bool prefersVstestHeader = newScore >= existingScore &&
            existing.Header.StartsWith("[xUnit.net", StringComparison.Ordinal) &&
            !block.Header.StartsWith("[xUnit.net", StringComparison.Ordinal);

        if (newScore > existingScore || prefersVstestHeader)
        {
            state.TestFailures[existingIdx] = block;
        }
    }

    private static string FormatDotNetOutcome(ParseState state, CommandFilterOptions options)
    {
        if (state.Errors.Count == 0 && state.SdkOnlyErrors.Count > 0)
        {
            state.Errors.AddRange(state.SdkOnlyErrors);
        }

        if (state.Errors.Count > 0)
        {
            return CliOutputFilterHelpers.FormatCompilerDiagnosticsOutput(
                state.Errors,
                state.Warnings,
                state.FallbackErrorLines,
                options,
                CliOutputFilterConstants.BuildFailedSummary);
        }

        if (state.TestFailures.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CliOutputFilterConstants.SimplifiedOutputHeader);
            foreach (var failure in state.TestFailures)
            {
                failure.AppendTo(sb);
            }

            if (state.Warnings.Count > 0)
            {
                if (options.SuppressWarningsOnErrors)
                {
                    sb.AppendLine(CliOutputFilterConstants.FormatSuppressedWarningsNotice(state.Warnings.Count, state.TestFailures.Count));
                }
                else
                {
                    CliOutputFilterHelpers.AppendCappedWarnings(sb, state.Warnings, options.MaxWarnings);
                }
            }

            if (state.TestSummaries.Count > 0)
            {
                foreach (var summary in state.TestSummaries)
                {
                    sb.AppendLine(summary);
                }
            }
            else
            {
                sb.AppendLine(CliOutputFilterConstants.TestRunFailedSummary);
            }

            return sb.ToString().TrimEnd();
        }

        string? outcomeSummary = null;
        if (state.TestSummaries.Count > 0)
        {
            outcomeSummary = string.Join("\n", state.TestSummaries);
        }
        else if (state.SawBuildFailed && !state.SawBuildSucceeded)
        {
            outcomeSummary = CliOutputFilterConstants.BuildFailedSummary;
        }
        else if (state.SawBuildSucceeded)
        {
            outcomeSummary = CliOutputFilterConstants.BuildSucceededSummary;
        }

        return CliOutputFilterHelpers.FormatCompilerDiagnosticsOutput(
            state.Errors,
            state.Warnings,
            state.SawBuildFailed ? state.FallbackErrorLines : null,
            options,
            outcomeSummary,
            assumedSuccess: !state.SawBuildFailed);
    }

    private static TestFailureBlock ParseTestFailureBlock(
        string[] lines,
        ref int index,
        string workspaceRoot,
        bool relativizePaths)
    {
        var header = lines[index].Trim();
        var errorMessageLines = new List<string>();
        var userStackLines = new List<string>();
        var allStackLines = new List<string>();
        string section = string.Empty;

        while (index + 1 < lines.Length)
        {
            var nextTrimmed = lines[index + 1].Trim();
            var nextContent = XunitNetPrefixRegex().Replace(nextTrimmed, string.Empty).Trim();

            if (string.IsNullOrEmpty(nextContent))
            {
                // Keep scanning across one blank line if Error Message or Stack Trace follows.
                if (index + 2 < lines.Length)
                {
                    var lookahead = XunitNetPrefixRegex().Replace(lines[index + 2].Trim(), string.Empty).Trim();
                    if (lookahead.StartsWith("Stack Trace:", StringComparison.OrdinalIgnoreCase) ||
                        lookahead.StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase))
                    {
                        index++;
                        continue;
                    }
                }
                break;
            }

            if (FailedTestHeaderRegex().IsMatch(nextTrimmed) ||
                PassingOrSkippedTestLineRegex().IsMatch(nextTrimmed) ||
                VsTestOutcomeSummaryRegex().IsMatch(nextTrimmed) ||
                VsTestRunBannerSummaryRegex().IsMatch(nextTrimmed) ||
                BoilerplateNoiseRegex().IsMatch(nextTrimmed) ||
                nextTrimmed.Equals("[STDERR]", StringComparison.Ordinal) ||
                nextContent.StartsWith("Standard Output Messages:", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            index++;

            if (nextContent.Equals("Error Message:", StringComparison.OrdinalIgnoreCase))
            {
                section = "ErrorMessage";
                continue;
            }

            if (nextContent.Equals("Stack Trace:", StringComparison.OrdinalIgnoreCase))
            {
                section = "StackTrace";
                continue;
            }

            var processed = relativizePaths
                ? CliOutputPathRelativizer.RelativizeLinePaths(nextContent, workspaceRoot)
                : nextContent;

            if (section == "ErrorMessage")
            {
                if (errorMessageLines.Count < MaxErrorMessageLinesPerFailure)
                {
                    errorMessageLines.Add(processed);
                }
            }
            else if (section == "StackTrace" || nextContent.StartsWith("at ", StringComparison.Ordinal))
            {
                if (allStackLines.Count < MaxStackFramesPerFailure)
                {
                    allStackLines.Add(processed);
                    if (!InternalReflectionStackFrameRegex().IsMatch(processed))
                    {
                        userStackLines.Add(processed);
                    }
                }
            }
            else if (errorMessageLines.Count < MaxErrorMessageLinesPerFailure)
            {
                errorMessageLines.Add(processed);
            }
        }

        var selectedStack = userStackLines.Count > 0 ? userStackLines : allStackLines;
        return new TestFailureBlock(header, errorMessageLines, selectedStack);
    }

    private sealed class ParseState
    {
        public List<string> Errors { get; } = [];
        public HashSet<string> SeenErrors { get; } = new(StringComparer.Ordinal);
        public List<string> SdkOnlyErrors { get; } = [];
        public HashSet<string> SeenSdkErrors { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
        public HashSet<string> SeenWarnings { get; } = new(StringComparer.Ordinal);
        public List<TestFailureBlock> TestFailures { get; } = [];
        public Dictionary<string, int> TestFailureIndices { get; } = new(StringComparer.Ordinal);
        public List<string> TestSummaries { get; } = [];
        public HashSet<string> SeenTestSummaries { get; } = new(StringComparer.Ordinal);
        public List<string> FallbackErrorLines { get; } = [];
        public HashSet<string> SeenFallbackErrors { get; } = new(StringComparer.Ordinal);
        public bool SawBuildFailed { get; set; }
        public bool SawBuildSucceeded { get; set; }
        public bool SawRecognizedSignal { get; set; }
        public bool InStderrSection { get; set; }
    }

    private sealed record TestFailureBlock(
        string Header,
        IReadOnlyList<string> ErrorMessageLines,
        IReadOnlyList<string> StackLines)
    {
        public void AppendTo(StringBuilder sb)
        {
            sb.AppendLine(Header);
            if (ErrorMessageLines.Count > 0)
            {
                sb.AppendLine("  Error Message:");
                foreach (var line in ErrorMessageLines)
                {
                    sb.AppendLine($"    {line}");
                }
            }

            if (StackLines.Count > 0)
            {
                sb.AppendLine("  Stack Trace:");
                foreach (var frame in StackLines)
                {
                    sb.AppendLine($"    {frame}");
                }
            }
        }
    }
}
