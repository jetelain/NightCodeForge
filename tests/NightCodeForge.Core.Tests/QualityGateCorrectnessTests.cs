using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class QualityGateCorrectnessTests
{
    private class FakeEnv : IExecutionEnvironment, IExecutionEnvironmentProvider
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public CommandResult NextResult { get; set; } = new CommandResult(0, "Passed: 5, Failed: 0, Total: 5", string.Empty, TimeSpan.FromMilliseconds(10));
        public string? LastCommand { get; private set; }
        public string? LastWorkingDir { get; private set; }

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
            return Task.FromResult(NextResult);
        }
    }

    private class FakePreload : IOllamaModelPreloadService
    {
        public List<(string Model, string KeepAlive)> Calls { get; } = new();

        public Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default)
        {
            Calls.Add((modelName, keepAlive));
            return Task.FromResult(true);
        }

        public Task<bool> EvictModelAsync(string modelName, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private class TestServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = new();
        public void AddService<T>(T service) where T : notnull => _services[typeof(T)] = service;
        public object? GetService(Type serviceType)
        {
            if (_services.TryGetValue(serviceType, out var s)) return s;
            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                var loggerType = typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()[0]);
                var instanceProp = loggerType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return instanceProp?.GetValue(null);
            }
            return null;
        }
    }

    [Fact]
    public void CoverletCoverageParser_ParseCobertura_ExtractsLineRateAndCounts()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.80"" lines-covered=""80"" lines-valid=""100"" version=""1.9"">
  <packages />
</coverage>";

        var metric = CoverletCoverageParser.ParseCobertura(xml);

        Assert.NotNull(metric);
        Assert.Equal(80, metric.CoveredLines);
        Assert.Equal(100, metric.ValidLines);
        Assert.Equal(0.80, metric.LineRate, 2);
    }

    [Fact]
    public void CoverletCoverageParser_ParseOpenCover_ExtractsSummaryMetrics()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CoverageSession>
  <Summary numSequencePoints=""200"" visitedSequencePoints=""160"" sequenceCoverage=""80.0"" />
  <Modules />
</CoverageSession>";

        var metric = CoverletCoverageParser.ParseOpenCover(xml);

        Assert.NotNull(metric);
        Assert.Equal(160, metric.CoveredLines);
        Assert.Equal(200, metric.ValidLines);
        Assert.Equal(0.80, metric.LineRate, 2);
    }

    [Fact]
    public void CoverletCoverageParser_ParseCoverletJson_TraversesMethodsAndCountsLines()
    {
        var json = @"{
  ""MyModule.dll"": {
    ""C:\\source\\File1.cs"": {
      ""Namespace.Class1"": {
        ""System.Void Namespace.Class1::M1()"": {
          ""Lines"": {
            ""10"": 1,
            ""11"": 0,
            ""12"": 5
          }
        },
        ""System.Void Namespace.Class1::M2()"": {
          ""Lines"": {
            ""20"": 0
          }
        }
      }
    }
  }
}";

        var metric = CoverletCoverageParser.ParseCoverletJson(json);

        Assert.NotNull(metric);
        Assert.Equal(2, metric.CoveredLines); // lines 10 and 12
        Assert.Equal(4, metric.ValidLines);   // lines 10, 11, 12, 20
        Assert.Equal(0.50, metric.LineRate, 2);
    }

    [Fact]
    public void CoverletCoverageParser_ParseConsoleOutput_ExtractsLineRatePercentage()
    {
        var consoleOutput = @"
Build succeeded.
+-------------------+--------+--------+--------+
| Module            | Line   | Branch | Method |
+-------------------+--------+--------+--------+
| Project.Core      | 87.5%  | 70.0%  | 90.0%  |
+-------------------+--------+--------+--------+
| Total             | 87.5%  | 70.0%  | 90.0%  |
+-------------------+--------+--------+--------+
Passed! - Failed: 0, Passed: 10, Total: 10";

        var rate = CoverletCoverageParser.ParseConsoleOutput(consoleOutput);

        Assert.NotNull(rate);
        Assert.Equal(0.875, rate.Value, 3);
    }

    [Fact]
    public async Task CoverletCoverageParser_AggregateCoverageAsync_AggregatesMultipleProjectsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_cov_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startTime = DateTime.UtcNow;

            // Project 1: 50 / 100 lines (50%)
            var proj1Dir = Path.Combine(tempDir, "TestResults", "guid1");
            Directory.CreateDirectory(proj1Dir);
            var xml1 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.50"" lines-covered=""50"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(proj1Dir, "coverage.cobertura.xml"), xml1);

            // Project 2: 100 / 100 lines (100%)
            var proj2Dir = Path.Combine(tempDir, "TestResults", "guid2");
            Directory.CreateDirectory(proj2Dir);
            var xml2 = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""1.00"" lines-covered=""100"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(proj2Dir, "coverage.cobertura.xml"), xml2);

            // Aggregate should be (50 + 100) / (100 + 100) = 150 / 200 = 75%
            var aggregateRate = await CoverletCoverageParser.AggregateCoverageAsync(tempDir, startTime, string.Empty);

            Assert.NotNull(aggregateRate);
            Assert.Equal(0.75, aggregateRate.Value, 2);
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
    public async Task CoverletCoverageParser_AggregateCoverageAsync_IgnoresStaleArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_cov_stale_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var staleDir = Path.Combine(tempDir, "TestResults", "stale");
            Directory.CreateDirectory(staleDir);
            var filePath = Path.Combine(staleDir, "coverage.cobertura.xml");
            var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.50"" lines-covered=""50"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(filePath, xml);

            // Set file write time 10 minutes in the past
            File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-10));

            var stageStartTime = DateTime.UtcNow;

            var result = await CoverletCoverageParser.AggregateCoverageAsync(tempDir, stageStartTime, string.Empty);

            Assert.Null(result);
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
    public async Task DeterministicTestStage_FailingRun_DoesNotRecordBaseline()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(1, "Passed: 0, Failed: 1, Total: 1\nFailed TestMethod", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.TestsBaselineTotal));
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.CoverageBaselineLineRate));
    }

    [Fact]
    public async Task DeterministicTestStage_ZeroTests_FailsWhenAllowNoTestsIsFalse()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 0, Failed: 0, Total: 0\nNo tests found.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Contains("0 tests executed", result.ErrorMessage);
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.TestsBaselineTotal));
    }

    [Fact]
    public async Task DeterministicTestStage_ZeroTests_PassesWhenAllowNoTestsIsTrue()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 0, Failed: 0, Total: 0\nNo tests found.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var profile = new AgentProfile { Id = "test-prof", AllowNoTests = true };
        var context = new PipelineContext(task, workspace) { CurrentProfile = profile };

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.TestsBaselineTotal)); // zero tests still doesn't create baseline
    }

    [Fact]
    public async Task DeterministicTestStage_TruncatesFailedTestsList_WhenMoreThan10()
    {
        var outputLines = new List<string>();
        for (int i = 1; i <= 15; i++)
        {
            outputLines.Add($"Failed MyNamespace.TestClass.TestMethod{i}");
        }
        outputLines.Add("Passed: 0, Failed: 15, Total: 15");

        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(1, string.Join("\n", outputLines), string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.True(context.DataBag.Get<bool>(PipelineDataBagKeys.FailedTestsTruncated));
        var failedList = context.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
        Assert.NotNull(failedList);
        Assert.Equal(11, failedList.Count); // 10 tests + "… and 5 more failure(s)"
        Assert.Contains("5 more failure(s)", failedList[^1]);
    }

    [Fact]
    public async Task DeterministicCoverageGate_WarnsAndPasses_WhenCoverageUnmeasuredOrMisconfigured()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", string.Empty, TimeSpan.FromSeconds(1))
        };
        var gate = new DeterministicTestWithCoverageGate(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await gate.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.CoverageFailureReason, out var warning));
        Assert.Contains("could not be extracted", warning);
    }

    [Fact]
    public async Task DeterministicCoverageGate_Passes_WhenCoverageMeasured()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 10, Failed: 0, Total: 10\n| Total | 85.0% |", string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var gate = new DeterministicTestWithCoverageGate(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await gate.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.True(context.DataBag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out var rate));
        Assert.Equal(0.85, rate, 2);
    }

    [Fact]
    public async Task DeterministicFormatStage_ExecutesStackResolvedFormatter()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Formatted 3 file(s).", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicFormatStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("dotnet format", fakeEnv.LastCommand);
    }

    [Fact]
    public async Task DeterministicSastStage_ExecutesConfiguredAnalyzer()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "0 vulnerabilities found.", string.Empty, TimeSpan.FromSeconds(1))
        };
        var stage = new DeterministicSastStage(fakeEnv, "sonar-scanner", DeterministicSastStage.DefaultStageId, "SAST");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("sonar-scanner", fakeEnv.LastCommand);
    }

    private class CommandAwareEnv : IExecutionEnvironment, IExecutionEnvironmentProvider
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public List<string> Commands { get; } = new();

        public IExecutionEnvironment GetEnvironment(Workspace workspace, SandboxSecurityTier securityTier = SandboxSecurityTier.Standard, int? taskId = null) => this;

        public Task<CommandResult> ExecuteAsync(
            string command,
            string? workingDirectory = null,
            int? timeoutSeconds = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var output = command.Contains("XPlat Code Coverage", StringComparison.Ordinal)
                ? "Passed: 10, Failed: 0, Total: 10\n| Total | 90.0% |"
                : "Passed: 10, Failed: 0, Total: 10";
            return Task.FromResult(new CommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(5)));
        }
    }

    [Fact]
    public async Task DeterministicCoverageGate_CollectsCoverage_WhenPrecedingTestStageDidNot()
    {
        var env = new CommandAwareEnv();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var testResult = await new DeterministicTestStage(env).ExecuteAsync(context);
        Assert.True(testResult.IsSuccess);
        Assert.False(context.DataBag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out _));

        var result = await new DeterministicTestWithCoverageGate(env).ExecuteAsync(context);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Contains(env.Commands, c => c.Contains("XPlat Code Coverage", StringComparison.Ordinal));
        Assert.True(context.DataBag.TryGet<double>(PipelineDataBagKeys.CoverageCurrentLineRate, out var rate));
        Assert.Equal(0.90, rate, 2);
    }

    [Fact]
    public async Task DeterministicBuildStage_SkipsPreload_WhenNoMatchingProfileExists()
    {
        var fakeEnv = new FakeEnv();
        var fakePreload = new FakePreload();
        var stage = new DeterministicBuildStage(fakeEnv, fakePreload, new BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundTaskRunner>.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicBuildStage>.Instance);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        // Workspace has profiles, but none matching the next stage
        workspace.AgentProfiles.Add(new AgentProfile { Id = "unrelated-agent", ModelName = "unrelated:model" });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage, TestWorkflowPipelines.CreateImplementationStage() });
        var context = new PipelineContext(task, workspace)
        {
            Pipeline = pipeline
        };

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        // Preload should be empty because implementation profile was not configured; no FirstOrDefault fallback!
        Assert.Empty(fakePreload.Calls);
    }

    [Fact]
    public async Task DeterministicFormatStage_FormatsCode_WhenCommandConfigured()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Formatted 3 file(s).", string.Empty, TimeSpan.FromMilliseconds(50))
        };
        var stage = new DeterministicFormatStage(fakeEnv, "dotnet format", "deterministic-format", "Format Code");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Equal("dotnet format", fakeEnv.LastCommand);
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.StageCommand("deterministic-format"), out var cmd));
        Assert.Equal("dotnet format", cmd);
    }

    [Fact]
    public async Task DeterministicFormatStage_SkipsGracefully_WhenNoCommandConfigured()
    {
        var fakeEnv = new FakeEnv();
        var stage = new DeterministicFormatStage(fakeEnv, null, "deterministic-format", "Format Code");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);
        // Explicitly clear format command in databag
        context.DataBag.Set(PipelineDataBagKeys.FormatCommand, string.Empty);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Null(fakeEnv.LastCommand);
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.StageOutput("deterministic-format"), out var output));
        Assert.Contains("skipped", output);
    }

    [Fact]
    public async Task DeterministicSastStage_SkipsGracefully_WhenNoCommandConfigured()
    {
        var fakeEnv = new FakeEnv();
        var stage = new DeterministicSastStage(fakeEnv, null, "deterministic-sast", "SAST Gate");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.Null(fakeEnv.LastCommand);
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.StageOutput("deterministic-sast"), out var output));
        Assert.Contains("skipped", output);
    }

    [Fact]
    public async Task CompositeDeterministicStage_RecordsAllCommandsAndOutputs()
    {
        var sp = new TestServiceProvider();
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Build succeeded.\nPassed: 5, Failed: 0, Total: 5\n| Total | 85.0% |", string.Empty, TimeSpan.FromMilliseconds(50))
        };
        sp.AddService<IExecutionEnvironmentProvider>(fakeEnv);
        sp.AddService<IOllamaModelPreloadService>(new FakePreload());
        sp.AddService<IBackgroundTaskRunner>(new BackgroundTaskRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundTaskRunner>.Instance));
        sp.AddService<IWorkflowStageRegistry>(new WorkflowStageRegistry());

        var config = new DeterministicStageConfig
        {
            ExtraParameters = new Dictionary<string, string>
            {
                ["Stages"] = "format,build,test-with-coverage,sast"
            }
        };

        var composite = new CompositeDeterministicStage("deterministic-post-impl", "Post-Impl Gates", config, sp, sp.GetRequiredService<IWorkflowStageRegistry>());
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await composite.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.True(context.DataBag.TryGet<List<StageCommandExecution>>(PipelineDataBagKeys.StageCommands("deterministic-post-impl"), out var list));
        Assert.NotNull(list);
        Assert.Equal(4, list.Count);
        Assert.Equal("format", list[0].StageName);
        Assert.Equal("dotnet format", list[0].Command);
        Assert.Equal("build", list[1].StageName);
        Assert.Equal("dotnet build", list[1].Command);
        Assert.Equal("test-with-coverage", list[2].StageName);
        Assert.Contains("dotnet test", list[2].Command);
        Assert.Equal("sast", list[3].StageName);
        Assert.Equal("sast (skipped)", list[3].Command);
    }

    [Fact]
    public async Task DeterministicTestWithCoverageGate_AlwaysRunsTests_EvenWhenPrecedingStageSetMetrics()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 15, Failed: 0, Total: 15\n| Total | 90.0% |", string.Empty, TimeSpan.FromMilliseconds(20))
        };
        var gate = new DeterministicTestWithCoverageGate(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        // Simulate pre-impl baseline having set 10 tests and 80% coverage
        context.DataBag.Set(PipelineDataBagKeys.TestsBaselineTotal, 10);
        context.DataBag.Set(PipelineDataBagKeys.TestsCurrentTotal, 10);
        context.DataBag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.80);
        context.DataBag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.80);

        var result = await gate.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        // Verify that command was actually executed and metrics updated to 15 tests and 90%
        Assert.NotNull(fakeEnv.LastCommand);
        Assert.Equal(15, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.Equal(0.90, context.DataBag.Get<double>(PipelineDataBagKeys.CoverageCurrentLineRate), 2);
    }

    [Fact]
    public async Task DeterministicTestStage_ClearsStaleFailureKeys_OnSuccessfulExecution()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 5, Failed: 0, Total: 5", string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        // Pre-populate stale failure keys from a previous attempt
        context.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "TestClass.FailedMethod" });
        context.DataBag.Set(PipelineDataBagKeys.FailedTestsTruncated, true);
        context.DataBag.Set(PipelineDataBagKeys.TestFailureReason, "Previous failure reason");

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.FailedTests));
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.FailedTestsTruncated));
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.TestFailureReason));
    }

    [Fact]
    public async Task DeterministicTestStage_RemovesCurrentCoverageRate_WhenRunProducesNoCoverage()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 5, Failed: 0, Total: 5", string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        // Pre-populate coverage from an earlier run
        context.DataBag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.95);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        // Key should have been removed since this run produced no coverage
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.CoverageCurrentLineRate));
    }

    [Fact]
    public async Task DeterministicFormatStage_SkipsGracefully_WhenScriptOrCommandMissing()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(1, string.Empty, "npm ERR! Missing script: \"format\"", TimeSpan.FromMilliseconds(10))
        };
        var stage = new DeterministicFormatStage(fakeEnv, "npm run format", "deterministic-format", "Format Code");
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
        Assert.False(context.DataBag.ContainsKey(PipelineDataBagKeys.FormatFailureReason));
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.StageOutput("deterministic-format"), out var outStr));
        Assert.Contains("skipped", outStr);
    }

    [Fact]
    public async Task CoverletCoverageParser_AvoidsDoubleCounting_WhenMultipleFormatsInSameDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_cov_dedup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startTime = DateTime.UtcNow;
            var projDir = Path.Combine(tempDir, "TestResults", "guid_single_proj");
            Directory.CreateDirectory(projDir);

            // Cobertura: 80 / 100 lines (80%)
            var xmlCobertura = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.80"" lines-covered=""80"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(projDir, "coverage.cobertura.xml"), xmlCobertura);

            // OpenCover in same folder: 160 / 200 sequence points (80%)
            var xmlOpenCover = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CoverageSession><Summary numSequencePoints=""200"" visitedSequencePoints=""160"" sequenceCoverage=""80.0"" /></CoverageSession>";
            await File.WriteAllTextAsync(Path.Combine(projDir, "coverage.opencover.xml"), xmlOpenCover);

            // Aggregate should pick the primary format (Cobertura) and NOT double-count (80/100, not 240/300)
            var aggregateRate = await CoverletCoverageParser.AggregateCoverageAsync(tempDir, startTime, string.Empty);

            Assert.NotNull(aggregateRate);
            Assert.Equal(0.80, aggregateRate.Value, 2);
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
    public async Task CoverletCoverageParser_AggregateCoverageAsync_PreservesMultipleProjectsSharingSameDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_cov_multisame_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startTime = DateTime.UtcNow;
            var testResultsDir = Path.Combine(tempDir, "TestResults");
            Directory.CreateDirectory(testResultsDir);

            // Project A in TestResults: 50 / 100 lines (50%)
            var xmlA = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.50"" lines-covered=""50"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(testResultsDir, "ProjectA.cobertura.xml"), xmlA);

            // Project B in same TestResults: 100 / 100 lines (100%)
            var xmlB = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""1.00"" lines-covered=""100"" lines-valid=""100""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(testResultsDir, "ProjectB.cobertura.xml"), xmlB);

            // Both projects must be aggregated: (50 + 100) / (100 + 100) = 150 / 200 = 75%
            var aggregateRate = await CoverletCoverageParser.AggregateCoverageAsync(tempDir, startTime, string.Empty);

            Assert.NotNull(aggregateRate);
            Assert.Equal(0.75, aggregateRate.Value, 2);
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
    public async Task DeterministicTestWithCoverageGate_FailsHard_WhenBaselineExists_AndCoverageCannotBeExtracted()
    {
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var gate = new DeterministicTestWithCoverageGate(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        // Pre-existing baseline established during earlier stage
        context.DataBag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.85);

        var result = await gate.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Contains("Coverage gate failure: baseline coverage was established at", result.ErrorMessage);
        Assert.True(context.DataBag.TryGet<string>(PipelineDataBagKeys.CoverageFailureReason, out var reason));
        Assert.Contains("Coverage gate failure", reason);
    }

    [Fact]
    public void CoverletCoverageParser_ParseOpenCover_OnlySumsModuleSummaries_WhenRootSummaryAbsent()
    {
        var xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<CoverageSession>
  <Modules>
    <Module>
      <Summary numSequencePoints=""100"" visitedSequencePoints=""80"" />
      <Classes>
        <Class>
          <Summary numSequencePoints=""100"" visitedSequencePoints=""80"" />
          <Methods>
            <Method>
              <Summary numSequencePoints=""100"" visitedSequencePoints=""80"" />
            </Method>
          </Methods>
        </Class>
      </Classes>
    </Module>
  </Modules>
</CoverageSession>";

        var metric = CoverletCoverageParser.ParseOpenCover(xml);

        Assert.NotNull(metric);
        Assert.Equal(80, metric.CoveredLines);
        Assert.Equal(100, metric.ValidLines);
        Assert.Equal(0.80, metric.LineRate, 2);
    }

    [Fact]
    public async Task CoverletCoverageParser_AggregateCoverageAsync_PreservesZeroPercentLineRate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_cov_zero_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startTime = DateTime.UtcNow;
            var testResultsDir = Path.Combine(tempDir, "TestResults");
            Directory.CreateDirectory(testResultsDir);

            // Report with line-rate="0.00" without lines-valid attribute
            var xmlZero = @"<?xml version=""1.0"" encoding=""utf-8""?>
<coverage line-rate=""0.00""><packages /></coverage>";
            await File.WriteAllTextAsync(Path.Combine(testResultsDir, "coverage.cobertura.xml"), xmlZero);

            var rate = await CoverletCoverageParser.AggregateCoverageAsync(tempDir, startTime, string.Empty);

            Assert.NotNull(rate);
            Assert.Equal(0.0, rate.Value, 2);
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
    public async Task DeterministicTestStage_AggregatesMultipleVSTestSummaries()
    {
        var output = @"
Starting test execution, please wait...
Total tests: 10. Passed: 10. Failed: 0. Skipped: 0.
Passed!  - Failed: 0, Passed: 10, Total: 10 - Assembly1.dll

Starting test execution, please wait...
Total tests: 15. Passed: 12. Failed: 3. Skipped: 0.
Failed!  - Failed: 3, Passed: 12, Total: 15 - Assembly2.dll
";
        var fakeEnv = new FakeEnv
        {
            NextResult = new CommandResult(1, output, string.Empty, TimeSpan.FromMilliseconds(10))
        };
        var stage = new DeterministicTestStage(fakeEnv);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1 };
        var workspace = new Workspace { Id = 1, RepositoryPath = @"C:\repo" };
        var context = new PipelineContext(task, workspace);

        var result = await stage.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(25, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.Equal(22, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentPassed));
        Assert.Equal(3, context.DataBag.Get<int>(PipelineDataBagKeys.TestsCurrentFailed));
    }

    [Fact]
    public void TestDiagnosticsParser_ParsesErrorsTimeoutsAndAbortsAsFailures()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_trx_err_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startTime = DateTime.UtcNow;
            var trxXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<TestRun id=""test-run-1"" xmlns=""http://microsoft.com/schemas/VisualStudio/TeamTest/2010"">
  <ResultSummary outcome=""Failed"">
    <Counters total=""5"" executed=""5"" passed=""2"" failed=""1"" error=""1"" timeout=""1"" aborted=""0"" />
  </ResultSummary>
  <Results>
    <UnitTestResult executionId=""1"" testName=""PassedTest"" outcome=""Passed"" />
    <UnitTestResult executionId=""2"" testName=""FailedTest"" outcome=""Failed"" />
    <UnitTestResult executionId=""3"" testName=""ErrorTest"" outcome=""Error"" />
    <UnitTestResult executionId=""4"" testName=""TimeoutTest"" outcome=""Timeout"" />
  </Results>
</TestRun>";
            File.WriteAllText(Path.Combine(tempDir, "testresults.trx"), trxXml);

            var metrics = TestDiagnosticsParser.ParseFreshTrxFiles(tempDir, startTime);

            Assert.NotNull(metrics);
            Assert.Equal(5, metrics.Total);
            Assert.Equal(2, metrics.Passed);
            Assert.Equal(3, metrics.Failed); // failed (1) + error (1) + timeout (1)
            Assert.Contains("FailedTest", metrics.FailedTestNames);
            Assert.Contains("ErrorTest", metrics.FailedTestNames);
            Assert.Contains("TimeoutTest", metrics.FailedTestNames);
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
    public void CompositeDeterministicStage_Constructors_FailFastWithoutOptionalParameters()
    {
        var sp = new TestServiceProvider();
        var def = new StageDefinition { StageId = "test-stage", DisplayName = "Test" };
        var registry = new WorkflowStageRegistry();

        // Constructor throws if registry is null
        Assert.Throws<ArgumentNullException>(() => new CompositeDeterministicStage(def, sp, null!));

        // Constructor with explicit registry
        var stage = new CompositeDeterministicStage(def, sp, registry);
        Assert.NotNull(stage);
        Assert.Equal("test-stage", stage.StageId);
    }
}


