using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class HarnessActivityRecorderTests
{
    private readonly Mock<IAgentActivityStreamer> _streamer = new();
    private readonly Mock<ISessionTranscriptService> _transcripts = new();
    private readonly InMemoryTaskRepository _taskRepository = new();

    private HarnessActivityRecorder Create()
        => new(_streamer.Object, _transcripts.Object, _taskRepository, NullLogger<HarnessActivityRecorder>.Instance);

    private static AgentSessionContext Context(string? repoPath = null) => new()
    {
        ForgeTask = new ForgeTask { Id = 7, Title = "T", Description = "D" },
        Profile = new AgentProfile { Name = "prof", ModelName = "model" },
        StageId = "stage",
        Workspace = repoPath == null ? null : new Workspace { Id = 1, RepositoryPath = repoPath }
    };

    [Fact]
    public void Implements_IHarnessActivityRecorder()
    {
        Assert.IsAssignableFrom<IHarnessActivityRecorder>(Create());
    }

    [Fact]
    public async Task BroadcastAsync_ForwardsEventToStreamer()
    {
        AgentActivityEvent? captured = null;
        _streamer.Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await Create().BroadcastAsync("sess", 7, "stage", AgentActivityKind.MessageDelta, "hi", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("sess", captured!.SessionId);
        Assert.Equal(7, captured.TaskId);
        Assert.Equal(AgentActivityKind.MessageDelta, captured.Kind);
        Assert.Equal("hi", captured.Content);
    }

    [Fact]
    public async Task SaveTranscriptAsync_WritesUserAndAssistantEntriesUnderRepoTranscriptsFolder()
    {
        TranscriptMetadata? meta = null;
        IReadOnlyList<TranscriptEntry>? entries = null;
        string? dir = null;
        _transcripts.Setup(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<TranscriptMetadata, IReadOnlyList<TranscriptEntry>, string?, CancellationToken>((m, e, d, _) => { meta = m; entries = e; dir = d; })
            .ReturnsAsync("path");

        await Create().SaveTranscriptAsync("h", Context("/repo"), "/ws", "prompt", "summary", 10, 1, 2, AgentRunStatus.Completed, CancellationToken.None);

        Assert.Equal(new[] { "User", "Assistant" }, entries!.Select(e => e.Role));
        Assert.Equal(AgentRunStatus.Completed, meta!.Status);
        Assert.Equal(3, meta.Metrics.TotalTokens);
        Assert.Equal(Path.Combine("/repo", "docs", "transcripts"), dir);
    }

    [Fact]
    public async Task SaveTranscriptAsync_SkipsEmptyEntries_AndFallsBackToWorkspacePath()
    {
        IReadOnlyList<TranscriptEntry>? entries = null;
        string? dir = null;
        _transcripts.Setup(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<TranscriptMetadata, IReadOnlyList<TranscriptEntry>, string?, CancellationToken>((_, e, d, _) => { entries = e; dir = d; })
            .ReturnsAsync("path");

        await Create().SaveTranscriptAsync("h", Context(), "/ws", "", "summary", 1, 0, 0, AgentRunStatus.Failed, CancellationToken.None);

        Assert.Single(entries!);
        Assert.Equal("Assistant", entries![0].Role);
        Assert.Equal(Path.Combine("/ws", "docs", "transcripts"), dir);
    }

    [Fact]
    public async Task SaveTranscriptAsync_WhenPersistenceFails_DoesNotThrow()
    {
        _transcripts.Setup(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        await Create().SaveTranscriptAsync("h", Context(), "/ws", "p", "s", 1, 0, 0, AgentRunStatus.Completed, CancellationToken.None);

        _transcripts.Verify(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConversationLifecycle_PersistsTurnsAndUsageWithNullCost_AndCompletesConversation()
    {
        _transcripts.Setup(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/repo/docs/transcripts/2026-11-16-task-7-stage-completed.md");

        var task = await _taskRepository.CreateAsync(new ForgeTask { Id = 7, Title = "T", Description = "D" });
        var ctx = new AgentSessionContext
        {
            ForgeTask = task,
            Profile = new AgentProfile { Id = "impl", Name = "prof", ModelName = "gemini-4-argon" },
            StageId = "stage",
            Workspace = new Workspace { Id = 1, RepositoryPath = "/repo" }
        };

        var recorder = Create();
        await recorder.BeginConversationAsync("s-1", StandardHarnessIds.Acp, ctx, CancellationToken.None);
        await recorder.RecordUserTurnAsync("s-1", StandardHarnessIds.Acp, ctx, "bootstrap prompt", CancellationToken.None);
        await recorder.RecordToolCallTurnAsync("s-1", "read_file", "{\"path\":\"a.cs\"}", "tc-1", CancellationToken.None);
        await recorder.RecordToolResultTurnAsync("s-1", "read_file", "class A {}", false, "tc-1", 12, CancellationToken.None);
        await recorder.RecordAssistantTurnAsync("s-1", "All done.", "Checked class A", CancellationToken.None);
        await recorder.SaveTranscriptAsync(StandardHarnessIds.Acp, ctx, "/repo", "bootstrap prompt", "All done.", 50, 200, 75, AgentRunStatus.Completed, CancellationToken.None, "s-1", 40);

        var convs = await _taskRepository.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Completed, conv.Status);
        Assert.Equal("2026-11-16-task-7-stage-completed.md", conv.TranscriptFileName);
        Assert.Equal(200, conv.InputTokens);
        Assert.Equal(75, conv.OutputTokens);
        Assert.Equal(40, conv.CachedInputTokens);
        Assert.Null(conv.Cost);

        var loaded = await _taskRepository.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(loaded);
        var turns = loaded!.Turns.OrderBy(t => t.TurnIndex).ToList();
        Assert.Equal(4, turns.Count);
        Assert.Equal(StageTurnRole.User, turns[0].Role);
        Assert.Equal(StageTurnRole.Tool, turns[1].Role);
        Assert.Equal(StageTurnRole.ToolResult, turns[2].Role);
        Assert.Equal(StageTurnRole.Assistant, turns[3].Role);
        Assert.Equal("Checked class A", turns[3].ThinkingTrace);
    }

    [Fact]
    public async Task ResumeConversation_WithExistingTurns_DoesNotDuplicateBootstrapUserTurn()
    {
        _transcripts.Setup(t => t.SaveTranscriptAsync(It.IsAny<TranscriptMetadata>(), It.IsAny<IReadOnlyList<TranscriptEntry>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/repo/docs/transcripts/resumed.md");

        var task = await _taskRepository.CreateAsync(new ForgeTask { Id = 8, Title = "T8", Description = "D8" });
        var initialConv = await _taskRepository.StartConversationAsync(task.Id, "stage", WorkflowStageKind.Agentic, 1, "impl", StandardHarnessIds.Acp, "gemini-4-argon");
        await _taskRepository.AddConversationTurnAsync(initialConv.Id, StageTurnRole.User, content: "original bootstrap prompt");
        await _taskRepository.CompleteConversationAsync(initialConv.Id, StageExecutionStatus.Interrupted, summary: "interrupted");

        var interruptedCtx = new AgentSessionContext
        {
            ForgeTask = task,
            Profile = new AgentProfile { Id = "impl", Name = "prof", ModelName = "gemini-4-argon" },
            StageId = "stage",
            Workspace = new Workspace { Id = 1, RepositoryPath = "/repo" }
        };

        var recorder = Create();
        await recorder.BeginConversationAsync("s-resume-1", StandardHarnessIds.Acp, interruptedCtx, CancellationToken.None);
        await recorder.RecordUserTurnAsync("s-resume-1", StandardHarnessIds.Acp, interruptedCtx, "rebuilt full enriched prompt", CancellationToken.None);
        await recorder.RecordAssistantTurnAsync("s-resume-1", "Resumed and finished.", null, CancellationToken.None);
        await recorder.SaveTranscriptAsync(StandardHarnessIds.Acp, interruptedCtx, "/repo", "rebuilt full enriched prompt", "Resumed and finished.", 20, 10, 5, AgentRunStatus.Completed, CancellationToken.None, "s-resume-1", null);

        var loadedInterrupted = await _taskRepository.GetConversationWithTurnsAsync(initialConv.Id);
        Assert.NotNull(loadedInterrupted);
        var interruptedUserTurns = loadedInterrupted!.Turns.Where(t => t.Role == StageTurnRole.User).ToList();
        Assert.Single(interruptedUserTurns);
        Assert.Equal("original bootstrap prompt", interruptedUserTurns[0].Content);

        // Now test WaitingForHuman with PendingHumanAnswer
        var waitingConv = await _taskRepository.StartConversationAsync(task.Id, "stage-wait", WorkflowStageKind.Agentic, 1, "impl", StandardHarnessIds.Acp, "gemini-4-argon");
        await _taskRepository.AddConversationTurnAsync(waitingConv.Id, StageTurnRole.User, content: "initial prompt");
        await _taskRepository.CompleteConversationAsync(waitingConv.Id, StageExecutionStatus.WaitingForHuman, summary: "waiting");

        var waitingCtx = new AgentSessionContext
        {
            ForgeTask = task,
            Profile = new AgentProfile { Id = "impl", Name = "prof", ModelName = "gemini-4-argon" },
            StageId = "stage-wait",
            Workspace = new Workspace { Id = 1, RepositoryPath = "/repo" },
            PendingHumanAnswer = "Yes, proceed"
        };

        await recorder.BeginConversationAsync("s-resume-2", StandardHarnessIds.Acp, waitingCtx, CancellationToken.None);
        await recorder.RecordUserTurnAsync("s-resume-2", StandardHarnessIds.Acp, waitingCtx, "[Human Answer]: Yes, proceed\n\ninitial prompt", CancellationToken.None);
        await recorder.SaveTranscriptAsync(StandardHarnessIds.Acp, waitingCtx, "/repo", "prompt", "Done", 10, 5, 5, AgentRunStatus.Completed, CancellationToken.None, "s-resume-2", null);

        var loadedWaiting = await _taskRepository.GetConversationWithTurnsAsync(waitingConv.Id);
        Assert.NotNull(loadedWaiting);
        var waitingUserTurns = loadedWaiting!.Turns.Where(t => t.Role == StageTurnRole.User).OrderBy(t => t.TurnIndex).ToList();
        Assert.Equal(2, waitingUserTurns.Count);
        Assert.Equal("initial prompt", waitingUserTurns[0].Content);
        Assert.Equal("[Human Answer]: Yes, proceed", waitingUserTurns[1].Content);
    }
}

