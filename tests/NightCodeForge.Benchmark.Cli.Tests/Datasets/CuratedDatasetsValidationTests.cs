using System.Globalization;
using System.Text.RegularExpressions;
using NightCodeForge.Benchmark.Cli.Datasets;
using NightCodeForge.Benchmark.Cli.Engine;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Benchmark.Cli.Verification;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests.Datasets;

public sealed partial class CuratedDatasetsValidationTests
{
    private readonly BenchmarkDatasetLoader _loader = new();

    [GeneratedRegex(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitSha40Regex();

    [GeneratedRegex(@"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@", RegexOptions.CultureInvariant)]
    private static partial Regex UnifiedDiffHunkRegex();

    [GeneratedRegex(@"^index (?<old>[0-9a-f]+)\.\.(?<new>[0-9a-f]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DiffIndexLineRegex();

    [GeneratedRegex(@"(.)\1{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedHexRunRegex();

    [GeneratedRegex(@"^(?:([1-9a-f])\1+|0+|1234567|89abcdef|1010101|2020202|3030303|4040404|5050505|6060606|7070707|8080808|9090909|a0a0a0a|a1a1a1a|b2b2b2b|c3c3c3c|d4d4d4d|e1e1e1e|f2f2f2f|1212121|2323232|3434343|4545454|4141414|5151515|6161616|7171717|8181818|9191919|1112131|1415161|2121212|3131313)$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderBlobRegex();

    [GeneratedRegex(@"^0+$", RegexOptions.CultureInvariant)]
    private static partial Regex ZeroBlobRegex();

    [Fact]
    public async Task Manifests_SmokeAndFull_MeetTierSizeStackMixAndSupersetConstraints()
    {
        var datasetsDir = FindDatasetsDirectory();
        var smoke = await BenchmarkDatasetManifestLoader.LoadAsync(Path.Combine(datasetsDir, "v1-smoke.manifest.json"));
        var full = await BenchmarkDatasetManifestLoader.LoadAsync(Path.Combine(datasetsDir, "v1-full.manifest.json"));

        Assert.Equal("v1-smoke", smoke.SuiteName);
        Assert.InRange(smoke.Entries.Count, 24, 30);
        AssertAllCategoriesPresent(smoke.Entries);
        AssertStackDistribution(smoke.Entries);

        Assert.Equal("v1-full", full.SuiteName);
        Assert.InRange(full.Entries.Count, 80, 100);
        AssertAllCategoriesPresent(full.Entries);
        AssertStackDistribution(full.Entries);

        var fullById = full.Entries.ToDictionary(e => e.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var smokeEntry in smoke.Entries)
        {
            Assert.True(fullById.TryGetValue(smokeEntry.InstanceId, out var fullEntry), $"Smoke entry '{smokeEntry.InstanceId}' not found in full manifest.");
            Assert.Equal(fullEntry.Category, smokeEntry.Category);
            Assert.Equal(fullEntry.Language, smokeEntry.Language);
            Assert.Equal(fullEntry.Source, smokeEntry.Source);
            Assert.Equal(fullEntry.Repo, smokeEntry.Repo);
            Assert.Equal(fullEntry.TestResultFormat, smokeEntry.TestResultFormat);
            Assert.Equal(fullEntry.CustomTestCommand, smokeEntry.CustomTestCommand);
            Assert.Equal(fullEntry.CustomContainerImage, smokeEntry.CustomContainerImage);
            Assert.Equal(fullEntry.PipConstraints ?? [], smokeEntry.PipConstraints ?? []);
            Assert.Equal(fullEntry.FailToPass ?? [], smokeEntry.FailToPass ?? []);
            Assert.Equal(fullEntry.PassToPass ?? [], smokeEntry.PassToPass ?? []);
        }

        var serilog1903 = Assert.Single(full.Entries, e => e.InstanceId == "serilog__serilog-1903");
        Assert.Equal(
            [
                "Serilog.Tests.Debugging.SelfLogTests.SelfLogReportsErrorWhenPositionalParameterCountIsMismatched",
                "Serilog.Tests.Debugging.SelfLogTests.SelfLogDoesNotReportErrorWhenPositionalParameterIsRepeated"
            ],
            serilog1903.FailToPass ?? []);
        Assert.Equal(
            ["Serilog.Tests.Capturing.GettablePropertyFinderTests.GetPropertiesRecursiveIntegerTypeYieldNoResult"],
            serilog1903.PassToPass ?? []);

        var axiosSmoke = Assert.Single(smoke.Entries, e => e.InstanceId == "axios__axios-5892");
        Assert.Equal(TestResultFormat.JUnitXml, axiosSmoke.TestResultFormat);
        Assert.Contains("mocha-junit-reporter", axiosSmoke.CustomTestCommand);

        var pytestSmoke = Assert.Single(smoke.Entries, e => e.InstanceId == "pytest-dev__pytest-10356");
        Assert.Equal("python:3.11", pytestSmoke.CustomContainerImage);

        var flaskSmoke = Assert.Single(smoke.Entries, e => e.InstanceId == "pallets__flask-5014");
        Assert.Equal("python:3.11", flaskSmoke.CustomContainerImage);
        Assert.Contains("Werkzeug<3.0", flaskSmoke.PipConstraints ?? []);

        var crPy1Smoke = Assert.Single(smoke.Entries, e => e.InstanceId == "ncf-cr-py-001");
        Assert.Equal("python:3.11", crPy1Smoke.CustomContainerImage);
        Assert.Contains("tests/test_utils.py", crPy1Smoke.CustomTestCommand);
        Assert.Contains("MarkupSafe<2.1", crPy1Smoke.PipConstraints ?? []);

        var crPy2Full = Assert.Single(full.Entries, e => e.InstanceId == "ncf-cr-py-002");
        Assert.Equal("python:3.11", crPy2Full.CustomContainerImage);
        Assert.Contains("Werkzeug<3.0", crPy2Full.PipConstraints ?? []);

        var crRust1Smoke = Assert.Single(smoke.Entries, e => e.InstanceId == "ncf-cr-rust-001");
        Assert.Contains("grep-regex", crRust1Smoke.CustomTestCommand);
    }

    [Fact]
    public async Task CraftedCodeReviewDataset_MeetsSchemaVerdictsDiffsAndManifestAlignment()
    {
        var datasetsDir = FindDatasetsDirectory();
        var full = await BenchmarkDatasetManifestLoader.LoadAsync(Path.Combine(datasetsDir, "v1-full.manifest.json"));
        var crDataset = await _loader.LoadAsync(Path.Combine(datasetsDir, "v1-code-review.jsonl"));

        Assert.InRange(crDataset.Count, 15, 24);

        var fullCrById = full.Entries
            .Where(e => e.Category == BenchmarkProblemCategory.CodeReview)
            .ToDictionary(e => e.InstanceId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fullCrById.Count, crDataset.Count);

        int approveCount = 0;
        int minorFixCount = 0;
        int requestChangesCount = 0;

        foreach (var instance in crDataset)
        {
            Assert.True(fullCrById.TryGetValue(instance.InstanceId, out var manifestEntry), $"'{instance.InstanceId}' missing from full manifest.");
            Assert.Equal(BenchmarkProblemCategory.CodeReview, instance.Category);
            Assert.Equal(manifestEntry.Repo, instance.Repo);
            Assert.Equal(manifestEntry.Language, instance.Language);
            Assert.Equal(manifestEntry.CustomContainerImage, instance.CustomContainerImage);
            Assert.Equal(manifestEntry.CustomTestCommand, instance.CustomTestCommand);
            Assert.Equal(manifestEntry.PipConstraints ?? [], instance.PipConstraints ?? []);
            Assert.Equal(manifestEntry.TestResultFormat, instance.ResolveEffectiveTestResultFormat());
            AssertAuthenticCommitSha(instance.BaseCommit, instance.InstanceId);
            AssertNoLeakedMetaInstructions(instance.TaskSpecification, instance.InstanceId);
            AssertValidUnifiedDiff(instance.CandidatePatch!, instance.InstanceId);
            if (!string.IsNullOrWhiteSpace(instance.GoldPatch))
            {
                AssertValidUnifiedDiff(instance.GoldPatch, $"{instance.InstanceId} (patch)");
            }
            Assert.True(BenchmarkDatasetLoader.TryParseCodeReviewVerdict(instance.ExpectedVerdict, out var verdict));

            switch (verdict)
            {
                case CodeReviewVerdict.Approve:
                    approveCount++;
                    break;
                case CodeReviewVerdict.MinorFix:
                    minorFixCount++;
                    Assert.NotEmpty(instance.ExpectedFindings);
                    break;
                case CodeReviewVerdict.RequestChanges:
                    requestChangesCount++;
                    Assert.NotEmpty(instance.ExpectedFindings);
                    break;
            }
        }

        Assert.True(approveCount >= 3);
        Assert.True(minorFixCount >= 5);
        Assert.True(requestChangesCount >= 6);
    }

    [Fact]
    public async Task CraftedTaskReviewDataset_MeetsSchemaVerdictsFrontmatterAndManifestAlignment()
    {
        var datasetsDir = FindDatasetsDirectory();
        var full = await BenchmarkDatasetManifestLoader.LoadAsync(Path.Combine(datasetsDir, "v1-full.manifest.json"));
        var trDataset = await _loader.LoadAsync(Path.Combine(datasetsDir, "v1-task-review.jsonl"));

        Assert.InRange(trDataset.Count, 12, 18);

        var fullTrById = full.Entries
            .Where(e => e.Category == BenchmarkProblemCategory.TaskReview)
            .ToDictionary(e => e.InstanceId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(fullTrById.Count, trDataset.Count);

        int readyStandardCount = 0;
        int readyEscalatedCount = 0;
        int needsClarificationCount = 0;

        foreach (var instance in trDataset)
        {
            Assert.True(fullTrById.TryGetValue(instance.InstanceId, out var manifestEntry), $"'{instance.InstanceId}' missing from full manifest.");
            Assert.Equal(BenchmarkProblemCategory.TaskReview, instance.Category);
            Assert.Equal(manifestEntry.Repo, instance.Repo);
            Assert.Equal(manifestEntry.Language, instance.Language);
            Assert.Equal(manifestEntry.CustomContainerImage, instance.CustomContainerImage);
            Assert.Equal(manifestEntry.CustomTestCommand, instance.CustomTestCommand);
            Assert.Equal(manifestEntry.PipConstraints ?? [], instance.PipConstraints ?? []);
            Assert.Equal(manifestEntry.TestResultFormat, instance.ResolveEffectiveTestResultFormat());
            AssertAuthenticCommitSha(instance.BaseCommit, instance.InstanceId);
            AssertNoLeakedMetaInstructions(instance.TaskSpecification, instance.InstanceId);
            Assert.True(BenchmarkDatasetLoader.TryParseTaskReviewVerdict(instance.ExpectedVerdict, out var verdict));

            var (frontmatter, body) = BenchmarkVerifier.ParseTaskMarkdown(instance.TaskSpecification);
            Assert.NotNull(frontmatter);
            Assert.True(frontmatter.Id > 0);
            Assert.False(string.IsNullOrWhiteSpace(frontmatter.Title));
            Assert.Equal("Open", frontmatter.Status);
            Assert.True(Enum.TryParse<TaskDifficulty>(frontmatter.Difficulty, ignoreCase: true, out _));
            Assert.False(string.IsNullOrWhiteSpace(body));
            Assert.NotEmpty(instance.ExpectedFindings);

            if (verdict == TaskReviewVerdict.Ready)
            {
                Assert.True(Enum.TryParse<TaskDifficulty>(instance.ExpectedDifficulty, ignoreCase: true, out var expectedDiff));
                if (expectedDiff is TaskDifficulty.Difficult or TaskDifficulty.Expert)
                {
                    readyEscalatedCount++;
                }
                else
                {
                    readyStandardCount++;
                }
            }
            else
            {
                needsClarificationCount++;
            }
        }

        Assert.True(readyStandardCount >= 4);
        Assert.True(readyEscalatedCount >= 3);
        Assert.True(needsClarificationCount >= 5);
    }

    private static void AssertAllCategoriesPresent(IReadOnlyList<BenchmarkManifestEntry> entries)
    {
        Assert.Contains(entries, e => e.Category == BenchmarkProblemCategory.Implementation);
        Assert.Contains(entries, e => e.Category == BenchmarkProblemCategory.CodeReview);
        Assert.Contains(entries, e => e.Category == BenchmarkProblemCategory.TaskReview);
    }

    private static void AssertStackDistribution(IReadOnlyList<BenchmarkManifestEntry> entries)
    {
        double total = entries.Count;
        double csharpPct = entries.Count(e => e.Language == BenchmarkLanguages.CSharp) / total;
        double tsJsPct = entries.Count(e => e.Language is BenchmarkLanguages.TypeScript or BenchmarkLanguages.JavaScript) / total;
        double pythonPct = entries.Count(e => e.Language == BenchmarkLanguages.Python) / total;
        double rustPct = entries.Count(e => e.Language == BenchmarkLanguages.Rust) / total;

        Assert.InRange(csharpPct, 0.50, 0.55);
        Assert.InRange(tsJsPct, 0.18, 0.25);
        Assert.InRange(pythonPct, 0.10, 0.20);
        Assert.InRange(rustPct, 0.10, 0.15);
    }

    private static void AssertAuthenticCommitSha(string sha, string instanceId)
    {
        Assert.True(CommitSha40Regex().IsMatch(sha), $"Instance '{instanceId}' has non-40-char-hex base_commit '{sha}'.");
        Assert.False(RepeatedHexRunRegex().IsMatch(sha), $"Instance '{instanceId}' has placeholder repeated hex run in base_commit '{sha}'.");
        Assert.DoesNotContain("01234567", sha, StringComparison.Ordinal);
        Assert.DoesNotContain("11223344", sha, StringComparison.Ordinal);
        Assert.DoesNotContain("4a1e8f9c", sha, StringComparison.Ordinal);
    }

    private static void AssertNoLeakedMetaInstructions(string? taskSpecification, string instanceId)
    {
        Assert.False(string.IsNullOrWhiteSpace(taskSpecification), $"Instance '{instanceId}' is missing task_specification.");
        Assert.DoesNotContain("MinorFix-level", taskSpecification, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Do not mention", taskSpecification, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertValidUnifiedDiff(string patch, string instanceId)
    {
        Assert.DoesNotContain("ï»¿", patch, StringComparison.Ordinal);
        Assert.DoesNotContain("â”", patch, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', patch);
        Assert.Contains("diff --git ", patch, StringComparison.Ordinal);
        var lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int hunkCount = 0;
        bool isNewFile = false;
        bool isDeletedFile = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                isNewFile = false;
                isDeletedFile = false;
                continue;
            }
            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                isNewFile = true;
                continue;
            }
            if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                isDeletedFile = true;
                continue;
            }

            var indexMatch = DiffIndexLineRegex().Match(line);
            if (indexMatch.Success)
            {
                var oldHash = indexMatch.Groups["old"].Value;
                var newHash = indexMatch.Groups["new"].Value;
                if (!(isNewFile && ZeroBlobRegex().IsMatch(oldHash)))
                {
                    Assert.False(PlaceholderBlobRegex().IsMatch(oldHash),
                        $"Instance '{instanceId}' uses placeholder index blob hash '{oldHash}'.");
                }
                if (!(isDeletedFile && ZeroBlobRegex().IsMatch(newHash)))
                {
                    Assert.False(PlaceholderBlobRegex().IsMatch(newHash),
                        $"Instance '{instanceId}' uses placeholder index blob hash '{newHash}'.");
                }
                continue;
            }

            var hunkMatch = UnifiedDiffHunkRegex().Match(line);
            if (!hunkMatch.Success)
            {
                continue;
            }

            hunkCount++;
            int expectedOld = hunkMatch.Groups["oldCount"].Success
                ? int.Parse(hunkMatch.Groups["oldCount"].Value, CultureInfo.InvariantCulture)
                : 1;
            int expectedNew = hunkMatch.Groups["newCount"].Success
                ? int.Parse(hunkMatch.Groups["newCount"].Value, CultureInfo.InvariantCulture)
                : 1;

            int actualOld = 0;
            int actualNew = 0;
            int j = i + 1;
            while (j < lines.Length)
            {
                var bodyLine = lines[j];
                if (bodyLine.StartsWith("diff --git ", StringComparison.Ordinal) ||
                    bodyLine.StartsWith("@@ ", StringComparison.Ordinal) ||
                    bodyLine.StartsWith("index ", StringComparison.Ordinal) ||
                    bodyLine.StartsWith("--- ", StringComparison.Ordinal) ||
                    bodyLine.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    break;
                }

                if (bodyLine.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                {
                    j++;
                    continue;
                }

                if (bodyLine.StartsWith('-'))
                {
                    actualOld++;
                }
                else if (bodyLine.StartsWith('+'))
                {
                    actualNew++;
                }
                else if (bodyLine.StartsWith(' '))
                {
                    actualOld++;
                    actualNew++;
                }
                else if (j == lines.Length - 1 && bodyLine.Length == 0)
                {
                    break;
                }
                else
                {
                    Assert.Fail($"Instance '{instanceId}' has unexpected unified diff line at hunk body index {j}: '{bodyLine}'.");
                }

                j++;
            }

            Assert.True(actualOld == expectedOld && actualNew == expectedNew,
                $"Instance '{instanceId}' hunk '{line}' expected (-{expectedOld}, +{expectedNew}) lines but counted (-{actualOld}, +{actualNew}).");
            i = j - 1;
        }

        Assert.True(hunkCount > 0, $"Instance '{instanceId}' unified diff contained zero hunks.");
    }

    private static string FindDatasetsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "benchmarks", "datasets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate 'benchmarks/datasets' relative to test execution directory.");
    }
}
