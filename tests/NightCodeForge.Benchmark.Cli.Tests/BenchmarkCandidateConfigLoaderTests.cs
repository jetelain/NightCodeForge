using NightCodeForge.Benchmark.Cli.Cli;
using NightCodeForge.Benchmark.Cli.Models;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Benchmark.Cli.Tests;

public sealed class BenchmarkCandidateConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkCandidateConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-bench-cfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadCandidates_FromModelsCsv_CreatesSanitizedUniqueCandidatesWithOverrides()
    {
        var options = new RunCommandOptions
        {
            DatasetPath = "ds.jsonl",
            ModelsCsv = "qwen3:8b, hf.co/org/model:latest, qwen3:8b",
            EnableStackSkills = false,
            TimeoutSeconds = 420
        };

        var candidates = BenchmarkCandidateConfigLoader.LoadCandidates(options);

        Assert.Equal(3, candidates.Count);
        Assert.Equal("qwen3_8b", candidates[0].CandidateId);
        Assert.Equal("qwen3:8b", candidates[0].ModelName);
        Assert.Equal("hf.co_org_model_latest", candidates[1].CandidateId);
        Assert.Equal("qwen3_8b-2", candidates[2].CandidateId);
        Assert.All(candidates, c =>
        {
            Assert.False(c.EnableStackSkills);
            Assert.Equal(420, c.InstanceTimeoutSeconds);
        });
    }

    [Fact]
    public void LoadCandidates_FromModelsCsvWithDotSegments_ProducesSafeCandidateSlug()
    {
        var candidates = BenchmarkCandidateConfigLoader.LoadFromModelsCsv(".., .", null, null);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(BenchmarkCliConstants.DefaultCandidateSlug, candidates[0].CandidateId);
        Assert.Equal($"{BenchmarkCliConstants.DefaultCandidateSlug}-2", candidates[1].CandidateId);
    }

    [Fact]
    public void LoadCandidates_FromJsonArrayManifest_LoadsAllPerModelParameters()
    {
        var configPath = Path.Combine(_tempDir, "candidates.json");
        File.WriteAllText(configPath, """
        [
          {
            "candidateId": "qwen-xhigh",
            "displayName": "Qwen 3.8 (xhigh)",
            "modelName": "Qwen3.8-27B:latest",
            "thinkingEffort": "High",
            "thinkingTokenBudget": 4096,
            "contextWindowTokens": 65536,
            "maxOutputTokens": 8192,
            "temperature": 0.15,
            "maxTurns": 60,
            "instanceTimeoutSeconds": 300,
            "enableStackSkills": false,
            "routingTarget": "Remote",
            "optimizationFlags": {
              "guidedGrammarDecoding": false,
              "deterministicSymbolMap": true,
              "smartCliOutputFiltering": false
            }
          }
        ]
        """);

        var options = new RunCommandOptions
        {
            DatasetPath = "ds.jsonl",
            ConfigPath = configPath
        };

        var candidates = BenchmarkCandidateConfigLoader.LoadCandidates(options);

        var c = Assert.Single(candidates);
        Assert.Equal("qwen-xhigh", c.CandidateId);
        Assert.Equal("Qwen 3.8 (xhigh)", c.DisplayName);
        Assert.Equal("Qwen3.8-27B:latest", c.ModelName);
        Assert.Equal(ThinkingEffort.High, c.ThinkingEffort);
        Assert.Equal(4096, c.ThinkingTokenBudget);
        Assert.Equal(65536, c.ContextWindowTokens);
        Assert.Equal(8192, c.MaxOutputTokens);
        Assert.Equal(0.15, c.Temperature);
        Assert.Equal(60, c.MaxTurns);
        Assert.Equal(300, c.InstanceTimeoutSeconds);
        Assert.False(c.EnableStackSkills);
        Assert.Equal(OllamaRoutingTarget.Remote, c.RoutingTarget);
        Assert.NotNull(c.OptimizationFlags);
        Assert.False(c.OptimizationFlags.GuidedGrammarDecoding);
        Assert.True(c.OptimizationFlags.DeterministicSymbolMap);
        Assert.False(c.OptimizationFlags.SmartCliOutputFiltering);
    }

    [Fact]
    public void LoadCandidates_FromEnvelopeWithCliOverrides_AppliesCliSkillAndTimeoutOverrides()
    {
        var configPath = Path.Combine(_tempDir, "envelope.json");
        File.WriteAllText(configPath, """
        {
          "candidates": [
            {
              "modelName": "llama3.1:8b",
              "enableStackSkills": false,
              "instanceTimeoutSeconds": 300
            }
          ]
        }
        """);

        var options = new RunCommandOptions
        {
            DatasetPath = "ds.jsonl",
            ConfigPath = configPath,
            EnableStackSkills = true,
            TimeoutSeconds = 900
        };

        var candidates = BenchmarkCandidateConfigLoader.LoadCandidates(options);

        var c = Assert.Single(candidates);
        Assert.Equal("llama3.1_8b", c.CandidateId);
        Assert.Equal("llama3.1:8b", c.EffectiveDisplayName);
        Assert.True(c.EnableStackSkills);
        Assert.Equal(900, c.InstanceTimeoutSeconds);
        Assert.Equal(BenchmarkDefaults.DefaultMaxTurns, c.MaxTurns);
    }

    [Fact]
    public void LoadCandidates_MissingFileOrInvalidManifest_ThrowsBenchmarkException()
    {
        var missingOptions = new RunCommandOptions
        {
            DatasetPath = "ds.jsonl",
            ConfigPath = Path.Combine(_tempDir, "missing.json")
        };
        Assert.Throws<BenchmarkException>(() => BenchmarkCandidateConfigLoader.LoadCandidates(missingOptions));

        var duplicatePath = Path.Combine(_tempDir, "dup.json");
        File.WriteAllText(duplicatePath, """
        [
          { "candidateId": "same", "modelName": "m1" },
          { "candidateId": "SAME", "modelName": "m2" }
        ]
        """);
        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(duplicatePath, null, null));

        var missingModelPath = Path.Combine(_tempDir, "nomodel.json");
        File.WriteAllText(missingModelPath, """[{ "candidateId": "c1" }]""");
        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(missingModelPath, null, null));
    }

    [Fact]
    public void LoadFromConfigFile_NullEntryOrSlugCollision_ThrowsBenchmarkException()
    {
        var nullEntryPath = Path.Combine(_tempDir, "null-entry.json");
        File.WriteAllText(nullEntryPath, "[null]");
        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(nullEntryPath, null, null));

        var slugCollisionPath = Path.Combine(_tempDir, "slug-collision.json");
        File.WriteAllText(slugCollisionPath, """
        [
          { "candidateId": "model:1", "modelName": "m1" },
          { "candidateId": "model_1", "modelName": "m2" }
        ]
        """);
        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(slugCollisionPath, null, null));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    public void LoadFromConfigFile_PathTraversalCandidateId_ThrowsBenchmarkException(string badId)
    {
        var path = Path.Combine(_tempDir, $"bad-id-{Guid.NewGuid():N}.json");
        var escapedId = System.Text.Json.JsonSerializer.Serialize(badId);
        File.WriteAllText(path, $$"""[{ "candidateId": {{escapedId}}, "modelName": "m1" }]""");

        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(path, null, null));
    }

    [Theory]
    [InlineData("""[{ "modelName": "m1", "thinkingTokenBudget": 0 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "thinkingTokenBudget": -10 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "contextWindowTokens": 0 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "maxOutputTokens": -5 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "temperature": -0.1 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "temperature": 2.5 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "maxTurns": 0 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "instanceTimeoutSeconds": 0 }]""", 300)]
    [InlineData("""[{ "modelName": "m1", "thinkingEffort": 999 }]""", null)]
    [InlineData("""[{ "modelName": "m1", "routingTarget": 999 }]""", null)]
    public void LoadFromConfigFile_InvalidNumericOrEnumField_ThrowsBenchmarkException(string jsonContent, int? timeoutOverride)
    {
        var path = Path.Combine(_tempDir, $"invalid-field-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, jsonContent);

        Assert.Throws<BenchmarkException>(() =>
            BenchmarkCandidateConfigLoader.LoadFromConfigFile(path, null, timeoutOverride));
    }
}
