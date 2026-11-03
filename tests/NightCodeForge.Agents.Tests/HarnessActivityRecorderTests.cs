using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.ExternalProcess;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tests;

public class HarnessActivityRecorderTests
{
    private readonly Mock<IAgentActivityStreamer> _streamer = new();
    private readonly Mock<ISessionTranscriptService> _transcripts = new();

    private HarnessActivityRecorder Create()
        => new(_streamer.Object, _transcripts.Object, NullLogger<HarnessActivityRecorder>.Instance);

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
}

