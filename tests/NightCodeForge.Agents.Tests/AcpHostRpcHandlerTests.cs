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
}
