using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class DeterministicStageTests
{
    private class FakeExecutionEnvironment : IExecutionEnvironment, IExecutionEnvironmentProvider
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public CommandResult NextResult { get; set; } = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromMilliseconds(10));
        public string? LastCommand { get; private set; }
        public string? LastWorkingDir { get; private set; }
        public int? LastTimeoutSeconds { get; private set; }

        public IExecutionEnvironment GetEnvironment(Workspace workspace, SandboxSecurityTier securityTier = SandboxSecurityTier.Standard, int? taskId = null) => this;

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            LastWorkingDir = workingDirectory;
            LastTimeoutSeconds = timeoutSeconds;
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
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, context.DataBag.Get<int>(PipelineDataBagKeys.WarningsCurrentTotal));
        Assert.Equal(2, context.DataBag.Get<int>(PipelineDataBagKeys.WarningsBaselineTotal));
        Assert.Equal("dotnet build", fakeEnv.LastCommand);
        Assert.Equal("dotnet build", context.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)));
        Assert.Contains("warning CS0168", context.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)));
        var newWarnings = context.DataBag.Get<List<string>>(PipelineDataBagKeys.WarningsNew);
        Assert.NotNull(newWarnings);
        Assert.Equal(2, newWarnings.Count);
    }

    [Fact]
    public async Task DeterministicBuildStage_Failure_ExtractsErrorsAndReturnsLoopback()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "error CS1002: ; expected [proj.csproj]\nBuild FAILED.", "stderr", TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Contains("Deterministic build failed", result.ErrorMessage);
        Assert.True(context.DataBag.TryGet<List<string>>(PipelineDataBagKeys.CompilerErrors, out var errors));
        Assert.NotEmpty(errors!);
    }

    [Fact]
    public async Task DeterministicBuildStage_TriggersPreload_WhenProfileConfigured()
    {
        var fakeEnv = new FakeExecutionEnvironment();
        var preload = new FakePreloadService();
        var stage = new DeterministicBuildStage(fakeEnv, preload);

        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var profile = new AgentProfile { Id = "p1", ModelName = "qwen2.5-coder:32b" };
        var context = new PipelineContext(task, workspace) { CurrentProfile = profile };

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
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.Equal(42, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentPassed));
        Assert.Equal(0, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentFailed));
        Assert.True(context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentDurationMs) >= 0);
        Assert.Equal("dotnet test --no-build", context.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)));
        Assert.Contains("Passed: 42", context.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)));
    }

    [Fact]
    public async Task DeterministicTestStage_Failure_FailsAndExtractsDiagnosticLines()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(1, "Failed TestNamespace.Tests.TestMethod1\nPassed: 10, Failed: 1, Total: 11", string.Empty, TimeSpan.FromSeconds(2))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(11, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.Equal(1, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentFailed));
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.True(context.DataBag.TryGet<List<string>>(PipelineDataBagKeys.FailedTests, out var failedList));
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
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var successRes = await stage.ExecuteAsync(context);
        Assert.True(successRes.IsSuccess);

        fakeEnv.NextResult = new CommandResult(1, string.Empty, "Security vulnerability detected.", TimeSpan.FromMilliseconds(50));
        var failRes = await stage.ExecuteAsync(context);
        Assert.False(failRes.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, failRes.TargetRoutingStageId);
    }

    [Fact]
    public async Task TriageStage_ExecutesDefaultOrHandler()
    {
        var stage = new TriageStage();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

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
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);
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
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);
        context.DataBag.Set("CodeStack.TestCommand", "pytest");

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(14, context.DataBag.Get<int>("tests.current.total"));
        Assert.Equal(2, context.DataBag.Get<int>("tests.current.failed"));
    }

    [Fact]
    public void ParseCoberturaLineRate_ExtractsLineRate_FromXmlAttributes()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.875"" branch-rate=""0.75"" version=""1.9"" timestamp=""1600000000"">
  <packages />
</coverage>";

        var rate = DeterministicTestStage.ParseCoberturaLineRate(xml);
        Assert.NotNull(rate);
        Assert.Equal(0.875, rate.Value, 3);
    }

    [Fact]
    public async Task DeterministicCoverageGate_ExecutesTestsAutomatically_WhenTestsMissingFromBag()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Passed: 10\nFailed: 0\nTotal: 10\nPassed!", string.Empty, TimeSpan.FromMilliseconds(50))
        };

        var gate = new DeterministicCoverageGate(envProvider: fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        // DataBag has no tests yet
        Assert.False(context.DataBag.TryGet<int>("tests.current.total", out _));

        var result = await gate.ExecuteAsync(context);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(context.DataBag.TryGet<int>("tests.current.total", out var total));
        Assert.Equal(10, total);
    }

    [Fact]
    public async Task DeterministicBuildStage_Timeout_RecordsTimeoutDiagnosticsAndLoopsBack()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(124, string.Empty, "Command timed out after 30 seconds", TimeSpan.FromSeconds(30), Truncated: true)
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo", BuildTimeoutSeconds = 30 };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Contains("timed out after 30 seconds", result.ErrorMessage);
        Assert.Equal(30, fakeEnv.LastTimeoutSeconds);
        Assert.Equal(30, context.DataBag.Get<int>(PipelineDataBagKeys.BuildTimeoutSeconds));
        Assert.True(context.DataBag.TryGet<List<string>>(PipelineDataBagKeys.CompilerErrors, out var errors));
        Assert.Contains("timed out after 30 seconds", errors![0]);
    }

    [Fact]
    public async Task DeterministicBuildStage_UsesWorkspaceTimeout_WhenConfigured()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo", BuildTimeoutSeconds = 45 };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(45, fakeEnv.LastTimeoutSeconds);
        Assert.Equal(45, context.DataBag.Get<int>(PipelineDataBagKeys.BuildTimeoutSeconds));
    }

    [Fact]
    public async Task DeterministicTestStage_Timeout_RecordsTimeoutDiagnosticsAndLoopsBack()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(124, "Tests running...\nExecution timed out", string.Empty, TimeSpan.FromSeconds(60), Truncated: true)
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo", TestTimeoutSeconds = 60 };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Contains("timed out after 60 seconds", result.ErrorMessage);
        Assert.Contains("resource", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(60, fakeEnv.LastTimeoutSeconds);
        Assert.Equal(60, context.DataBag.Get<int>(PipelineDataBagKeys.TestTimeoutSeconds));
        Assert.True(context.DataBag.TryGet<List<string>>(PipelineDataBagKeys.FailedTests, out var failedTests));
        Assert.Contains("timed out after 60 seconds", failedTests![0]);
    }

    [Fact]
    public async Task DeterministicTestStage_UsesWorkspaceTimeout_WhenConfigured()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Passed: 5, Failed: 0, Total: 5", string.Empty, TimeSpan.FromSeconds(2))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo", TestTimeoutSeconds = 90 };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal(90, fakeEnv.LastTimeoutSeconds);
        Assert.Equal(90, context.DataBag.Get<int>(PipelineDataBagKeys.TestTimeoutSeconds));
    }

    [Fact]
    public async Task DeterministicBuildStage_UsesExt4MirrorPath_WhenSet()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicBuildStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace
        {
            Id = 1,
            RepositoryPath = @"C:\host\repo",
            Ext4MirrorPath = "/home/user/.nightcodeforge/wks/1"
        };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("/home/user/.nightcodeforge/wks/1", fakeEnv.LastWorkingDir);
    }

    [Fact]
    public async Task DeterministicBuildStage_PreloadsNextAgentModel_WhenPipelineAndLookaheadAvailable()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var preloadService = new FakePreloadService();
        var stage = new DeterministicBuildStage(fakeEnv, preloadService: preloadService);

        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "qwen-coder", ModelName = "qwen2.5-coder:14b" };
        workspace.AgentProfiles.Add(profile);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage, TestWorkflowPipelines.CreateImplementationStage() });
        var context = new PipelineContext(task, workspace)
        {
            Pipeline = pipeline
        };

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(preloadService.PreloadedModels);
        Assert.Contains(preloadService.PreloadedModels, m => m.ModelName == "qwen2.5-coder:14b");
    }

    [Fact]
    public async Task DeterministicSastStage_RecordsStageCommandAndOutput()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "CodeQL SAST scan passed with 0 findings.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicSastStage(fakeEnv, "codeql analyze", DeterministicSastStage.DefaultStageId, "Deterministic SAST Gate");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);
        context.DataBag.Set(PipelineDataBagKeys.SastCommand, "codeql analyze");

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("codeql analyze", context.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)));
        Assert.Contains("CodeQL SAST scan passed", context.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)));
    }

    [Fact]
    public async Task DeterministicScriptStage_RecordsStageCommandAndOutput()
    {
        var fakeEnv = new FakeExecutionEnvironment
        {
            NextResult = new CommandResult(0, "Script output success", string.Empty, TimeSpan.FromSeconds(1))
        };
        var config = new NightCodeForge.Core.Workflow.Configuration.DeterministicStageConfig
        {
            Command = "python run_checks.py"
        };
        var stage = new DeterministicScriptStage("custom-script", "Custom Script", config, fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("python run_checks.py", context.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)));
        Assert.Contains("Script output success", context.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)));
    }
}

