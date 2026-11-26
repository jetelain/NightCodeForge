using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Commands;

/// <summary>
/// Executes <c>ncf-bench dataset validate</c>, running pre-flight verification across dataset instances
/// and reporting pass/fail diagnostics.
/// </summary>
public sealed class DatasetValidateCommand
{
    private readonly IBenchmarkDatasetValidator _validator;

    public DatasetValidateCommand(IBenchmarkDatasetValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<int> ExecuteAsync(
        DatasetValidateCommandOptions options,
        Action<string> writeLine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writeLine);

        if (string.IsNullOrWhiteSpace(options.DatasetPath))
        {
            throw new BenchmarkDatasetException("Dataset path cannot be empty.", options.DatasetPath);
        }

        writeLine($"[{BenchmarkCliConstants.ToolName}] Validating dataset '{Path.GetFullPath(options.DatasetPath)}'...");

        var filter = options.FilterInstanceIds.Count > 0 ? options.FilterInstanceIds.ToList() : null;
        var report = await _validator.ValidateAsync(
            options.DatasetPath,
            options.Category,
            filter,
            options.Limit,
            options.VerifyWithTests,
            writeLine,
            options.ManifestPath,
            options.Concurrency,
            cancellationToken).ConfigureAwait(false);

        writeLine($"[{BenchmarkCliConstants.ToolName}] Validation complete: {report.PassedCount}/{report.TotalCount} passed, {report.FailedCount} failed.");
        return report.AllPassed ? BenchmarkCliConstants.ExitSuccess : BenchmarkCliConstants.ExitFailure;
    }
}
