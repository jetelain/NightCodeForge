using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class DeterministicStageTests
{
    private class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public CommandResult NextResult { get; set; } = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromMilliseconds(10));
        public string? LastCommand { get; private set; }
        public string? LastWorkingDir { get; private set; }

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            LastWorkingDir = workingDirectory;
            return Task.FromResult(NextResult);
        }
    }

    private class FakePreloadService : IOllamaModelPreloadService
    {
        public List<(string ModelName, string KeepAlive)> PreloadedModels { get; } = new();

        public Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default)
        {
            PreloadedModels.Add((modelName, keepAlive));
            return Task.FromResult(true);
        }

        public Task<bool> EvictModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task DeterministicBuildStage_Success_RecordsWarningsAndSucceeds()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "warning CS0168: variable unused [proj.csproj]\nwarning CS0219: variable assigned [proj.csproj]\nBuild succeeded.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, context.DataBag.Get<int>("warnings.current.total"));
        Assert.Equal(2, context.DataBag.Get<int>("warnings.baseline.total"));
        Assert.Equal("dotnet build", fakeEnv.LastCommand);
    }

    [Fact]
    public async Task DeterministicBuildStage_Failure_ExtractsErrorsAndReturnsLoopback()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "error CS1002: ; expected [proj.csproj]\nBuild FAILED.", "stderr", TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", result.TargetRoutingStageId);
        Assert.Contains("Deterministic build failed", result.ErrorMessage);
        Assert.True(context.DataBag.TryGet<List<string>>("diagnostics.compiler_errors", out var errors));
        Assert.NotEmpty(errors!);
    }

    [Fact]
    public async Task DeterministicBuildStage_TriggersPreload_WhenProfileConfigured()
    {
        var fakeEnv = new FakeExecutionEnvironment();
        var preload = new FakePreloadService();
        var stage = new DeterministicBuildStage(fakeEnv, preload);

        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var profile = new AgentProfile { Id = "p1", ModelName = "qwen2.5-coder:32b" };
        var context = new PipelineContext(issue, workspace) { CurrentProfile = profile };

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Single(preload.PreloadedModels);
        Assert.Equal("qwen2.5-coder:32b", preload.PreloadedModels[0].ModelName);
    }

    [Fact]
    public async Task DeterministicTestStage_Success_ParsesTotalAndPassedTests()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Passed: 42, Failed: 0, Total: 42", string.Empty, TimeSpan.FromSeconds(2))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, context.DataBag.Get<int>("tests.current.total"));
        Assert.Equal(0, context.DataBag.Get<int>("tests.current.failed"));
    }

    [Fact]
    public async Task DeterministicTestStage_Failure_FailsAndExtractsDiagnosticLines()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "Failed TestNamespace.Tests.TestMethod1\nPassed: 10, Failed: 1, Total: 11", string.Empty, TimeSpan.FromSeconds(2))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(11, context.DataBag.Get<int>("tests.current.total"));
        Assert.Equal(1, context.DataBag.Get<int>("tests.current.failed"));
        Assert.Equal("implementation", result.TargetRoutingStageId);
        Assert.True(context.DataBag.TryGet<List<string>>("diagnostics.failed_tests", out var failedList));
        Assert.NotEmpty(failedList!);
    }

    [Fact]
    public async Task DeterministicSastStage_SuccessAndFailure()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "All rules passed.", string.Empty, TimeSpan.FromMilliseconds(50))
        };
        var stage = new DeterministicSastStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var successRes = await stage.ExecuteAsync(context);
        Assert.True(successRes.IsSuccess);

        fakeEnv.NextResult = new CommandResult(1, string.Empty, "Security vulnerability detected.", TimeSpan.FromMilliseconds(50));
        var failRes = await stage.ExecuteAsync(context);
        Assert.False(failRes.IsSuccess);
        Assert.Equal("implementation", failRes.TargetRoutingStageId);
    }

    [Fact]
    public async Task TriageStage_ExecutesDefaultOrHandler()
    {
        var stage = new TriageStage();
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);

        var defaultRes = await stage.ExecuteAsync(context);
        Assert.True(defaultRes.IsSuccess);

        stage.Handler = (_, _) => Task.FromResult(StageExecutionResult.Success());
        var customRes = await stage.ExecuteAsync(context);
        Assert.True(customRes.IsSuccess);
    }

    [Fact]
    public async Task DeterministicBuildStage_UsesDynamicLoopbackTarget_WhenSpecifiedInDataBag()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "error CS1002: ; expected", string.Empty, TimeSpan.FromMilliseconds(50))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);
        context.DataBag.Set("Stage.LoopBackId", "code-review");

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("code-review", result.TargetRoutingStageId);
    }

    [Fact]
    public async Task DeterministicTestStage_ParsesPytestResults_Correctly()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "=== 12 passed, 2 failed in 1.45s ===", string.Empty, TimeSpan.FromMilliseconds(50))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var issue = new Issue { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(issue, workspace);
        context.DataBag.Set("CodeStack.TestCommand", "pytest");

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(14, context.DataBag.Get<int>("tests.current.total"));
        Assert.Equal(2, context.DataBag.Get<int>("tests.current.failed"));
    }
}
