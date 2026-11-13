using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

public class AcpHostRpcHandlerTests
{
    private static AcpHostRpcHandler Create(IExecutionEnvironmentProvider? env = null)
        => new(
            env ?? new Mock<IExecutionEnvironmentProvider>().Object,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            new Mock<IWorkspacePathResolver>().Object,
            NullLogger<AcpHostRpcHandler>.Instance);

    private static AcpHostRpcContext Ctx(string sessionId, Workspace? workspace, string root)
        => new(sessionId, new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 1, Title = "T", Description = "D" },
            Profile = new AgentProfile { Name = "p" },
            StageId = "s",
            Workspace = workspace
        }, root, root);

    [Fact]
    public async Task FsReadTextFile_AppliesOneBasedOffsetAndLimit()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "l1\nl2\nl3\nl4");
            var res = await Create().HandleAsync(AcpHostMethods.FsReadTextFile,
                JsonNode.Parse("{\"path\":\"a.txt\",\"offset\":2,\"limit\":2}"), Ctx("s", null, dir), CancellationToken.None);

            Assert.False(res.IsError);
            Assert.Equal(string.Join(Environment.NewLine, "l2", "l3"), res.Result!.GetType().GetProperty("content")!.GetValue(res.Result));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FsReadTextFile_MissingFile_ReturnsError()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var res = await Create().HandleAsync(AcpHostMethods.FsReadTextFile,
                JsonNode.Parse("{\"path\":\"nope.txt\"}"), Ctx("s", null, dir), CancellationToken.None);
            Assert.True(res.IsError);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TerminalCreate_WithoutWorkspaceOrEnvironment_FailsFast()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().HandleAsync(AcpHostMethods.TerminalCreate,
            JsonNode.Parse("{\"command\":\"echo\"}"), Ctx("s", null, "."), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().HandleAsync(AcpHostMethods.TerminalCreate,
            JsonNode.Parse("{\"command\":\"echo\"}"), Ctx("s", new Workspace { Id = 1 }, "."), CancellationToken.None));
    }

    [Fact]
    public async Task TerminalState_IsScopedPerSession_AndCaseInsensitiveOnTerminalId()
    {
        var env = new Mock<IExecutionEnvironment>();
        env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(3, "out", "", TimeSpan.Zero));
        var provider = new SingleExecutionEnvironmentProvider(env.Object);
        var handler = Create(provider);
        var ws = new Workspace { Id = 1 };

        var created = await handler.HandleAsync(AcpHostMethods.TerminalCreate, JsonNode.Parse("{\"command\":\"x\"}"), Ctx("A", ws, "."), CancellationToken.None);
        var terminalId = (string)created.Result!.GetType().GetProperty("terminalId")!.GetValue(created.Result)!;
        var paramsNode = JsonNode.Parse($"{{\"terminalId\":\"{terminalId.ToUpperInvariant()}\"}}");

        var sameSession = await handler.HandleAsync(AcpHostMethods.TerminalOutput, paramsNode, Ctx("A", ws, "."), CancellationToken.None);
        var otherSession = await handler.HandleAsync(AcpHostMethods.TerminalOutput, paramsNode, Ctx("B", ws, "."), CancellationToken.None);

        Assert.Equal("out", sameSession.Result!.GetType().GetProperty("output")!.GetValue(sameSession.Result));
        Assert.Equal("", otherSession.Result!.GetType().GetProperty("output")!.GetValue(otherSession.Result));
    }

    [Fact]
    public async Task HandleAsync_WithSandboxPathResolver_ResolvesNightwatchScopeWithoutWorkspaceEntityAndEnforcesGuards()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var logsDir = Path.Combine(dir, "logs");
            Directory.CreateDirectory(logsDir);
            File.WriteAllText(Path.Combine(logsDir, "app.log"), "hello nightwatch");

            string? capturedCmd = null;
            string? capturedCwd = null;
            var env = new Mock<IExecutionEnvironment>();
            env.SetupGet(e => e.Tier).Returns(ExecutionTier.WslDirect);
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((c, cwd, _, _, _) =>
                {
                    capturedCmd = c;
                    capturedCwd = cwd;
                })
                .ReturnsAsync(new CommandResult(0, "ok", "", TimeSpan.Zero));

            var nwScope = SandboxScopeDescriptor.ForNightwatch(
                hostNcfLogsPath: logsDir,
                wslNcfLogsPath: "/mnt/c/ncf/logs",
                hostSkillsPath: null,
                wslSkillsPath: null,
                executionMode: ExecutionMode.WslDirect);

            var handler = Create(new SingleExecutionEnvironmentProvider(env.Object));
            var ctx = new AcpHostRpcContext(
                "nw-session",
                new AgentSessionContext
                {
                    ForgeTask = new ForgeTask { Id = 0, Title = "NW", Description = "NW" },
                    Profile = new AgentProfile { Name = "Nightwatch", SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
                    StageId = "triage",
                    Workspace = null
                },
                "/nightcodeforge",
                dir,
                new SandboxPathResolver(nwScope));

            // 1. Read virtual path via SandboxPathResolver
            var readRes = await handler.HandleAsync(
                AcpHostMethods.FsReadTextFile,
                JsonNode.Parse("{\"path\":\"/nightcodeforge/ncf-logs/app.log\"}"),
                ctx,
                CancellationToken.None);
            Assert.False(readRes.IsError);
            Assert.Equal("hello nightwatch", readRes.Result!.GetType().GetProperty("content")!.GetValue(readRes.Result));

            // 2. Write on read-only Nightwatch scope returns error
            var writeRes = await handler.HandleAsync(
                AcpHostMethods.FsWriteTextFile,
                JsonNode.Parse("{\"path\":\"/nightcodeforge/ncf-logs/app.log\",\"content\":\"tamper\"}"),
                ctx,
                CancellationToken.None);
            Assert.True(writeRes.IsError);

            // 3. Terminal run translates virtual path in WslDirect mode and resolves fallback cwd without Workspace entity
            var termRes = await handler.HandleAsync(
                AcpHostMethods.TerminalRun,
                JsonNode.Parse("{\"command\":\"cat /nightcodeforge/ncf-logs/app.log\"}"),
                ctx,
                CancellationToken.None);
            Assert.False(termRes.IsError);
            Assert.Equal("cat /mnt/c/ncf/logs/app.log", capturedCmd);
            Assert.Equal(SandboxMountTable.DefaultNightwatchFallbackWorkingDirectory, capturedCwd);

            // 4. Destructive shell commands are rejected in read-only Nightwatch scope before reaching IExecutionEnvironment
            capturedCmd = null;
            var rmRes = await handler.HandleAsync(
                AcpHostMethods.TerminalRun,
                JsonNode.Parse("{\"command\":\"rm -rf /nightcodeforge/ncf-logs/app.log\"}"),
                ctx,
                CancellationToken.None);
            Assert.True(rmRes.IsError);
            Assert.Contains("[READ-ONLY VIOLATION]", rmRes.ErrorMessage);
            Assert.Null(capturedCmd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AcpAgentHarness_ForwardsSessionSandboxScopeToHostRpcHandler_AndEnforcesReadOnlyProfileWithoutResolver()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // 1. Read-only AgentProfile without SandboxPathResolver still blocks terminal mutations and file writes
            var envMock = new Mock<IExecutionEnvironment>();
            var handler = Create(new SingleExecutionEnvironmentProvider(envMock.Object));
            var readOnlyCtx = new AcpHostRpcContext(
                "ro-profile-session",
                new AgentSessionContext
                {
                    ForgeTask = new ForgeTask { Id = 2, Title = "RO", Description = "RO" },
                    Profile = new AgentProfile { Name = "Reviewer", Mode = AgentMode.ReadOnly },
                    StageId = "review",
                    Workspace = new Workspace { Id = 1, RepositoryPath = dir }
                },
                dir,
                dir);

            var roWrite = await handler.HandleAsync(
                AcpHostMethods.FsWriteTextFile,
                JsonNode.Parse("{\"path\":\"file.txt\",\"content\":\"mutated\"}"),
                readOnlyCtx,
                CancellationToken.None);
            Assert.True(roWrite.IsError);

            var roTerm = await handler.HandleAsync(
                AcpHostMethods.TerminalRun,
                JsonNode.Parse("{\"command\":\"echo pwned > file.txt\"}"),
                readOnlyCtx,
                CancellationToken.None);
            Assert.True(roTerm.IsError);
            Assert.Contains("[READ-ONLY VIOLATION]", roTerm.ErrorMessage);
            envMock.Verify(
                e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // 2. AcpAgentHarness forwards AgentSessionContext.SandboxScope as SandboxPathResolver into AcpHostRpcContext
            AcpHostRpcContext? capturedHostContext = null;
            var rpcHandlerMock = new Mock<IAcpHostRpcHandler>();
            rpcHandlerMock.Setup(h => h.CanHandle(AcpHostMethods.FsReadTextFile)).Returns(true);
            rpcHandlerMock.Setup(h => h.EndsAssistantTurn(AcpHostMethods.FsReadTextFile)).Returns(true);
            rpcHandlerMock
                .Setup(h => h.HandleAsync(AcpHostMethods.FsReadTextFile, It.IsAny<JsonNode?>(), It.IsAny<AcpHostRpcContext>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonNode?, AcpHostRpcContext, CancellationToken>((_, _, c, _) => capturedHostContext = c)
                .ReturnsAsync(AcpHostRpcResult.Success(new { content = "log-line" }));

            var nwTable = SandboxMountTable.ForNightwatch(
                hostNcfLogsPath: dir,
                wslNcfLogsPath: "/mnt/c/ncf/logs",
                hostSkillsPath: null,
                wslSkillsPath: null,
                nonContainerFallbackWorkingDirectory: Path.Combine(dir, "nw-root"));
            var nwScope = SandboxScopeDescriptor.ForNightwatch(nwTable);

            var harness = new Fixtures.TestAcpHarnessBuilder
            {
                HostRpcHandler = rpcHandlerMock.Object,
                ActivityRecorder = new Mock<NightCodeForge.Agents.ExternalProcess.IHarnessActivityRecorder>().Object
            }.Build();

            var agentStdoutLines = string.Join("\n",
                "{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"call-1\",\"method\":\"fs/read_text_file\",\"params\":{\"path\":\"/nightcodeforge/ncf-logs/app.log\"}}",
                "{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}") + "\n";

            using var stdinStream = new MemoryStream();
            using var stdinWriter = new StreamWriter(stdinStream) { AutoFlush = true };
            using var stdoutReader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(agentStdoutLines)));

            var sessionContext = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 0, Title = "NW", Description = "Inspect logs" },
                Profile = new AgentProfile { Name = "Nightwatch", SecurityTier = SandboxSecurityTier.RestrictedReadOnly },
                StageId = "triage",
                Workspace = null,
                SandboxScope = nwScope
            };

            var runResult = await harness.RunSessionStreamAsync(
                stdinWriter,
                stdoutReader,
                sessionContext,
                "acp-nw-session",
                System.Diagnostics.Stopwatch.StartNew(),
                CancellationToken.None);

            Assert.Equal(AgentRunStatus.Completed, runResult.Status);
            Assert.NotNull(capturedHostContext);
            Assert.NotNull(capturedHostContext!.SandboxPathResolver);
            Assert.Same(nwScope, capturedHostContext.SandboxPathResolver!.Scope);
            Assert.Equal("/nightcodeforge", capturedHostContext.WorkspacePath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
