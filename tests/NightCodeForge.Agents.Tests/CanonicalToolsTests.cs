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

            var tools = new CanonicalTools(tempDir);
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

            var tools = new CanonicalTools(tempDir);
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

            var tools = new CanonicalTools(tempDir);
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
    public async Task BashAsync_DefaultsTestCommandToConfiguredTimeout()
    {
        var env = new FakeEnv();
        var tools = new CanonicalTools(
            workspaceRoot: AppContext.BaseDirectory,
            executionEnv: env,
            testTimeoutSeconds: 75,
            buildTimeoutSeconds: 45);

        await tools.BashAsync("dotnet test");

        Assert.Equal(75, env.LastTimeoutSeconds);
        Assert.Equal("dotnet test", env.LastCommand);
    }

    [Fact]
    public async Task BashAsync_DetectsExitCode124AsTimeout()
    {
        var env = new FakeEnv
        {
            NextResult = new NightCodeForge.Core.Interfaces.CommandResult(124, string.Empty, "Timed out", TimeSpan.FromSeconds(30))
        };
        var tools = new CanonicalTools(
            workspaceRoot: AppContext.BaseDirectory,
            executionEnv: env,
            testTimeoutSeconds: 30);

        var output = await tools.BashAsync("npm test");

        Assert.Contains("[TIMEOUT]", output);
        Assert.Contains("30s", output);
        Assert.Contains("hanging tests", output);
    }

    private class FakePathResolver : NightCodeForge.Core.Interfaces.IWorkspacePathResolver
    {
        private readonly string _baseDir;
        public FakePathResolver(string baseDir) => _baseDir = baseDir;

        public string GetAgentWorkingClonePath(int workspaceId) => Path.Combine(_baseDir, workspaceId.ToString());
        public string GetHostRemoteUrl(string repositoryPath) => repositoryPath;
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath)
        {
            var rel = relativeOrContainerPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(_baseDir, workspaceId.ToString(), rel);
        }
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
            var tools = new CanonicalTools(
                workspaceRoot: @"C:\host\repo",
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
    public void ProposeIssues_AddsToPendingProposals_ReturnsFormattedJson()
    {
        var tools = new CanonicalTools(AppContext.BaseDirectory);
        var proposals = new List<NightCodeForge.Core.Domain.IssueProposal>
        {
            new("Fix memory leak", "Disposed streams properly", "bug"),
            new("Add XML doc comments", "Add summary tags to public methods", "chore")
        };

        var result = tools.ProposeIssues(proposals);

        Assert.Equal(2, tools.PendingProposals.Count);
        Assert.Equal("Fix memory leak", tools.PendingProposals[0].Title);
        Assert.Equal("chore", tools.PendingProposals[1].Type);
        Assert.Contains("Proposed 2 backlog issue(s)", result);
        Assert.Contains("Fix memory leak", result);
    }
}

