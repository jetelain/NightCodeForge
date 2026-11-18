using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Semantic CLI output filter for Rust <c>cargo</c> (<c>build</c>, <c>test</c>, <c>check</c>, <c>clippy</c>).
/// </summary>
public sealed partial class CargoCliFilter : ICliOutputFilter
{
    private static readonly HashSet<string> SupportedCargoSubcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "test", "check", "clippy"
    };

    [GeneratedRegex(@"^\s*(?:Compiling|Downloading|Downloaded|Updating|Blocking|Fresh|Checking|Finished|Running|Doc-tests)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CargoBoilerplateRegex();

    [GeneratedRegex(@"^error(?:\[[A-Za-z0-9]+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CargoErrorHeaderRegex();

    [GeneratedRegex(@"^warning(?:\[[A-Za-z0-9]+\])?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CargoWarningHeaderRegex();

    [GeneratedRegex(@"^-->\s*(?<loc>.+)$")]
    private static partial Regex CargoLocationPointerRegex();

    [GeneratedRegex(@"^thread\s+'[^']+'\s+panicked\s+at\s+", RegexOptions.IgnoreCase)]
    private static partial Regex CargoThreadPanicRegex();

    public bool CanHandle(string command)
    {
        foreach (var (exe, sub, _) in CliOutputFilterHelpers.EnumerateCommands(command))
        {
            if ((exe.Equals("cargo", StringComparison.OrdinalIgnoreCase) ||
                 exe.Equals("cargo.exe", StringComparison.OrdinalIgnoreCase)) &&
                sub != null &&
                SupportedCargoSubcommands.Contains(sub))
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
        foreach (var rawLine in CliOutputFilterHelpers.SplitLines(rawOutput))
        {
            ProcessLine(rawLine.Trim(), workspaceRoot, options, state);
        }

        state.FlushPending(null);

        if (!state.SawRecognizedSignal)
        {
            return rawOutput.TrimEnd();
        }

        return CliOutputFilterHelpers.FormatCompilerDiagnosticsOutput(
            state.Errors,
            state.Warnings,
            state.TestFailures,
            options,
            state.ExplicitOutcomeSummary,
            assumedSuccess: state.Errors.Count == 0 && state.TestFailures.Count == 0 && !state.SawTestResultFailed);
    }

    private static void ProcessLine(string trimmed, string workspaceRoot, CommandFilterOptions options, ParseState state)
    {
        if (string.IsNullOrEmpty(trimmed))
        {
            state.InStdoutBlock = false;
            state.JustSawPanicLine = false;
            return;
        }

        if (trimmed.Equals("[STDERR]", StringComparison.Ordinal))
        {
            return;
        }

        if (CargoBoilerplateRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            state.FlushPending(null);
            return;
        }

        // Skip Cargo summary warning counts like "warning: `crate` (lib) generated 3 warnings".
        if (trimmed.StartsWith("warning:", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Contains("generated ", StringComparison.OrdinalIgnoreCase) &&
            trimmed.Contains(" warning", StringComparison.OrdinalIgnoreCase))
        {
            state.SawRecognizedSignal = true;
            return;
        }

        if (CargoErrorHeaderRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            state.FlushPending(null);
            if (!trimmed.StartsWith("error: could not compile", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("error: aborting due to", StringComparison.OrdinalIgnoreCase))
            {
                state.PendingType = "error";
                state.PendingHeader = RelativizeIfEnabled(trimmed, workspaceRoot, options);
            }
            return;
        }

        if (CargoWarningHeaderRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            state.FlushPending(null);
            state.PendingType = "warning";
            state.PendingHeader = RelativizeIfEnabled(trimmed, workspaceRoot, options);
            return;
        }

        var locMatch = CargoLocationPointerRegex().Match(trimmed);
        if (locMatch.Success && state.PendingHeader != null)
        {
            state.SawRecognizedSignal = true;
            var loc = locMatch.Groups["loc"].Value.Trim();
            if (options.RelativizePaths)
            {
                loc = CliOutputPathRelativizer.RelativizePath(loc, workspaceRoot);
            }
            state.FlushPending(loc);
            return;
        }

        ProcessTestLine(trimmed, workspaceRoot, options, state);
    }

    private static void ProcessTestLine(string trimmed, string workspaceRoot, CommandFilterOptions options, ParseState state)
    {
        if (trimmed.StartsWith("---- ", StringComparison.Ordinal) &&
            trimmed.EndsWith(" stdout ----", StringComparison.Ordinal))
        {
            state.SawRecognizedSignal = true;
            state.InStdoutBlock = true;
            state.JustSawPanicLine = false;
            return;
        }

        if (trimmed.Equals("failures:", StringComparison.Ordinal))
        {
            state.SawRecognizedSignal = true;
            state.InStdoutBlock = false;
            state.JustSawPanicLine = false;
            return;
        }

        if (trimmed.StartsWith("test result:", StringComparison.OrdinalIgnoreCase))
        {
            state.SawRecognizedSignal = true;
            state.InStdoutBlock = false;
            state.JustSawPanicLine = false;
            if (trimmed.StartsWith("test result: FAILED", StringComparison.OrdinalIgnoreCase))
            {
                state.SawTestResultFailed = true;
            }
            if (state.TestFailures.Count > 0)
            {
                state.ExplicitOutcomeSummary = trimmed;
            }
            return;
        }

        if (trimmed.StartsWith("test ", StringComparison.Ordinal) &&
            (trimmed.EndsWith("... FAILED", StringComparison.Ordinal) ||
             trimmed.EndsWith("... ok", StringComparison.Ordinal) ||
             trimmed.EndsWith("... ignored", StringComparison.Ordinal)))
        {
            state.SawRecognizedSignal = true;
            if (trimmed.EndsWith("... FAILED", StringComparison.Ordinal))
            {
                CliOutputFilterHelpers.AddUnique(state.TestFailures, state.SeenFailures, trimmed);
            }
            return;
        }

        if (CargoThreadPanicRegex().IsMatch(trimmed))
        {
            state.SawRecognizedSignal = true;
            state.JustSawPanicLine = true;
            CliOutputFilterHelpers.AddUnique(state.TestFailures, state.SeenFailures, RelativizeIfEnabled(trimmed, workspaceRoot, options));
            return;
        }

        if (state.InStdoutBlock && IsCargoAssertionDetail(trimmed, state.JustSawPanicLine))
        {
            CliOutputFilterHelpers.AddUnique(state.TestFailures, state.SeenFailures, RelativizeIfEnabled(trimmed, workspaceRoot, options));
        }
    }

    private static bool IsCargoAssertionDetail(string trimmed, bool justSawPanicLine) =>
        (trimmed.StartsWith("assertion", StringComparison.OrdinalIgnoreCase) &&
         trimmed.Contains("failed", StringComparison.OrdinalIgnoreCase)) ||
        trimmed.StartsWith("left:", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("right:", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("panicked at", StringComparison.OrdinalIgnoreCase) ||
        (justSawPanicLine && !trimmed.StartsWith("note:", StringComparison.OrdinalIgnoreCase));

    private static string RelativizeIfEnabled(string line, string workspaceRoot, CommandFilterOptions options) =>
        options.RelativizePaths ? CliOutputPathRelativizer.RelativizeLinePaths(line, workspaceRoot) : line;

    private sealed class ParseState
    {
        public List<string> Errors { get; } = [];
        public HashSet<string> SeenErrors { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
        public HashSet<string> SeenWarnings { get; } = new(StringComparer.Ordinal);
        public List<string> TestFailures { get; } = [];
        public HashSet<string> SeenFailures { get; } = new(StringComparer.Ordinal);
        public string? PendingType { get; set; }
        public string? PendingHeader { get; set; }
        public string? ExplicitOutcomeSummary { get; set; }
        public bool SawRecognizedSignal { get; set; }
        public bool SawTestResultFailed { get; set; }
        public bool InStdoutBlock { get; set; }
        public bool JustSawPanicLine { get; set; }

        public void FlushPending(string? location)
        {
            if (PendingHeader == null)
            {
                return;
            }

            var combined = string.IsNullOrWhiteSpace(location)
                ? PendingHeader
                : $"{location}: {PendingHeader}";
            if (PendingType == "error")
            {
                CliOutputFilterHelpers.AddUnique(Errors, SeenErrors, combined);
            }
            else if (PendingType == "warning")
            {
                CliOutputFilterHelpers.AddUnique(Warnings, SeenWarnings, combined);
            }

            PendingType = null;
            PendingHeader = null;
        }
    }
}
