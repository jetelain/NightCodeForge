using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Metric result representing covered lines, total valid lines, and calculated line rate.
/// </summary>
public record CoverageMetric(long CoveredLines, long ValidLines, double LineRate);

/// <summary>
/// Comprehensive parser and multi-project aggregator for Coverlet code coverage formats:
/// - Cobertura XML (dotnet test --collect:"XPlat Code Coverage")
/// - OpenCover XML (/p:CoverletOutputFormat=opencover)
/// - Coverlet JSON (coverage.json)
/// - Coverlet console summary table stdout fallback
/// </summary>
public static partial class CoverletCoverageParser
{
    [GeneratedRegex(@"(?im)^\s*\|?\s*Total\s*\|\s*([0-9]+(?:[\.,][0-9]+)?)\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex CoverletTableTotalRegex();

    /// <summary>
    /// Parses Cobertura coverage XML and extracts valid/covered lines and line-rate.
    /// </summary>
    public static CoverageMetric? ParseCobertura(string xmlContent, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var root = doc.Root;
            if (root == null) return null;

            var linesCoveredAttr = root.Attribute("lines-covered");
            var linesValidAttr = root.Attribute("lines-valid");
            var lineRateAttr = root.Attribute("line-rate");

            if (linesCoveredAttr != null && linesValidAttr != null &&
                long.TryParse(linesCoveredAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered) &&
                long.TryParse(linesValidAttr.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valid) &&
                valid > 0)
            {
                var rate = (double)covered / valid;
                return new CoverageMetric(covered, valid, rate);
            }

            // Fallback: aggregate packages/package
            var packages = doc.Descendants().Where(e => e.Name.LocalName == "package").ToList();
            if (packages.Count > 0)
            {
                long pkgCovered = 0;
                long pkgValid = 0;
                foreach (var pkg in packages)
                {
                    var c = pkg.Attribute("lines-covered");
                    var v = pkg.Attribute("lines-valid");
                    if (c != null && v != null &&
                        long.TryParse(c.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cv) &&
                        long.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var vv))
                    {
                        pkgCovered += cv;
                        pkgValid += vv;
                    }
                }

                if (pkgValid > 0)
                {
                    return new CoverageMetric(pkgCovered, pkgValid, (double)pkgCovered / pkgValid);
                }
            }

            // Fallback: line-rate on root attribute
            if (lineRateAttr != null && double.TryParse(lineRateAttr.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var rateVal))
            {
                return new CoverageMetric(0, 0, rateVal);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Cobertura coverage XML");
        }

        return null;
    }

    /// <summary>
    /// Parses OpenCover coverage XML and extracts valid/covered sequence points.
    /// </summary>
    public static CoverageMetric? ParseOpenCover(string xmlContent, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(xmlContent)) return null;

        try
        {
            var doc = XDocument.Parse(xmlContent);
            var summaries = doc.Descendants().Where(e => e.Name.LocalName == "Summary").ToList();
            if (summaries.Count == 0) return null;

            // Check root-level summary first
            var rootSummary = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Summary");
            if (rootSummary != null)
            {
                var numSeq = rootSummary.Attribute("numSequencePoints");
                var visitedSeq = rootSummary.Attribute("visitedSequencePoints");
                if (numSeq != null && visitedSeq != null &&
                    long.TryParse(numSeq.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valid) &&
                    long.TryParse(visitedSeq.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered) &&
                    valid > 0)
                {
                    return new CoverageMetric(covered, valid, (double)covered / valid);
                }

                var seqCoverage = rootSummary.Attribute("sequenceCoverage");
                if (seqCoverage != null && double.TryParse(seqCoverage.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
                {
                    var rate = pct > 1.0 ? pct / 100.0 : pct;
                    return new CoverageMetric(0, 0, rate);
                }
            }

            // Sum module-level summaries (avoiding class and method child summaries)
            long totalValid = 0;
            long totalCovered = 0;
            var moduleSummaries = doc.Descendants()
                .Where(e => e.Name.LocalName == "Module")
                .Select(m => m.Elements().FirstOrDefault(e => e.Name.LocalName == "Summary"))
                .Where(s => s != null);

            foreach (var summary in moduleSummaries)
            {
                var numSeq = summary!.Attribute("numSequencePoints");
                var visitedSeq = summary.Attribute("visitedSequencePoints");
                if (numSeq != null && visitedSeq != null &&
                    long.TryParse(numSeq.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valid) &&
                    long.TryParse(visitedSeq.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var covered) &&
                    valid > 0)
                {
                    totalValid += valid;
                    totalCovered += covered;
                }
            }

            if (totalValid > 0)
            {
                return new CoverageMetric(totalCovered, totalValid, (double)totalCovered / totalValid);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse OpenCover coverage XML");
        }

        return null;
    }

    /// <summary>
    /// Parses Coverlet JSON output by traversing module/class/method line dictionaries.
    /// </summary>
    public static CoverageMetric? ParseCoverletJson(string jsonContent, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(jsonContent)) return null;

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            long covered = 0;
            long valid = 0;

            TraverseJson(doc.RootElement, ref covered, ref valid);

            if (valid > 0)
            {
                return new CoverageMetric(covered, valid, (double)covered / valid);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Coverlet coverage JSON");
        }

        return null;
    }

    private static void TraverseJson(JsonElement element, ref long covered, ref long valid)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, "Lines", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var line in prop.Value.EnumerateObject())
                    {
                        valid++;
                        if (line.Value.ValueKind == JsonValueKind.Number && line.Value.TryGetInt64(out var hits) && hits > 0)
                        {
                            covered++;
                        }
                    }
                }
                else
                {
                    TraverseJson(prop.Value, ref covered, ref valid);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                TraverseJson(item, ref covered, ref valid);
            }
        }
    }

    /// <summary>
    /// Parses Coverlet console summary table stdout fallback when files are absent.
    /// </summary>
    public static double? ParseConsoleOutput(string consoleOutput, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(consoleOutput)) return null;

        try
        {
            var matches = CoverletTableTotalRegex().Matches(consoleOutput);
            if (matches.Count > 0)
            {
                var rates = new List<double>();
                foreach (Match match in matches)
                {
                    var val = match.Groups[1].Value.Replace(',', '.');
                    if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent))
                    {
                        rates.Add(percent / 100.0);
                    }
                }

                if (rates.Count > 0)
                {
                    return rates.Average();
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse Coverlet console summary table");
        }

        return null;
    }

    /// <summary>
    /// Scans fresh coverage artifacts in <paramref name="workDir"/> and aggregates lines across all projects.
    /// If no fresh files are found, falls back to parsing console stdout.
    /// </summary>
    public static async Task<double?> AggregateCoverageAsync(
        string workDir,
        DateTime stageStartTimeUtc,
        string consoleOutput,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        long totalCovered = 0;
        long totalValid = 0;
        double? fallbackRateWithoutValidLines = null;
        int scannedCount = 0;

        if (Directory.Exists(workDir))
        {
            try
            {
                // Allow up to 10 seconds of clock skew between execution environment / mounts and host
                var minWriteTime = stageStartTimeUtc.AddSeconds(-10);
                var candidateFiles = Directory.EnumerateFiles(workDir, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var norm = f.Replace('\\', '/');
                        if (norm.Contains("/.git/") || norm.Contains("/node_modules/") || norm.Contains("/obj/"))
                        {
                            return false;
                        }

                        var ext = Path.GetExtension(f);
                        if (!string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                        {
                            return false;
                        }

                        var name = Path.GetFileName(f);
                        var isTestResults = norm.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase);

                        return name.Equals("coverage.cobertura.xml", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("coverage.opencover.xml", StringComparison.OrdinalIgnoreCase) ||
                               name.EndsWith(".opencover.xml", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("coverage.json", StringComparison.OrdinalIgnoreCase) ||
                               name.Equals("coverage.xml", StringComparison.OrdinalIgnoreCase) ||
                               (isTestResults && (name.Contains("coverage", StringComparison.OrdinalIgnoreCase) ||
                                                  name.Contains("cobertura", StringComparison.OrdinalIgnoreCase) ||
                                                  name.Contains("opencover", StringComparison.OrdinalIgnoreCase)));
                    })
                    .Where(f =>
                    {
                        try
                        {
                            return File.GetLastWriteTimeUtc(f) >= minWriteTime;
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to read LastWriteTimeUtc for candidate coverage file '{FilePath}'", f);
                            return false;
                        }
                    })
                    .ToList();

                // Group files by (directory, report stem) to avoid double-counting if multiple formats were emitted
                // for the same project, while ensuring distinct projects sharing a directory are all processed.
                var filesToProcess = candidateFiles
                    .GroupBy(f => (Directory: Path.GetDirectoryName(f) ?? string.Empty, Stem: GetReportStem(f).ToLowerInvariant()))
                    .Select(g =>
                    {
                        // Preference order: Cobertura > OpenCover > JSON
                        var cob = g.FirstOrDefault(f => f.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase) ||
                                                        f.EndsWith("coverage.xml", StringComparison.OrdinalIgnoreCase));
                        if (cob != null) return cob;

                        var oc = g.FirstOrDefault(f => f.EndsWith(".opencover.xml", StringComparison.OrdinalIgnoreCase));
                        if (oc != null) return oc;

                        return g.First();
                    })
                    .ToList();

                foreach (var file in filesToProcess)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    try
                    {
                        var content = await File.ReadAllTextAsync(file, cancellationToken);
                        CoverageMetric? metric = null;

                        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                        {
                            metric = ParseCoverletJson(content, logger);
                        }
                        else if (string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            if (content.Contains("<CoverageSession", StringComparison.OrdinalIgnoreCase))
                            {
                                metric = ParseOpenCover(content, logger);
                            }
                            else if (content.Contains("<coverage", StringComparison.OrdinalIgnoreCase))
                            {
                                metric = ParseCobertura(content, logger);
                            }
                            else
                            {
                                metric = ParseCobertura(content, logger) ?? ParseOpenCover(content, logger);
                            }
                        }

                        if (metric != null)
                        {
                            if (metric.ValidLines > 0)
                            {
                                totalCovered += metric.CoveredLines;
                                totalValid += metric.ValidLines;
                                scannedCount++;
                            }
                            else if (!fallbackRateWithoutValidLines.HasValue)
                            {
                                fallbackRateWithoutValidLines = metric.LineRate;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to read or parse coverage file '{FilePath}'", file);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed while scanning for coverage files in '{WorkDir}'", workDir);
            }
        }

        if (totalValid > 0)
        {
            var aggregateRate = (double)totalCovered / totalValid;
            logger?.LogInformation(
                "Aggregated code coverage across {Count} fresh report(s): {Rate:P2} ({Covered}/{Valid} lines)",
                scannedCount, aggregateRate, totalCovered, totalValid);
            return aggregateRate;
        }

        if (fallbackRateWithoutValidLines.HasValue)
        {
            return fallbackRateWithoutValidLines.Value;
        }

        var consoleRate = ParseConsoleOutput(consoleOutput, logger);
        if (consoleRate.HasValue)
        {
            logger?.LogInformation("Parsed code coverage from runner console output: {Rate:P2}", consoleRate.Value);
            return consoleRate.Value;
        }

        return null;
    }

    private static string GetReportStem(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".cobertura.xml", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".cobertura.xml".Length];
        if (fileName.EndsWith(".opencover.xml", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".opencover.xml".Length];
        if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".xml".Length];
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".json".Length];
        return fileName;
    }
}
