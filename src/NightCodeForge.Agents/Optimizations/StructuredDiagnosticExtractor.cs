using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Optimizations;

public record CompilerDiagnostic(
    string File,
    int Line,
    int Column,
    string Code,
    string Message);

public record TestFailure(
    string TestName,
    string? ErrorMessage,
    string? StackTraceLine);

public record DiagnosticSummary(
    IReadOnlyList<CompilerDiagnostic> CompilerErrors,
    IReadOnlyList<TestFailure> TestFailures,
    string FormattedMarkdown);

/// <summary>
/// Extracts structured compiler errors (CS\d+) and test failures from verbose CLI stdout/stderr,
/// eliminating "lost in the middle" syndrome.
/// </summary>
public partial class StructuredDiagnosticExtractor
{
    [GeneratedRegex(@"(?<file>[^\r\n()]+\.cs)\((?<line>\d+),(?<col>\d+)\):\s*error\s*(?<code>CS\d+):\s*(?<msg>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoslynErrorRegex();

    [GeneratedRegex(@"(?<file>[^\r\n():]+\.tsx?)(?:\((?<line>\d+),(?<col>\d+)\)|:(?<line>\d+):(?<col>\d+))(?::|\s*-)?\s*error\s*(?<code>TS\d+):\s*(?<msg>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TypeScriptErrorRegex();

    [GeneratedRegex(@"error\[(?<code>E\d+)\]:\s*(?<msg>[^\r\n]+)[\r\n]+\s*-->\s*(?<file>[^:\r\n]+):(?<line>\d+):(?<col>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RustErrorRegex();

    [GeneratedRegex(@"File ""(?<file>[^""]+)"", line (?<line>\d+).*?[\r\n]+(?<code>\w*Error|\w*Exception):\s*(?<msg>[^\r\n]+)", RegexOptions.Singleline)]
    private static partial Regex PythonErrorRegex();

    [GeneratedRegex(@"(?<file>[^\s:]+\.go):(?<line>\d+):(?<col>\d+):\s*(?<msg>[^\r\n]+)")]
    private static partial Regex GoErrorRegex();

    [GeneratedRegex(@"(?:^|\r?\n)\s*Failed\s+(?<test>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_<>]+)+(?:\([^\r\n]*\))?)[^\r\n]*(?:[\r\n]+(?:\s*Error Message:[\r\n]+\s*(?<msg>[^\r\n]+))?(?:[\r\n]+\s*Stack Trace:[\r\n]+\s*(?<stack>[^\r\n]+))?)?")]
    private static partial Regex TestFailedRegex();

    public DiagnosticSummary ExtractDiagnostics(string rawBuildOrTestOutput, LocalOptimizationFlags? flags = null)
    {
        if (flags != null && !flags.StructuredDiagnosticExtraction)
        {
            return new DiagnosticSummary(Array.Empty<CompilerDiagnostic>(), Array.Empty<TestFailure>(), rawBuildOrTestOutput);
        }

        if (string.IsNullOrWhiteSpace(rawBuildOrTestOutput))
        {
            return new DiagnosticSummary(Array.Empty<CompilerDiagnostic>(), Array.Empty<TestFailure>(), "No output.");
        }

        var errors = new List<CompilerDiagnostic>();

        // 1. Roslyn (C#)
        foreach (Match m in RoslynErrorRegex().Matches(rawBuildOrTestOutput))
        {
            var msg = m.Groups["msg"].Value.Trim();
            var projIdx = msg.LastIndexOf(" [");
            if (projIdx > 0 && msg.EndsWith(']'))
            {
                msg = msg[..projIdx].Trim();
            }

            errors.Add(new CompilerDiagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.Parse(m.Groups["line"].Value),
                Column: int.Parse(m.Groups["col"].Value),
                Code: m.Groups["code"].Value.Trim(),
                Message: msg
            ));
        }

        // 2. TypeScript
        foreach (Match m in TypeScriptErrorRegex().Matches(rawBuildOrTestOutput))
        {
            errors.Add(new CompilerDiagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.Parse(m.Groups["line"].Value),
                Column: int.Parse(m.Groups["col"].Value),
                Code: m.Groups["code"].Value.Trim(),
                Message: m.Groups["msg"].Value.Trim()
            ));
        }

        // 3. Rust
        foreach (Match m in RustErrorRegex().Matches(rawBuildOrTestOutput))
        {
            errors.Add(new CompilerDiagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.Parse(m.Groups["line"].Value),
                Column: int.Parse(m.Groups["col"].Value),
                Code: m.Groups["code"].Value.Trim(),
                Message: m.Groups["msg"].Value.Trim()
            ));
        }

        // 4. Python
        foreach (Match m in PythonErrorRegex().Matches(rawBuildOrTestOutput))
        {
            errors.Add(new CompilerDiagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.Parse(m.Groups["line"].Value),
                Column: 1,
                Code: m.Groups["code"].Value.Trim(),
                Message: m.Groups["msg"].Value.Trim()
            ));
        }

        // 5. Go
        foreach (Match m in GoErrorRegex().Matches(rawBuildOrTestOutput))
        {
            errors.Add(new CompilerDiagnostic(
                File: m.Groups["file"].Value.Trim(),
                Line: int.Parse(m.Groups["line"].Value),
                Column: int.Parse(m.Groups["col"].Value),
                Code: "GO",
                Message: m.Groups["msg"].Value.Trim()
            ));
        }

        var testFailures = new List<TestFailure>();
        foreach (Match m in TestFailedRegex().Matches(rawBuildOrTestOutput))
        {
            testFailures.Add(new TestFailure(
                TestName: m.Groups["test"].Value.Trim(),
                ErrorMessage: m.Groups["msg"].Success ? m.Groups["msg"].Value.Trim() : null,
                StackTraceLine: m.Groups["stack"].Success ? m.Groups["stack"].Value.Trim() : null
            ));
        }

        if (errors.Count == 0 && testFailures.Count == 0)
        {
            // If no structured errors detected, return trimmed original or success note
            return new DiagnosticSummary(errors, testFailures, rawBuildOrTestOutput);
        }

        var sb = new StringBuilder();
        if (errors.Count > 0)
        {
            sb.AppendLine($"❌ Build Failed ({errors.Count} Fatal Error{(errors.Count > 1 ? "s" : "")}):");
            foreach (var err in errors)
            {
                sb.AppendLine($"- File: {err.File} (Line {err.Line}, Col {err.Column})");
                sb.AppendLine($"  Code: {err.Code}");
                sb.AppendLine($"  Error: {err.Message}");
            }
        }

        if (testFailures.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"❌ Test Run Failed ({testFailures.Count} Failed Test{(testFailures.Count > 1 ? "s" : "")}):");
            foreach (var t in testFailures)
            {
                sb.AppendLine($"- Test: {t.TestName}");
                if (!string.IsNullOrEmpty(t.ErrorMessage))
                {
                    sb.AppendLine($"  Error: {t.ErrorMessage}");
                }
                if (!string.IsNullOrEmpty(t.StackTraceLine))
                {
                    sb.AppendLine($"  Location: {t.StackTraceLine}");
                }
            }
        }

        return new DiagnosticSummary(errors, testFailures, sb.ToString().TrimEnd());
    }

    public string CheckRegression(int baselineTestCount, int currentTestCount, double? baselineCoverage, double? currentCoverage)
    {
        var sb = new StringBuilder();

        if (currentTestCount < baselineTestCount)
        {
            sb.AppendLine($"❌ Test Suite Regression: Total test count dropped from {baselineTestCount} to {currentTestCount} (-{baselineTestCount - currentTestCount}). Restoring or adding tests is required; deleting tests to pass builds is prohibited.");
        }

        if (baselineCoverage.HasValue && currentCoverage.HasValue && currentCoverage.Value < baselineCoverage.Value)
        {
            double delta = baselineCoverage.Value - currentCoverage.Value;
            sb.AppendLine($"❌ Coverage Regression: Code coverage dropped from {baselineCoverage.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}% to {currentCoverage.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}% (-{delta.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%). Please add unit tests covering modified code paths.");
        }

        return sb.ToString().TrimEnd();
    }
}
