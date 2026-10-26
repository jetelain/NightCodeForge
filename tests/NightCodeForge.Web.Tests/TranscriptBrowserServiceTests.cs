using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
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

    private TranscriptBrowserService CreateService(
        IOllamaChatClientFactory? chatClientFactory = null,
        NightCodeForge.Core.Services.ITranscriptAnalysisService? customAnalysisService = null)
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var factory = chatClientFactory ?? new Mock<IOllamaChatClientFactory>().Object;
        var analysisService = customAnalysisService ?? new NightCodeForge.Core.Services.TranscriptAnalysisService();
        var logger = NullLogger<TranscriptBrowserService>.Instance;
        return new TranscriptBrowserService(_fakeGlobalOptService, scopeFactory, factory, analysisService, logger);
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
  guided_grammar_decoding: false
  deterministic_symbol_map: true
  structured_diagnostic_extraction: false
  jit_model_preload: true
kv_cache_hit_rate: 0.92
status: Completed
---

# Title
Body text
";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("test.md", content);

        Assert.NotNull(summary);
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
        Assert.False(summary.Optimizations.GuidedGrammarDecoding);
        Assert.True(summary.Optimizations.DeterministicSymbolMap);
        Assert.False(summary.Optimizations.StructuredDiagnosticExtraction);
        Assert.True(summary.Optimizations.JitModelPreload);
    }

    [Fact]
    public void ParseSummaryFromContent_WithoutFrontmatter_ExtractsFromFilename()
    {
        var content = @"# Title Without Frontmatter
Just raw discussion";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("2026-10-15-task-42-unit-tests-failed.md", content);

        Assert.NotNull(summary);
        Assert.Equal(42, summary.TaskId);
        Assert.Equal("unit-tests", summary.StageId);
        Assert.Equal("failed", summary.Status);
    }

    [Fact]
    public void ParseSummaryFromContent_AgentsMdOrNonTranscript_ReturnsNull()
    {
        var content = @"# Project Guidelines (AGENTS.md)
Rules for agent execution:
- Do not make silent failures
- Follow clean break policy";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("AGENTS.md", content);

        Assert.Null(summary);
    }

    [Fact]
    public async Task GetTranscriptsAsync_IgnoresRootFiles_DiscoversDocsAndWorktreeTranscripts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_transcripts_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Root file: AGENTS.md (must be ignored)
            await File.WriteAllTextAsync(Path.Combine(tempDir, "AGENTS.md"), "# Guidelines\nDo not fail");

            // docs/transcripts file
            var docsDir = Path.Combine(tempDir, "docs", "transcripts");
            Directory.CreateDirectory(docsDir);
            await File.WriteAllTextAsync(Path.Combine(docsDir, "2026-10-23-task-10-implementation-completed.md"), "---\ntask_id: 10\nstage_id: implementation\nstatus: Completed\n---\n# Transcript");

            // .nightcodeforge/worktrees/task-12/docs/transcripts file
            var wtDir = Path.Combine(tempDir, ".nightcodeforge", "worktrees", "task-12", "docs", "transcripts");
            Directory.CreateDirectory(wtDir);
            await File.WriteAllTextAsync(Path.Combine(wtDir, "2026-10-23-task-12-task_review-completed.md"), "---\ntask_id: 12\nstage_id: task_review\nstatus: Completed\n---\n# Worktree Transcript");

            var service = CreateService();
            var results = await service.GetTranscriptsAsync(tempDir);

            Assert.Equal(2, results.Count);
            Assert.DoesNotContain(results, t => t.FileName == "AGENTS.md");
            Assert.Contains(results, t => t.TaskId == 10 && t.StageId == "implementation");
            Assert.Contains(results, t => t.TaskId == 12 && t.StageId == "task_review");
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
    public async Task RunDiagnosticsAsync_WhenExecutionCompleted_SuggestsJitModelPreload()
    {
        var service = CreateService();

        var summary = new TranscriptSummary(
            FileName: "clean.md",
            FilePath: "clean.md",
            TaskId: 10,
            StageId: "clean-stage",
            ModelName: "llama3",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 200,
            CompletionTokens: 100,
            DurationMs: 5000,
            Status: "Completed");

        var detail = new ParsedTranscriptDetail(summary, "All tests passed cleanly.", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        var report = await service.RunDiagnosticsAsync(detail, "Local");

        Assert.NotNull(report);
        Assert.Contains("Clean", report.FailureCategory);
        Assert.Contains(report.SuggestedActions, a => a.TargetKey == "JitModelPreload");
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WhenCompilerErrorsPresent_SuggestsDeterministicSymbolMap()
    {
        var service = CreateService();

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
            Optimizations: new LocalOptimizationFlags());

        var content = "Build output: error CS0117: 'Bar' does not contain a definition for 'Baz'.";
        var detail = new ParsedTranscriptDetail(summary, content, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        var report = await service.RunDiagnosticsAsync(detail, "Local");

        Assert.NotNull(report);
        Assert.Contains("Compilation", report.FailureCategory);
        Assert.Contains(report.SuggestedActions, a => a.TargetKey == "DeterministicSymbolMap");
    }

    [Fact]
    public async Task RunDiagnosticsAsync_WithLiveChatClient_ReturnsConvertedLlmReport()
    {
        var mockChatClient = new Mock<Microsoft.Extensions.AI.IChatClient>();
        mockChatClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<Microsoft.Extensions.AI.ChatMessage>>(), It.IsAny<Microsoft.Extensions.AI.ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
                "Root cause category: InfiniteLoop\nDiagnosis: Model repeatedly tried reading missing file.\nSuggested action: Check file existence.")));

        var mockFactory = new Mock<IOllamaChatClientFactory>();
        mockFactory
            .Setup(f => f.CreateClient(null, OllamaRoutingTarget.Cloud))
            .Returns(mockChatClient.Object);

        var service = CreateService(mockFactory.Object);

        var summary = new TranscriptSummary(
            FileName: "test.md",
            FilePath: "test.md",
            TaskId: 10,
            StageId: "test-stage",
            ModelName: "llama3",
            Timestamp: DateTime.UtcNow,
            PromptTokens: 200,
            CompletionTokens: 100,
            DurationMs: 5000,
            Status: "Failed",
            Optimizations: new LocalOptimizationFlags());

        var detail = new ParsedTranscriptDetail(summary, "raw content", null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ParsedTranscriptTurn>());

        var report = await service.RunDiagnosticsAsync(detail, "Cloud");

        Assert.NotNull(report);
        Assert.Equal("InfiniteLoop", report.FailureCategory);
        mockFactory.Verify(f => f.CreateClient(null, OllamaRoutingTarget.Cloud), Times.Once);
    }

    [Fact]
    public async Task ApplyRemediationAsync_TogglesFlagInGlobalSettingsAndAgentProfiles()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var service = CreateService();

        _fakeGlobalOptService.Flags.JitModelPreload = false;

        var suggestion = new RemediationSuggestion(
            Title: "Enable JIT Model Preload",
            Description: "Pre-loads model weights",
            ActionType: "ToggleOptimizationFlag",
            TargetKey: "JitModelPreload",
            TargetValue: true);

        await service.ApplyRemediationAsync(suggestion);

        // Global flag updated
        Assert.True(_fakeGlobalOptService.Flags.JitModelPreload);

        // Database agent profiles updated
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profiles = await db.AgentProfiles.ToListAsync();
        Assert.NotEmpty(profiles);
        foreach (var p in profiles)
        {
            Assert.True(p.OptimizationFlags?.JitModelPreload ?? false);
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

            var service = CreateService();

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

    [Fact]
    public async Task GetTranscriptAnalysisAsync_And_GetFleetAnalysisAsync_ReturnExpectedAnalyses()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_fleet_test_" + Guid.NewGuid().ToString("N"));
        var transcriptsDir = Path.Combine(tempDir, "docs", "transcripts");
        Directory.CreateDirectory(transcriptsDir);

        try
        {
            var file1 = Path.Combine(transcriptsDir, "2026-10-15-task-10-stage-1-failed.md");
            var content1 = @"---
task_id: 10
stage_id: stage-1
status: Failed
duration_ms: 1000
tokens:
  total: 500
---
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `EditFile`
```json
{ ""path"": ""foo.cs"" }
```
### [ 00:02 ] 🛠️ TOOL
neither exact nor fuzzy indentation match succeeded
";
            await File.WriteAllTextAsync(file1, content1);

            var service = CreateService();
            var analysis = await service.GetTranscriptAnalysisAsync(file1);
            Assert.NotNull(analysis);
            Assert.True(analysis.HasFriction);
            Assert.Equal(1, analysis.EditFailures);

            var fleet = await service.GetFleetAnalysisAsync(tempDir);
            Assert.NotEmpty(fleet);
            Assert.Contains(fleet, a => a.FileName.Contains("task-10"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetTranscriptDetailAsync_WithMockedAnalysisService_AttachesCustomAnalysis()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var content = @"---
task_id: 99
stage_id: test-stage
status: Completed
---
# Transcript
";
            await File.WriteAllTextAsync(tempFile, content);

            var mockAnalysisService = new Mock<NightCodeForge.Core.Services.ITranscriptAnalysisService>();
            var expectedAnalysis = new NightCodeForge.Core.Services.TranscriptAnalysis(
                FilePath: tempFile,
                FileName: Path.GetFileName(tempFile),
                Frontmatter: null,
                TotalTurns: 5,
                UserTurns: 1,
                AssistantTurns: 2,
                ToolTurns: 2,
                ThinkingTurns: 1,
                ToolUsage: new Dictionary<string, int>(),
                BashErrors: 0,
                EditFailures: 0,
                ToolErrors: 0,
                CompilerErrors: 0,
                TestFailures: 0,
                CompactionEvents: 0,
                HistoryRewrites: 0,
                TopReadFiles: new List<KeyValuePair<string, int>>(),
                TopEditedFiles: new List<KeyValuePair<string, int>>(),
                HighChurnFiles: new List<KeyValuePair<string, int>>(),
                FrictionPoints: new[] { "Mocked friction point" },
                Recommendations: new[] { "Mocked recommendation" });

            mockAnalysisService.Setup(s => s.Analyze(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(expectedAnalysis);

            var service = CreateService(customAnalysisService: mockAnalysisService.Object);
            var detail = await service.GetTranscriptDetailAsync(tempFile);

            Assert.NotNull(detail);
            Assert.NotNull(detail.Analysis);
            Assert.Contains("Mocked friction point", detail.Analysis.FrictionPoints);
            Assert.True(detail.Metadata.HasFriction);
            mockAnalysisService.Verify(s => s.Analyze(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseSummaryFromContent_InvalidTranscript_ReturnsNullWithoutCallingAnalysisService()
    {
        var mockAnalysisService = new Mock<NightCodeForge.Core.Services.ITranscriptAnalysisService>();
        var nonTranscriptContent = "# Project Overview\nThis is just a regular documentation file without frontmatter.";

        var summary = TranscriptBrowserService.ParseSummaryFromContent("overview.md", nonTranscriptContent, mockAnalysisService.Object);

        Assert.Null(summary);
        mockAnalysisService.Verify(s => s.Analyze(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void ParseSummaryFromContent_WithAnalysisService_SetsHasFrictionAndTotalErrors()
    {
        var validContent = @"---
task_id: 42
stage_id: test-stage
status: Failed
---
# Transcript Content
";
        var mockAnalysisService = new Mock<NightCodeForge.Core.Services.ITranscriptAnalysisService>();
        var fakeAnalysis = new NightCodeForge.Core.Services.TranscriptAnalysis(
            FilePath: "test.md",
            FileName: "test.md",
            Frontmatter: null,
            TotalTurns: 2,
            UserTurns: 1,
            AssistantTurns: 1,
            ToolTurns: 0,
            ThinkingTurns: 0,
            ToolUsage: new Dictionary<string, int>(),
            BashErrors: 1,
            EditFailures: 1,
            ToolErrors: 2,
            CompilerErrors: 1,
            TestFailures: 1,
            CompactionEvents: 0,
            HistoryRewrites: 0,
            TopReadFiles: new List<KeyValuePair<string, int>>(),
            TopEditedFiles: new List<KeyValuePair<string, int>>(),
            HighChurnFiles: new List<KeyValuePair<string, int>>(),
            FrictionPoints: new[] { "Some error" },
            Recommendations: Array.Empty<string>());

        mockAnalysisService.Setup(s => s.Analyze(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(fakeAnalysis);

        var summary = TranscriptBrowserService.ParseSummaryFromContent("test.md", validContent, mockAnalysisService.Object);

        Assert.NotNull(summary);
        Assert.True(summary.HasFriction);
        Assert.Equal(6, summary.TotalErrors); // 1 + 1 + 2 + 1 + 1 = 6
    }

    private class FakeGlobalOptService : IGlobalOptimizationSettingsService
    {
        public LocalOptimizationFlags Flags { get; } = new();

        public event Action<LocalOptimizationFlags>? OnFlagsChanged;

        public LocalOptimizationFlags GetFlags() => Flags;

        public Task<LocalOptimizationFlags> LoadFlagsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Flags);

        public Task UpdateFlagsAsync(LocalOptimizationFlags flags, CancellationToken cancellationToken = default)
        {
            Flags.GuidedGrammarDecoding = flags.GuidedGrammarDecoding;
            Flags.DeterministicSymbolMap = flags.DeterministicSymbolMap;
            Flags.StructuredDiagnosticExtraction = flags.StructuredDiagnosticExtraction;
            Flags.JitModelPreload = flags.JitModelPreload;
            OnFlagsChanged?.Invoke(Flags);
            return Task.CompletedTask;
        }
    }
}
