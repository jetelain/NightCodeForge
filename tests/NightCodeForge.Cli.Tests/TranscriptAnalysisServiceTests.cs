namespace NightCodeForge.Cli.Tests;

using NightCodeForge.Core.Services;

public class TranscriptAnalysisServiceTests
{
    private readonly TranscriptAnalysisService _service = new();

    [Fact]
    public void Analyze_ExtractsToolInvocationsAndArguments()
    {
        var content = @"---
task_id: 101
stage_id: implementation
status: Completed
duration_ms: 12000
tokens:
  prompt: 5000
  completion: 1000
  total: 6000
---
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
Error: Edit #1 failed: could not locate target chunk in 'src/Churn.cs'.
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

        var analysis = _service.Analyze(content, "test.md");

        Assert.Equal(1, analysis.EditFailures);
        Assert.Equal(1, analysis.BashErrors);
        Assert.Equal(1, analysis.CompilerErrors);
        Assert.Contains(analysis.FrictionPoints, fp => fp.Contains("Stale test execution"));
    }

    [Fact]
    public void Analyze_DistinguishesCompactionEventsAndHistoryRewritesWithoutDoubleCounting()
    {
        var content = @"---
task_id: 102
stage_id: implementation
status: Completed
---
### [ 00:00 ] 👤 USER
Start task.
### [ 00:01 ] 🤖 ASSISTANT
Working on it.
### [ 00:02 ] ✂️ **COMPACTION**
[CONTEXT COMPACTION CHECKPOINT]
Summary of past operations.
#### [ 00:03 ] Call 3
⚠ **History rewritten since the previous call** (compaction or message mutation); the messages below replace the previous tail.
#### [ 00:04 ] Call 4
⚠ **History rewritten since the previous call** (compaction or message mutation); the messages below replace the previous tail.
";

        var analysis = _service.Analyze(content, "compaction-test.md");

        Assert.Equal(1, analysis.CompactionEvents);
        Assert.Equal(2, analysis.HistoryRewrites);
    }

    [Fact]
    public void Analyze_DoesNotDoubleCountTestFailuresWhenAssertionFails()
    {
        var content = @"---
task_id: 103
stage_id: implementation
status: Completed
---
### [ 00:00 ] 👤 USER
Run test.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet test"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
  Failed NightCodeForge.Core.Tests.MyServiceTests.Execute_ThrowsException [12 ms]
  Error Message:
   Assert.Equal() Failure: Values are not equal
  Stack Trace:
     at NightCodeForge.Core.Tests.MyServiceTests.cs:line 42
";
        var analysis = _service.Analyze(content, "test-failures.md");

        Assert.Equal(1, analysis.TestFailures);
    }

    [Fact]
    public void Analyze_HandlesNegativeExitCodes()
    {
        var content = @"---
task_id: 104
stage_id: implementation
status: Failed
---
### [ 00:00 ] 👤 USER
Run command.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""run_native"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: -1
Fatal error: Process terminated.
";
        var analysis = _service.Analyze(content, "negative-exit.md");

        Assert.Equal(1, analysis.BashErrors);
    }

    [Fact]
    public void Analyze_DoesNotFlagStaleTestWhenEditingMarkdownDocFiles()
    {
        var content = @"---
task_id: 105
stage_id: implementation
status: Completed
---
### [ 00:00 ] 👤 USER
Update task spec and verify.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""docs/tasks/task-105.md"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet test --no-build"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Passed! All 42 tests passed.
";
        var analysis = _service.Analyze(content, "doc-edit.md");

        Assert.DoesNotContain(analysis.FrictionPoints, fp => fp.Contains("Stale test execution"));
    }

    [Fact]
    public void Analyze_CountsParameterlessToolsAndDoesNotDoubleCountEditFailuresAsGenericErrors()
    {
        var content = @"---
task_id: 106
stage_id: implementation
status: Completed
---
### [ 00:00 ] 👤 USER
Check status.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `git_status`
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `git_status`
**Tool Output:**
Clean working tree.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/File.cs"" }
```
### [ 00:00 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Error: Edit #1 failed: could not locate target chunk in 'src/File.cs'.
";
        var analysis = _service.Analyze(content, "tools.md");

        Assert.Equal(1, analysis.ToolUsage.GetValueOrDefault("git_status", 0));
        Assert.Equal(1, analysis.ToolUsage.GetValueOrDefault("edit", 0));
        Assert.Equal(1, analysis.EditFailures);
        Assert.Equal(0, analysis.ToolErrors);
    }

    [Fact]
    public void Analyze_WithGenericToolErrors_FlagsFrictionAndComputesTotalErrors()
    {
        var content = @"---
task_id: 107
stage_id: implementation
status: Failed
---
### [ 00:00 ] 👤 USER
Run custom tool.
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `custom_tool`
### [ 00:01 ] 🔧 TOOL
**Tool Invocation:** `custom_tool`
**Tool Output:**
Error: File not found in workspace: config.json
";
        var analysis = _service.Analyze(content, "tool-error.md");

        Assert.Equal(1, analysis.ToolErrors);
        Assert.Equal(1, analysis.TotalErrors);
        Assert.True(analysis.HasFriction);
        Assert.Contains(analysis.FrictionPoints, fp => fp.Contains("Tool execution errors") && fp.Contains("1 generic tool error(s)"));
    }

    [Fact]
    public void Analyze_CodeReviewStage_WithExcessiveGitDiffCommands_FlagsCodeReviewOverheadFriction()
    {
        var content = @"---
task_id: 108
stage_id: code-review-fix
status: Completed
---
### [ 00:00 ] 👤 USER
Review the implementation changes.
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff HEAD~1"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
diff --git a/src/A.cs b/src/A.cs
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff --stat origin/main...feature/task-108"" }
```
### [ 00:04 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
src/A.cs | 2 +-
### [ 00:05 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git --no-pager diff origin/main...feature/task-108 -- src/A.cs"" }
```
### [ 00:06 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
+ // change
";
        var analysis = _service.Analyze(content, "code-review-overhead.md");

        Assert.True(analysis.HasFriction);
        Assert.Contains(analysis.FrictionPoints, fp =>
            fp.Contains("Code review exploration overhead: Agent ran 3 'git diff' command(s) to inspect changes for a zero-file edit."));
        Assert.Contains(analysis.Recommendations, r =>
            r.Target == RecommendationTarget.NightCodeForge &&
            (r.Title + " " + r.ActionableDetail).Contains("Pre-inject git diff summary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_CodeReviewStage_WithManyBashCommandsThatAreNotGitDiff_DoesNotFlagCodeReviewOverheadFriction()
    {
        var content = @"---
task_id: 109
stage_id: code-review-fix
status: Completed
---
### [ 00:00 ] 👤 USER
Review the implementation changes.
";
        // Append 18 non-git-diff bash commands (e.g. dotnet test, ls, cat)
        var sb = new System.Text.StringBuilder(content);
        for (int i = 0; i < 18; i++)
        {
            sb.AppendLine($"### [ 00:{i:D2} ] 🤖 ASSISTANT");
            sb.AppendLine("**Tool Invocation:** `bash`");
            sb.AppendLine("```json");
            sb.AppendLine(@"{ ""command"": ""dotnet test --filter Category=Unit"" }");
            sb.AppendLine("```");
            sb.AppendLine($"### [ 00:{i:D2} ] 🔧 TOOL");
            sb.AppendLine("**Tool Invocation:** `bash`");
            sb.AppendLine("**Tool Output:**");
            sb.AppendLine("Passed! Total: 10, Failed: 0");
        }

        var analysis = _service.Analyze(sb.ToString(), "code-review-tests.md");

        Assert.Equal(18, analysis.ToolUsage.GetValueOrDefault("bash", 0));
        Assert.DoesNotContain(analysis.FrictionPoints, fp => fp.Contains("exploration overhead"));
    }

    [Fact]
    public void Analyze_TaskReviewStage_WithManyGitDiffAndBashCommands_DoesNotFlagCodeReviewOverheadFriction()
    {
        var content = @"---
task_id: 110
stage_id: task-review
status: Completed
---
### [ 00:00 ] 👤 USER
Review requirements specification.
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff HEAD"" }
```
### [ 00:02 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff origin/main"" }
```
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff --stat"" }
```
";
        var analysis = _service.Analyze(content, "task-review.md");

        Assert.Equal(3, analysis.ToolUsage.GetValueOrDefault("bash", 0));
        Assert.DoesNotContain(analysis.FrictionPoints, fp => fp.Contains("exploration overhead"));
    }

    [Fact]
    public void Analyze_DeterministicPostReviewStage_DoesNotFlagCodeReviewOverheadFriction()
    {
        var content = @"---
task_id: 111
stage_id: deterministic-post-review
status: Completed
---
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff"" }
```
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff --stat"" }
```
### [ 00:02 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff HEAD~1"" }
```
";
        var analysis = _service.Analyze(content, "post-review.md");

        Assert.DoesNotContain(analysis.FrictionPoints, fp => fp.Contains("exploration overhead"));
    }

    [Fact]
    public void Analyze_DoesNotDoubleCountModelCallsEnvironmentOrMafSections()
    {
        var content = @"---
task_id: 112
stage_id: implementation
status: Completed
---
# Execution Transcript
### [ 00:00 ] 👤 USER
Do clean work.
### [ 00:01 ] 🤖 ASSISTANT
All done without tools.

---
### Environment
Tools: bash, edit
### MAF Pipeline Configuration
Loop threshold: 3
### Model calls
#### [ 00:02 ] Call 1
### [ 00:03 ] 🔧 TOOL
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff"" }
```
**Tool Output:**
Exit Code: 1
error CS0103: Fake error inside Model calls snapshot
";
        var analysis = _service.Analyze(content, "model-calls-section.md");

        Assert.Equal(2, analysis.TotalTurns);
        Assert.Equal(1, analysis.UserTurns);
        Assert.Equal(1, analysis.AssistantTurns);
        Assert.Equal(0, analysis.ToolTurns);
        Assert.Equal(0, analysis.BashErrors);
        Assert.Equal(0, analysis.CompilerErrors);
        Assert.Empty(analysis.ToolUsage);
        Assert.False(analysis.HasFriction);
    }

    [Fact]
    public void Analyze_CorrelatesPatternBAndPatternCTurnFormatsWithoutDoubleCounting()
    {
        var content = @"---
task_id: 113
stage_id: implementation
status: Completed
---
#### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/Sample.cs"" }
```
#### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
class Sample {}
### 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/Sample.cs"" }
```
### 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
class Sample {}
";
        var analysis = _service.Analyze(content, "multi-pattern.md");

        Assert.Equal(2, analysis.ToolUsage.GetValueOrDefault("read", 0));
        var sampleFile = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/Sample.cs");
        Assert.Equal(2, sampleFile.ReadCount);
    }

    [Fact]
    public void Analyze_ExtractsBreakdownsBehavioralSignalsAndCategorizedRecommendations()
    {
        var oversizedOutput = new string('x', 10_500);
        var content = $@"---
task_id: 114
stage_id: implementation
status: Failed
---
### [ 00:00 ] 👤 USER
Fix the build in src/ChurnTarget.cs and update docs/notes.md.
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{{ ""path"": ""src/Target.cs"" }}
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
class Target {{}}
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{{ ""path"": ""src/Target.cs"" }}
```
### [ 00:04 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
{oversizedOutput}
### [ 00:05 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{{ ""path"": ""src/Target.cs"" }}
```
### [ 00:06 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
class Target {{}}
### [ 00:07 ] 🤖 ASSISTANT
**Tool Invocation:** `grep`
```json
{{ ""pattern"": ""MissingSymbol"" }}
```
### [ 00:08 ] 🔧 TOOL
**Tool Invocation:** `grep`
**Tool Output:**
No matches found.
### [ 00:09 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""src/ChurnTarget.cs"", ""old_string"": ""a"", ""new_string"": ""b"" }}
```
### [ 00:10 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:11 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""src/ChurnTarget.cs"", ""old_string"": ""b"", ""new_string"": ""a"" }}
```
### [ 00:12 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:13 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""src/ChurnTarget.cs"", ""old_string"": ""a"", ""new_string"": ""c"" }}
```
### [ 00:14 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:15 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""docs/notes.md"" }}
```
### [ 00:16 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:17 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""docs/notes.md"" }}
```
### [ 00:18 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:19 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{{ ""path"": ""docs/notes.md"" }}
```
### [ 00:20 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:21 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{{ ""command"": ""dotnet test"" }}
```
### [ 01:30 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
src/ChurnTarget.cs(10,5): error CS0103: The name 'Bar' does not exist in the current context
### [ 02:15 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{{ ""command"": ""dotnet test --filter Foo"" }}
```
### [ 02:16 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Command timed out after 120s while running tests.
  Failed MyApp.Tests.SlowTest [100 ms]
### [ 75:12 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{{ ""command"": ""dotnet test"" }}
```
### [ 76:30 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Passed!
";
        var analysis = _service.Analyze(content, "full-friction.md");

        // File friction
        var churnEntry = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/ChurnTarget.cs");
        Assert.Equal(3, churnEntry.EditCount);
        Assert.Equal(3, churnEntry.EditedWithoutReadCount);
        Assert.True(churnEntry.IsSplitCandidate);

        var targetEntry = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/Target.cs");
        Assert.True(targetEntry.ConsecutiveReReadCount >= 3);

        var docEntry = Assert.Single(analysis.FileFriction, f => f.FilePath == "docs/notes.md");
        Assert.False(docEntry.IsSplitCandidate);

        // Tool friction & output bloat
        var readTool = Assert.Single(analysis.ToolFriction, t => t.ToolName == "read");
        Assert.Equal(1, readTool.OversizedOutputCount);

        // Bash & compiler error breakdowns
        Assert.Contains(analysis.BashCommandFriction, b => b.FailureCount > 0 || b.HangOrTimeoutCount > 0);
        Assert.Contains(analysis.CompilerErrorBreakdown, c => c.DiagnosticCode == "CS0103" && c.Count >= 1);

        // Behavioral & latency signals
        Assert.Contains(analysis.BehavioralSignals.RepeatedToolLoops, l => l.ToolName == "read" && l.RepeatCount >= 3);
        Assert.True(analysis.BehavioralSignals.DeadEndSearchCount >= 1);
        Assert.Contains("MyApp.Tests.SlowTest", analysis.BehavioralSignals.FailingTests);
        Assert.NotEmpty(analysis.BehavioralSignals.SlowToolExecutions); // 00:21->01:30 (69s) and 75:12->76:30 (78s)
        Assert.Contains(analysis.BehavioralSignals.SlowToolExecutions, s => s.StartOffset == "75:12" && s.DurationSeconds == 78);
        Assert.NotEmpty(analysis.BehavioralSignals.SlowLlmTurns); // 01:30->02:15 (45s)

        // Categorized recommendations
        Assert.NotEmpty(analysis.UserCodebaseRecommendations);
        Assert.NotEmpty(analysis.NightCodeForgeRecommendations);
    }

    [Fact]
    public async Task AnalyzeDirectoryWindowAsync_OrdersNewestFirstAndReportsTotalAvailableCount()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_window_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var names = new[]
            {
                "2026-10-01-task-1-stage-run1.md",
                "2026-10-02-task-2-stage-run1.md",
                "2026-10-03-task-3-stage-run1.md",
                "2026-10-03-task-4-stage-run1.md",
                "2026-10-03-task-4-stage-run2.md"
            };

            foreach (var name in names)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(tempDir, name),
                    "---\ntask_id: 1\nstage_id: impl\nstatus: Completed\n---\n### [ 00:00 ] 👤 USER\nHi\n");
            }

            var limited = await _service.AnalyzeDirectoryWindowAsync(tempDir, maxTranscripts: 2);
            Assert.Equal(5, limited.TotalAvailableCount);
            Assert.Equal(2, limited.AppliedLimit);
            Assert.Equal(2, limited.Items.Count);
            Assert.Equal("2026-10-03-task-4-stage-run2.md", limited.Items[0].FileName);
            Assert.Equal("2026-10-03-task-4-stage-run1.md", limited.Items[1].FileName);

            var all = await _service.AnalyzeDirectoryWindowAsync(tempDir, maxTranscripts: null);
            Assert.Equal(5, all.TotalAvailableCount);
            Assert.Equal(5, all.Items.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetWorkspaceFrictionHotspotsAsync_AggregatesHotspotsAndSkipsRootNonTranscriptMarkdown()
    {
        var tempWs = Path.Combine(Path.GetTempPath(), "ncf_hotspots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempWs);
        try
        {
            // Workspace with only a non-transcript README.md at root -> Empty hotspots
            await File.WriteAllTextAsync(Path.Combine(tempWs, "README.md"), "# Project Docs\nerror CS9999 example only");
            var emptyHotspots = await _service.GetWorkspaceFrictionHotspotsAsync(tempWs);
            Assert.False(emptyHotspots.HasHotspots);
            Assert.Equal(string.Empty, emptyHotspots.FormatPromptDigest());

            // Add real transcript under docs/transcripts with split candidate + compiler error + failing test
            var transcriptDir = Path.Combine(tempWs, "docs", "transcripts");
            Directory.CreateDirectory(transcriptDir);
            var transcriptContent = @"---
task_id: 210
stage_id: implementation
status: Failed
---
### [ 00:00 ] 👤 USER
Update BigService.cs
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/BigService.cs"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/BigService.cs"" }
```
### [ 00:04 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:05 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/BigService.cs"" }
```
### [ 00:06 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:07 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet test"" }
```
### [ 00:08 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
src/BigService.cs(12,4): error CS1061: 'Foo' does not contain a definition for 'Bar'
  Failed MyApp.Tests.BigServiceTests.WorksCorrectly [42 ms]
";
            await File.WriteAllTextAsync(Path.Combine(transcriptDir, "2026-11-01-task-210-impl.md"), transcriptContent);
            await File.WriteAllTextAsync(Path.Combine(transcriptDir, "2026-11-02-task-211-impl.md"), @"---
task_id: 211
stage_id: implementation
status: Failed
---
### [ 00:00 ] 👤 USER
Verify tests
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet test"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
  Failed MyApp.Tests.BigServiceTests.WorksCorrectly [19 ms]
");

            var hotspots = await _service.GetWorkspaceFrictionHotspotsAsync(tempWs);
            Assert.True(hotspots.HasHotspots);
            Assert.Contains(hotspots.SplitCandidateFiles, f => f.FilePath == "src/BigService.cs");
            Assert.Contains(hotspots.TopCompilerErrors, c => c.DiagnosticCode == "CS1061");
            Assert.Contains("MyApp.Tests.BigServiceTests.WorksCorrectly", hotspots.RecurringFailingTests);

            var digest = hotspots.FormatPromptDigest();
            Assert.Contains("<workspace_friction_hotspots", digest);
            Assert.Contains("src/BigService.cs", digest);
            Assert.Contains("CS1061", digest);
            Assert.Contains("MyApp.Tests.BigServiceTests.WorksCorrectly", digest);
        }
        finally
        {
            if (Directory.Exists(tempWs)) Directory.Delete(tempWs, recursive: true);
        }
    }

    [Fact]
    public void Analyze_DoesNotCrossCorrelateUnfinishedAssistantToolWithDifferentSubsequentTool()
    {
        var content = @"---
task_id: 301
stage_id: impl
status: Failed
---
### [ 00:00 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Heavy.cs"" }
```
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""src/Heavy.cs"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
Exit Code: 1
C:\Users\Dev\repo\src\Heavy.cs(15,8): error CS0246: The type or namespace name 'Missing' could not be found
";

        var analysis = _service.Analyze(content, "unmatched-pending.md");

        Assert.Equal(2, analysis.ToolUsage.GetValueOrDefault("edit", 0));
        Assert.Equal(1, analysis.ToolUsage.GetValueOrDefault("bash", 0));

        var heavyFile = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/Heavy.cs");
        Assert.Equal(2, heavyFile.EditCount);
        Assert.Equal(0, heavyFile.EditFailureCount);

        var bashTool = Assert.Single(analysis.ToolFriction, t => t.ToolName == "bash");
        Assert.Equal(1, bashTool.FailureCount);

        var bashCmd = Assert.Single(analysis.BashCommandFriction);
        Assert.Equal(1, bashCmd.FailureCount);
        Assert.Equal(1, bashCmd.LastExitCode);

        var diag = Assert.Single(analysis.CompilerErrorBreakdown, c => c.DiagnosticCode == "CS0246");
        Assert.Equal(@"C:\Users\Dev\repo\src\Heavy.cs", diag.SampleFilePath);
    }

    [Fact]
    public void Analyze_WhenToolResultEchoesInvocationAndJsonArgs_DoesNotDoubleCountInvocation()
    {
        var content = @"---
task_id: 302
stage_id: impl
status: Completed
---
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/Once.cs"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `read`
```json
{ ""path"": ""src/Once.cs"" }
```
**Tool Output:**
public class Once {}
";

        var analysis = _service.Analyze(content, "echoed-args.md");

        Assert.Equal(1, analysis.ToolUsage.GetValueOrDefault("read", 0));
        var file = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/Once.cs");
        Assert.Equal(1, file.ReadCount);
    }

    [Fact]
    public void Analyze_WhenConsecutiveReReadsTriggerSplitCandidateWithoutErrors_FlagsFrictionAndRecommendation()
    {
        var content = @"---
task_id: 303
stage_id: impl
status: Completed
---
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/LargeReader.cs"", ""startLine"": 1, ""endLine"": 100 }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
// lines 1-100
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/LargeReader.cs"", ""startLine"": 101, ""endLine"": 200 }
```
### [ 00:04 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
// lines 101-200
### [ 00:05 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/LargeReader.cs"", ""startLine"": 201, ""endLine"": 300 }
```
### [ 00:06 ] 🔧 TOOL
**Tool Invocation:** `read`
**Tool Output:**
// lines 201-300
";

        var analysis = _service.Analyze(content, "reread-split.md");

        Assert.Empty(analysis.BehavioralSignals.RepeatedToolLoops);
        var file = Assert.Single(analysis.FileFriction, f => f.FilePath == "src/LargeReader.cs");
        Assert.Equal(3, file.ConsecutiveReReadCount);
        Assert.True(file.IsSplitCandidate);
        Assert.True(analysis.HasFriction);
        Assert.Contains(analysis.FrictionPoints, fp => fp.Contains("Repeated consecutive file re-reads without edits"));
        Assert.Contains(analysis.UserCodebaseRecommendations, r =>
            r.Category == FrictionCategory.FileArchitecture && r.Subject == "src/LargeReader.cs");
    }

    [Fact]
    public void Analyze_WhenToolResultEchoesInvocationWithoutToolOutputMarker_DetectsSubsequentSlowLlmTurn()
    {
        var content = @"---
task_id: 304
stage_id: impl
status: Completed
---
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `read`
```json
{ ""path"": ""src/App.cs"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `read`
public class App {}
### [ 00:45 ] 🤖 ASSISTANT
Done inspecting App.cs.
";

        var analysis = _service.Analyze(content, "slow-llm-after-echoed-tool.md");

        var slowTurn = Assert.Single(analysis.BehavioralSignals.SlowLlmTurns);
        Assert.Equal("00:02", slowTurn.StartOffset);
        Assert.Equal("00:45", slowTurn.EndOffset);
        Assert.Equal(43, slowTurn.DurationSeconds);
    }

    [Fact]
    public void Analyze_IgnoresHeadingsAndErrorLiteralsInsideEnvironmentSystemPromptAndReadToolOutputs()
    {
        var content = @"---
task_id: 305
stage_id: impl
status: Completed
---
# Execution Transcript

### Environment

**Available tools**
- read
- bash

**System prompt**
````
You are an implementation agent.
### Coding Rules & System Overview
Never produce `error CS0103: Fake system prompt example` or `Exit Code: 1`.
### [ 00:00 ] 🤖 ASSISTANT
Fake turn inside system prompt fence.
````

### [ 00:00 ] 👤 USER
Inspect the parser and summarize.

### [ 00:01 ] 🤖 ASSISTANT
### System Architecture
### User Interface
**Tool Invocation:** `read`
````json
{ ""path"": ""src/TranscriptAnalysisService.cs"" }
````

### [ 00:02 ] 📤 TOOL_RESULT
**Tool:** `read`
````
1: // Regex test cases:
2: // error CS0103: The name 'Fake' does not exist in the current context
3: // Exit Code: 1
4: // Error: Edit #1 failed: could not locate target chunk
5: //   Failed MyApp.Tests.FakeTest [10 ms]
6: // ### [ 00:09 ] 🤖 ASSISTANT
7: // **Tool Invocation:** `bash`
````

### [ 00:03 ] 🤖 ASSISTANT
All clean.
";

        var analysis = _service.Analyze(content, "fenced-false-positives.md");

        Assert.Equal(4, analysis.TotalTurns);
        Assert.Equal(1, analysis.UserTurns);
        Assert.Equal(2, analysis.AssistantTurns);
        Assert.Equal(1, analysis.ToolTurns);
        Assert.Equal(1, analysis.ToolUsage.GetValueOrDefault("read", 0));
        Assert.Equal(0, analysis.ToolUsage.GetValueOrDefault("bash", 0));
        Assert.Equal(0, analysis.CompilerErrors);
        Assert.Equal(0, analysis.BashErrors);
        Assert.Equal(0, analysis.EditFailures);
        Assert.Equal(0, analysis.TestFailures);
        Assert.Equal(0, analysis.ToolErrors);
        Assert.Empty(analysis.ToolFriction);
        Assert.False(analysis.HasFriction);
    }

    [Fact]
    public void Analyze_IgnoresDiagnosticsAndTimeoutsInGitDiffOutput_AndExtractsMsBuildNodePrefixFilePaths()
    {
        var content = @"---
task_id: 306
stage_id: impl
status: Completed
---
### [ 00:00 ] 👤 USER
Check diff and build.
### [ 00:01 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""git diff"" }
```
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
diff --git a/src/Service.cs b/src/Service.cs
+// error CS0246: Fake diff comment
+// Command timed out after 30s
Exit Code: 0
### [ 00:03 ] 🤖 ASSISTANT
**Tool Invocation:** `bash`
```json
{ ""command"": ""dotnet build"" }
```
### [ 00:04 ] 🔧 TOOL
**Tool Invocation:** `bash`
**Tool Output:**
  1>C:\Users\Dev\repo\src\Heavy.cs(15,8): error CS0246: The type or namespace name 'Missing' could not be found [C:\Users\Dev\repo\MyProj.csproj]
Exit Code: 1
";

        var analysis = _service.Analyze(content, "git-diff-and-msbuild-node.md");

        Assert.Equal(1, analysis.CompilerErrors);
        var diag = Assert.Single(analysis.CompilerErrorBreakdown);
        Assert.Equal("CS0246", diag.DiagnosticCode);
        Assert.Equal(1, diag.Count);
        Assert.Equal(@"C:\Users\Dev\repo\src\Heavy.cs", diag.SampleFilePath);

        var gitDiffEntry = Assert.Single(analysis.BashCommandFriction, b => b.CommandPattern == "git diff");
        Assert.Equal(0, gitDiffEntry.FailureCount);
        Assert.Equal(0, gitDiffEntry.HangOrTimeoutCount);
    }

    [Fact]
    public void Analyze_CorrelatesParallelPatternBParameterlessToolCalls_AndExcludesMarkdownFromHighChurnFiles()
    {
        var content = @"---
task_id: 307
stage_id: impl
status: Completed
---
### [ 00:01 ] 🔧 TOOL
**Tool Invocation:** `git_status`
### [ 00:02 ] 🔧 TOOL
**Tool Invocation:** `git_status`
### [ 00:03 ] 📤 TOOL_RESULT
**Tool:** `git_status`
````
On branch main
````
### [ 00:04 ] 📤 TOOL_RESULT
**Tool:** `git_status`
````
Clean working tree
````
### [ 00:05 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""docs/notes.md"", ""old_string"": ""a"", ""new_string"": ""b"" }
```
### [ 00:06 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:07 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""docs/notes.md"", ""old_string"": ""b"", ""new_string"": ""c"" }
```
### [ 00:08 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
### [ 00:09 ] 🤖 ASSISTANT
**Tool Invocation:** `edit`
```json
{ ""path"": ""docs/notes.md"", ""old_string"": ""c"", ""new_string"": ""d"" }
```
### [ 00:10 ] 🔧 TOOL
**Tool Invocation:** `edit`
**Tool Output:**
Edit succeeded.
";

        var analysis = _service.Analyze(content, "parallel-pattern-b.md");

        Assert.Equal(2, analysis.ToolUsage.GetValueOrDefault("git_status", 0));
        Assert.Empty(analysis.HighChurnFiles);
        Assert.DoesNotContain(analysis.FrictionPoints, fp => fp.Contains("notes.md"));
        Assert.False(analysis.HasFriction);
    }
}

