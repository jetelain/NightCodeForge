using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class ModelCapabilityResolverTests
{
    [Theory]
    [InlineData("qwen3.8:35b", "high", "xhigh")]
    [InlineData("qwen3.8:35b", "xhigh", "xhigh")]
    [InlineData("qwen3.8:35b", "medium", "medium")]
    [InlineData("qwen3.8:35b", "low", "low")]
    [InlineData("QWEN-3.8-coder", "high", "xhigh")]
    [InlineData("Qwen3.8-27B:latest", "high", "xhigh")]
    public void Qwen38_MapsHighToXhigh_AndKeepsSupportedLevels(string model, string requested, string expected)
    {
        var options = new LlmProviderOptions();
        Assert.Equal(expected, ModelCapabilityResolver.ResolveReasoningEffort(options, model, requested));
    }

    [Fact]
    public void Qwen38_UnsupportedLevel_FallsBackToNearestSupported()
    {
        var options = new LlmProviderOptions();
        options.ModelProfiles["qwen3.8"].EffortMap.Clear();
        Assert.Equal("xhigh", ModelCapabilityResolver.ResolveReasoningEffort(options, "qwen3.8:35b", "high"));
    }

    [Fact]
    public void UnknownModel_UsesConfiguredDefault_AndOpenAiNormalization()
    {
        var options = new LlmProviderOptions { ReasoningEffort = "medium" };
        Assert.Equal("medium", ModelCapabilityResolver.ResolveReasoningEffort(options, "llama3:8b"));

        var xhigh = new LlmProviderOptions();
        Assert.Equal("xhigh", ModelCapabilityResolver.ResolveReasoningEffort(xhigh, "llama3:8b"));
        var openAiResolver = new OpenAiCapabilityResolver(Microsoft.Extensions.Options.Options.Create(xhigh));
        Assert.Equal("high", openAiResolver.ResolveReasoningEffort("o3-mini"));
    }

    [Fact]
    public void BuildReasoningProperties_EnablesThinkingWithResolvedEffort()
    {
        var props = ModelCapabilityResolver.BuildReasoningProperties(new LlmProviderOptions { ReasoningEffort = "high" }, "qwen3.8:35b");
        Assert.Equal(true, props["think"]);
        Assert.Equal("xhigh", props["reasoning_effort"]);
    }

    [Fact]
    public void BuildCompactionStrategy_ReturnsStrategy()
    {
        Assert.NotNull(ModelCapabilityResolver.BuildCompactionStrategy(new LlmProviderOptions(), "qwen3.8:35b"));
        Assert.NotNull(ModelCapabilityResolver.BuildCompactionStrategy((LlmProviderOptions?)null, (string?)null));
        Assert.NotNull(ModelCapabilityResolver.BuildCompactionStrategy(new AgentProfile { Id = "test", ModelName = "qwen3.8:35b" }));
    }

    [Fact]
    public void BuildCompactionStrategy_WithExplicitTokens_ResolvesToAgentProfile()
    {
        var strategy = (NightCodeForge.Agents.Compaction.PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            new LlmProviderOptions { ContextWindowTokens = 225280 },
            "test-model",
            explicitContextWindowTokens: 65536);

        Assert.Equal(65536, strategy.Options.ContextWindowTokens);
        Assert.Equal("AgentProfile", strategy.Options.ResolutionSource);
    }

    [Fact]
    public void BuildCompactionStrategy_WithExplicitTokens_SizesCompactionToFleetMaximumCeiling()
    {
        var profile = new AgentProfile
        {
            Id = "coder",
            ModelName = "qwen3.8-27b",
            ContextWindowTokens = 49152 // Desktop node limit
        };

        var strategy = (NightCodeForge.Agents.Compaction.PiStyleCrucibleCompactionStrategy)
            ModelCapabilityResolver.BuildCompactionStrategy(
                profile,
                explicitContextWindowTokens: 262144);

        Assert.Equal(262144, strategy.Options.ContextWindowTokens);
    }

    [Fact]
    public void BuildCompactionStrategy_WithModelProfileTokens_ResolvesToModelCapabilityProfile()
    {
        var options = new LlmProviderOptions { ContextWindowTokens = 225280 };
        options.ModelProfiles["custom-model"] = new ModelCapabilityProfile
        {
            ContextWindowTokens = 180000
        };

        var strategy = (NightCodeForge.Agents.Compaction.PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            options,
            "custom-model:latest");

        Assert.Equal(180000, strategy.Options.ContextWindowTokens);
        Assert.Equal("ModelCapabilityProfile", strategy.Options.ResolutionSource);
    }

    [Fact]
    public void BuildCompactionStrategy_WithOptionsTokens_ResolvesToLlmProviderOptions()
    {
        var options = new LlmProviderOptions { ContextWindowTokens = 225280 };

        var strategy = (NightCodeForge.Agents.Compaction.PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            options,
            "unmatched-model");

        Assert.Equal(225280, strategy.Options.ContextWindowTokens);
        Assert.Equal("LlmProviderOptions (appsettings.json)", strategy.Options.ResolutionSource);
    }

    [Fact]
    public void BuildCompactionStrategy_WithNullOrZeroOptions_ResolvesToDefaultFallback()
    {
        var strategy = (NightCodeForge.Agents.Compaction.PiStyleCrucibleCompactionStrategy)ModelCapabilityResolver.BuildCompactionStrategy(
            null,
            "unmatched-model");

        Assert.Equal(LlmProviderOptions.DefaultContextWindowTokens, strategy.Options.ContextWindowTokens);
        Assert.Equal("Default (128k fallback)", strategy.Options.ResolutionSource);
    }

    [Fact]
    public void Transcript_FormatOffset_RendersElapsedMinutesAndSeconds()
    {
        Assert.Equal("[ 00:00 ]", SessionTranscriptService.FormatOffset(TimeSpan.Zero));
        Assert.Equal("[ 01:05 ]", SessionTranscriptService.FormatOffset(TimeSpan.FromSeconds(65)));
        Assert.Equal("[ 75:00 ]", SessionTranscriptService.FormatOffset(TimeSpan.FromMinutes(75)));
    }

    [Fact]
    public async Task Transcript_UsesElapsedOffsetsRelativeToFirstEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncf-offset-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var entries = new List<TranscriptEntry>
            {
                new(start, "USER", "hi"),
                new(start.AddSeconds(125), "ASSISTANT", "hello")
            };
            var metadata = new TranscriptMetadata(1, "s", "p", "m", 1, new AgentRunMetrics(), new LocalOptimizationFlags(), AgentRunStatus.Completed);

            var path = await new SessionTranscriptService(dir).SaveTranscriptAsync(metadata, entries);
            var text = await File.ReadAllTextAsync(path);

            Assert.Contains("### [ 00:00 ] 🧑 USER", text);
            Assert.Contains("### [ 02:05 ] 🤖 ASSISTANT", text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
