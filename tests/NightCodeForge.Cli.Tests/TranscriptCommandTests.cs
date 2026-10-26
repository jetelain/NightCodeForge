namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Cli.Commands;
using NightCodeForge.Core.Services;
using Moq;

public class TranscriptCommandTests
{
    private readonly string _testDir;

    public TranscriptCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "ncf_transcript_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    [Fact]
    public void ParseTranscriptOptions_WithDefaults_SetsCurrentDirectory()
    {
        var options = Program.ParseTranscriptOptions(Array.Empty<string>(), out var error);
        Assert.NotNull(options);
        Assert.Null(error);
        Assert.False(options.Verbose);
        Assert.False(options.Json);
    }

    [Fact]
    public void ParseTranscriptOptions_WithVerboseAndJson_SetsFlags()
    {
        var args = new[] { "my/path.md", "--verbose", "--json" };
        var options = Program.ParseTranscriptOptions(args, out var error);

        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal("my/path.md", options.TargetPath);
        Assert.True(options.Verbose);
        Assert.True(options.Json);
    }

    [Fact]
    public void ParseTranscriptOptions_WithUnrecognizedFlag_ReturnsError()
    {
        var args = new[] { "--unknown-flag" };
        var options = Program.ParseTranscriptOptions(args, out var error);

        Assert.Null(options);
        Assert.NotNull(error);
        Assert.Contains("Unrecognized option", error);
    }

    [Fact]
    public async Task ExecuteAsync_WithSingleCleanTranscript_ReturnsZero()
    {
        var filePath = Path.Combine(_testDir, "clean-transcript.md");
        var content = @"---
task_id: 100
stage_id: implementation
agent_profile: Implementation Agent
model: test-model
duration_ms: 12000
tokens:
  prompt: 1000
  completion: 200
  total: 1200
status: Completed
---
# Execution Transcript
### [ 00:00 ] 👤 USER
Start task.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/File.cs"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
Content
";
        await File.WriteAllTextAsync(filePath, content);

        var logged = new List<string>();
        var command = new TranscriptCommand(new TranscriptAnalysisService());
        var exitCode = await command.ExecuteAsync(new TranscriptOptions { TargetPath = filePath }, logged.Add);

        Assert.Equal(0, exitCode);
        Assert.Contains(logged, l => l.Contains("clean-transcript.md"));
        Assert.Contains(logged, l => l.Contains("1,200 total"));
    }

    [Fact]
    public async Task ExecuteAsync_WithFrictionTranscript_ReturnsOneAndDetectsFriction()
    {
        var filePath = Path.Combine(_testDir, "friction-transcript.md");
        var content = @"---
task_id: 101
stage_id: implementation
agent_profile: Implementation Agent
model: test-model
duration_ms: 12000
tokens:
  prompt: 5000
  completion: 1000
  total: 6000
status: Completed
---
# Execution Transcript
### [ 00:00 ] 👤 USER
Start task.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Churn.cs"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Error: Edit #1 failed: could not locate target chunk in 'src/Churn.cs'. Neither exact nor fuzzy indentation match succeeded.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet test --no-build"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
error CS0103: The name 'Foo' does not exist in the current context
";
        await File.WriteAllTextAsync(filePath, content);

        var logged = new List<string>();
        var command = new TranscriptCommand(new TranscriptAnalysisService());
        var exitCode = await command.ExecuteAsync(new TranscriptOptions { TargetPath = filePath }, logged.Add);

        Assert.Equal(1, exitCode); // Has friction -> exit code 1
        Assert.Contains(logged, l => l.Contains("Edit Mismatches:      1"));
        Assert.Contains(logged, l => l.Contains("Generic Tool Errors:  0"));
        Assert.Contains(logged, l => l.Contains("Compiler Diagnostics: 1"));
        Assert.Contains(logged, l => l.Contains("Stale test execution"));
    }

    [Fact]
    public async Task ExecuteAsync_WithDirectory_RendersFleetSummary()
    {
        var file1 = Path.Combine(_testDir, "t1.md");
        var file2 = Path.Combine(_testDir, "t2.md");

        await File.WriteAllTextAsync(file1, "---\ntask_id: 1\nstage_id: task-review\nstatus: Completed\nduration_ms: 1000\n---\n### [ 00:00 ] 👤 USER\nGo\n");
        await File.WriteAllTextAsync(file2, "---\ntask_id: 2\nstage_id: implementation\nstatus: Completed\nduration_ms: 2000\n---\n### [ 00:00 ] 👤 USER\nGo\n");

        var logged = new List<string>();
        var command = new TranscriptCommand(new TranscriptAnalysisService());
        var exitCode = await command.ExecuteAsync(new TranscriptOptions { TargetPath = _testDir }, logged.Add);

        Assert.Equal(0, exitCode);
        Assert.Contains(logged, l => l.Contains("TRANSCRIPT FLEET ANALYSIS"));
        Assert.Contains(logged, l => l.Contains("Found 2 transcript(s)"));
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(-100L, "-")]
    [InlineData(12000L, "00:12")]
    [InlineData(330000L, "05:30")]
    [InlineData(4500000L, "01:15:00")]
    [InlineData(93600000L, "26:00:00")]
    public void FormatDuration_HandlesAllRangesAccurately(long? durationMs, string expected)
    {
        var result = TranscriptCommand.FormatDuration(durationMs);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithMockedAnalysisService_DispatchesExpectedAnalysisCall()
    {
        var filePath = Path.Combine(_testDir, "mock-transcript.md");
        await File.WriteAllTextAsync(filePath, "mock-content");

        var mockService = new Mock<ITranscriptAnalysisService>();
        var fakeAnalysis = new TranscriptAnalysis(
            FilePath: filePath,
            FileName: "mock-transcript.md",
            Frontmatter: null,
            TotalTurns: 1,
            UserTurns: 1,
            AssistantTurns: 0,
            ToolTurns: 0,
            ThinkingTurns: 0,
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
            FrictionPoints: Array.Empty<string>(),
            Recommendations: Array.Empty<string>());

        mockService.Setup(s => s.AnalyzeFileAsync(filePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeAnalysis);

        var command = new TranscriptCommand(mockService.Object);
        var exitCode = await command.ExecuteAsync(new TranscriptOptions { TargetPath = filePath });

        Assert.Equal(0, exitCode);
        mockService.Verify(s => s.AnalyzeFileAsync(filePath, It.IsAny<CancellationToken>()), Times.Once);
    }
}
