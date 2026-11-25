using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Cli;

/// <summary>
/// Output format for comparative benchmark reports.
/// </summary>
public enum ReportOutputFormat
{
    Markdown = 0,
    Ascii = 1,
    Json = 2
}

/// <summary>
/// Parsed CLI options for <c>ncf-bench run</c>.
/// </summary>
public sealed class RunCommandOptions
{
    public string DatasetPath { get; set; } = string.Empty;
    public string? ManifestPath { get; set; }
    public string? SuiteName { get; set; }
    public string? ModelsCsv { get; set; }
    public string? ConfigPath { get; set; }
    public LlmProviderType Provider { get; set; } = LlmProviderType.Ollama;
    public string? Endpoint { get; set; }
    public BenchmarkProblemCategory? Category { get; set; }
    public List<string> FilterInstanceIds { get; } = [];
    public int? Limit { get; set; }
    public bool VerifyWithTests { get; set; } = true;
    public bool? EnableStackSkills { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int Concurrency { get; set; } = 1;
    public bool IncludeHints { get; set; }
    public string? ResumeFromDir { get; set; }
    public string? JudgeModel { get; set; }
    public LlmProviderType? JudgeProvider { get; set; }
    public string? JudgeEndpoint { get; set; }
    public string? OutputDirectory { get; set; }
}

/// <summary>
/// Parsed CLI options for <c>ncf-bench report</c>.
/// </summary>
public sealed class ReportCommandOptions
{
    public List<string> ResultDirectories { get; } = [];
    public string? BaselineDirectory { get; set; }
    public ReportOutputFormat Format { get; set; } = ReportOutputFormat.Markdown;
}

/// <summary>
/// Parsed CLI options for <c>ncf-bench dataset import</c>.
/// </summary>
public sealed class DatasetImportCommandOptions
{
    public BenchmarkDatasetSource Source { get; set; }
    public string InputPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
}

/// <summary>
/// Parsed CLI options for <c>ncf-bench dataset validate</c>.
/// </summary>
public sealed class DatasetValidateCommandOptions
{
    public string DatasetPath { get; set; } = string.Empty;
    public string? ManifestPath { get; set; }
    public BenchmarkProblemCategory? Category { get; set; }
    public List<string> FilterInstanceIds { get; } = [];
    public int? Limit { get; set; }
    public bool VerifyWithTests { get; set; } = true;
}
