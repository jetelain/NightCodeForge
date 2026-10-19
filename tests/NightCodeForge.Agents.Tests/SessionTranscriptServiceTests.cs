using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class SessionTranscriptServiceTests
{
    [Fact]
    public void BuildTranscriptEntries_UnpacksAllMessageTypesAndToolCalls_Correctly()
    {
        var chatHistory = new List<ChatMessage>
        {
            new(ChatRole.System, "<tools>\n- read\n</tools>\n<rules>\n- Be concise\n</rules>"),
            new(ChatRole.User, "Please fix the failing unit test."),
            new(ChatRole.Assistant, new AIContent[]
            {
                new TextContent("<think>I will read the test file first.</think>"),
                new FunctionCallContent("call_1", "read", new Dictionary<string, object?> { ["path"] = "tests/AppTests.cs" })
            }),
            new(ChatRole.Tool, new AIContent[]
            {
                new FunctionResultContent("call_1", "Assert.True failed on line 42")
            }),
            new(ChatRole.Assistant, "Fixed the failing test by updating assertion.")
        };

        var entries = CrucibleAgentHarness.BuildTranscriptEntries(chatHistory);

        Assert.Equal(5, entries.Count);

        // 1. System
        Assert.Equal("system", entries[0].Role);
        Assert.Contains("<tools>", entries[0].Content);

        // 2. User
        Assert.Equal("user", entries[1].Role);
        Assert.Equal("Please fix the failing unit test.", entries[1].Content);

        // 3. Assistant with tool call
        Assert.Equal("assistant", entries[2].Role);
        Assert.Contains("<think>I will read the test file first.</think>", entries[2].Content);
        Assert.Equal("read", entries[2].ToolName);
        Assert.NotNull(entries[2].ToolArguments);
        Assert.Contains("tests/AppTests.cs", entries[2].ToolArguments);

        // 4. Tool output
        Assert.Equal("tool", entries[3].Role);
        Assert.Equal("read", entries[3].ToolName);
        Assert.Equal("Assert.True failed on line 42", entries[3].ToolResult);

        // 5. Final Assistant text
        Assert.Equal("assistant", entries[4].Role);
        Assert.Equal("Fixed the failing test by updating assertion.", entries[4].Content);
    }

    [Fact]
    public async Task SessionTranscriptService_WritesMarkdownWithFrontmatter_ToolCallsAndOutputs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-test-" + Guid.NewGuid().ToString("N"));
        var transcriptsDir = Path.Combine(tempDir, "docs", "transcripts");

        try
        {
            var service = new SessionTranscriptService();
            var metadata = new TranscriptMetadata(
                TaskId: 42,
                StageId: "stage-2",
                AgentProfile: "CoderPro",
                ModelName: "qwen2.5-coder:7b",
                DurationMs: 1250,
                Metrics: new AgentRunMetrics(1250, 450, 120, 570),
                Optimizations: new LocalOptimizationFlags
                {
                    GuidedGrammarDecoding = true,
                    PrefixStabilization = true
                },
                Status: AgentRunStatus.Completed,
                KvCacheHitRate: 0.94);

            var entries = new List<TranscriptEntry>
            {
                new(DateTime.UtcNow, "system", "<tools>\n- load_skill\n</tools>"),
                new(DateTime.UtcNow, "user", "Run skill script"),
                new(DateTime.UtcNow, "assistant", "Loading skill...", "load_skill", "{\n  \"skillName\": \"unit-testing\"\n}", null),
                new(DateTime.UtcNow, "tool", "", "load_skill", null, "# Unit Testing Guidelines\nAlways write tests."),
                new(DateTime.UtcNow, "assistant", "Skill loaded successfully.")
            };

            var filePath = await service.SaveTranscriptAsync(metadata, entries, transcriptsDir);

            Assert.True(File.Exists(filePath));
            var content = await File.ReadAllTextAsync(filePath);

            // Frontmatter checks
            Assert.Contains("---", content);
            Assert.Contains("task_id: 42", content);
            Assert.Contains("stage_id: stage-2", content);
            Assert.Contains("agent_profile: CoderPro", content);
            Assert.Contains("model: qwen2.5-coder:7b", content);
            Assert.Contains("prompt: 450", content);
            Assert.Contains("completion: 120", content);
            Assert.Contains("guided_grammar_decoding: true", content);
            Assert.Contains("prefix_stabilization: true", content);
            Assert.Contains("kv_cache_hit_rate: 0.94", content);
            Assert.Contains("status: Completed", content);

            // Turn-by-turn markdown checks
            Assert.Contains("### [", content);
            Assert.Contains("SYSTEM", content);
            Assert.Contains("USER", content);
            Assert.Contains("ASSISTANT", content);
            Assert.Contains("TOOL", content);
            Assert.Contains("**Tool Invocation:** `load_skill`", content);
            Assert.Contains("```json", content);
            Assert.Contains("\"skillName\": \"unit-testing\"", content);
            Assert.Contains("**Tool Output:**", content);
            Assert.Contains("# Unit Testing Guidelines", content);
            Assert.Contains("Skill loaded successfully.", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SessionTranscriptService_GeneratesCorrectFileName_ForDifferentStatuses()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-name-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SessionTranscriptService();
            var metaWaiting = new TranscriptMetadata(101, "stage-1", "Reviewer", "llama3", 100, new AgentRunMetrics(100, 10, 10, 20), new LocalOptimizationFlags(), AgentRunStatus.WaitingForHuman);
            var metaFailed = new TranscriptMetadata(102, "stage-2", "Coder", "llama3", 100, new AgentRunMetrics(100, 10, 10, 20), new LocalOptimizationFlags(), AgentRunStatus.Failed);

            var pathWaiting = await service.SaveTranscriptAsync(metaWaiting, Array.Empty<TranscriptEntry>(), tempDir);
            var pathFailed = await service.SaveTranscriptAsync(metaFailed, Array.Empty<TranscriptEntry>(), tempDir);

            Assert.Contains("task-101-stage-1-waitingforhuman.md", pathWaiting);
            Assert.Contains("task-102-stage-2-failed.md", pathFailed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SessionTranscriptService_RendersFullFidelityEnvironment_AndTruncatesLargeEntries()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-fullfidelity-" + Guid.NewGuid().ToString("N"));

        try
        {
            var service = new SessionTranscriptService();
            var systemPrompt = "You are an expert coder.\nUse the following guidelines:\n```csharp\npublic void Run() {}\n```";
            var availableTools = new[]
            {
                "read: Reads file with line slicing",
                "bash: Executes shell command",
                "load_skill: Loads modular skill"
            };

            var hugeOutput = new string('A', 40000);

            var meta = new TranscriptMetadata(
                TaskId: 99,
                StageId: "stage-crucible",
                AgentProfile: "Architect",
                ModelName: "qwen2.5:32b",
                DurationMs: 2500,
                Metrics: new AgentRunMetrics(2500, 1000, 200, 1200),
                Optimizations: new LocalOptimizationFlags(),
                Status: AgentRunStatus.Completed,
                SystemPrompt: systemPrompt,
                AvailableTools: availableTools,
                MafConfiguration: "TodoProvider: disabled, FileMemory: disabled");

            var entries = new List<TranscriptEntry>
            {
                new(DateTime.UtcNow, "THINKING", "Deep model reasoning trace"),
                new(DateTime.UtcNow, "USER", "Implement feature"),
                new(DateTime.UtcNow, "TOOL", "", "bash", "{\"command\": \"cat huge.txt\"}", hugeOutput),
                new(DateTime.UtcNow, "ASSISTANT", "Feature implemented.")
            };

            var path = await service.SaveTranscriptAsync(meta, entries, tempDir);
            Assert.True(File.Exists(path));

            var content = await File.ReadAllTextAsync(path);

            // Verify Environment section
            Assert.Contains("### Environment", content);
            Assert.Contains("**Available tools**", content);
            Assert.Contains("- read: Reads file with line slicing", content);
            Assert.Contains("- bash: Executes shell command", content);
            Assert.Contains("- load_skill: Loads modular skill", content);
            Assert.Contains("**System prompt**", content);
            Assert.Contains("````", content);
            Assert.Contains("public void Run() {}", content);

            // Verify MAF configuration
            Assert.Contains("### MAF Pipeline Configuration", content);
            Assert.Contains("TodoProvider: disabled", content);

            // Verify ordered icons
            Assert.Contains("💭 THINKING", content);
            Assert.Contains("Deep model reasoning trace", content);
            Assert.Contains("🧑 USER", content);
            Assert.Contains("🔧 TOOL", content);
            Assert.Contains("🤖 ASSISTANT", content);

            // Verify 4-tick safe fences and truncation
            Assert.Contains("````json", content);
            Assert.Contains("````", content);
            Assert.Contains("... [Output truncated; exceeded 32768 characters]", content);
            Assert.DoesNotContain(new string('A', 35000), content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
