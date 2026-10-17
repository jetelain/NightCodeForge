using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.PiRpc;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tests;

public class HarnessSessionResumptionTests
{
    [Fact]
    public async Task PiRpcAgentHarness_WhenResumingWithReconstructedHistory_InjectsPriorHistoryIntoPrompt()
    {
        var harness = new TestPiRpcHarnessBuilder().Build();

        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Build login page" },
            new() { TurnIndex = 1, Role = StageTurnRole.Thinking, Content = "Need username and password fields" },
            new() { TurnIndex = 2, Role = StageTurnRole.System, Content = "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]" }
        };
        var reconstructedHistory = ConversationHistorySerializer.Serialize(turns);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 50, Title = "Login Task", Description = "Implement login" },
            StageId = "stage-impl",
            Profile = new AgentProfile { Name = "PiDev", ModelName = "qwen2.5-coder:7b" },
            SerializedStateJson = reconstructedHistory
        };

        var piOutput = new StringBuilder();
        piOutput.AppendLine("{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Resuming login page work.\"}}");
        piOutput.AppendLine("{\"type\":\"agent_settled\"}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(piOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-pi-resume", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        Assert.Contains("[Resumed Conversation History]:", sentCommands);
        Assert.Contains("[User]: Build login page", sentCommands);
        Assert.Contains("[Thinking]: Need username and password fields", sentCommands);
        Assert.Contains("[User]: [System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]", sentCommands);
    }

    [Fact]
    public async Task AcpAgentHarness_WhenResumingWithReconstructedHistory_StartsNewSessionAndInjectsHistoryIntoPrompt()
    {
        var harness = new TestAcpHarnessBuilder().Build();

        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "Fix null reference in Service.cs" },
            new() { TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "read_file", ToolArguments = "{\"path\":\"Service.cs\"}" },
            new() { TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolResult = "class Service { void Run() {} }" },
            new() { TurnIndex = 3, Role = StageTurnRole.System, Content = "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]" }
        };
        var reconstructedHistory = ConversationHistorySerializer.Serialize(turns);

        var context = new AgentSessionContext
        {
            ForgeTask = new ForgeTask { Id = 51, Title = "Bugfix Task", Description = "Fix bug" },
            StageId = "stage-fix",
            Profile = new AgentProfile { Name = "AcpDev" },
            Workspace = new Workspace { Id = 1, RepositoryPath = "C:\\dummy" },
            SerializedStateJson = reconstructedHistory
        };

        var acpOutput = new StringBuilder();
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"init-1\",\"result\":{\"protocolVersion\":2}}");
        acpOutput.AppendLine("{\"jsonrpc\":\"2.0\",\"id\":\"prompt-1\",\"result\":{\"stopReason\":\"end_turn\"}}");

        using var inStream = new MemoryStream(Encoding.UTF8.GetBytes(acpOutput.ToString()));
        using var reader = new StreamReader(inStream);
        using var outStream = new MemoryStream();
        using var writer = new StreamWriter(outStream, Encoding.UTF8) { AutoFlush = true };

        var sw = Stopwatch.StartNew();
        var result = await harness.RunSessionStreamAsync(writer, reader, context, "sess-acp-resume", sw);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var sentCommands = Encoding.UTF8.GetString(outStream.ToArray());
        // For reconstructed history, it should start a new session instead of crashing on resume method
        Assert.Contains("\"method\":\"session/new\"", sentCommands);
        Assert.Contains("[Resumed Conversation History]:", sentCommands);
        Assert.Contains("[User]: Fix null reference in Service.cs", sentCommands);
        Assert.Contains("[Tool Call]: read_file", sentCommands);
        Assert.Contains("[User]: [System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]", sentCommands);
    }

    [Fact]
    public void CrucibleAgentHarness_DeserializeSession_ReplaysRecoveryNoticeAsUserMessage()
    {
        var turns = new List<ConversationTurn>
        {
            new() { TurnIndex = 0, Role = StageTurnRole.User, Content = "User question" },
            new() { TurnIndex = 1, Role = StageTurnRole.System, Content = "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]" }
        };
        var json = ConversationHistorySerializer.Serialize(turns);

        var chatHistory = CrucibleAgentHarness.DeserializeSession(json);

        // No mid-history system message: chat templates (Qwen, Gemma) reject them
        Assert.Equal(2, chatHistory.Count);
        Assert.All(chatHistory, m => Assert.Equal(ChatRole.User, m.Role));
        Assert.Equal("User question", chatHistory[0].Text);
        Assert.Contains("[System Notice: Host restarted unexpectedly", chatHistory[1].Text);
    }
}
