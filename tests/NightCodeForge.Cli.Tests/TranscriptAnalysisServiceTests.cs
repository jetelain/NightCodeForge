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
}
