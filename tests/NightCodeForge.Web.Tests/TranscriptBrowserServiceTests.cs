using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TranscriptBrowserServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly ServiceProvider _serviceProvider;
    private readonly FakeGlobalOptService _fakeGlobalOptService;

    public TranscriptBrowserServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var initDb = new AppDbContext(_dbOptions))
        {
            initDb.Database.EnsureCreated();
            initDb.EnsureDefaultGlobalAgentProfilesAsync("qwen2.5-coder:32b").GetAwaiter().GetResult();
        }

        _fakeGlobalOptService = new FakeGlobalOptService();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton<IGlobalOptimizationSettingsService>(_fakeGlobalOptService);

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void ParseSummaryFromContent_WithValidFrontmatter_ExtractsAllFieldsCorrectly()
    {
        var content = @"---
task_id: 105
stage_id: stage-review
agent_profile: ReviewerExpert
model: qwen2.5-coder:32b
duration_ms: 3200
tokens:
  prompt: 1500
  completion: 450
  total: 1950
optimizations:
  thinking_trace_compaction: true
  loop_circuit_breaker: true
  guided_grammar_decoding: false
  deterministic_symbol_map: true
  structured_diagnostic_extraction: false
  prefix_stabilization: true
  jit_model_preload: true
  compact_older_tool_results: false
kv_cache_hit_rate: 0.92
status: Completed
---

# Title
Body text
";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("test.md", content);

        Assert.Equal(105, summary.TaskId);
        Assert.Equal("stage-review", summary.StageId);
        Assert.Equal("ReviewerExpert", summary.AgentProfile);
        Assert.Equal("qwen2.5-coder:32b", summary.ModelName);
        Assert.Equal(3200, summary.DurationMs);
        Assert.Equal(1500, summary.PromptTokens);
        Assert.Equal(450, summary.CompletionTokens);
        Assert.Equal(1950, summary.TotalTokens);
        Assert.Equal("Completed", summary.Status);
        Assert.Equal(0.92, summary.KvCacheHitRate);
        Assert.NotNull(summary.Optimizations);
        Assert.True(summary.Optimizations.ThinkingTraceCompaction);
        Assert.True(summary.Optimizations.LoopCircuitBreaker);
        Assert.False(summary.Optimizations.GuidedGrammarDecoding);
        Assert.True(summary.Optimizations.DeterministicSymbolMap);
        Assert.True(summary.Optimizations.PrefixStabilization);
    }

    [Fact]
    public void ParseSummaryFromContent_WithoutFrontmatter_ExtractsFromFilename()
    {
        var content = @"# Title Without Frontmatter
Just raw discussion";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("2026-10-15-task-42-unit-tests-failed.md", content);

        Assert.Equal(42, summary.TaskId);
        Assert.Equal("unit-tests", summary.StageId);
        Assert.Equal("failed", summary.Status);
    }

    [Fact]
    public void ParseDetail_ExtractsEnvironment_Turns_Thinking_Tools_AndModelCalls()
    {
        var summary = new TranscriptSummary(
            FileName: "sample.md",
            FilePath: "sample.md",
            TaskId: 1,
            StageId: "refactor",
            ModelName: "llama3",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 100,
            CompletionTokens: 50,
            DurationMs: 1200,
            Status: "Completed");

        var content = @"---
task_id: 1
stage_id: refactor
status: Completed
---

# Execution Transcript: ForgeTask #1 (refactor)

### Environment

**Available tools**
- ReadFile
- EditFile

**Active skills**
- csharp-refactoring

**System prompt**
````
You are an expert engineer.
````

### [ 00:00 ] 🧑 USER
Please refactor Foo.cs.

### [ 00:02 ] 💭 THINKING
Analyzing class structure.

### [ 00:05 ] 🔧 TOOL
**Tool Invocation:** `ReadFile`
````json
{ ""path"": ""Foo.cs"" }
````
**Tool Output:**
````
public class Foo {}
````

### [ 00:08 ] 🤖 ASSISTANT
<think>
Now edit Foo.cs to record.
</think>
Done refactoring.

### Model calls (requests as sent to the model)
#### [ 00:01 ] Call 1
**Messages:** 2 total
";

        var detail = TranscriptBrowserService.ParseDetail(summary, content);

        Assert.NotNull(detail);
        Assert.Contains("ReadFile", detail.AvailableTools);
        Assert.Contains("EditFile", detail.AvailableTools);
        Assert.Contains("csharp-refactoring", detail.ActiveSkills);
        Assert.Equal("You are an expert engineer.", detail.SystemPrompt);

        Assert.Equal(4, detail.Turns.Count);

        // Turn 0: User
        Assert.Equal("USER", detail.Turns[0].Role);
        Assert.Equal("00:00", detail.Turns[0].Offset);
        Assert.Contains("Please refactor Foo.cs.", detail.Turns[0].Content);

        // Turn 1: Thinking
        Assert.Equal("THINKING", detail.Turns[1].Role);
        Assert.Equal("Analyzing class structure.", detail.Turns[1].ThinkingTrace);

        // Turn 2: Tool
        Assert.Equal("TOOL", detail.Turns[2].Role);
        Assert.True(detail.Turns[2].IsTool);
        Assert.Equal("ReadFile", detail.Turns[2].ToolName);
        Assert.Contains("Foo.cs", detail.Turns[2].ToolArguments);
        Assert.Contains("public class Foo {}", detail.Turns[2].ToolResult);

        // Turn 3: Assistant with think tag
        Assert.Equal("ASSISTANT", detail.Turns[3].Role);
        Assert.Equal("Now edit Foo.cs to record.", detail.Turns[3].ThinkingTrace);
        Assert.Contains("Done refactoring.", detail.Turns[3].Content);

        // Model calls
        Assert.NotNull(detail.ModelCallsMarkdown);
        Assert.Contains("Call 1", detail.ModelCallsMarkdown);
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WhenRunawayLoopDetected_SuggestsLoopCircuitBreaker()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new TranscriptBrowserService(_fakeGlobalOptService, scopeFactory);

        var summary = new TranscriptSummary(
            FileName: "loop.md",
            FilePath: "loop.md",
            TaskId: 10,
            StageId: "loop-stage",
            ModelName: "llama3",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 200,
            CompletionTokens: 100,
            DurationMs: 5000,
            Status: "Failed",
            Optimizations: new LocalOptimizationFlags { LoopCircuitBreaker = false });

        var turns = new List<ParsedTranscriptTurn>
        {
            new("00:00", "TOOL", "", ToolName: "EditFile", IsTool: true),
            new("00:02", "TOOL", "", ToolName: "EditFile", IsTool: true),
            new("00:04", "TOOL", "", ToolName: "EditFile", IsTool: true)
        };

        var detail = new ParsedTranscriptDetail(summary, "raw content", null, Array.Empty<string>(), Array.Empty<string>(), turns);

        var report = await service.RunDiagnosticsAsync(detail, "Cloud");

        Assert.NotNull(report);
        Assert.Contains("Loop", report.FailureCategory);
        Assert.Contains(report.SuggestedActions, a => a.TargetKey == "LoopCircuitBreaker");
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WhenCompilerErrorsPresent_SuggestsDeterministicSymbolMap()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new TranscriptBrowserService(_fakeGlobalOptService, scopeFactory);

        var summary = new TranscriptSummary(
            FileName: "compile.md",
            FilePath: "compile.md",
            TaskId: 11,
            StageId: "build-stage",
            ModelName: "llama3",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 200,
            CompletionTokens: 100,
            DurationMs: 3000,
            Status: "Failed",
            Optimizations: new LocalOptimizationFlags { LoopCircuitBreaker = true });

        var content = "Build output: error CS0117: 'Bar' does not contain a definition for 'Baz'.";
        var detail = new ParsedTranscriptDetail(summary, content, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        var report = await service.RunDiagnosticsAsync(detail, "Local");

        Assert.NotNull(report);
        Assert.Contains("Compilation", report.FailureCategory);
        Assert.Contains(report.SuggestedActions, a => a.TargetKey == "DeterministicSymbolMap");
    }

    [Fact]
    public async Task ApplyRemediationAsync_TogglesFlagInGlobalSettingsAndAgentProfiles()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = new TranscriptBrowserService(_fakeGlobalOptService, scopeFactory);

        _fakeGlobalOptService.Flags.LoopCircuitBreaker = false;

        var suggestion = new RemediationSuggestion(
            Title: "Enable Loop Circuit Breaker",
            Description: "Enables breaker",
            ActionType: "ToggleOptimizationFlag",
            TargetKey: "LoopCircuitBreaker",
            TargetValue: true);

        await service.ApplyRemediationAsync(suggestion);

        // Global flag updated
        Assert.True(_fakeGlobalOptService.Flags.LoopCircuitBreaker);

        // Database agent profiles updated
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profiles = await db.AgentProfiles.ToListAsync();
        Assert.NotEmpty(profiles);
        foreach (var p in profiles)
        {
            Assert.True(p.OptimizationFlags?.LoopCircuitBreaker ?? false);
        }
    }

    [Fact]
    public async Task GetTranscriptsAsync_DiscoversAndSortsTranscriptsByTimestamp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-transcripts-test-" + Guid.NewGuid().ToString("N"));
        var transcriptsDir = Path.Combine(tempDir, "docs", "transcripts");
        Directory.CreateDirectory(transcriptsDir);

        try
        {
            var file1 = Path.Combine(transcriptsDir, "2026-10-10-task-1-init-completed.md");
            var file2 = Path.Combine(transcriptsDir, "2026-10-15-task-2-build-failed.md");

            await File.WriteAllTextAsync(file1, "---\ntask_id: 1\nstage_id: init\nstatus: Completed\n---\n# Transcript 1");
            await File.WriteAllTextAsync(file2, "---\ntask_id: 2\nstage_id: build\nstatus: Failed\n---\n# Transcript 2");

            var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
            var service = new TranscriptBrowserService(_fakeGlobalOptService, scopeFactory);

            var list = await service.GetTranscriptsAsync(tempDir);

            Assert.Equal(2, list.Count);
            // Sorted descending: file2 (Oct 15) before file1 (Oct 10)
            Assert.Equal(2, list[0].TaskId);
            Assert.Equal("Failed", list[0].Status);
            Assert.Equal(1, list[1].TaskId);
            Assert.Equal("Completed", list[1].Status);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private class FakeGlobalOptService : IGlobalOptimizationSettingsService
    {
        public LocalOptimizationFlags Flags { get; } = new();

        public event Action<LocalOptimizationFlags>? OnFlagsChanged;

        public LocalOptimizationFlags GetFlags() => Flags;

        public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Flags);

        public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
        {
            Flags.LoopCircuitBreaker = flags.LoopCircuitBreaker;
            Flags.ThinkingTraceCompaction = flags.ThinkingTraceCompaction;
            Flags.GuidedGrammarDecoding = flags.GuidedGrammarDecoding;
            Flags.DeterministicSymbolMap = flags.DeterministicSymbolMap;
            Flags.StructuredDiagnosticExtraction = flags.StructuredDiagnosticExtraction;
            Flags.PrefixStabilization = flags.PrefixStabilization;
            Flags.JitModelPreload = flags.JitModelPreload;
            Flags.CompactOlderToolResults = flags.CompactOlderToolResults;
            OnFlagsChanged?.Invoke(Flags);
            return Task.CompletedTask;
        }
    }
}
