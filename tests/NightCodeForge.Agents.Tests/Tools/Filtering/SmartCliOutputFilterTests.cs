using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Filtering;
using Xunit;

namespace NightCodeForge.Agents.Tests;

public sealed class SmartCliOutputFilterTests : IDisposable
{
    private readonly string _tempRoot;

    public SmartCliOutputFilterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-smart-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("dotnet build", true)]
    [InlineData("dotnet test --no-build --filter Foo", true)]
    [InlineData("dotnet msbuild My.sln /t:Rebuild", true)]
    [InlineData("cd /workspace && DOTNET_CLI_HOME=/tmp dotnet build -c Release", true)]
    [InlineData("msbuild /m", true)]
    [InlineData("dotnet --version", false)]
    [InlineData("git status", false)]
    [InlineData("tsc --noEmit", false)]
    public void DotNetCliOutputFilter_CanHandle_DetectsSupportedCommands(string command, bool expected)
    {
        var filter = new DotNetCliOutputFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Theory]
    [InlineData("tsc --noEmit", true)]
    [InlineData("npx tsc --noEmit", true)]
    [InlineData("npm run build", true)]
    [InlineData("pnpm test", true)]
    [InlineData("yarn typecheck", true)]
    [InlineData("node index.js", false)]
    public void NodeTscCliFilter_CanHandle_DetectsSupportedCommands(string command, bool expected)
    {
        var filter = new NodeTscCliFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Theory]
    [InlineData("cargo build", true)]
    [InlineData("cargo test --release", true)]
    [InlineData("cargo clippy", true)]
    [InlineData("cargo check", true)]
    [InlineData("cargo --version", false)]
    public void CargoCliFilter_CanHandle_DetectsSupportedCommands(string command, bool expected)
    {
        var filter = new CargoCliFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Fact]
    public void Registry_ResolvesMatchingFilterOrNull()
    {
        var registry = CliOutputFilterRegistry.Default;
        Assert.IsType<DotNetCliOutputFilter>(registry.GetFilter("dotnet build"));
        Assert.IsType<NodeTscCliFilter>(registry.GetFilter("npm run build"));
        Assert.IsType<CargoCliFilter>(registry.GetFilter("cargo test"));
        Assert.Null(registry.GetFilter("ls -la"));
        Assert.Null(registry.GetFilter(string.Empty));
    }

    [Fact]
    public void DotNetCliOutputFilter_CleanNoisyBuild_CompressesMoreThan90Percent()
    {
        var sb = new StringBuilder();
        sb.AppendLine("\x1B[32mMSBuild version 17.10.4+10fbfbf2e for .NET\x1B[0m");
        sb.AppendLine("  Determining projects to restore...");
        for (int i = 0; i < 120; i++)
        {
            sb.AppendLine($"  Restored /workspace/src/Proj{i}/Proj{i}.csproj (in 14 ms).");
            sb.AppendLine($"  Proj{i} -> /workspace/src/Proj{i}/bin/Debug/net10.0/Proj{i}.dll");
        }

        sb.AppendLine();
        sb.AppendLine("Build succeeded.");
        sb.AppendLine("    0 Warning(s)");
        sb.AppendLine("    0 Error(s)");
        sb.AppendLine("Time Elapsed 00:00:08.42");

        var raw = sb.ToString();
        var filter = new DotNetCliOutputFilter();
        var filtered = filter.FilterOutput(raw, "/workspace", new CommandFilterOptions());

        Assert.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, filtered);
        Assert.Contains(CliOutputFilterConstants.BuildSucceededSummary, filtered);
        Assert.DoesNotContain("Restored", filtered);
        Assert.DoesNotContain("\x1B[32m", filtered);

        double ratio = 1.0 - (double)filtered.Length / raw.Length;
        Assert.True(ratio > 0.90, $"Expected >90% reduction, got {ratio:P1} ({raw.Length} -> {filtered.Length}).");
    }

    [Fact]
    public void DotNetCliOutputFilter_FatalErrorsWithWarnings_SuppressesWarningsAndRelativizesPaths()
    {
        var workspaceWin = @"C:\Users\dev\repo";
        var raw = $@"  Determining projects to restore...
  All projects are up-to-date for restore.
  1>C:\Users\dev\repo\src\Core\Engine.cs(14,9): warning CS0168: The variable 'unused' is declared but never used [C:\Users\dev\repo\src\Core\Core.csproj]
  1>/workspace/src/Core/Helper.cs(22,5): warning CS0219: The variable 'x' is assigned [C:\Users\dev\repo\src\Core\Core.csproj]
  2>\\wsl.localhost\Ubuntu\home\u\.nightcodeforge\wks\1\clone\src\App\Service.cs(45,13): error CS0103: The name 'MissingVar' does not exist in the current context [C:\Users\dev\repo\src\App\App.csproj]
  2>/workspace/task-7/src/App/Parser.cs(10,33): error CS1002: ; expected [/workspace/task-7/src/App/App.csproj]
Build FAILED.
    2 Warning(s)
    2 Error(s)";

        var filtered = new DotNetCliOutputFilter().FilterOutput(raw, workspaceWin, new CommandFilterOptions());

        Assert.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, filtered);
        Assert.Contains("src/App/Service.cs(45,13): error CS0103", filtered);
        Assert.Contains("src/App/Parser.cs(10,33): error CS1002", filtered);
        Assert.Contains("[⚠️ Suppressed 2 compiler warnings to focus on 2 fatal errors.]", filtered);
        Assert.EndsWith(CliOutputFilterConstants.BuildFailedSummary, filtered);

        Assert.DoesNotContain("CS0168", filtered);
        Assert.DoesNotContain("CS0219", filtered);
        Assert.DoesNotContain("Core.csproj", filtered);
        Assert.DoesNotContain(@"C:\Users\dev\repo", filtered);
        Assert.DoesNotContain(@"\\wsl.localhost", filtered);
        Assert.DoesNotContain("/workspace/task-7/", filtered);
    }

    [Fact]
    public void DotNetCliOutputFilter_WarningsOnlyExceedingMaxWarnings_CapsAndShowsOmissionNotice()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  Determining projects to restore...");
        for (int i = 1; i <= 12; i++)
        {
            sb.AppendLine($"  1>/workspace/src/File{i}.cs({i},4): warning CS0168: Unused variable w{i} [/workspace/Demo.csproj]");
        }
        sb.AppendLine("Build succeeded.");

        var filtered = new DotNetCliOutputFilter().FilterOutput(sb.ToString(), "/workspace", new CommandFilterOptions(MaxWarnings: 5));

        for (int i = 1; i <= 5; i++)
        {
            Assert.Contains($"src/File{i}.cs({i},4): warning CS0168", filtered);
        }

        for (int i = 6; i <= 12; i++)
        {
            Assert.DoesNotContain($"File{i}.cs", filtered);
        }

        Assert.Contains("[... and 7 additional warnings omitted. Run with disable_filter=true for complete list.]", filtered);
        Assert.EndsWith(CliOutputFilterConstants.BuildSucceededSummary, filtered);
    }

    [Fact]
    public void DotNetCliOutputFilter_FailedTestSuite_StripsPassingTestsAndRelativizesStackTrace()
    {
        var sb = new StringBuilder();
        sb.AppendLine("  Determining projects to restore...");
        for (int i = 1; i <= 80; i++)
        {
            sb.AppendLine($"  Passed Demo.Tests.FastTests.PassingTest_{i} [3 ms]");
        }

        sb.AppendLine("  Failed Demo.Tests.CalculatorTests.Add_Fails [12 ms]");
        sb.AppendLine("  Error Message:");
        sb.AppendLine("   Assert.Equal() Failure: Values differ");
        sb.AppendLine("   Expected: 4");
        sb.AppendLine("   Actual:   5");
        sb.AppendLine("  Stack Trace:");
        sb.AppendLine("     at Demo.Tests.CalculatorTests.Add_Fails() in /workspace/tests/CalculatorTests.cs:line 27");
        sb.AppendLine("Failed!  - Failed:     1, Passed:    80, Skipped:     0, Total:    81");
        sb.AppendLine("Test Run Failed.");

        var raw = sb.ToString();
        var filtered = new DotNetCliOutputFilter().FilterOutput(raw, "/workspace", new CommandFilterOptions());

        Assert.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, filtered);
        Assert.Contains("Failed Demo.Tests.CalculatorTests.Add_Fails", filtered);
        Assert.Contains("Assert.Equal() Failure", filtered);
        Assert.Contains("tests/CalculatorTests.cs:line 27", filtered);
        Assert.Contains("Failed!  - Failed:     1", filtered);
        Assert.DoesNotContain("PassingTest_1", filtered);
        Assert.DoesNotContain("PassingTest_80", filtered);
        Assert.DoesNotContain("/workspace/", filtered);
    }

    [Fact]
    public void DotNetCliOutputFilter_ShortUnrecognizedOutput_IsPassedThroughUnmodified()
    {
        var filter = new DotNetCliOutputFilter();
        Assert.Equal("Passed: 42", filter.FilterOutput("Passed: 42", "/workspace", new CommandFilterOptions()));
        Assert.Equal("ok", filter.FilterOutput("ok", "/workspace", new CommandFilterOptions()));
    }

    [Fact]
    public void NodeTscAndCargoFilters_SimplifyDiagnosticsAndRelativizePaths()
    {
        var tscRaw = """
            > web@1.0.0 build
            > tsc --noEmit
            /workspace/src/components/Button.tsx(12,5): error TS2322: Type 'string' is not assignable to type 'number'.
            /workspace/src/utils/date.ts(4,1): warning TS6133: 'tmp' is declared but never read.
            """;

        var tscFiltered = new NodeTscCliFilter().FilterOutput(tscRaw, "/workspace", new CommandFilterOptions());
        Assert.Contains("src/components/Button.tsx(12,5): error TS2322", tscFiltered);
        Assert.Contains("Suppressed 1 compiler warnings", tscFiltered);
        Assert.DoesNotContain("/workspace/", tscFiltered);

        var cargoRaw = """
               Compiling demo v0.1.0 (/workspace)
            error[E0308]: mismatched types
             --> /workspace/src/main.rs:8:14
            warning: unused variable: `x`
             --> /workspace/src/lib.rs:3:9
            error: could not compile `demo` (bin "demo") due to 1 previous error
            """;

        var cargoFiltered = new CargoCliFilter().FilterOutput(cargoRaw, "/workspace", new CommandFilterOptions());
        Assert.Contains("src/main.rs:8:14: error[E0308]: mismatched types", cargoFiltered);
        Assert.Contains("Suppressed 1 compiler warnings", cargoFiltered);
        Assert.DoesNotContain("Compiling demo", cargoFiltered);
        Assert.DoesNotContain("/workspace/", cargoFiltered);
    }

    [Fact]
    public async Task SandboxBashToolModule_StandardScope_SmartFiltersAndPersistsFullRawOutputToTmp()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-std");
        var tmpDir = Path.Combine(_tempRoot, "tmp-std");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tmpDir);

        var workspace = new Workspace { Id = 17, Name = "FilterWS", RepositoryPath = repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: repoDir,
            isReadOnly: false,
            hostTmpDirectory: tmpDir);
        var scopeDescriptor = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            SandboxSecurityTier.Standard,
            taskId: null,
            mountTable: mountTable,
            defaultWorkingDirectory: "/workspace",
            hostWorkspacePath: repoDir);
        var toolScope = new CanonicalToolScope(
            repoDir,
            WorkspaceId: 17,
            IsReadOnly: false,
            SandboxScope: scopeDescriptor);

        var resolver = new SandboxPathResolver(scopeDescriptor);

        var rawNoisyLog = string.Join("\n",
            Enumerable.Range(1, 60).Select(i => $"  Restored /workspace/Proj{i}/Proj{i}.csproj (in 5 ms)."))
            + "\nBuild succeeded.\n    0 Warning(s)\n    0 Error(s)";

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        execEnv
            .Setup(e => e.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, rawNoisyLog, string.Empty, TimeSpan.FromMilliseconds(20)));

        BashExecutionAuditEntry? capturedAudit = null;
        var module = new SandboxBashToolModule(
            toolScope with { OnBashExecutionAudited = entry => capturedAudit = entry },
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var result = await module.BashAsync("dotnet build");

        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, result);
        Assert.Contains(CliOutputFilterConstants.BuildSucceededSummary, result);
        Assert.Contains("[Full output : /tmp/bash-", result);
        Assert.Contains($"({rawNoisyLog.Length} chars), use grep/rg/sed to extract additional informations]", result);
        Assert.DoesNotContain("Restored /workspace/Proj1/", result);

        Assert.NotNull(capturedAudit);
        Assert.True(capturedAudit.WasSmartFiltered);
        Assert.False(capturedAudit.WasTruncated);
        Assert.Equal(rawNoisyLog.Length, capturedAudit.RawChars);
        Assert.True(capturedAudit.SavedChars > 0);
        Assert.NotNull(capturedAudit.VirtualTmpPath);

        var hostTmpFile = resolver.ResolveHostPath(capturedAudit.VirtualTmpPath, forWrite: false);
        Assert.True(File.Exists(hostTmpFile));
        Assert.Equal(rawNoisyLog, File.ReadAllText(hostTmpFile));
    }

    [Fact]
    public async Task SandboxBashToolModule_RestrictedReadOnlyScope_PersistsFullRawOutputToTmp_AndHeadTruncatesOversizedOutput()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-ro");
        var tmpDir = Path.Combine(_tempRoot, "tmp-ro");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tmpDir);

        var workspace = new Workspace { Id = 19, Name = "FilterReadOnlyWS", RepositoryPath = repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: repoDir,
            isReadOnly: true,
            hostTmpDirectory: tmpDir);
        var scopeDescriptor = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            SandboxSecurityTier.RestrictedReadOnly,
            taskId: null,
            mountTable: mountTable,
            defaultWorkingDirectory: "/workspace",
            hostWorkspacePath: repoDir);
        var toolScope = new CanonicalToolScope(
            repoDir,
            WorkspaceId: 19,
            IsReadOnly: true,
            SandboxScope: scopeDescriptor);

        var resolver = new SandboxPathResolver(scopeDescriptor);

        var hugeReadOnlyLog = "FIRST_LINE_KEPT\n" + new string('X', WorkflowDefaults.MaxToolInlineChars + 12_000) + "\nTAIL_LINE_OMITTED";

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        execEnv
            .Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, hugeReadOnlyLog, string.Empty, TimeSpan.FromMilliseconds(15)));

        BashExecutionAuditEntry? capturedAudit = null;
        var module = new SandboxBashToolModule(
            toolScope with { OnBashExecutionAudited = entry => capturedAudit = entry },
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var result = await module.BashAsync("cat big.log");

        Assert.True(result.Length <= WorkflowDefaults.MaxToolInlineChars);
        Assert.Contains("FIRST_LINE_KEPT", result);
        Assert.DoesNotContain("TAIL_LINE_OMITTED", result);
        Assert.Contains("... additional ", result);
        Assert.Contains("[Full output : /tmp/bash-", result);

        Assert.NotNull(capturedAudit);
        Assert.False(capturedAudit.WasSmartFiltered);
        Assert.True(capturedAudit.WasTruncated);
        Assert.Equal(hugeReadOnlyLog.Length, capturedAudit.RawChars);
        Assert.NotNull(capturedAudit.VirtualTmpPath);

        var hostTmpFile = resolver.ResolveHostPath(capturedAudit.VirtualTmpPath, forWrite: false);
        Assert.True(File.Exists(hostTmpFile));
        Assert.Equal(hugeReadOnlyLog, File.ReadAllText(hostTmpFile));
    }

    [Fact]
    public async Task SandboxBashToolModule_DisableFilterAndFlagDisabled_ReturnRawOutputWhilePreservingTmpOffload()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-disable");
        var tmpDir = Path.Combine(_tempRoot, "tmp-disable");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tmpDir);

        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: repoDir,
            isReadOnly: false,
            hostTmpDirectory: tmpDir);
        var scopeDescriptor = SandboxScopeDescriptor.ForWorkspace(
            new Workspace { Id = 18, RepositoryPath = repoDir },
            SandboxSecurityTier.Standard,
            null,
            mountTable,
            hostWorkspacePath: repoDir);
        var resolver = new SandboxPathResolver(scopeDescriptor);

        var noisyLog = "  Determining projects to restore...\n  Proj -> /workspace/Proj.dll\nBuild succeeded.\n    0 Warning(s)";

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        execEnv
            .Setup(e => e.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, noisyLog, string.Empty, TimeSpan.FromMilliseconds(10)));

        // 1. disable_filter: true via AIFunction tool invocation
        var moduleEnabled = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, SandboxScope: scopeDescriptor, OptimizationFlags: new LocalOptimizationFlags { SmartCliOutputFiltering = true }),
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var bashFn = Assert.Single(moduleEnabled.CreateFunctions());
        var rawViaTool = (await bashFn.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["command"] = "dotnet build",
            ["disable_filter"] = true
        })))?.ToString();

        Assert.NotNull(rawViaTool);
        Assert.Contains("Determining projects to restore...", rawViaTool);
        Assert.DoesNotContain(CliOutputFilterConstants.SimplifiedOutputHeader, rawViaTool);
        Assert.True(Directory.GetFiles(tmpDir, "bash-*.txt").Length >= 1);

        // 2. SmartCliOutputFiltering = false optimization flag
        var moduleDisabled = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, SandboxScope: scopeDescriptor, OptimizationFlags: new LocalOptimizationFlags { SmartCliOutputFiltering = false }),
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var rawViaFlag = await moduleDisabled.BashAsync("dotnet build");
        Assert.Contains("Determining projects to restore...", rawViaFlag);
        Assert.DoesNotContain(CliOutputFilterConstants.SimplifiedOutputHeader, rawViaFlag);
    }

    [Fact]
    public async Task SandboxBashToolModule_NonZeroShellFailureOnMatchedCommand_DoesNotSynthesizeBuildSucceeded()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-shell-err");
        Directory.CreateDirectory(repoDir);
        var resolver = new WorkspacePathResolverSandboxAdapter(
            new CanonicalToolScope(repoDir),
            TestPathResolver.Create(),
            NullLogger.Instance);

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.BareHost);
        execEnv
            .Setup(e => e.ExecuteAsync("dotnet build", repoDir, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(127, string.Empty, "bash: dotnet: command not found", TimeSpan.FromMilliseconds(5)));

        var module = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir),
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var output = await module.BashAsync("dotnet build");

        Assert.StartsWith("Exit Code: 127", output);
        Assert.Contains("command not found", output);
        Assert.DoesNotContain(CliOutputFilterConstants.BuildSucceededSummary, output);
    }

    [Fact]
    public async Task CrucibleAgentHarness_PersistsCompactOutputInChatHistory_AndRecordsBashAuditInTurnMetadata()
    {
        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 88, Title = "Audit Task", WorkspaceId = 1 };
        await repo.CreateAsync(task);

        var repoDir = Path.Combine(_tempRoot, "harness-repo");
        var tmpDir = Path.Combine(_tempRoot, "harness-tmp");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tmpDir);

        var workspace = new Workspace { Id = 1, Name = "HarnessWS", RepositoryPath = repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(hostWorkingDirectory: repoDir, isReadOnly: false, hostTmpDirectory: tmpDir);
        var scopeDesc = SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.Standard, null, mountTable, hostWorkspacePath: repoDir);

        var noisyBuild = string.Join("\n",
            Enumerable.Range(1, 50).Select(i => $"  Restored /workspace/Lib{i}.csproj (in 3 ms)."))
            + "\nBuild succeeded.\n    0 Warning(s)\n    0 Error(s)";

        var mockExecEnv = new Mock<IExecutionEnvironment>();
        mockExecEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        mockExecEnv
            .Setup(e => e.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, noisyBuild, string.Empty, TimeSpan.FromMilliseconds(10)));

        var envProviderMock = new Mock<IExecutionEnvironmentProvider>();
        envProviderMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockExecEnv.Object);
        envProviderMock
            .Setup(p => p.GetEnvironmentAsync(It.IsAny<SandboxScopeDescriptor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockExecEnv.Object);

        int turn = 0;
        var chatClient = new LocalScriptedChatClient(async (messages, options, ct) =>
        {
            await Task.CompletedTask;
            turn++;
            if (turn == 1)
            {
                return
                [
                    new ChatResponseUpdate
                    {
                        Contents =
                        [
                            new FunctionCallContent("call-bash-1", CanonicalToolNames.Bash, new Dictionary<string, object?> { ["command"] = "dotnet build" })
                        ]
                    }
                ];
            }

            return [new ChatResponseUpdate { Contents = [new TextContent("Build verified.")] }];
        });

        var harness = new TestCrucibleHarnessBuilder
        {
            BaseChatClient = chatClient,
            EnvProvider = envProviderMock.Object,
            PathResolver = TestPathResolver.Create(),
            TaskRepository = repo
        }.Build();

        var sessionContext = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Verify build.",
            WorkspacePath = repoDir,
            Workspace = workspace,
            SandboxScope = scopeDesc,
            OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = true },
            Profile = new AgentProfile
            {
                Id = "p-impl",
                Name = "Impl",
                ModelName = "test-model",
                Mode = AgentMode.Full,
                PermittedTools = [CanonicalToolNames.Bash]
            }
        };

        var result = await harness.RunStageAsync(sessionContext);
        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var conv = Assert.Single(await repo.GetConversationsAsync(task.Id));
        var loaded = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loaded);

        var bashTurn = Assert.Single(loaded.Turns, t => t.Role == StageTurnRole.ToolResult && t.ToolName == CanonicalToolNames.Bash);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, bashTurn.ToolResult);
        Assert.DoesNotContain("Restored /workspace/Lib1.csproj", bashTurn.ToolResult);

        Assert.NotNull(bashTurn.MetadataJson);
        using var doc = JsonDocument.Parse(bashTurn.MetadataJson);
        Assert.True(doc.RootElement.TryGetProperty("BashAudit", out var auditProp));
        Assert.Equal(JsonValueKind.Object, auditProp.ValueKind);
        Assert.True(auditProp.GetProperty("WasSmartFiltered").GetBoolean());
        Assert.Equal(noisyBuild.Length, auditProp.GetProperty("RawChars").GetInt32());
        Assert.True(auditProp.GetProperty("SavedChars").GetInt32() > 0);
        Assert.Contains("/tmp/bash-", auditProp.GetProperty("VirtualTmpPath").GetString());
    }

    [Fact]
    public async Task CrucibleConversationCoordinator_FiltersVerboseDeterministicPriming_WhenSmartFilteringEnabled()
    {
        var noisyTestOutput = "Exit Code: 0\n  Determining projects to restore...\n"
            + string.Join("\n", Enumerable.Range(1, 50).Select(i => $"  Passed Suite.Test_{i} [2 ms]"))
            + "\nPassed!  - Failed:     0, Passed:    50, Skipped:     0, Total:    50";

        var priming = new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            ToolName: CanonicalToolNames.Bash,
            Command: "dotnet test",
            Output: noisyTestOutput,
            IsSuccess: true);

        var repo = new InMemoryTaskRepository();
        var task = new ForgeTask { Id = 99, Title = "Priming Filter Test", WorkspaceId = 1 };
        await repo.CreateAsync(task);
        var coordinator = new CrucibleConversationCoordinator(
            repo,
            new TestCrucibleHarnessBuilder { TaskRepository = repo }.TranscriptService,
            Mock.Of<ILlmGatewayService>(),
            NullLogger<CrucibleConversationCoordinator>.Instance);

        var ctxEnabled = new AgentSessionContext
        {
            ForgeTask = task,
            StageId = StandardStageIds.Implementation,
            BootstrapPrompt = "Review code.",
            WorkspacePath = "/workspace",
            OptimizationFlags = new LocalOptimizationFlags { SmartCliOutputFiltering = true },
            SyntheticPriming = priming,
            Profile = new AgentProfile { Id = "p-rev", Name = "Reviewer", ModelName = "m1" }
        };

        var state = await coordinator.BeginAsync(ctxEnabled, StandardHarnessIds.Crucible, CancellationToken.None);
        var primedToolResult = state.ChatHistory
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .FirstOrDefault()?.Result?.ToString();

        Assert.NotNull(primedToolResult);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, primedToolResult);
        Assert.Contains("Passed!  - Failed:     0", primedToolResult);
        Assert.DoesNotContain("Passed Suite.Test_1", primedToolResult);
    }

    [Fact]
    public async Task SandboxBashToolModule_HugeSmartFilteredOutputExceedingCap_HeadTruncatesWithinMaxToolInlineChars()
    {
        var repoDir = Path.Combine(_tempRoot, "repo-huge-filtered");
        var tmpDir = Path.Combine(_tempRoot, "tmp-huge-filtered");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(tmpDir);

        var workspace = new Workspace { Id = 22, Name = "HugeFilterWS", RepositoryPath = repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(hostWorkingDirectory: repoDir, isReadOnly: false, hostTmpDirectory: tmpDir);
        var scopeDesc = SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.Standard, null, mountTable, hostWorkspacePath: repoDir);
        var resolver = new SandboxPathResolver(scopeDesc);

        // Construct raw output containing >51,200 chars of distinct CS0103 errors so filtered output ALSO exceeds MaxToolInlineChars
        var sb = new StringBuilder();
        for (int i = 0; i < 650; i++)
        {
            sb.AppendLine($"  1>src/Module{i}/File{i}.cs({i + 1},5): error CS0103: The name 'MissingIdentifierNumber_{i}_WithExtraPaddingTextToEnsureSizeExceeds50KB' does not exist [src/Proj.csproj]");
        }
        sb.AppendLine("Build FAILED.");
        var hugeErrorLog = sb.ToString();

        var execEnv = new Mock<IExecutionEnvironment>(MockBehavior.Strict);
        execEnv.SetupGet(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        execEnv
            .Setup(e => e.ExecuteAsync("dotnet build", It.IsAny<string?>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(1, hugeErrorLog, string.Empty, TimeSpan.FromMilliseconds(30)));

        var module = new SandboxBashToolModule(
            new CanonicalToolScope(repoDir, WorkspaceId: 22, SandboxScope: scopeDesc),
            resolver,
            execEnv.Object,
            CliOutputFilterRegistry.Default,
            NullLogger.Instance);

        var result = await module.BashAsync("dotnet build");

        Assert.True(result.Length <= WorkflowDefaults.MaxToolInlineChars);
        Assert.Contains(CliOutputFilterConstants.SimplifiedOutputHeader, result);
        Assert.Contains("... additional ", result);
        Assert.Contains("[Full output : /tmp/bash-", result);
    }

    [Theory]
    [InlineData("dotnet build | grep Warning", false)]
    [InlineData("dotnet test | head -n 5", false)]
    [InlineData("dotnet build | tee build.log", true)]
    [InlineData("dotnet build | cat", true)]
    [InlineData("dotnet test --filter \"Foo|Bar\"", true)]
    [InlineData("dotnet test --list-tests", false)]
    [InlineData("dotnet test -t", false)]
    [InlineData("dotnet build --help", false)]
    [InlineData("dotnet build -h", false)]
    [InlineData("dotnet build -?", false)]
    [InlineData("timeout -k 5 30s sudo -E dotnet build", true)]
    public void DotNetCliOutputFilter_CanHandle_RespectsPipelinesWrappersAndInformationalFlags(string command, bool expected)
    {
        var filter = new DotNetCliOutputFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Theory]
    [InlineData("tsc --version", false)]
    [InlineData("tsc -v", false)]
    [InlineData("npx tsc -v", false)]
    [InlineData("tsc --noEmit | head -n 10", false)]
    [InlineData("npm run build | tee build.log", true)]
    public void NodeTscCliFilter_CanHandle_RespectsPipelinesAndVersionFlags(string command, bool expected)
    {
        var filter = new NodeTscCliFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Theory]
    [InlineData("cargo run", false)]
    [InlineData("cargo check | head -n 5", false)]
    [InlineData("cargo test -- --nocapture", true)]
    [InlineData("cargo clippy --help", false)]
    public void CargoCliFilter_CanHandle_ExcludesRunPipelinesAndInformationalFlags(string command, bool expected)
    {
        var filter = new CargoCliFilter();
        Assert.Equal(expected, filter.CanHandle(command));
    }

    [Fact]
    public void CliOutputPathRelativizer_RelativizesParenthesizedFramesProjectErrorsSingleLineAndClonePaths()
    {
        var stackLine = "    at Object.toBe (/workspace/src/math.test.ts:5:21)";
        Assert.Equal(
            "    at Object.toBe (src/math.test.ts:5:21)",
            CliOutputPathRelativizer.RelativizeLinePaths(stackLine, "/workspace"));

        var nugetLine = "/workspace/src/App/App.csproj : error NU1101: Unable to find package Foo.";
        Assert.Equal(
            "src/App/App.csproj : error NU1101: Unable to find package Foo.",
            CliOutputPathRelativizer.RelativizeLinePaths(nugetLine, "/workspace"));

        var singleLineLoc = "/workspace/src/Foo.cs(14): error CS1002: ; expected";
        Assert.Equal(
            "src/Foo.cs(14): error CS1002: ; expected",
            CliOutputPathRelativizer.RelativizeLinePaths(singleLineLoc, "/workspace"));

        var cloneLoc = "/clone/src/Core/Engine.cs(14,9): warning CS0168: unused";
        Assert.Equal(
            "src/Core/Engine.cs(14,9): warning CS0168: unused",
            CliOutputPathRelativizer.RelativizeLinePaths(cloneLoc, "/workspace"));
    }

    [Fact]
    public void DotNetCliOutputFilter_DeduplicatesXunitAndVsTestBlocks_AndHonorsSuppressWarningsFalse()
    {
        var raw = """
            VSTest version 17.14.0 (x64)
            [xUnit.net 00:00:00.00] xUnit.net VSTest Adapter v3.1.4
            [xUnit.net 00:00:00.10]   Starting:    Demo.Tests
            [xUnit.net 00:00:00.12]     Demo.Tests.CalcTests.Broken [FAIL]
            [xUnit.net 00:00:00.12]       Assert.Equal() Failure: Values differ
            [xUnit.net 00:00:00.12]       Expected: 42
            [xUnit.net 00:00:00.12]       Actual:   41
            [xUnit.net 00:00:00.12]       Stack Trace:
            [xUnit.net 00:00:00.12]         /workspace/tests/CalcTests.cs(18,0): at Demo.Tests.CalcTests.Broken()
            [xUnit.net 00:00:00.15]   Finished:    Demo.Tests
              1>/workspace/src/Lib.cs(9,5): warning CS0168: unused var [/workspace/src/Lib.csproj]
              Failed Demo.Tests.CalcTests.Broken [4 ms]
              Error Message:
               Assert.Equal() Failure: Values differ
               Expected: 42
               Actual:   41
              Stack Trace:
                 at Demo.Tests.CalcTests.Broken() in /workspace/tests/CalcTests.cs:line 18
            Failed!  - Failed:     1, Passed:    10, Skipped:     0, Total:    11
            Test Run Failed.
            """;

        var filteredSuppressed = new DotNetCliOutputFilter().FilterOutput(raw, "/workspace", new CommandFilterOptions(SuppressWarningsOnErrors: true));
        Assert.Contains("Failed Demo.Tests.CalcTests.Broken [4 ms]", filteredSuppressed);
        Assert.Single(filteredSuppressed.Split('\n'), l => l.Contains("CalcTests.Broken", StringComparison.Ordinal) && !l.Contains("at ", StringComparison.Ordinal));
        Assert.Contains("Assert.Equal() Failure", filteredSuppressed);
        Assert.Contains("tests/CalcTests.cs:line 18", filteredSuppressed);
        Assert.Contains("Suppressed 1 compiler warnings", filteredSuppressed);
        Assert.DoesNotContain("warning CS0168", filteredSuppressed);

        var filteredWithWarnings = new DotNetCliOutputFilter().FilterOutput(raw, "/workspace", new CommandFilterOptions(SuppressWarningsOnErrors: false));
        Assert.Contains("src/Lib.cs(9,5): warning CS0168", filteredWithWarnings);
        Assert.DoesNotContain("Suppressed 1 compiler warnings", filteredWithWarnings);
    }

    [Fact]
    public void NodeTscCliFilter_PreservesJestVitestFailureDetails_AndPassesUnrecognizedOutputThrough()
    {
        var jestRaw = """
            > app@1.0.0 test
            > vitest run
            PASS src/ok.test.ts
            FAIL  src/math.test.ts > adds numbers
            AssertionError: expected 3 to be 4
              at Object.toBe (/workspace/src/math.test.ts:5:21)
            """;

        var filtered = new NodeTscCliFilter().FilterOutput(jestRaw, "/workspace", new CommandFilterOptions());
        Assert.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, filtered);
        Assert.Contains("FAIL  src/math.test.ts > adds numbers", filtered);
        Assert.Contains("AssertionError: expected 3 to be 4", filtered);
        Assert.Contains("at Object.toBe (src/math.test.ts:5:21)", filtered);
        Assert.DoesNotContain("/workspace/", filtered);
        Assert.DoesNotContain("PASS src/ok.test.ts", filtered);
        Assert.EndsWith(CliOutputFilterConstants.BuildFailedSummary, filtered);

        Assert.Equal("All custom checks passed", new NodeTscCliFilter().FilterOutput("All custom checks passed", "/workspace", new CommandFilterOptions()));
    }

    [Fact]
    public void CargoCliFilter_PreservesRustTestPanicsAndAssertions_AndPassesUnrecognizedOutputThrough()
    {
        var cargoTestRaw = """
               Compiling demo v0.1.0 (/workspace)
                Finished `test` profile [unoptimized + debuginfo] target(s) in 0.42s
                 Running unittests src/lib.rs (target/debug/deps/demo-123)
            test tests::passes ... ok
            test tests::fails_math ... FAILED

            failures:

            ---- tests::fails_math stdout ----
            thread 'tests::fails_math' panicked at /workspace/src/lib.rs:12:9:
            assertion `left == right` failed
              left: 2
             right: 3
            note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace

            failures:
                tests::fails_math

            test result: FAILED. 1 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out; finished in 0.00s
            """;

        var filtered = new CargoCliFilter().FilterOutput(cargoTestRaw, "/workspace", new CommandFilterOptions());
        Assert.StartsWith(CliOutputFilterConstants.SimplifiedOutputHeader, filtered);
        Assert.Contains("test tests::fails_math ... FAILED", filtered);
        Assert.Contains("thread 'tests::fails_math' panicked at src/lib.rs:12:9:", filtered);
        Assert.Contains("assertion `left == right` failed", filtered);
        Assert.Contains("left: 2", filtered);
        Assert.Contains("right: 3", filtered);
        Assert.Contains("test result: FAILED.", filtered);
        Assert.DoesNotContain("tests::passes", filtered);
        Assert.DoesNotContain("/workspace/", filtered);

        Assert.Equal("custom cargo plugin output", new CargoCliFilter().FilterOutput("custom cargo plugin output", "/workspace", new CommandFilterOptions()));
    }

    private sealed class LocalScriptedChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> _streamHandler;

        public LocalScriptedChatClient(
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<IReadOnlyList<ChatResponseUpdate>>> streamHandler)
        {
            _streamHandler = streamHandler;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var updates = await _streamHandler(messages, options, cancellationToken);
            foreach (var u in updates)
            {
                yield return u;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
