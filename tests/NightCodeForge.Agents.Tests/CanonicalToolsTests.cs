using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;

namespace NightCodeForge.Agents.Tests;

public class CanonicalToolsTests
{
    [Fact]
    public async Task ReadAsync_SlicesLinesWith1IndexedOffset()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-tools-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            var content = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"Line {i}"));
            await File.WriteAllTextAsync(filePath, content);

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var result = await tools.ReadAsync("file.txt", offset: 5, limit: 3);

            Assert.Contains("Line 5", result);
            Assert.Contains("Line 6", result);
            Assert.Contains("Line 7", result);
            Assert.DoesNotContain("Line 4", result);
            Assert.DoesNotContain("Line 8", result);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditAsync_AppliesMultiDisjointEdits()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-tools-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "Service.cs");
            var code = @"using System;

public class Service
{
    public int Value { get; set; } = 1;

    public void DoWork()
    {
        Console.WriteLine(Value);
    }
}";
            await File.WriteAllTextAsync(filePath, code);

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var edits = new List<EditChunk>
            {
                new("using System;", "using System;\nusing System.Threading.Tasks;"),
                new("public int Value { get; set; } = 1;", "public int Value { get; set; } = 42;")
            };

            var editResult = await tools.EditAsync("Service.cs", edits);
            Assert.Contains("Successfully applied 2 edit(s)", editResult);

            var updated = await File.ReadAllTextAsync(filePath);
            Assert.Contains("using System.Threading.Tasks;", updated);
            Assert.Contains("Value { get; set; } = 42;", updated);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditAsync_FuzzyIndentationMatching_ReindentsReplacementCode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-tools-test3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var filePath = Path.Combine(tempDir, "Indented.cs");
            // File has 8 spaces indent
            var code = "namespace MyNamespace\n{\n    public class Foo\n    {\n        public void Bar()\n        {\n            int a = 10;\n        }\n    }\n}";
            await File.WriteAllTextAsync(filePath, code);

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            // LLM sends edit with only 2 spaces indent
            var edits = new List<EditChunk>
            {
                new("  public void Bar()\n  {\n    int a = 10;\n  }", "  public void Bar()\n  {\n    int a = 20;\n    int b = 30;\n  }")
            };

            await tools.EditAsync("Indented.cs", edits);

            var updated = await File.ReadAllTextAsync(filePath);
            Assert.Contains("int a = 20;", updated);
            Assert.Contains("int b = 30;", updated);
            // Verify indentation was adapted to surrounding 8 spaces
            Assert.Contains("        public void Bar()", updated);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private class FakeEnv : NightCodeForge.Core.Interfaces.IExecutionEnvironment
    {
        public NightCodeForge.Core.Interfaces.ExecutionTier Tier => NightCodeForge.Core.Interfaces.ExecutionTier.BareHost;
        public NightCodeForge.Core.Interfaces.CommandResult NextResult { get; set; } = new(0, "Success", string.Empty, TimeSpan.Zero);
        public int? LastTimeoutSeconds { get; private set; }
        public string? LastCommand { get; private set; }

        public Task<NightCodeForge.Core.Interfaces.CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            LastTimeoutSeconds = timeoutSeconds;
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task BashAsync_DefaultsToDynamicCombinedSuiteTimeoutFloorOf300Seconds()
    {
        var env = new FakeEnv();
        var tools = new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = AppContext.BaseDirectory,
            PathResolver = TestPathResolver.Create(),
            ExecutionEnv = env,
            TestTimeoutSeconds = 75,
            BuildTimeoutSeconds = 45
        }.Build();

        await tools.BashAsync("dotnet test");

        // Math.Max(300, 75 + 45) = 300
        Assert.Equal(300, env.LastTimeoutSeconds);
        Assert.Equal("dotnet test", env.LastCommand);
    }

    [Fact]
    public async Task BashAsync_UsesSumOfConfiguredBuildAndTestTimeoutsWhenExceeding300Seconds()
    {
        var env = new FakeEnv();
        var tools = new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = AppContext.BaseDirectory,
            PathResolver = TestPathResolver.Create(),
            ExecutionEnv = env,
            TestTimeoutSeconds = 200,
            BuildTimeoutSeconds = 150
        }.Build();

        await tools.BashAsync("dotnet test");

        // Math.Max(300, 200 + 150) = 350
        Assert.Equal(350, env.LastTimeoutSeconds);
        Assert.Equal("dotnet test", env.LastCommand);
    }

    [Fact]
    public async Task BashAsync_ExplicitTimeoutBelowSuiteCeiling_IsPromotedToCeiling()
    {
        var env = new FakeEnv();
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            executionEnv: env,
            pathResolver: TestPathResolver.Create());

        await tools.BashAsync("git status", timeout: 60);

        // Combined suite timeout is 300 (defaults: 120 + 120 = 240, floor 300). Explicit 60 is promoted to 300.
        Assert.Equal(300, env.LastTimeoutSeconds);
    }

    [Fact]
    public async Task BashAsync_ExplicitTimeoutAboveSuiteCeiling_IsHonored()
    {
        var env = new FakeEnv();
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            executionEnv: env,
            pathResolver: TestPathResolver.Create());

        await tools.BashAsync("long-command", timeout: 500);

        Assert.Equal(500, env.LastTimeoutSeconds);
    }

    [Fact]
    public async Task BashAsync_DetectsExitCode124AsTimeout()
    {
        var env = new FakeEnv
        {
            NextResult = new NightCodeForge.Core.Interfaces.CommandResult(124, string.Empty, "Timed out", TimeSpan.FromSeconds(300))
        };
        var tools = new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = AppContext.BaseDirectory,
            PathResolver = TestPathResolver.Create(),
            ExecutionEnv = env,
            TestTimeoutSeconds = 30
        }.Build();

        var output = await tools.BashAsync("npm test");

        Assert.Contains("[TIMEOUT]", output);
        Assert.Contains("300s", output);
        Assert.Contains("hanging tests", output);
    }

    private class TimeoutThrowingEnv : NightCodeForge.Core.Interfaces.IExecutionEnvironment
    {
        public NightCodeForge.Core.Interfaces.ExecutionTier Tier => NightCodeForge.Core.Interfaces.ExecutionTier.BareHost;
        public Task<NightCodeForge.Core.Interfaces.CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            throw new TimeoutException("Simulated process hang");
        }
    }

    [Fact]
    public async Task BashAsync_HandlesTimeoutExceptionCleanly()
    {
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            executionEnv: new TimeoutThrowingEnv(),
            pathResolver: TestPathResolver.Create());

        var output = await tools.BashAsync("rg -n 'foo' -r .");

        Assert.Contains("Exit Code: 124", output);
        Assert.Contains("[TIMEOUT]", output);
        Assert.Contains("300s", output);
    }

    [Fact]
    public async Task BashAsync_Tier1Clamping_WhenOutputExceedsMaxStageOutputChars_TailTruncates()
    {
        var env = new FakeEnv();
        var tools = new TestCanonicalToolsBuilder
        {
            WorkspaceRoot = AppContext.BaseDirectory,
            PathResolver = TestPathResolver.Create(),
            ExecutionEnv = env
        }.Build();

        var massiveStdout = new string('A', 50000) + "\nFINAL_IMPORTANT_LINE";
        env.NextResult = new NightCodeForge.Core.Interfaces.CommandResult(0, massiveStdout, string.Empty, TimeSpan.Zero);

        var result = await tools.BashAsync("run-heavy-script");

        Assert.Contains("Exit Code: 0", result);
        Assert.Contains("... [Output truncated to last 32768 characters] ...", result);
        Assert.Contains("FINAL_IMPORTANT_LINE", result);
        Assert.DoesNotContain(new string('A', 40000), result);
    }

    private class FakePathResolver : NightCodeForge.Core.Interfaces.IWorkspacePathResolver
    {
        private readonly string _baseDir;
        private readonly NightCodeForge.Core.Interfaces.IWorkspacePathResolver _inner = TestPathResolver.Create();
        public FakePathResolver(string baseDir) => _baseDir = baseDir;

        public string GetAgentWorkingClonePath(int workspaceId) => Path.Combine(_baseDir, workspaceId.ToString());
        public string GetHostRemoteUrl(string repositoryPath) => repositoryPath;
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath)
            => ResolveCruciblePath(workspaceId, relativeOrContainerPath, null);
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath, int? taskId)
        {
            var rel = relativeOrContainerPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            if (NightCodeForge.Infrastructure.Execution.WorkspacePathResolver.TryResolveTmpSubPath(relativeOrContainerPath, out _))
            {
                return Path.Combine(_baseDir, workspaceId.ToString(), rel);
            }
            var root = taskId.HasValue
                ? Path.Combine(_baseDir, workspaceId.ToString(), $"task-{taskId.Value}")
                : Path.Combine(_baseDir, workspaceId.ToString());
            return string.IsNullOrEmpty(rel) ? root : Path.Combine(root, rel);
        }
        public string ResolveCruciblePath(string workspaceRoot, string relativeOrContainerPath, int? taskId = null)
            => _inner.ResolveCruciblePath(workspaceRoot, relativeOrContainerPath, taskId);
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "user";
    }

    [Fact]
    public void ResolveFullPath_WhenRootCrucibleDirectoryExists_ResolvesCruciblePathForNewFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-crucible-resolve-" + Guid.NewGuid().ToString("N"));
        var wksDir = Path.Combine(tempDir, "42");
        Directory.CreateDirectory(wksDir);

        try
        {
            var fakeResolver = new FakePathResolver(tempDir);
            var tools = TestCanonicalToolsBuilder.Create(
                @"C:\host\repo",
                pathResolver: fakeResolver,
                workspaceId: 42);

            var resolved = tools.ResolveFullPath("src/NewFile.cs");
            Assert.Equal(Path.Combine(wksDir, "src", "NewFile.cs"), resolved);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ProposeTasks_AddsToPendingProposals_ReturnsFormattedJson()
    {
        var tools = TestCanonicalToolsBuilder.Create(AppContext.BaseDirectory, pathResolver: TestPathResolver.Create());
        var proposals = new List<NightCodeForge.Core.Domain.TaskProposal>
        {
            new("Fix memory leak", "Disposed streams properly", "bug"),
            new("Add XML doc comments", "Add summary tags to public methods", "chore")
        };

        var result = tools.ProposeTasks(proposals);

        Assert.Equal(2, tools.PendingProposals.Count);
        Assert.Equal("Fix memory leak", tools.PendingProposals[0].Title);
        Assert.Equal("chore", tools.PendingProposals[1].Type);
        Assert.Contains("Proposed 2 backlog task(s)", result);
        Assert.Contains("Fix memory leak", result);
    }

    [Fact]
    public void ResolveFullPath_OnlyRemapsWorkspacePrefix_AndResolvesCloneAndWorktree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "test-repo-" + Guid.NewGuid().ToString("N"));
        var cloneDir = Path.Combine(tempRoot, "clone");
        var taskDir = Path.Combine(tempRoot, "task-42");
        Directory.CreateDirectory(cloneDir);
        Directory.CreateDirectory(taskDir);

        var cloneTools = TestCanonicalToolsBuilder.Create(cloneDir, pathResolver: TestPathResolver.Create());

        // 1. /workspace/clone/Calculator.cs resolves to cloneDir/Calculator.cs
        var res1 = cloneTools.ResolveFullPath("/workspace/clone/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "Calculator.cs")), res1);

        // 2. /workspace/Calculator.cs resolves to cloneDir/Calculator.cs
        var res2 = cloneTools.ResolveFullPath("/workspace/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "Calculator.cs")), res2);

        // 3. Calculator.cs (pure relative) resolves to cloneDir/Calculator.cs
        var res3 = cloneTools.ResolveFullPath("Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "Calculator.cs")), res3);

        // 4. clone/Calculator.cs (relative without /workspace) is NOT stripped
        var res4 = cloneTools.ResolveFullPath("clone/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "clone", "Calculator.cs")), res4);

        // 4b. /clone/Calculator.cs (rooted container clone path) resolves to cloneDir/Calculator.cs
        var res4b = cloneTools.ResolveFullPath("/clone/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "Calculator.cs")), res4b);

        // 5. Worktree-scoped tools
        var taskTools = TestCanonicalToolsBuilder.Create(taskDir, pathResolver: TestPathResolver.Create(), taskId: 42);
        var res5 = taskTools.ResolveFullPath("/workspace/task-42/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(taskDir, "Calculator.cs")), res5);

        var res6 = taskTools.ResolveFullPath("/workspace/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(taskDir, "Calculator.cs")), res6);

        var res7 = taskTools.ResolveFullPath("/workspace/clone/Calculator.cs");
        Assert.Equal(Path.GetFullPath(Path.Combine(cloneDir, "Calculator.cs")), res7);
    }

    [Fact]
    public void ResolveFullPath_DirectRepositoryRootWithoutCloneSubdir_ResolvesCloneAndWorkspacePrefixesDirectly()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRepo);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempRepo, pathResolver: TestPathResolver.Create());

            var p1 = tools.ResolveFullPath("/clone/Calculator.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepo, "Calculator.cs")), p1);

            var p2 = tools.ResolveFullPath("/workspace/clone/Calculator.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepo, "Calculator.cs")), p2);

            var p3 = tools.ResolveFullPath("/workspace/Calculator.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepo, "Calculator.cs")), p3);

            var p4 = tools.ResolveFullPath("Calculator.cs");
            Assert.Equal(Path.GetFullPath(Path.Combine(tempRepo, "Calculator.cs")), p4);
        }
        finally
        {
            if (Directory.Exists(tempRepo))
            {
                Directory.Delete(tempRepo, true);
            }
        }
    }

    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("../../../etc/shadow")]
    [InlineData("..\\..\\Windows\\win.ini")]
    public async Task ReadAsync_PathOutsideWorkspaceSandbox_ReturnsAccessDeniedError(string invalidPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var result = await tools.ReadAsync(invalidPath);

            Assert.StartsWith("Error: Access denied:", result);
            Assert.Contains("outside the allowed workspace sandbox", result);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("../../../etc/shadow")]
    [InlineData("..\\..\\Windows\\win.ini")]
    public async Task WriteAsync_PathOutsideWorkspaceSandbox_ReturnsAccessDeniedError(string invalidPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-test2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var result = await tools.WriteAsync(invalidPath, "malicious payload");

            Assert.StartsWith("Error: Access denied:", result);
            Assert.Contains("outside the allowed workspace sandbox", result);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("../../../etc/shadow")]
    [InlineData("..\\..\\Windows\\win.ini")]
    public async Task EditAsync_PathOutsideWorkspaceSandbox_ReturnsAccessDeniedError(string invalidPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-test3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var edits = new List<EditChunk> { new("old", "new") };
            var result = await tools.EditAsync(invalidPath, edits);

            Assert.StartsWith("Error: Access denied:", result);
            Assert.Contains("outside the allowed workspace sandbox", result);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("../../")]
    [InlineData("..\\..\\")]
    public async Task BashAsync_WorkingDirectoryOutsideWorkspaceSandbox_ReturnsAccessDeniedError(string invalidDir)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-test4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var result = await tools.BashAsync("echo test", workingDirectory: invalidDir);

            Assert.StartsWith("Error: Access denied:", result);
            Assert.Contains("outside the allowed workspace sandbox", result);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteReadEditAsync_TmpDirectoryPath_SucceedsWithinAllowedTmpSandbox()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-tmp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var uniqueScript = $"/tmp/hp_patch_{Guid.NewGuid():N}.py";

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());

            var writeRes = await tools.WriteAsync(uniqueScript, "print('hello')\n");
            Assert.Contains("Successfully wrote", writeRes);

            var readRes = await tools.ReadAsync(uniqueScript);
            Assert.Contains("print('hello')", readRes);

            var editRes = await tools.EditAsync(uniqueScript, "hello", "world");
            Assert.Contains("Successfully applied", editRes);

            var readAfterEdit = await tools.ReadAsync(uniqueScript);
            Assert.Contains("print('world')", readAfterEdit);

            var expectedHostPath = Path.Combine(tempDir, "tmp", Path.GetFileName(uniqueScript));
            Assert.True(File.Exists(expectedHostPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("/tmp/../etc/shadow")]
    [InlineData("/tmp/../../outside.txt")]
    [InlineData("/tmp/sub/../../outside.txt")]
    public async Task WriteAndReadAsync_TmpTraversalEscapingTmp_ReturnsAccessDeniedError(string invalidPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-sandbox-tmp-escape-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());

            var writeResult = await tools.WriteAsync(invalidPath, "payload");
            Assert.StartsWith("Error: Access denied:", writeResult);

            var readResult = await tools.ReadAsync(invalidPath);
            Assert.StartsWith("Error: Access denied:", readResult);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteAndReadAsync_TmpDirectory_WhenTaskCrucibleDirMissing_SucceedsWhetherCrucibleTmpExistsOrNot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-tmp-asymmetry-" + Guid.NewGuid().ToString("N"));
        var localCloneDir = Path.Combine(tempDir, "local", "clone");
        var crucibleBaseDir = Path.Combine(tempDir, "crucible");
        Directory.CreateDirectory(localCloneDir);
        Directory.CreateDirectory(crucibleBaseDir);

        try
        {
            var fakeResolver = new FakePathResolver(crucibleBaseDir);

            // Case 1: Neither wks/42 (rootCrucibleDir) nor wks/42/tmp exists -> falls back to localCloneDir's parent tmp
            var toolsFallback = TestCanonicalToolsBuilder.Create(
                localCloneDir,
                pathResolver: fakeResolver,
                workspaceId: 42,
                taskId: 99);

            var writeRes1 = await toolsFallback.WriteAsync("/tmp/new_fallback.py", "print('fallback')\n");
            Assert.Contains("Successfully wrote", writeRes1);
            var readRes1 = await toolsFallback.ReadAsync("/tmp/new_fallback.py");
            Assert.Contains("print('fallback')", readRes1);

            // Case 2: wks/42/tmp exists on disk, while task worktree crucible dir does not exist -> resolves consistently to wks/42/tmp
            var crucibleTmpDir = Path.Combine(crucibleBaseDir, "42", "tmp");
            Directory.CreateDirectory(crucibleTmpDir);

            var toolsCrucibleTmp = TestCanonicalToolsBuilder.Create(
                localCloneDir,
                pathResolver: fakeResolver,
                workspaceId: 42,
                taskId: 99);

            var writeRes2 = await toolsCrucibleTmp.WriteAsync("/tmp/new_crucible.py", "print('crucible-tmp')\n");
            Assert.Contains("Successfully wrote", writeRes2);
            Assert.True(File.Exists(Path.Combine(crucibleTmpDir, "new_crucible.py")));

            var readRes2 = await toolsCrucibleTmp.ReadAsync("/tmp/new_crucible.py");
            Assert.Contains("print('crucible-tmp')", readRes2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

