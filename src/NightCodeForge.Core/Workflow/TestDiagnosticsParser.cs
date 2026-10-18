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
                            failedNames.Add(testName);
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
}
