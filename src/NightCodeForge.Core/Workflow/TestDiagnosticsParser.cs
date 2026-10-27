using System.Globalization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Structured test diagnostic metrics parsed from TRX or runner outputs.
/// </summary>
public record TrxTestMetrics(int Total, int Passed, int Failed, IReadOnlyList<string> FailedTestNames);

/// <summary>
/// Diagnostic parser for test reports (e.g. VSTest/dotnet test TRX files).
/// </summary>
public static class TestDiagnosticsParser
{
    /// <summary>
    /// Scans fresh TRX files in <paramref name="workDir"/> and aggregates total, passed, and failed test metrics.
    /// </summary>
    public static TrxTestMetrics? ParseFreshTrxFiles(string workDir, DateTime stageStartTimeUtc, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir)) return null;

        try
        {
            var minWriteTime = stageStartTimeUtc.AddSeconds(-10);
            var trxFiles = Directory.EnumerateFiles(workDir, "*.trx", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var norm = f.Replace('\\', '/');
                    if (norm.Contains("/.git/") || norm.Contains("/node_modules/") || norm.Contains("/obj/"))
                    {
                        return false;
                    }

                    try
                    {
                        return File.GetLastWriteTimeUtc(f) >= minWriteTime;
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to read LastWriteTimeUtc for candidate TRX file '{FilePath}'", f);
                        return false;
                    }
                })
                .ToList();

            if (trxFiles.Count == 0) return null;

            int totalSum = 0;
            int passedSum = 0;
            int failedSum = 0;
            var failedNames = new List<string>();

            foreach (var trx in trxFiles)
            {
                try
                {
                    var doc = XDocument.Load(trx);
                    var counters = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Counters");
                    if (counters != null)
                    {
                        int.TryParse(counters.Attribute("total")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t);
                        int.TryParse(counters.Attribute("passed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p);
                        int.TryParse(counters.Attribute("failed")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f);
                        int.TryParse(counters.Attribute("error")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var err);
                        int.TryParse(counters.Attribute("timeout")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var to);
                        int.TryParse(counters.Attribute("aborted")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ab);
                        totalSum += t;
                        passedSum += p;
                        failedSum += (f + err + to + ab);
                    }

                    var failedResults = doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult" &&
                        (string.Equals(e.Attribute("outcome")?.Value, "Failed", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.Attribute("outcome")?.Value, "Error", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.Attribute("outcome")?.Value, "Timeout", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(e.Attribute("outcome")?.Value, "Aborted", StringComparison.OrdinalIgnoreCase)));

                    foreach (var failure in failedResults)
                    {
                        var testName = failure.Attribute("testName")?.Value;
                        if (!string.IsNullOrWhiteSpace(testName))
                        {
                            var errorInfo = failure.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
                            var message = errorInfo?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value?.Trim();
                            var stackTrace = errorInfo?.Descendants().FirstOrDefault(e => e.Name.LocalName == "StackTrace")?.Value?.Trim();

                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                var formatted = $"Failed {testName}\n  Error Message:\n   {message}";
                                if (!string.IsNullOrWhiteSpace(stackTrace))
                                {
                                    var firstStack = stackTrace.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                                    if (!string.IsNullOrWhiteSpace(firstStack))
                                    {
                                        formatted += $"\n  Stack Trace:\n   {firstStack}";
                                    }
                                }
                                failedNames.Add(formatted);
                            }
                            else
                            {
                                failedNames.Add(testName);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to parse TRX file '{FilePath}'", trx);
                }
            }

            if (totalSum > 0 || failedNames.Count > 0)
            {
                return new TrxTestMetrics(totalSum, passedSum, failedSum, failedNames);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed scanning TRX test reports in '{WorkDir}'", workDir);
        }

        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex FailureHeaderPattern =
        new(@"^\s*(?:Failed|\[FAIL\]|FAIL:|Échec|Echec)\s+([A-Za-z0-9_<>`\-]+(?:\.[A-Za-z0-9_<>`\-]+)+(?:\([^\r\n]*\))?|[A-Za-z0-9_<>`\-]+(?:\([^\r\n]*\)))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex TrailingDurationRegex =
        new(@"\s*\[\s*(?:<\s*)?\d+(?:\.\d+)?\s*(?:ms|s|m|min)?\s*\]\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Runner summary lines: "Failed!", "Passed!" or counters such as "Failed:     1".
    /// A bare "Failed: some message" (no digit after the colon) is NOT a summary.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SummaryLineRegex =
        new(@"^(?:Passed|Failed|Réussi|Échec|Echec)(?:!|:\s*\d)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Inside an "Error Message:" section, a header-looking line only starts a new failure
    /// when it carries a trailing duration (e.g. "Failed Ns.Test [12 ms]", as emitted by dotnet test)
    /// or is not indented (xunit "[FAIL] ..." / pytest "FAIL: ..." style). Indented message text is not a header.
    /// </summary>
    private static bool IsNextFailureHeader(string line, bool inErrorMessage)
    {
        var match = FailureHeaderPattern.Match(line);
        if (!match.Success) return false;
        return !inErrorMessage
            || TrailingDurationRegex.IsMatch(line.TrimEnd())
            || !char.IsWhiteSpace(line[0]);
    }

    /// <summary>
    /// Parses detailed test failure records from runner output (e.g. dotnet test / vstest / xunit / nunit),
    /// extracting test name, error message, and stack trace.
    /// </summary>
    public static List<string> ParseTestFailuresFromOutput(string output)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return result;

        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        int i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var match = FailureHeaderPattern.Match(line);
            if (match.Success)
            {
                var testName = TrailingDurationRegex.Replace(match.Groups[1].Value.Trim(), "").Trim().TrimEnd('.');

                var errorLines = new List<string>();
                var stackLines = new List<string>();
                bool inErrorMessage = false;
                bool inStackTrace = false;

                i++;
                while (i < lines.Count)
                {
                    var nextLine = lines[i];
                    var trimmed = nextLine.Trim();

                    if (IsNextFailureHeader(nextLine, inErrorMessage) ||
                        SummaryLineRegex.IsMatch(trimmed) ||
                        trimmed.StartsWith("Total tests:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Au total,", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Standard Output Messages:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Attachments:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Test Run ", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (trimmed.StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase))
                    {
                        inErrorMessage = true;
                        inStackTrace = false;
                        var afterHeader = trimmed.Substring("Error Message:".Length).Trim();
                        if (!string.IsNullOrEmpty(afterHeader))
                        {
                            errorLines.Add(afterHeader);
                        }
                    }
                    else if (trimmed.StartsWith("Stack Trace:", StringComparison.OrdinalIgnoreCase))
                    {
                        inErrorMessage = false;
                        inStackTrace = true;
                        var afterHeader = trimmed.Substring("Stack Trace:".Length).Trim();
                        if (!string.IsNullOrEmpty(afterHeader))
                        {
                            stackLines.Add(afterHeader);
                        }
                    }
                    else if (inErrorMessage)
                    {
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            errorLines.Add(trimmed);
                        }
                    }
                    else if (inStackTrace)
                    {
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            stackLines.Add(trimmed);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        if (trimmed.StartsWith("Assert.", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("Expected:", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("Actual:", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
                        {
                            errorLines.Add(trimmed);
                        }
                    }

                    i++;
                }

                var sb = new System.Text.StringBuilder();
                sb.Append("Failed ").Append(testName);
                if (errorLines.Count > 0)
                {
                    sb.Append("\n  Error Message:\n   ").Append(string.Join("\n   ", errorLines));
                }
                if (stackLines.Count > 0)
                {
                    sb.Append("\n  Stack Trace:\n   ").Append(stackLines[0]);
                }

                result.Add(sb.ToString());
                continue;
            }

            i++;
        }

        return result;
    }
}
