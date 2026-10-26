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
            r.Contains("Pre-inject git diff summary and changed files directly into the code review stage prompt"));
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
}

