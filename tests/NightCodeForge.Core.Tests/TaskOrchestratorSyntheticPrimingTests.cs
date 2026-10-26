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
            new("dotnet build", "Build succeeded.", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test", "Exit Code: 1\nAssert.Equal() Failure\nExpected: 42\nActual: 0", DeterministicStageConstants.TestSubStage, false, 350)
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
        Assert.Equal("dotnet test", priming.Command);
        Assert.False(priming.IsSuccess);
        Assert.Contains("Exit Code: 1", priming.Output);
        Assert.Contains("Assert.Equal() Failure", priming.Output);
        Assert.Contains("[Failed Tests]:", priming.Output);
        Assert.Contains("TestClass.TestMethod failed", priming.Output);
        Assert.Equal(350, priming.DurationMs);
    }

    [Fact]
    public async Task DeterministicStageSuccess_PrioritizesTestSubStage_AndPopulatesPendingSyntheticPriming()
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
            new("dotnet build", "Build succeeded.", DeterministicStageConstants.BuildSubStage, true, 200),
            new("dotnet test", "Test run successful. Total: 42, Passed: 42.", DeterministicStageConstants.TestSubStage, true, 300),
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
        Assert.Equal("dotnet test", priming.Command);
        Assert.Contains("Test run successful", priming.Output);
        Assert.Contains("Exit Code: 0", priming.Output);
        Assert.True(priming.IsSuccess);
        Assert.Equal(300, priming.DurationMs);
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
}
