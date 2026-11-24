using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Tests.Engine;

public sealed class BenchmarkSummaryAggregatorTests
{
    [Fact]
    public void AggregateCandidate_ExcludesZeroTokenErrorsFromAverages_WhileIncludingTimeoutsWithPreservedTokens()
    {
        var candidate = new BenchmarkModelCandidate
        {
            CandidateId = "cand-1",
            DisplayName = "Candidate 1",
            ModelName = "test-model"
        };

        var results = new List<BenchmarkInstanceResult>
        {
            new()
            {
                InstanceId = "inst-pass",
                CandidateId = "cand-1",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Passed,
                DurationMs = 10_000,
                Turns = 5,
                PromptTokens = 800,
                CompletionTokens = 200,
                TotalTokens = 1_000,
                TokensPerSecond = 20.0,
                DeterministicScore = 1.0
            },
            new()
            {
                InstanceId = "inst-timeout-with-tokens",
                CandidateId = "cand-1",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Timeout,
                DurationMs = 20_000,
                Turns = 12,
                PromptTokens = 2_400,
                CompletionTokens = 600,
                TotalTokens = 3_000,
                TokensPerSecond = 30.0,
                DeterministicScore = 0.0
            },
            new()
            {
                InstanceId = "inst-provisioning-error",
                CandidateId = "cand-1",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Error,
                DurationMs = 500,
                Turns = 0,
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0,
                TokensPerSecond = 0.0,
                DeterministicScore = 0.0,
                ErrorMessage = "Workspace provisioning failed"
            }
        };

        var summary = BenchmarkSummaryAggregator.AggregateCandidate(candidate, results);

        Assert.Equal(3, summary.TotalInstances);
        Assert.Equal(1, summary.PassedInstances);
        Assert.Equal(1, summary.TimeoutInstances);
        Assert.Equal(1, summary.ErrorInstances);
        // (1_000 + 3_000) / 2 = 2_000 (0-token provisioning error excluded)
        Assert.Equal(2_000.0, summary.AverageTokens, 2);
        // (20.0 + 30.0) / 2 = 25.0 (0-throughput provisioning error excluded)
        Assert.Equal(25.0, summary.AverageTokensPerSecond, 2);
    }

    [Fact]
    public void AggregateCandidate_AllZeroTokenRuns_ReturnsZeroAveragesWithoutThrowing()
    {
        var candidate = new BenchmarkModelCandidate
        {
            CandidateId = "cand-empty",
            ModelName = "test-model"
        };

        var results = new List<BenchmarkInstanceResult>
        {
            new()
            {
                InstanceId = "err-1",
                CandidateId = "cand-empty",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Error,
                DurationMs = 100,
                Turns = 0,
                TotalTokens = 0,
                TokensPerSecond = 0.0
            },
            new()
            {
                InstanceId = "to-1",
                CandidateId = "cand-empty",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Timeout,
                DurationMs = 1_000,
                Turns = 0,
                TotalTokens = 0,
                TokensPerSecond = 0.0
            }
        };

        var summary = BenchmarkSummaryAggregator.AggregateCandidate(candidate, results);

        Assert.Equal(0.0, summary.AverageTokens);
        Assert.Equal(0.0, summary.AverageTokensPerSecond);
    }

    [Fact]
    public void AggregateCandidate_ComputesEffectiveTokensWhenTotalTokensIsZero_AndIgnoresZeroOrNonFiniteThroughput()
    {
        var candidate = new BenchmarkModelCandidate
        {
            CandidateId = "cand-fallback",
            ModelName = "test-model"
        };

        var results = new List<BenchmarkInstanceResult>
        {
            new()
            {
                InstanceId = "inst-prompt-completion-only",
                CandidateId = "cand-fallback",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Passed,
                DurationMs = 2_000,
                Turns = 3,
                PromptTokens = 700,
                CompletionTokens = 300,
                TotalTokens = 0,
                TokensPerSecond = 0.0
            },
            new()
            {
                InstanceId = "inst-valid-throughput",
                CandidateId = "cand-fallback",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Passed,
                DurationMs = 4_000,
                Turns = 4,
                PromptTokens = 1_500,
                CompletionTokens = 500,
                TotalTokens = 2_000,
                TokensPerSecond = 40.0
            },
            new()
            {
                InstanceId = "inst-nonfinite-tps",
                CandidateId = "cand-fallback",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Failed,
                DurationMs = 1_000,
                Turns = 1,
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 3_000,
                TokensPerSecond = double.PositiveInfinity
            },
            new()
            {
                InstanceId = "inst-nan-tps",
                CandidateId = "cand-fallback",
                ModelName = "test-model",
                Outcome = BenchmarkInstanceOutcome.Failed,
                DurationMs = 1_000,
                Turns = 1,
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0,
                TokensPerSecond = double.NaN
            }
        };

        var summary = BenchmarkSummaryAggregator.AggregateCandidate(candidate, results);

        // (1_000 + 2_000 + 3_000) / 3 = 2_000
        Assert.Equal(2_000.0, summary.AverageTokens, 2);
        // Only inst-valid-throughput has finite positive TokensPerSecond (0.0, +Inf, NaN excluded)
        Assert.Equal(40.0, summary.AverageTokensPerSecond, 2);
    }
}
