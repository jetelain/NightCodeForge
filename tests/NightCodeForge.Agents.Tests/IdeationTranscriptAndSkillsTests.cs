using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Ideation;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Covers the single delivery of the system prompt, the skills catalog and the transcript fidelity
/// (coalesced deltas, active skills, tool results) of <see cref="IdeationService"/>.
/// </summary>
public class IdeationTranscriptAndSkillsTests
{
    private sealed class ScriptedStreamingClient : IChatClient
    {
        private readonly IReadOnlyList<ChatResponseUpdate> _updates;

        public List<ChatMessage> LastRawMessages { get; private set; } = new();
        public ChatOptions? LastOptions { get; private set; }

        public ScriptedStreamingClient(params ChatResponseUpdate[] updates) => _updates = updates;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRawMessages = chatMessages.ToList();
            LastOptions = options;
            foreach (var update in _updates)
            {
                yield return update;
            }
            await Task.Yield();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponseUpdate Reasoning(string text) =>
        new(ChatRole.Assistant, new List<AIContent> { new TextReasoningContent(text) });

    private static ChatResponseUpdate Text(string text) =>
        new(ChatRole.Assistant, new List<AIContent> { new TextContent(text) });

    private static async Task<IdeationResult> RunAsync(IdeationService service, Workspace workspace, string prompt)
    {
        if (!workspace.AgentProfiles.Any(p => p.Id == "ideation"))
        {
            workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(workspace.Id));
        }
        IdeationResult? result = null;
        await foreach (var update in service.ConverseStreamingAsync(workspace, prompt))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed)
            {
                result = update.FinalResult;
            }
        }
        Assert.NotNull(result);
        return result;
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task ConverseStreamingAsync_SendsSystemPromptOnce_ThroughInstructionsOnly()
    {
        var client = new ScriptedStreamingClient(Text("Ready."));
        var service = new IdeationService(
            new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver(),
            defaultChatClient: client);
        var workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() };

        await RunAsync(service, workspace, "Hello");

        Assert.DoesNotContain(client.LastRawMessages, m => m.Role == ChatRole.System);
        Assert.NotNull(client.LastOptions);
        Assert.Contains("<environment>", client.LastOptions.Instructions);
        Assert.DoesNotContain("<tools>", client.LastOptions.Instructions);
    }

    [Fact]
    public async Task ConverseStreamingAsync_InjectsSkillsCatalog_AndRecordsActiveSkills()
    {
        var repo = NewTempDir("ncf-ideation-skills-");
        var transcripts = NewTempDir("ncf-ideation-transcripts-");
        try
        {
            var skillDir = Path.Combine(repo, ".agents", "skills", "unit-testing");
            Directory.CreateDirectory(skillDir);
            await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), @"---
name: unit-testing
description: Guidelines for unit testing with xUnit
---
# Unit Testing Guidelines
");

            var client = new ScriptedStreamingClient(Text("Ready."));
            var service = new IdeationService(
                new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver(),
                defaultChatClient: client,
                transcriptService: new SessionTranscriptService(transcripts));
            var workspace = new Workspace { Id = 2, RepositoryPath = repo };

            var result = await RunAsync(service, workspace, "Hello");

            Assert.NotNull(client.LastOptions);
            Assert.Contains("<skills>", client.LastOptions.Instructions);
            Assert.Contains("unit-testing", client.LastOptions.Instructions);
            Assert.NotNull(result.FullTranscriptMarkdown);
            Assert.Contains("**Active skills**", result.FullTranscriptMarkdown);
            Assert.Contains("unit-testing", result.FullTranscriptMarkdown);
        }
        finally
        {
            TryDelete(repo);
            TryDelete(transcripts);
        }
    }

    [Fact]
    public async Task ConverseStreamingAsync_CoalescesStreamedDeltas_IntoSingleTranscriptEntries()
    {
        var transcripts = NewTempDir("ncf-ideation-coalesce-");
        try
        {
            var client = new ScriptedStreamingClient(
                Reasoning("Let me "), Reasoning("think "), Reasoning("about it."),
                Text("The answer "), Text("is "), Text("42."));
            var service = new IdeationService(
                new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver(),
                defaultChatClient: client,
                transcriptService: new SessionTranscriptService(transcripts));
            var workspace = new Workspace { Id = 3, RepositoryPath = transcripts };

            var result = await RunAsync(service, workspace, "Question");

            var markdown = result.FullTranscriptMarkdown;
            Assert.NotNull(markdown);
            Assert.Equal(1, CountOccurrences(markdown, "THINKING"));
            Assert.Equal(1, CountOccurrences(markdown, "ASSISTANT"));
            Assert.Contains("Let me think about it.", markdown);
            Assert.Contains("The answer is 42.", markdown);
        }
        finally
        {
            TryDelete(transcripts);
        }
    }

    [Fact]
    public async Task ConverseStreamingAsync_TreatsPlainTextThinkTagsAsAssistantOutput_StructuredReasoningOnly()
    {
        var client = new ScriptedStreamingClient(Text("<think>not reasoning</think>Answer"));
        var service = new IdeationService(
            new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver(),
            defaultChatClient: client);
        var workspace = new Workspace { Id = 4, RepositoryPath = Directory.GetCurrentDirectory() };
        workspace.AgentProfiles.Add(AgentProfile.CreateDefaultIdeationProfile(4));

        var kinds = new List<IdeationStreamUpdateKind>();
        await foreach (var update in service.ConverseStreamingAsync(workspace, "Hi"))
        {
            kinds.Add(update.Kind);
        }

        Assert.DoesNotContain(IdeationStreamUpdateKind.ThinkingDelta, kinds);
        Assert.Contains(IdeationStreamUpdateKind.MessageDelta, kinds);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public void TranscriptStreamBuffer_MergesConsecutiveDeltas_AndSplitsOnRoleChangeOrFlush()
    {
        var entries = new List<TranscriptEntry>();
        var buffer = new TranscriptStreamBuffer(entries);

        buffer.Append("THINKING", "a");
        buffer.Append("THINKING", "b");
        buffer.Append("ASSISTANT", "c");
        buffer.Append("ASSISTANT", "d");
        buffer.Flush();
        buffer.Append("ASSISTANT", "e");
        buffer.Append("ASSISTANT", string.Empty);
        buffer.Flush();
        buffer.Flush();

        Assert.Equal(3, entries.Count);
        Assert.Equal(("THINKING", "ab"), (entries[0].Role, entries[0].Content));
        Assert.Equal(("ASSISTANT", "cd"), (entries[1].Role, entries[1].Content));
        Assert.Equal(("ASSISTANT", "e"), (entries[2].Role, entries[2].Content));
    }

    [Fact]
    public async Task SessionTranscriptService_RendersToolResultsAndActiveSkills()
    {
        var dir = NewTempDir("ncf-transcript-render-");
        try
        {
            var service = new SessionTranscriptService(dir);
            var entries = new List<TranscriptEntry>
            {
                new(DateTime.UtcNow, "USER", "go"),
                new(DateTime.UtcNow, "TOOL_RESULT", "file contents", ToolName: "read"),
                new(DateTime.UtcNow, "TOOL_ERROR", "boom", ToolName: "bash"),
            };
            var metadata = new TranscriptMetadata(
                TaskId: 1,
                StageId: "ideation",
                AgentProfile: "ideation",
                ModelName: "m",
                DurationMs: 1,
                Metrics: new AgentRunMetrics(1, 0, 0, 0),
                Optimizations: new LocalOptimizationFlags(),
                Status: AgentRunStatus.Completed,
                ActiveSkills: new[] { "skill-a", "skill-b" });

            var path = await service.SaveTranscriptAsync(metadata, entries);
            var markdown = await File.ReadAllTextAsync(path);

            Assert.Contains("**Active skills**", markdown);
            Assert.Contains("skill-a", markdown);
            Assert.Contains("skill-b", markdown);
            Assert.Contains("**Tool:** `read`", markdown);
            Assert.Contains("file contents", markdown);
            Assert.Contains("**Tool:** `bash`", markdown);
            Assert.Contains("boom", markdown);
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
