using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class BenchmarkCliArgumentParserTests
{
    [Fact]
    public void ParseRunOptions_AllFlagsSeparateAndInline_ParsesCorrectly()
    {
        var args = new[]
        {
            "--dataset", "datasets/swe.jsonl",
            "--suite=swe-csharp",
            "--models", "qwen3:8b,deepseek-coder:6.7b",
            "--provider", "ollama-router",
            "--endpoint=http://localhost:11435",
            "--category", "code-review",
            "--filter", "inst-1,inst-2",
            "--filter=inst-3",
            "--limit", "5",
            "--no-verify",
            "--no-skills",
            "--hints",
            "--timeout=900",
            "--concurrency", "2",
            "--resume", "prev-run",
            "--judge-model", "gpt-4o-mini",
            "--judge-provider", "openai",
            "--judge-endpoint", "https://api.openai.com/v1",
            "--out", "out-dir"
        };

        var options = BenchmarkCliArgumentParser.ParseRunOptions(args, out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("datasets/swe.jsonl", options.DatasetPath);
        Assert.Equal("swe-csharp", options.SuiteName);
        Assert.Equal("qwen3:8b,deepseek-coder:6.7b", options.ModelsCsv);
        Assert.Null(options.ConfigPath);
        Assert.Equal(LlmProviderType.OllamaRouter, options.Provider);
        Assert.Equal("http://localhost:11435", options.Endpoint);
        Assert.Equal(BenchmarkProblemCategory.CodeReview, options.Category);
        Assert.Equal(new[] { "inst-1", "inst-2", "inst-3" }, options.FilterInstanceIds);
        Assert.Equal(5, options.Limit);
        Assert.False(options.VerifyWithTests);
        Assert.False(options.EnableStackSkills);
        Assert.True(options.IncludeHints);
        Assert.Equal(900, options.TimeoutSeconds);
        Assert.Equal(2, options.Concurrency);
        Assert.Equal("prev-run", options.ResumeFromDir);
        Assert.Equal("gpt-4o-mini", options.JudgeModel);
        Assert.Equal(LlmProviderType.OpenAiCompatible, options.JudgeProvider);
        Assert.Equal("https://api.openai.com/v1", options.JudgeEndpoint);
        Assert.Equal("out-dir", options.OutputDirectory);
    }

    [Fact]
    public void ParseRunOptions_Defaults_PreserveSequentialExecutionAndVerifyTrue()
    {
        var args = new[]
        {
            "--dataset", "tasks.json",
            "--config", "candidates.json",
            "--provider", "openai",
            "--category", "task-review",
            "--verify",
            "--skills"
        };

        var options = BenchmarkCliArgumentParser.ParseRunOptions(args, out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("candidates.json", options.ConfigPath);
        Assert.Equal(LlmProviderType.OpenAiCompatible, options.Provider);
        Assert.Equal(BenchmarkProblemCategory.TaskReview, options.Category);
        Assert.Equal(1, options.Concurrency);
        Assert.True(options.VerifyWithTests);
        Assert.True(options.EnableStackSkills);
        Assert.False(options.IncludeHints);
    }

    [Theory]
    [InlineData(new[] { "--models", "qwen3:8b" }, "--dataset")]
    [InlineData(new[] { "--dataset", "d.jsonl" }, "Either '--models")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--config", "c.json" }, "mutually exclusive")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--provider", "bad" }, "--provider")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--provider", "ollarouter" }, "--provider")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--category", "unknown" }, "--category")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--limit", "0" }, "--limit")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--concurrency", "-1" }, "--concurrency")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--timeout", "abc" }, "--timeout")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--endpoint", "not-a-url" }, "--endpoint")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--endpoint", "ftp://host" }, "http://")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--endpoint", "file:///tmp/x" }, "http://")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--endpoint", @"C:\foo" }, "http://")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--judge-model", "m", "--judge-endpoint", "ftp://host" }, "--judge-endpoint")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--judge-model", "m", "--judge-endpoint", "file:///tmp/x" }, "--judge-endpoint")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--judge-model", "m", "--judge-endpoint", @"C:\foo" }, "--judge-endpoint")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--judge-provider", "openai" }, "--judge-model")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--filter", " , , " }, "--filter")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", " , , " }, "--models")]
    [InlineData(new[] { "--dataset", "--models", "m1" }, "Missing value for option '--dataset'")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--out", "--no-verify" }, "Missing value for option '--out'")]
    [InlineData(new[] { "--dataset", "d.jsonl", "--models", "a", "--unknown-flag" }, "Unrecognized option")]
    public void ParseRunOptions_InvalidInputs_ReturnExpectedError(string[] args, string expectedSubstring)
    {
        var options = BenchmarkCliArgumentParser.ParseRunOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ollama-router", LlmProviderType.OllamaRouter)]
    [InlineData("ollamarouter", LlmProviderType.OllamaRouter)]
    [InlineData("ollama", LlmProviderType.Ollama)]
    [InlineData("openai", LlmProviderType.OpenAiCompatible)]
    public void TryParseProvider_ValidAliases_ReturnsExpectedProvider(string raw, LlmProviderType expected)
    {
        Assert.True(BenchmarkCliArgumentParser.TryParseProvider(raw, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseReportOptions_MultipleDirectoriesWithBaselineAndFormat_ParsesCorrectly()
    {
        var args = new[] { "run-v1", "run-v2", "--baseline", "run-v0", "--format=ascii" };

        var options = BenchmarkCliArgumentParser.ParseReportOptions(args, out var error);

        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(new[] { "run-v1", "run-v2" }, options.ResultDirectories);
        Assert.Equal("run-v0", options.BaselineDirectory);
        Assert.Equal(ReportOutputFormat.Ascii, options.Format);
    }

    [Theory]
    [InlineData(new string[0], "At least one results directory")]
    [InlineData(new[] { "run-1", "--format", "xml" }, "--format")]
    [InlineData(new[] { "run-1", "--baseline", "--format", "ascii" }, "Missing value for option '--baseline'")]
    [InlineData(new[] { "run-1", "--bogus" }, "Unrecognized option")]
    public void ParseReportOptions_InvalidInputs_ReturnExpectedError(string[] args, string expectedSubstring)
    {
        var options = BenchmarkCliArgumentParser.ParseReportOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error, StringComparison.OrdinalIgnoreCase);
    }
}
