using System.Text.Json;
using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Core.Benchmarking;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class ProgramTests : IDisposable
{
    private readonly string _tempDir;

    public ProgramTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-prog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--HELP")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("run", "--help")]
    [InlineData("report", "--HELP")]
    public async Task ExecuteAsync_HelpTokens_ReturnsSuccessWithUsage(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.ExecuteAsync(args, stdout, stderr, CancellationToken.None);

        Assert.Equal(BenchmarkCliConstants.ExitSuccess, exitCode);
        Assert.Contains("USAGE:", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--VERSION")]
    [InlineData("-v")]
    [InlineData("version")]
    public async Task ExecuteAsync_VersionTokens_ReturnsSuccessWithVersion(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.ExecuteAsync(args, stdout, stderr, CancellationToken.None);

        Assert.Equal(BenchmarkCliConstants.ExitSuccess, exitCode);
        Assert.Contains(BenchmarkCliConstants.ToolName, stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_ReportWithDirectoryNamedHelp_DoesNotTriggerHelpUsage()
    {
        var helpDir = Path.Combine(_tempDir, "help");
        Directory.CreateDirectory(helpDir);
        var report = new BenchmarkRunReport
        {
            SuiteName = "help-dir-suite",
            DatasetPath = "ds.jsonl",
            Provenance = new BenchmarkRunProvenance("0.1.0", "1111111", "main", DateTimeOffset.UtcNow, LlmProviderType.Ollama, "http://localhost:11434", true, null),
            Summaries =
            [
                new BenchmarkCandidateSummary
                {
                    CandidateId = "qwen",
                    DisplayName = "Qwen",
                    ModelName = "qwen3:8b",
                    TotalInstances = 1,
                    PassedInstances = 1,
                    PassRate = 1.0
                }
            ]
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });
        await File.WriteAllTextAsync(Path.Combine(helpDir, BenchmarkPaths.ResultsFileName), json);

        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDir);
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await Program.ExecuteAsync(["report", "help"], stdout, stderr, CancellationToken.None);

            Assert.Equal(BenchmarkCliConstants.ExitSuccess, exitCode);
            Assert.Contains("Benchmark Report: help-dir-suite", stdout.ToString());
            Assert.DoesNotContain("USAGE:", stdout.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    [Theory]
    [InlineData(new[] { "unknown-cmd" }, "Unknown command")]
    [InlineData(new[] { "run", "--models", "m1" }, "--dataset")]
    [InlineData(new[] { "report" }, "At least one results directory")]
    public async Task ExecuteAsync_InvalidCommandOrOptions_ReturnsUsageError(string[] args, string expectedError)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.ExecuteAsync(args, stdout, stderr, CancellationToken.None);

        Assert.Equal(BenchmarkCliConstants.ExitUsageError, exitCode);
        Assert.Contains(expectedError, stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USAGE:", stdout.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_MissingReportDirectory_ReturnsFailureExitCode()
    {
        var missingDir = Path.Combine(_tempDir, "does-not-exist");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.ExecuteAsync(["report", missingDir], stdout, stderr, CancellationToken.None);

        Assert.Equal(BenchmarkCliConstants.ExitFailure, exitCode);
        Assert.Contains("Benchmark error:", stderr.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_CancelledToken_ReturnsFailureExitCode()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.ExecuteAsync(["report", _tempDir], stdout, stderr, cts.Token);

        Assert.Equal(BenchmarkCliConstants.ExitFailure, exitCode);
        Assert.Contains("Cancelled", stderr.ToString());
    }
}
