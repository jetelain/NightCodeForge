using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorSyntheticPrimingTests
{
    [Fact]
    public void LocalOptimizationFlags_Clone_PreservesSyntheticConversationPriming()
    {
        var flags = new LocalOptimizationFlags
        {
            SyntheticConversationPriming = false
        };

        var clone = flags.Clone();

        Assert.False(clone.SyntheticConversationPriming);

        flags.SyntheticConversationPriming = true;
        var clone2 = flags.Clone();
        Assert.True(clone2.SyntheticConversationPriming);
    }

    [Fact]
    public void SyntheticPrimingExecution_DurationMs_PreservesLongValueWithoutTruncation()
    {
        const long longDuration = 1234567890123L;
        var priming = new SyntheticPrimingExecution(
            SourceStageId: "deterministic-pre-impl",
            DurationMs: longDuration
        );

        Assert.Equal(longDuration, priming.DurationMs);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_OmitsDuplicateDiagnostics_WhenPrimedWithFailure()
    {
        var task = new ForgeTask
        {
            Id = 1,
            Title = "Test Prompt Deduplication",
            Description = "Fix errors"
        };
        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "Test", RepositoryPath = "C:\\test" });
        context.DataBag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "CS0103: The name 'foo' does not exist" });
        context.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "UnitTest1.TestMethod1 failed" });

        // Set failure priming
        context.DataBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PostImplementationGates,
            Command: "dotnet test",
            Output: "Exit Code: 1\n[Failed Tests]: UnitTest1.TestMethod1",
            IsSuccess: false
        ));

        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Prompt: {{ task.title }}\n{{- if context.compiler_errors }}\n[Compiler Errors]:\n{{ context.compiler_errors }}\n{{- end }}\n{{- if context.failed_tests }}\n[Failed Tests]:\n{{ context.failed_tests }}\n{{- end }}"
        };

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.DoesNotContain("[Compiler Errors]:", prompt);
        Assert.DoesNotContain("[Failed Tests]:", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_PreservesDiagnostics_WhenUnprimedOrPrimingDisabled()
    {
        var task = new ForgeTask
        {
            Id = 1,
            Title = "Test Prompt Preservation",
            Description = "Fix errors"
        };
        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "Test", RepositoryPath = "C:\\test" });
        context.DataBag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "CS0103: The name 'foo' does not exist" });
        context.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "UnitTest1.TestMethod1 failed" });

        // Opt out of synthetic priming
        context.DataBag.Set(PipelineDataBagKeys.OptimizationFlags, new LocalOptimizationFlags
        {
            SyntheticConversationPriming = false
        });
        context.DataBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PostImplementationGates,
            Command: "dotnet test",
            Output: "Exit Code: 1\nErrors",
            IsSuccess: false
        ));

        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Prompt: {{ task.title }}\n{{- if context.compiler_errors }}\n[Compiler Errors]:\n{{ context.compiler_errors }}\n{{- end }}\n{{- if context.failed_tests }}\n[Failed Tests]:\n{{ context.failed_tests }}\n{{- end }}"
        };

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.Contains("[Compiler Errors]:", prompt);
        Assert.Contains("CS0103", prompt);
        Assert.Contains("[Failed Tests]:", prompt);
        Assert.Contains("UnitTest1.TestMethod1", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_PreservesDiagnostics_WhenPrimingDisabledOnAgentProfile()
    {
        var task = new ForgeTask
        {
            Id = 2,
            Title = "Test Profile Optimization Flag Resolution",
            Description = "Fix errors"
        };
        var profile = new AgentProfile
        {
            Id = "test-profile",
            OptimizationFlags = new LocalOptimizationFlags
            {
                SyntheticConversationPriming = false
            }
        };
        var context = new PipelineContext(task, new Workspace { Id = 1, Name = "Test", RepositoryPath = "C:\\test" })
        {
            CurrentProfile = profile
        };
        context.DataBag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "CS0103: The name 'bar' does not exist" });
        context.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "UnitTest1.TestMethod2 failed" });

        // Pending failure priming is in databag (not yet consumed by orchestrator)
        context.DataBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PostImplementationGates,
            Command: "dotnet test",
            Output: "Exit Code: 1\nErrors",
            IsSuccess: false
        ));

        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Prompt: {{ task.title }}\n{{- if context.compiler_errors }}\n[Compiler Errors]:\n{{ context.compiler_errors }}\n{{- end }}\n{{- if context.failed_tests }}\n[Failed Tests]:\n{{ context.failed_tests }}\n{{- end }}"
        };

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.Contains("[Compiler Errors]:", prompt);
        Assert.Contains("CS0103", prompt);
        Assert.Contains("[Failed Tests]:", prompt);
        Assert.Contains("UnitTest1.TestMethod2", prompt);
    }

    [Fact]
    public async Task DeterministicStageFailure_Loopback_PopulatesPendingSyntheticPriming_WithFailedCommandAndDiagnostics()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet format --verify-no-changes", "All files formatted.", DeterministicStageConstants.FormatSubStage, true, 100),
            new("dotnet build --no-incremental", "Build succeeded.", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test --no-build", "Exit Code: 1\nAssert.Equal() Failure\nExpected: 42\nActual: 0", DeterministicStageConstants.TestSubStage, false, 350)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, ct) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "TestClass.TestMethod failed" });
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure("1 test failed"));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview); // Cross-cohort to exit cohort cleanly

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { detStageMock.Object, targetStageMock.Object }));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 300,
            WorkspaceId = 1,
            Title = "Task with failing gate",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(StandardStageIds.Implementation, reloaded.CurrentStageId);

        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);
        Assert.Equal(StandardStageIds.PostImplementationGates, priming.SourceStageId);
        Assert.Equal("dotnet format --verify-no-changes && dotnet build --no-incremental && dotnet test --no-build", priming.Command);
        Assert.False(priming.IsSuccess);
        Assert.True(priming.IsPreFiltered);
        Assert.Contains("Exit Code: 1", priming.Output);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("Build succeeded.", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.Contains("Assert.Equal() Failure", priming.Output);
        Assert.Contains("[Failed Tests]:", priming.Output);
        Assert.Contains("TestClass.TestMethod failed", priming.Output);
        Assert.Equal(650, priming.DurationMs);
    }

    [Fact]
    public async Task DeterministicStageSuccess_CombinesExecutedSubStages_AndPopulatesPendingSyntheticPriming()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };

        // Custom pipeline with composite deterministic stage that succeeds
        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet format --verify-no-changes", "All files formatted.", DeterministicStageConstants.FormatSubStage, true, 100),
            new("dotnet build --no-incremental", "Build succeeded.", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test --no-build", "Test run successful. Total: 42, Passed: 42.", DeterministicStageConstants.TestSubStage, true, 300),
            new("sast-scan", "No vulnerabilities found.", DeterministicStageConstants.SastSubStage, true, 50)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PreImplementationBaseline);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, ct) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PreImplementationBaseline), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var nextStageMock = new Mock<IWorkflowStage>();
        nextStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        nextStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        nextStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { detStageMock.Object, nextStageMock.Object }));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 200,
            WorkspaceId = 1,
            Title = "Task with baseline",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);

        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);
        Assert.Equal(StandardStageIds.PreImplementationBaseline, priming.SourceStageId);
        Assert.Equal("dotnet format --verify-no-changes && dotnet build --no-incremental && dotnet test --no-build && sast-scan", priming.Command);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("Build succeeded.", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.Contains("Test run successful", priming.Output);
        Assert.Contains("Exit Code: 0", priming.Output);
        Assert.True(priming.IsSuccess);
        Assert.True(priming.IsPreFiltered);
        Assert.Equal(650, priming.DurationMs);
    }

    [Fact]
    public async Task AgentStageExecution_ConsumesPendingSyntheticPriming_AndSuppliesToAgentSessionContext()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[]
            {
                new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
                {
                    QueueTier = StageQueueTier.Implementation
                }
            }))
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            Command: "dotnet test",
            Output: "Exit Code: 0\nAll tests passed",
            IsSuccess: true,
            DurationMs: 250
        ));

        var task = new ForgeTask
        {
            Id = 201,
            WorkspaceId = 1,
            Title = "Task with pending priming",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation complete");
            });

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext.SyntheticPriming);
        Assert.Equal("dotnet test", capturedContext.SyntheticPriming.Command);
        Assert.True(capturedContext.SyntheticPriming.IsSuccess);
        Assert.Equal(250, capturedContext.SyntheticPriming.DurationMs);

        // DataBag in repo must have consumed the priming
        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bagAfter = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Null(bagAfter.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming));
    }

    [Fact]
    public async Task AgentStageExecution_SuppressesSyntheticPriming_WhenFlagIsDisabled()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags
                    {
                        SyntheticConversationPriming = false
                    }
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            Command: "dotnet test",
            Output: "Exit Code: 0\nAll tests passed",
            IsSuccess: true
        ));

        var task = new ForgeTask
        {
            Id = 202,
            WorkspaceId = 1,
            Title = "Task with disabled priming",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation complete");
            });

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(capturedContext);
        Assert.Null(capturedContext.SyntheticPriming);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeterministicStageSuccess_RespectsTargetStageSmartCliOutputFilteringFlag_InPendingSyntheticPriming(bool smartFilteringEnabled)
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };

        var noisyTestOutput = "  Determining projects to restore...\n"
            + string.Join("\n", Enumerable.Range(1, 20).Select(i => $"  Passed Suite.Test_{i} [2 ms]"))
            + "\nPassed!  - Failed:     0, Passed:    20, Skipped:     0, Total:    20";

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet test", noisyTestOutput, DeterministicStageConstants.TestSubStage, true, 300)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PreImplementationBaseline);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PreImplementationBaseline), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var nextStageMock = new Mock<IWorkflowStage>();
        nextStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        nextStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        nextStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, nextStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 10,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = smartFilteringEnabled }
                }
            ]
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = smartFilteringEnabled ? 301 : 302,
            WorkspaceId = 10,
            Title = "Smart Priming Filter Test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 10, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        if (smartFilteringEnabled)
        {
            Assert.Contains(NightCodeForge.Core.Workflow.Filtering.CliOutputFilterConstants.SimplifiedOutputHeader, priming.Output);
            Assert.DoesNotContain("Passed Suite.Test_1", priming.Output);
            Assert.Contains("Passed!  - Failed:     0", priming.Output);
        }
        else
        {
            Assert.DoesNotContain(NightCodeForge.Core.Workflow.Filtering.CliOutputFilterConstants.SimplifiedOutputHeader, priming.Output);
            Assert.Contains("Passed Suite.Test_1", priming.Output);
        }
    }

    [Fact]
    public async Task DeterministicStageSuccess_ExcludesSkippedUnavailableIgnoredAndPassingRatchetSubStages()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var subExecutions = new List<StageCommandExecution>
        {
            new("format (skipped)", "Deterministic code formatting step skipped.", DeterministicStageConstants.FormatSubStage, true, 5),
            new("dotnet build --no-incremental", "Build succeeded.\n    0 Warning(s)", DeterministicStageConstants.BuildSubStage, true, 210),
            new("dotnet test --no-build", "Test run successful. Total: 15, Passed: 15.", DeterministicStageConstants.TestSubStage, true, 340),
            new("sast (skipped)", "Deterministic SAST gate skipped.", DeterministicStageConstants.SastSubStage, true, 5),
            new("dotnet format (unavailable)", "Command unavailable.", DeterministicStageConstants.FormatSubStage, true, 5),
            new("dotnet format (failed, ignored)", "Format diff ignored.", DeterministicStageConstants.FormatSubStage, true, 5),
            new(NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage.DefaultDisplayName, "Verification completed successfully.", DeterministicStageConstants.RatchetSubStage, true, 2)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var nextStageMock = new Mock<IWorkflowStage>();
        nextStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        nextStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        nextStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, nextStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 11, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 303,
            WorkspaceId = 11,
            Title = "Exclusion test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 11, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.Equal("dotnet build --no-incremental && dotnet test --no-build", priming.Command);
        Assert.True(priming.IsSuccess);
        Assert.True(priming.IsPreFiltered);
        Assert.Equal(550, priming.DurationMs);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.DoesNotContain("format (skipped)", priming.Output);
        Assert.DoesNotContain("sast (skipped)", priming.Output);
        Assert.DoesNotContain("(unavailable)", priming.Output);
        Assert.DoesNotContain("(failed, ignored)", priming.Output);
        Assert.DoesNotContain($"[{DeterministicStageConstants.RatchetSubStage}]", priming.Output);
    }

    [Fact]
    public async Task DeterministicStageFailure_OnNonShellRatchet_PreservesShellCommandAndIncludesRatchetDiagnostics()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var ratchetError = "Warning ratchet violation: warning count increased from 0 to 2 (Delta: +2). Details: warning CS0168";
        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet build --no-incremental", "Build succeeded.\n    2 Warning(s)", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test --no-build", "Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10", DeterministicStageConstants.TestSubStage, true, 300),
            new(NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage.DefaultDisplayName, ratchetError, DeterministicStageConstants.RatchetSubStage, false, 3)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure(ratchetError, StandardStageIds.Implementation));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, targetStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 12, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 304,
            WorkspaceId = 12,
            Title = "Ratchet failure test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 12, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.Equal("dotnet build --no-incremental && dotnet test --no-build", priming.Command);
        Assert.False(priming.IsSuccess);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.Contains($"=== [{DeterministicStageConstants.RatchetSubStage}] ===", priming.Output);
        Assert.Contains(ratchetError, priming.Output);
    }

    [Fact]
    public async Task DeterministicStageFailure_StoppingEarlyAtBuild_EmitsOnlyBuildCommandAndDiagnostics()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet build --no-incremental", "Program.cs(10,5): error CS1002: ; expected\nBuild FAILED.", DeterministicStageConstants.BuildSubStage, false, 180)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "error CS1002: ; expected" });
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure("Deterministic build failed with exit code 1.", StandardStageIds.Implementation));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, targetStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 13, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 305,
            WorkspaceId = 13,
            Title = "Early build failure test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 13, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.Equal("dotnet build --no-incremental", priming.Command);
        Assert.False(priming.IsSuccess);
        Assert.Equal(180, priming.DurationMs);
        Assert.Contains("Exit Code: 1", priming.Output);
        Assert.Contains("CS1002", priming.Output);
        Assert.Contains("[Compiler Diagnostics]:", priming.Output);
        Assert.DoesNotContain("dotnet test", priming.Command);
    }

    [Fact]
    public async Task DeterministicStageSuccess_FiltersBuildAndTestSubStagesIndividuallyBeforeConcatenation()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var noisyBuildOutput = "  Determining projects to restore...\n"
            + "  App -> C:\\repo\\bin\\Debug\\net10.0\\App.dll\n"
            + "  Core -> C:\\repo\\bin\\Debug\\net10.0\\Core.dll\n"
            + "Build succeeded.\n    0 Warning(s)\n    0 Error(s)";
        var noisyTestOutput = "  Determining projects to restore...\n"
            + string.Join("\n", Enumerable.Range(1, 15).Select(i => $"  Passed Suite.Test_{i} [2 ms]"))
            + "\nPassed!  - Failed:     0, Passed:    15, Skipped:     0, Total:    15";

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet build --no-incremental", noisyBuildOutput, DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test --no-build", noisyTestOutput, DeterministicStageConstants.TestSubStage, true, 300)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PreImplementationBaseline);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PreImplementationBaseline), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var nextStageMock = new Mock<IWorkflowStage>();
        nextStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        nextStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        nextStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, nextStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 14,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = true }
                }
            ]
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 306,
            WorkspaceId = 14,
            Title = "Per-command filter test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 14, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.Equal("dotnet build --no-incremental && dotnet test --no-build", priming.Command);
        Assert.True(priming.IsPreFiltered);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.Contains("Passed!  - Failed:     0", priming.Output);
        Assert.DoesNotContain("Determining projects to restore...", priming.Output);
        Assert.DoesNotContain("Passed Suite.Test_1", priming.Output);
    }

    [Fact]
    public async Task DeterministicStageOversizedTestOutput_PreservesExitCodeAndBuildSectionAndDiagnosticsWithinMaxChars()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var oversizedTestOutput = "Unrecognized runner banner\n" + new string('z', WorkflowDefaults.MaxStageOutputChars + 5000);
        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet build --no-incremental", "Build succeeded.\n    0 Warning(s)", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test --no-build", oversizedTestOutput, DeterministicStageConstants.TestSubStage, false, 400)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "Suite.FailingTest failed" });
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure("1 test failed", StandardStageIds.Implementation));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, targetStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 15, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 307,
            WorkspaceId = 15,
            Title = "Oversized priming budget test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 15, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.True(priming.Output.Length <= WorkflowDefaults.MaxStageOutputChars);
        Assert.StartsWith("Exit Code: 1\n", priming.Output, StringComparison.Ordinal);
        Assert.Contains("=== [build] dotnet build --no-incremental ===", priming.Output);
        Assert.Contains("Build succeeded.", priming.Output);
        Assert.Contains("=== [test] dotnet test --no-build ===", priming.Output);
        Assert.Contains("... additional ", priming.Output);
        Assert.Contains("[Quality Gate Failure Reason]:", priming.Output);
        Assert.Contains("1 test failed", priming.Output);
        Assert.Contains("[Failed Tests]:", priming.Output);
        Assert.Contains("Suite.FailingTest failed", priming.Output);
    }

    [Fact]
    public async Task PostImplementationGatesSuccess_ResolvesSmartCliFilteringFromNextAgenticStageAndGlobalProfiles()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var noisyTestOutput = "  Determining projects to restore...\n"
            + string.Join("\n", Enumerable.Range(1, 10).Select(i => $"  Passed Suite.Test_{i} [2 ms]"))
            + "\nPassed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10";

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet test --no-build", noisyTestOutput, DeterministicStageConstants.TestSubStage, true, 300)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var reviewStageMock = new Mock<IWorkflowStage>();
        reviewStageMock.Setup(s => s.StageId).Returns(StandardStageIds.CodeReviewAndFix);
        reviewStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        reviewStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.CodeReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, reviewStageMock.Object]));

        var inMemWsRepo = (InMemoryWorkspaceRepository)builder.WorkspaceRepository;
        inMemWsRepo.SeedGlobalProfiles(
        [
            new AgentProfile
            {
                Id = StandardStageIds.Implementation,
                HarnessId = StandardHarnessIds.Crucible,
                Mode = AgentMode.Full,
                OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = true }
            },
            new AgentProfile
            {
                Id = StandardStageIds.CodeReviewAndFix,
                HarnessId = StandardHarnessIds.Crucible,
                Mode = AgentMode.Review,
                OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = false }
            }
        ]);

        var orchestrator = builder.Build();
        var ws = new Workspace { Id = 16, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 308,
            WorkspaceId = 16,
            Title = "Global target profile resolution test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 16, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.False(priming.IsPreFiltered);
        Assert.Contains("Passed Suite.Test_1", priming.Output);
        Assert.DoesNotContain(NightCodeForge.Core.Workflow.Filtering.CliOutputFilterConstants.SimplifiedOutputHeader, priming.Output);
    }

    [Fact]
    public async Task DeterministicStageFailure_WhenOnlySkippedAndRatchetSubStagesRan_DoesNotLeakPlaceholdersIntoCommand()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var ratchetError = "Warning ratchet violation: warning count increased from 0 to 1.";
        var subExecutions = new List<StageCommandExecution>
        {
            new($"format{DeterministicStageConstants.SkippedCommandSuffix}", "Format skipped.", DeterministicStageConstants.FormatSubStage, true, 5),
            new(NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage.DefaultDisplayName, ratchetError, DeterministicStageConstants.RatchetSubStage, false, 3)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommand(StandardStageIds.PostImplementationGates),
                    $"format{DeterministicStageConstants.SkippedCommandSuffix} && {NightCodeForge.Core.Workflow.Stages.DeterministicWarningRatchetStage.DefaultDisplayName}");
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure(ratchetError, StandardStageIds.Implementation));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, targetStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 17, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 309,
            WorkspaceId = 17,
            Title = "No placeholder command leak test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 17, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.Equal(DeterministicStageConstants.DefaultTestCommand, priming.Command);
        Assert.DoesNotContain(DeterministicStageConstants.SkippedCommandSuffix, priming.Command);
        Assert.DoesNotContain("Ratchet", priming.Command);
        Assert.True(priming.IsPreFiltered);
        Assert.Contains(ratchetError, priming.Output);
    }

    [Fact]
    public async Task DeterministicStageSuccess_WhenAllSubStagesSkipped_DoesNotEmitFakeSyntheticPriming()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var subExecutions = new List<StageCommandExecution>
        {
            new($"format{DeterministicStageConstants.SkippedCommandSuffix}", "Format skipped.", DeterministicStageConstants.FormatSubStage, true, 5),
            new($"sast{DeterministicStageConstants.SkippedCommandSuffix}", "SAST skipped.", DeterministicStageConstants.SastSubStage, true, 5)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommand(StandardStageIds.PostImplementationGates),
                    $"format{DeterministicStageConstants.SkippedCommandSuffix} && sast{DeterministicStageConstants.SkippedCommandSuffix}");
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Success());

        var nextStageMock = new Mock<IWorkflowStage>();
        nextStageMock.Setup(s => s.StageId).Returns(StandardStageIds.CodeReviewAndFix);
        nextStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        nextStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.CodeReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, nextStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 18, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution("stale-stage", Command: "stale", Output: "stale"));

        var task = new ForgeTask
        {
            Id = 310,
            WorkspaceId = 18,
            Title = "All skipped sub-stages test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates,
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 18, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Null(bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming));
    }

    [Theory]
    [InlineData(311, "Exit Code: 0\nPassed! - Failed: 0, Passed: 10, Total: 10", "Coverage regression detected", "Exit Code: 1\n")]
    [InlineData(312, "Exit Code: 124\n[TIMEOUT] Command timed out", "Test run timed out", "Exit Code: 124\n")]
    public async Task DeterministicStageFailure_NormalizesContradictoryZeroExitPrefixAndPreservesNonZeroCodes(
        int taskId,
        string subOutput,
        string errorMessage,
        string expectedPrefix)
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };

        var subExecutions = new List<StageCommandExecution>
        {
            new("dotnet test --no-build", subOutput, DeterministicStageConstants.TestWithCoverageSubStage, false, 300)
        };

        var detStageMock = new Mock<IWorkflowStage>();
        detStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        detStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        detStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        detStageMock.Setup(s => s.LoopBackStageId).Returns(StandardStageIds.Implementation);
        detStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, _) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.StageCommands(StandardStageIds.PostImplementationGates), subExecutions);
            })
            .ReturnsAsync(StageExecutionResult.Failure(errorMessage, StandardStageIds.Implementation));

        var targetStageMock = new Mock<IWorkflowStage>();
        targetStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        targetStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        targetStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([detStageMock.Object, targetStageMock.Object]));
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = taskId, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = taskId,
            WorkspaceId = taskId,
            Title = "Exit code normalization test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, taskId, task.Status);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        var priming = bag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming);
        Assert.NotNull(priming);

        Assert.StartsWith(expectedPrefix, priming.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit Code: 0", priming.Output);
        Assert.Contains(errorMessage, priming.Output);
    }
}
