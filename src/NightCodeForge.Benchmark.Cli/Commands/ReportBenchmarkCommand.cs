using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Reporting;

namespace NightCodeForge.Benchmark.Cli.Commands;

/// <summary>
/// Executes <c>ncf-bench report</c>, rendering single-run leaderboards or cross-version regression comparisons.
/// </summary>
public sealed class ReportBenchmarkCommand
{
    public async Task<int> ExecuteAsync(
        ReportCommandOptions options,
        Action<string> writeLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeLine);

        var loadedRuns = new List<LoadedBenchmarkRun>(options.ResultDirectories.Count);
        foreach (var dir in options.ResultDirectories)
        {
            var run = await BenchmarkReportLoader.LoadAsync(dir, cancellationToken).ConfigureAwait(false);
            loadedRuns.Add(run);
        }

        string formatted;
        if (!string.IsNullOrWhiteSpace(options.BaselineDirectory))
        {
            var baselineFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.BaselineDirectory.Trim()));
            var existingBaseline = loadedRuns.FirstOrDefault(r =>
                string.Equals(Path.TrimEndingDirectorySeparator(r.SourceDirectory), baselineFull, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.ResultsFilePath, baselineFull, StringComparison.OrdinalIgnoreCase));

            if (existingBaseline == null)
            {
                existingBaseline = await BenchmarkReportLoader.LoadAsync(options.BaselineDirectory, cancellationToken).ConfigureAwait(false);
                loadedRuns.Insert(0, existingBaseline);
            }

            formatted = BenchmarkReportFormatter.FormatComparison(loadedRuns, existingBaseline, options.Format);
        }
        else if (loadedRuns.Count > 1)
        {
            formatted = BenchmarkReportFormatter.FormatComparison(loadedRuns, loadedRuns[0], options.Format);
        }
        else
        {
            formatted = BenchmarkReportFormatter.FormatSingleRun(loadedRuns[0].Report, options.Format);
        }

        writeLine(formatted);
        return BenchmarkCliConstants.ExitSuccess;
    }
}
