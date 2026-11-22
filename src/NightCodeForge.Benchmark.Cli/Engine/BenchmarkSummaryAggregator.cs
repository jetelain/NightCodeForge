using NightCodeForge.Benchmark.Cli.Models;

namespace NightCodeForge.Benchmark.Cli.Engine;

/// <summary>
/// Aggregates per-instance benchmark telemetry and outcomes into candidate-level scorecards
/// and language breakdowns.
/// </summary>
public static class BenchmarkSummaryAggregator
{
    public static BenchmarkRunReport BuildReport(
        string suiteName,
        string datasetPath,
        BenchmarkRunProvenance provenance,
        IReadOnlyList<BenchmarkModelCandidate> candidates,
        IReadOnlyList<BenchmarkInstanceResult> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteName);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(results);

        var summaries = new List<BenchmarkCandidateSummary>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var candidateResults = results
                .Where(r => string.Equals(r.CandidateId, candidate.CandidateId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            summaries.Add(AggregateCandidate(candidate, candidateResults));
        }

        // Include any candidates present in resumed results that were not in the current request list
        var knownIds = candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extraGroup in results.Where(r => !knownIds.Contains(r.CandidateId)).GroupBy(r => r.CandidateId, StringComparer.OrdinalIgnoreCase))
        {
            var first = extraGroup.First();
            var syntheticCandidate = new BenchmarkModelCandidate
            {
                CandidateId = first.CandidateId,
                DisplayName = first.ModelName,
                ModelName = first.ModelName
            };
            summaries.Add(AggregateCandidate(syntheticCandidate, extraGroup.ToList()));
        }

        return new BenchmarkRunReport
        {
            SuiteName = suiteName,
            DatasetPath = datasetPath,
            Provenance = provenance,
            Summaries = summaries,
            Results = results
        };
    }

    public static BenchmarkCandidateSummary AggregateCandidate(
        BenchmarkModelCandidate candidate,
        IReadOnlyList<BenchmarkInstanceResult> results)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0)
        {
            return new BenchmarkCandidateSummary
            {
                CandidateId = candidate.CandidateId,
                DisplayName = candidate.EffectiveDisplayName,
                ModelName = candidate.ModelName
            };
        }

        int total = results.Count;
        int passed = results.Count(r => r.Outcome == BenchmarkInstanceOutcome.Passed);
        int failed = results.Count(r => r.Outcome == BenchmarkInstanceOutcome.Failed);
        int errored = results.Count(r => r.Outcome == BenchmarkInstanceOutcome.Error);
        int timedOut = results.Count(r => r.Outcome == BenchmarkInstanceOutcome.Timeout);

        int patchOrVerdictCount = results.Count(r =>
            !string.IsNullOrWhiteSpace(r.ModelPatch) || !string.IsNullOrWhiteSpace(r.ActualVerdict));

        var judgeResults = results.Where(r => r.JudgeScore.HasValue).ToList();
        double? avgJudge = judgeResults.Count > 0 ? judgeResults.Average(r => r.JudgeScore!.Value) : null;

        int totalEdits = results.Sum(r => r.Telemetry.EditCalls);
        int totalEditFailures = results.Sum(r => r.Telemetry.EditFailures);
        double editFailureRate = totalEdits > 0 ? (double)totalEditFailures / totalEdits : 0.0;

        var byLanguage = results
            .GroupBy(r => BenchmarkLanguages.Normalize(r.Language), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupList = g.ToList();
                var groupJudge = groupList.Where(r => r.JudgeScore.HasValue).ToList();
                return new BenchmarkLanguageBreakdown
                {
                    Language = g.Key,
                    TotalInstances = groupList.Count,
                    PassedInstances = groupList.Count(r => r.Outcome == BenchmarkInstanceOutcome.Passed),
                    PassRate = (double)groupList.Count(r => r.Outcome == BenchmarkInstanceOutcome.Passed) / groupList.Count,
                    AverageDeterministicScore = groupList.Average(r => r.DeterministicScore),
                    AverageJudgeScore = groupJudge.Count > 0 ? groupJudge.Average(r => r.JudgeScore!.Value) : null,
                    AverageDurationMs = groupList.Average(r => r.DurationMs)
                };
            })
            .ToList();

        return new BenchmarkCandidateSummary
        {
            CandidateId = candidate.CandidateId,
            DisplayName = candidate.EffectiveDisplayName,
            ModelName = candidate.ModelName,
            TotalInstances = total,
            PassedInstances = passed,
            FailedInstances = failed,
            ErrorInstances = errored,
            TimeoutInstances = timedOut,
            PassRate = (double)passed / total,
            PatchOrVerdictRate = (double)patchOrVerdictCount / total,
            AverageDeterministicScore = results.Average(r => r.DeterministicScore),
            AverageJudgeScore = avgJudge,
            AverageDurationMs = results.Average(r => r.DurationMs),
            AverageTurns = results.Average(r => r.Turns),
            AverageTokens = results.Average(r => r.TotalTokens),
            AverageTokensPerSecond = results.Average(r => r.TokensPerSecond),
            EditFailureRate = editFailureRate,
            TotalCsNavBashCalls = results.Sum(r => r.Telemetry.CsNavBashCalls),
            TotalCodeCondenserBashCalls = results.Sum(r => r.Telemetry.CodeCondenserBashCalls),
            TotalDispatcherRepairs = results.Sum(r => r.Telemetry.DispatcherRepairs),
            ByLanguage = byLanguage
        };
    }
}
