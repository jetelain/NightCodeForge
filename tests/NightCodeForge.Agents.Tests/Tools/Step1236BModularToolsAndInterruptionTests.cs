using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class Step1236BModularToolsAndInterruptionTests : IDisposable
{
    private readonly string _tempDir;

    public Step1236BModularToolsAndInterruptionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-step1236b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void CanonicalTools_ComposesAllFiveCoreAgentToolModules_WithComplete14ToolCatalog()
    {
        var tools = TestCanonicalToolsBuilder.Create(_tempDir, pathResolver: TestPathResolver.Create());

        Assert.Equal(5, tools.Modules.Count);
        Assert.Same(tools.FileModule, tools.Modules[0]);
        Assert.Same(tools.BashModule, tools.Modules[1]);
        Assert.Same(tools.InteractionModule, tools.Modules[2]);
        Assert.Same(tools.BacklogModule, tools.Modules[3]);
        Assert.Same(tools.ResearchAndSkillsModule, tools.Modules[4]);

        Assert.Equal(
            [CanonicalToolNames.Read, CanonicalToolNames.Write, CanonicalToolNames.Edit],
            tools.FileModule.CreateFunctions().Select(f => f.Name).ToArray());

        Assert.Equal(
            [CanonicalToolNames.Bash],
            tools.BashModule.CreateFunctions().Select(f => f.Name).ToArray());

        Assert.Equal(
            [CanonicalToolNames.Ask, CanonicalToolNames.RequestChanges],
            tools.InteractionModule.CreateFunctions().Select(f => f.Name).ToArray());

        Assert.Equal(
            [
                CanonicalToolNames.ProposeTasks,
                CanonicalToolNames.GetTask,
                CanonicalToolNames.EditTask,
                CanonicalToolNames.SetTaskPriority,
                CanonicalToolNames.ReworkPriorities,
                CanonicalToolNames.RemoveTask
            ],
            tools.BacklogModule.CreateFunctions().Select(f => f.Name).ToArray());

        Assert.Equal(
            [CanonicalToolNames.LoadSkill, CanonicalToolNames.WebSearch],
            tools.ResearchAndSkillsModule.CreateFunctions().Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task WorkspaceFileToolModule_And_SandboxBashToolModule_DelegateAllPathResolutionToISandboxPathResolver()
    {
        var scope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: _tempDir,
            wslNcfLogsPath: "/mnt/c/ncf-logs",
            hostSkillsPath: _tempDir,
            wslSkillsPath: "/mnt/c/skills",
            executionMode: ExecutionMode.WslDirect);
        var pathResolver = new SandboxPathResolver(scope);

        var logFile = Path.Combine(_tempDir, "nightwatch.log");
        await File.WriteAllTextAsync(logFile, "Line 1\nLine 2\nLine 3");

        var toolScope = new CanonicalToolScope(
            "/nightcodeforge",
            IsReadOnly: true,
            SandboxScope: scope,
            SandboxPathResolver: pathResolver);

        var fileModule = new WorkspaceFileToolModule(
            toolScope,
            pathResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        var readOutput = await fileModule.ReadAsync("/nightcodeforge/ncf-logs/nightwatch.log", offset: 2, limit: 1);
        Assert.Contains("Line 2", readOutput);
        Assert.DoesNotContain("Line 1", readOutput);

        string? capturedCommand = null;
        string? capturedWorkDir = null;
        var mockEnv = new Mock<IExecutionEnvironment>();
        mockEnv.Setup(e => e.Tier).Returns(ExecutionTier.WslDirect);
        mockEnv.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, wd, _, _, _) =>
            {
                capturedCommand = cmd;
                capturedWorkDir = wd;
            })
            .ReturnsAsync(new CommandResult(0, "matched line", string.Empty, TimeSpan.FromMilliseconds(5)));

        var bashModule = new SandboxBashToolModule(
            toolScope,
            pathResolver,
            mockEnv.Object,
            NullLogger.Instance);

        var bashOutput = await bashModule.BashAsync("grep -n 'Line' /nightcodeforge/ncf-logs/nightwatch.log", workingDirectory: "/nightcodeforge/ncf-logs");
        Assert.Contains("Exit Code: 0", bashOutput);
        Assert.Equal("/mnt/c/ncf-logs", capturedWorkDir);
        Assert.Equal("grep -n 'Line' /mnt/c/ncf-logs/nightwatch.log", capturedCommand);
    }

    [Fact]
    public async Task WorkspaceFileToolModule_ResolvesITaskMarkdownValidatorPerToolInvocationViaIServiceScopeFactory()
    {
        int scopesCreated = 0;
        int scopesDisposed = 0;
        int validationsExecuted = 0;

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope())
            .Returns(() =>
            {
                Interlocked.Increment(ref scopesCreated);
                var validator = new Mock<ITaskMarkdownValidator>();
                validator.Setup(v => v.IsTaskSpecificationPath(It.IsAny<string>()))
                    .Returns<string>(p => p.StartsWith("docs/tasks/", StringComparison.OrdinalIgnoreCase));
                validator.Setup(v => v.ValidateAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() =>
                    {
                        var callNum = Interlocked.Increment(ref validationsExecuted);
                        return callNum == 1
                            ? TaskMarkdownValidationResult.Valid
                            : TaskMarkdownValidationResult.Invalid("Error: Dependency cycle detected in task spec.");
                    });

                var sp = new Mock<IServiceProvider>();
                sp.Setup(p => p.GetService(typeof(ITaskMarkdownValidator))).Returns(validator.Object);

                return new TrackingServiceScope(sp.Object, () => Interlocked.Increment(ref scopesDisposed));
            });

        var scope = new CanonicalToolScope(_tempDir, WorkspaceId: 10, TaskId: 5);
        var pathResolver = new WorkspacePathResolverSandboxAdapter(scope, TestPathResolver.Create(), NullLogger.Instance);
        var fileModule = new WorkspaceFileToolModule(
            scope,
            pathResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            mockScopeFactory.Object,
            NullLogger.Instance);

        // First invocation succeeds and disposes its scope immediately
        var write1 = await fileModule.WriteAsync("docs/tasks/0005-feature.md", "# Task 5\nValid content");
        Assert.Contains("Successfully wrote", write1);
        Assert.Equal(1, scopesCreated);
        Assert.Equal(1, scopesDisposed);

        // Second invocation in the same warm module creates a fresh scope and surfaces validation error
        var write2 = await fileModule.WriteAsync("docs/tasks/0005-feature.md", "# Task 5\nCyclic content");
        Assert.Equal("Error: Dependency cycle detected in task spec.", write2);
        Assert.Equal(2, scopesCreated);
        Assert.Equal(2, scopesDisposed);
    }

    [Fact]
    public async Task BacklogManagementToolModule_ResolvesIBacklogMutationToolServicePerInvocationViaIServiceScopeFactory()
    {
        int scopesCreated = 0;
        int scopesDisposed = 0;

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope())
            .Returns(() =>
            {
                var scopeNumber = Interlocked.Increment(ref scopesCreated);
                bool disposed = false;

                var service = new Mock<IBacklogMutationToolService>();
                service.Setup(s => s.GetTaskAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((BacklogToolScope bScope, int id, CancellationToken _) =>
                    {
                        if (disposed)
                        {
                            throw new ObjectDisposedException("AppDbContext");
                        }
                        return $"{{\"id\":{id},\"scopeCall\":{scopeNumber},\"workspaceId\":{bScope.WorkspaceId}}}";
                    });
                service.Setup(s => s.SetTaskPriorityAsync(It.IsAny<BacklogToolScope>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((BacklogToolScope _, int id, int prio, CancellationToken _) =>
                    {
                        if (disposed)
                        {
                            throw new ObjectDisposedException("AppDbContext");
                        }
                        return $"Updated task #{id} priority to {prio} in scope #{scopeNumber}";
                    });

                var sp = new Mock<IServiceProvider>();
                sp.Setup(p => p.GetService(typeof(IBacklogMutationToolService))).Returns(service.Object);

                return new TrackingServiceScope(sp.Object, () =>
                {
                    disposed = true;
                    Interlocked.Increment(ref scopesDisposed);
                });
            });

        var scope = new CanonicalToolScope(_tempDir, WorkspaceId: 77);
        var backlogModule = new BacklogManagementToolModule(scope, mockScopeFactory.Object, attachBacklogMutationService: true);

        Assert.True(backlogModule.IsBacklogMutationAvailable);

        var res1 = await backlogModule.GetTaskAsync(12);
        Assert.Contains("\"scopeCall\":1", res1);
        Assert.Equal(1, scopesCreated);
        Assert.Equal(1, scopesDisposed);

        var res2 = await backlogModule.SetTaskPriorityAsync(12, 1);
        Assert.Equal("Updated task #12 priority to 1 in scope #2", res2);
        Assert.Equal(2, scopesCreated);
        Assert.Equal(2, scopesDisposed);
    }

    [Fact]
    public async Task WorkflowInteractionToolModule_And_ToolTurnInterruptionController_SupportInPlaceAndTerminalUnwindingModes()
    {
        var module = new WorkflowInteractionToolModule();
        var questions = new[] { new QuestionItem("Which database provider?", "Storage selection", ["SQLite", "PostgreSQL"]) };

        // 1. In-Place Interactive Mode (e.g., Ideation): AskHandler returns answer without unwinding
        using var cts = new CancellationTokenSource();
        using var controller = new ToolTurnInterruptionController(cts.Token);
        module.InterruptionController = controller;
        module.AskHandler = (q, _) => Task.FromResult($"1. {q[0].Prompt} -> SQLite");

        var inPlaceAnswer = await module.AskAsync(questions);
        Assert.Equal("1. Which database provider? -> SQLite", inPlaceAnswer);
        Assert.False(controller.HasInterruption);
        Assert.False(controller.InterruptionToken.IsCancellationRequested);

        // 2. Turn-Unwinding Mode for Ask (e.g., Crucible stage): AskHandler is null, InterruptionController unwinds
        module.AskHandler = null;
        await Assert.ThrowsAsync<OperationCanceledException>(() => module.AskAsync(questions));
        Assert.True(controller.HasInterruption);
        Assert.True(controller.InterruptionToken.IsCancellationRequested);
        var askInterruption = Assert.IsType<HumanClarificationInterruption>(controller.CapturedInterruption);
        Assert.Single(askInterruption.Questions);
        Assert.Equal("Which database provider?", askInterruption.Questions[0].Prompt);

        // 3. Turn-Unwinding Mode for RequestChanges
        using var controller2 = new ToolTurnInterruptionController();
        module.InterruptionController = controller2;
        Assert.Throws<OperationCanceledException>(() => module.RequestChanges("Fix null check", "NullReferenceException in handler"));
        Assert.True(controller2.HasInterruption);
        var rcInterruption = Assert.IsType<ReviewerChangesRequestedInterruption>(controller2.CapturedInterruption);
        Assert.Equal("Fix null check", rcInterruption.Feedback);
        Assert.Equal("NullReferenceException in handler", rcInterruption.Reason);
        Assert.Equal(StandardStageIds.Implementation, rcInterruption.TargetStageId);
    }

    [Fact]
    public async Task CanonicalToolsetFactory_SupportsAdditionalModules_AndExtensibleTerminalInterruptions()
    {
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IExecutionEnvironment>().Object);

        var mcp = new Mock<IMcpClientManager>();
        mcp.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, NightCodeForge.Agents.Mcp.McpServerDefinition>());

        var factory = new CanonicalToolsetFactory(
            TestPathResolver.Create(),
            envProvider.Object,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            mcp.Object,
            TestBacklogContext.CreateDefault());

        var customModule = new SimulatedNightwatchToolModule();
        var workspace = new Workspace { Id = 1, Name = "TestWS", RepositoryPath = _tempDir };
        var request = new CanonicalToolsetRequest(
            workspace,
            new AgentProfile { Id = "nw", Name = "Nightwatch", SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
            new CanonicalToolScope(_tempDir, WorkspaceId: 1, IsReadOnly: true),
            PermittedTools: [CanonicalToolNames.Read, CanonicalToolNames.Bash],
            CustomTools: [],
            ToolDirectives: new Dictionary<string, string>
            {
                ["nightwatch_conclude_triage"] = "Custom directive for nightwatch triage conclusion."
            },
            AttachBacklogMutationService: false,
            AdditionalModules: [customModule]);

        var session = await factory.CreateAsync(request, CancellationToken.None);

        // Verify the additional module is included in session.Modules, session.AITools, and registered on Dispatcher
        Assert.Equal(6, session.Modules!.Count);
        Assert.Contains(customModule, session.Modules!);
        Assert.True(session.Dispatcher.IsToolRegistered("nightwatch_conclude_triage"));

        var triageTool = session.AITools.OfType<AIFunction>().Single(t => t.Name == "nightwatch_conclude_triage");
        Assert.Equal("Custom directive for nightwatch triage conclusion.", triageTool.Description);

        // Verify custom module can trigger an extensible ToolTurnInterruption via the session's controller
        // (UseInterruptionController automatically propagates the controller to all session.Modules)
        using var controller = session.UseInterruptionController();
        Assert.Same(controller, customModule.InterruptionController);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.Dispatcher.ExecuteAsync("nightwatch_conclude_triage", """{"verdict":"ResumeTask","summary":"Recovered"}"""));

        var customInterruption = Assert.IsType<SimulatedNightwatchDecisionInterruption>(controller.CapturedInterruption);
        Assert.Equal("ResumeTask", customInterruption.Verdict);
        Assert.Equal("Recovered", customInterruption.Summary);
    }

    [Fact]
    public async Task ReadOnlyScopeFromSandboxPathResolver_IsEnforcedInFileAndBashModules_EvenWhenCanonicalToolScopeIsReadOnlyFalse()
    {
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: _tempDir,
            wslNcfLogsPath: "/mnt/c/ncf-logs",
            hostSkillsPath: _tempDir,
            wslSkillsPath: "/mnt/c/skills",
            executionMode: ExecutionMode.WslDirect,
            securityTier: SandboxSecurityTier.RestrictedReadOnly);
        var nwResolver = new SandboxPathResolver(nwScope);

        // CanonicalToolScope.IsReadOnly is left at default (false), but nwResolver.Scope.IsReadOnly is true
        var toolScope = new CanonicalToolScope(
            "/nightcodeforge",
            IsReadOnly: false,
            SandboxScope: nwScope,
            SandboxPathResolver: nwResolver);

        var fileModule = new WorkspaceFileToolModule(
            toolScope,
            nwResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fileModule.WriteAsync("/nightcodeforge/ncf-logs/out.log", "blocked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fileModule.EditAsync("/nightcodeforge/ncf-logs/out.log", "old", "new"));

        var mockEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        var bashModule = new SandboxBashToolModule(
            toolScope,
            nwResolver,
            mockEnv.Object,
            NullLogger.Instance);

        var bashResult = await bashModule.BashAsync("rm -rf /nightcodeforge/ncf-logs");
        Assert.StartsWith("[READ-ONLY VIOLATION]:", bashResult);

        // Also verify WorkspacePathResolverSandboxAdapter blocks forWrite: true when IsReadOnly is true
        var adapterScope = new CanonicalToolScope(_tempDir, WorkspaceId: 5, IsReadOnly: true);
        var adapter = new WorkspacePathResolverSandboxAdapter(adapterScope, TestPathResolver.Create(), NullLogger.Instance);
        Assert.NotNull(adapter.MountTable.SandboxTmpMount);
        Assert.Throws<UnauthorizedAccessException>(() => adapter.ResolveHostPath("src/File.cs", forWrite: true));
    }

    [Fact]
    public async Task ResearchAndSkillsToolModule_WebSearchAsync_GracefullyHandlesUnmountedTmpInNightwatchScope()
    {
        var nwScope = SandboxScopeDescriptor.ForNightwatch(
            hostNcfLogsPath: _tempDir,
            wslNcfLogsPath: "/mnt/c/ncf-logs",
            hostSkillsPath: _tempDir,
            wslSkillsPath: "/mnt/c/skills");
        var nwResolver = new SandboxPathResolver(nwScope);

        var mockWebSearch = new Mock<IWebSearchService>();
        mockWebSearch.SetupGet(s => s.IsEnabled).Returns(true);
        mockWebSearch.SetupGet(s => s.IsConfigured).Returns(true);
        mockWebSearch.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebSearchResponse(
            [
                new WebSearchResultItem("Doc 1", "https://example.com/1", new string('X', WorkflowDefaults.MaxWebSearchResultInlineChars + 500))
            ]));

        var researchModule = new ResearchAndSkillsToolModule(
            nwResolver,
            mockWebSearch.Object,
            new Mock<IWorkspaceSkillsService>().Object);

        var output = await researchModule.WebSearchAsync("large nightwatch query");
        Assert.Contains("Web search results for: 'large nightwatch query'", output);
        Assert.Contains("truncated inline to fit context budget", output);
        Assert.DoesNotContain("/tmp/web-search-", output);
        Assert.DoesNotContain("full content:", output);
    }

    [Fact]
    public async Task BacklogPlanningContext_ResolvesSummaryServicePerScope_AndControllerHandlesDisposeAndDuplicateTriggers()
    {
        int scopesCreated = 0;
        int scopesDisposed = 0;

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope())
            .Returns(() =>
            {
                var num = Interlocked.Increment(ref scopesCreated);
                var summary = new Mock<IWorkspaceBacklogSummaryService>();
                summary.Setup(s => s.BuildDigestAsync(It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync($"<digest call=\"{num}\" />");

                var sp = new Mock<IServiceProvider>();
                sp.Setup(p => p.GetService(typeof(IWorkspaceBacklogSummaryService))).Returns(summary.Object);
                return new TrackingServiceScope(sp.Object, () => Interlocked.Increment(ref scopesDisposed));
            });

        var planningContext = new BacklogPlanningContext(
            new Mock<IWorkspaceBacklogSummaryService>().Object,
            new Mock<ITaskMarkdownValidator>().Object,
            new Mock<IBacklogMutationToolService>().Object,
            NullLogger<CanonicalTools>.Instance,
            mockScopeFactory.Object);

        var digest1 = await planningContext.BuildBacklogDigestAsync(1, null, CancellationToken.None);
        var digest2 = await planningContext.BuildBacklogDigestAsync(1, 2, CancellationToken.None);
        Assert.Equal("<digest call=\"1\" />", digest1);
        Assert.Equal("<digest call=\"2\" />", digest2);
        Assert.Equal(2, scopesCreated);
        Assert.Equal(2, scopesDisposed);

        // ToolTurnInterruptionController preserves the first captured interruption and remains safe after Dispose
        var controller = new ToolTurnInterruptionController();
        var first = new ReviewerChangesRequestedInterruption("First feedback", "Reason 1");
        var second = new ReviewerChangesRequestedInterruption("Second feedback", "Reason 2");

        Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(first));
        controller.Dispose();
        Assert.Throws<OperationCanceledException>(() => controller.TriggerTerminalInterruption(second));
        Assert.Same(first, controller.CapturedInterruption);
    }

    [Fact]
    public async Task CrucibleAgentHarness_UnwindsAskAndRequestChangesViaToolTurnInterruptionController()
    {
        // 1. Test Ask interruption unwinding in CrucibleAgentHarness
        int askModelCalls = 0;
        var askClient = new Mock<IChatClient>();
        askClient.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken _) =>
            {
                Interlocked.Increment(ref askModelCalls);
                var askCall = new FunctionCallContent(
                    "call_ask_1",
                    CanonicalToolNames.Ask,
                    new Dictionary<string, object?>
                    {
                        ["questions"] = new[]
                        {
                            new QuestionItem("Should we use Redis or MemoryCache?", "Caching tier choice", ["Redis", "MemoryCache"])
                        }
                    });
                return ToAsyncEnumerable(new ChatResponseUpdate(ChatRole.Assistant, [askCall]));
            });

        var askHarness = new TestCrucibleHarnessBuilder
        {
            PathResolver = TestPathResolver.Create(),
            BaseChatClient = askClient.Object
        }.Build();

        var workspace = new Workspace { Id = 1, Name = "TestWS", RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Implement caching", Description = "Add cache layer" };
        var askContext = new AgentSessionContext
        {
            Workspace = workspace,
            ForgeTask = task,
            WorkspacePath = _tempDir,
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            Profile = new AgentProfile { Id = "impl", Name = "Implementer", Mode = AgentMode.Full }
        };

        var askResult = await askHarness.RunStageAsync(askContext);
        Assert.Equal(AgentRunStatus.WaitingForHuman, askResult.Status);
        Assert.NotNull(askResult.PendingQuestions);
        Assert.Single(askResult.PendingQuestions!);
        Assert.Equal("Should we use Redis or MemoryCache?", askResult.PendingQuestions![0].Prompt);
        Assert.Equal(1, askModelCalls); // Unwound immediately without a second LLM turn

        // 2. Test RequestChanges interruption unwinding in CrucibleAgentHarness
        int rcModelCalls = 0;
        var rcClient = new Mock<IChatClient>();
        rcClient.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken _) =>
            {
                Interlocked.Increment(ref rcModelCalls);
                var rcCall = new FunctionCallContent(
                    "call_rc_1",
                    CanonicalToolNames.RequestChanges,
                    new Dictionary<string, object?>
                    {
                        ["feedback"] = "Missing unit tests for cache eviction.",
                        ["reason"] = "Incomplete test coverage"
                    });
                return ToAsyncEnumerable(new ChatResponseUpdate(ChatRole.Assistant, [rcCall]));
            });

        var rcHarness = new TestCrucibleHarnessBuilder
        {
            PathResolver = TestPathResolver.Create(),
            BaseChatClient = rcClient.Object
        }.Build();

        var rcContext = new AgentSessionContext
        {
            Workspace = workspace,
            ForgeTask = task,
            WorkspacePath = _tempDir,
            StageId = StandardStageIds.CodeReviewAndFix,
            RequiredMode = AgentMode.Review,
            Profile = new AgentProfile { Id = "rev", Name = "Reviewer", Mode = AgentMode.Review }
        };

        var rcResult = await rcHarness.RunStageAsync(rcContext);
        Assert.Equal(AgentRunStatus.Completed, rcResult.Status);
        Assert.Equal(StandardStageIds.Implementation, rcResult.TargetRouting);
        Assert.Equal("Missing unit tests for cache eviction.", rcResult.ReviewerFeedback);
        Assert.Equal(1, rcModelCalls); // Unwound immediately without a second LLM turn
    }

    [Fact]
    public async Task UseInterruptionController_DetachesFromSessionAndModulesOnDispose_WhilePreservingCapturedInterruption()
    {
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        envProvider.Setup(p => p.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IExecutionEnvironment>().Object);

        var mcp = new Mock<IMcpClientManager>();
        mcp.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, NightCodeForge.Agents.Mcp.McpServerDefinition>());

        var factory = new CanonicalToolsetFactory(
            TestPathResolver.Create(),
            envProvider.Object,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            mcp.Object,
            TestBacklogContext.CreateDefault());

        var customModule = new SimulatedNightwatchToolModule();
        var workspace = new Workspace { Id = 1, Name = "TestWS", RepositoryPath = _tempDir };
        var request = new CanonicalToolsetRequest(
            workspace,
            new AgentProfile { Id = "nw", Name = "Nightwatch", SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
            new CanonicalToolScope(_tempDir, WorkspaceId: 1, IsReadOnly: true),
            PermittedTools: [CanonicalToolNames.Read],
            CustomTools: [],
            ToolDirectives: new Dictionary<string, string>(),
            AttachBacklogMutationService: false,
            AdditionalModules: [null!, customModule]);

        var session = await factory.CreateAsync(request, CancellationToken.None);
        Assert.Equal(6, session.Modules!.Count);
        Assert.All(session.Modules!, Assert.NotNull);

        ToolTurnInterruptionController capturedController;
        using (var controller = session.UseInterruptionController())
        {
            capturedController = controller;
            Assert.Same(controller, session.InterruptionController);
            Assert.Same(controller, session.Tools.InterruptionController);
            Assert.Same(controller, session.Tools.InteractionModule.InterruptionController);
            Assert.Same(controller, customModule.InterruptionController);

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await session.Dispatcher.ExecuteAsync("nightwatch_conclude_triage", """{"verdict":"PauseTask","summary":"Needs env fix"}"""));
        }

        // After Dispose, session and all modules are cleanly detached so warm sessions do not retain disposed controllers
        Assert.Null(session.InterruptionController);
        Assert.Null(session.Tools.InterruptionController);
        Assert.Null(session.Tools.InteractionModule.InterruptionController);
        Assert.Null(customModule.InterruptionController);

        // CapturedInterruption on the controller instance itself remains preserved for caller inspection
        Assert.True(capturedController.HasInterruption);
        var interruption = Assert.IsType<SimulatedNightwatchDecisionInterruption>(capturedController.CapturedInterruption);
        Assert.Equal("PauseTask", interruption.Verdict);

        // Also verify CanonicalToolsetSession constructed with Modules: null propagates via Tools.Modules
        var minimalSession = new CanonicalToolsetSession(session.Tools, session.Dispatcher, session.SkillsService, session.AITools, Modules: null);
        using (var c2 = minimalSession.UseInterruptionController())
        {
            Assert.Same(c2, session.Tools.InteractionModule.InterruptionController);
        }
        Assert.Null(session.Tools.InteractionModule.InterruptionController);
    }

    [Fact]
    public async Task CrucibleAgentHarness_WhenUnhandledToolTurnInterruptionSubtypeTriggered_FailsFastInsteadOfCancelling()
    {
        var customModule = new SimulatedNightwatchToolModule();

        var envProvider = new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object);
        var mcp = new Mock<IMcpClientManager>();
        mcp.Setup(m => m.GetConfiguredServersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, NightCodeForge.Agents.Mcp.McpServerDefinition>());

        var innerToolsetFactory = new CanonicalToolsetFactory(
            TestPathResolver.Create(),
            envProvider,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWebSearchService>().Object,
            new WorkspaceSkillsServiceFactory(new SkillsService(), new BundledSkillsProvider(), NullLogger<WorkspaceSkillsService>.Instance),
            mcp.Object,
            TestBacklogContext.CreateDefault());

        var decoratingToolsetFactory = new Mock<ICanonicalToolsetFactory>();
        decoratingToolsetFactory
            .Setup(f => f.CreateAsync(It.IsAny<CanonicalToolsetRequest>(), It.IsAny<CancellationToken>()))
            .Returns((CanonicalToolsetRequest req, CancellationToken ct) =>
                innerToolsetFactory.CreateAsync(req with { AdditionalModules = [customModule] }, ct));

        var mockClient = new Mock<IChatClient>();
        mockClient.Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? _, CancellationToken _) =>
            {
                var customCall = new FunctionCallContent(
                    "call_nw_1",
                    "nightwatch_conclude_triage",
                    new Dictionary<string, object?>
                    {
                        ["verdict"] = "Escalate",
                        ["summary"] = "Unexpected subtype in Crucible"
                    });
                return ToAsyncEnumerable(new ChatResponseUpdate(ChatRole.Assistant, [customCall]));
            });

        var pathResolver = TestPathResolver.Create();
        var backlogContext = TestBacklogContext.CreateDefault();
        var gateway = new Mock<ILlmGatewayService>();
        gateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(131072);
        var llmOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Agents.Inference.LlmProviderOptions());
        var capabilityResolver = new NightCodeForge.Agents.Inference.OllamaCapabilityResolver(
            llmOptions,
            EmptyLlmContextWindowCache.Instance);
        var opsControl = new Mock<IOperationsControlService>();
        opsControl.Setup(o => o.IsPaused).Returns(false);

        var harness = new CrucibleAgentHarness(
            decoratingToolsetFactory.Object,
            new AgentWorkspaceContextService(
                pathResolver,
                new AgentsMdService(),
                new Mock<IWorkspaceSymbolService>().Object,
                new Mock<ITaskMarkdownSyncService>().Object,
                backlogContext,
                NullLogger<AgentWorkspaceContextService>.Instance),
            new HarnessAgentFactory(
                new TestCrucibleHarnessBuilder.TestSingleChatClientFactory(mockClient.Object),
                gateway.Object,
                capabilityResolver,
                new Mock<IGlobalOptimizationSettingsService>().Object,
                llmOptions,
                NullLogger<HarnessAgentFactory>.Instance),
            new CrucibleConversationCoordinator(
                new InMemoryTaskRepository(),
                new NightCodeForge.Agents.Transcripts.SessionTranscriptService(),
                gateway.Object,
                NullLogger<CrucibleConversationCoordinator>.Instance),
            new InMemoryAgentActivityStreamer(),
            new NightCodeForge.Core.Concurrency.TaskCancellationManager(NullLogger<NightCodeForge.Core.Concurrency.TaskCancellationManager>.Instance),
            opsControl.Object,
            Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.LlmRetryPolicyOptions()),
            NullLogger<CrucibleAgentHarness>.Instance,
            TimeProvider.System);

        var context = new AgentSessionContext
        {
            Workspace = new Workspace { Id = 1, Name = "TestWS", RepositoryPath = _tempDir },
            ForgeTask = new ForgeTask { Id = 42, WorkspaceId = 1, Title = "Test", Description = "Desc" },
            WorkspacePath = _tempDir,
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            Profile = new AgentProfile { Id = "impl", Name = "Implementer", Mode = AgentMode.Full }
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Contains("Unhandled terminal tool interruption 'SimulatedNightwatchDecisionInterruption'", result.Error);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(params ChatResponseUpdate[] updates)
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            yield return u;
        }
    }

    private sealed record SimulatedNightwatchDecisionInterruption(string Verdict, string Summary) : ToolTurnInterruption;

    private sealed class SimulatedNightwatchToolModule : IAgentToolModule
    {
        public IToolTurnInterruptionController? InterruptionController { get; set; }

        public IReadOnlyList<AIFunction> CreateFunctions() =>
        [
            AIFunctionFactory.Create(
                ([Description("Triage verdict")] string verdict,
                 [Description("Triage summary")] string summary) =>
                {
                    if (InterruptionController != null)
                    {
                        InterruptionController.TriggerTerminalInterruption(new SimulatedNightwatchDecisionInterruption(verdict, summary));
                    }
                    return $"Verdict: {verdict} ({summary})";
                },
                "nightwatch_conclude_triage",
                "Concludes the Nightwatch reactive triage session.")
        ];
    }

    private sealed class TrackingServiceScope(IServiceProvider serviceProvider, Action onDispose) : IServiceScope
    {
        private bool _disposed;
        public IServiceProvider ServiceProvider => serviceProvider;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                onDispose();
            }
        }
    }
}
