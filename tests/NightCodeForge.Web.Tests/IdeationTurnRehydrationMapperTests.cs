using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class IdeationTurnRehydrationMapperTests
{
    [Fact]
    public void ProjectConversation_ProjectsStructuredTurnsChronologically_WithoutDuplicatingThinkingTrace()
    {
        var attachment = new TaskAttachment
        {
            Id = 7,
            WorkspaceId = 1,
            Title = "arch-spec.md",
            SourceRef = "arch-spec.md",
            Sha256 = new string('a', 64),
            ContentKind = TaskAttachmentContentKind.PlainText
        };

        var proposal = new IdeationProposal("Extract Pipeline Service", "Move pipeline logic out", 2);
        var checkpointMeta = JsonSerializer.Serialize(new ConversationHistorySerializer.CompactionCheckpointMetadata(
            IsCompactionCheckpoint: true,
            CutTurnIndex: 0,
            PreCompactionTokens: 48_000,
            PostCompactionTokens: 8_200,
            DurationMs: 145.5));

        var conv = new Conversation
        {
            Id = 10,
            WorkspaceId = 1,
            Turns =
            [
                new ConversationTurn
                {
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "Review @attachment:7 and plan improvements",
                    MetadataJson = "{\"AttachmentIds\":[7]}",
                    Timestamp = DateTime.UtcNow.AddMinutes(-5)
                },
                new ConversationTurn
                {
                    TurnIndex = 1,
                    Role = StageTurnRole.Thinking,
                    ThinkingTrace = "Inspecting repository layout...",
                    DurationMs = 210
                },
                new ConversationTurn
                {
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Read,
                    ToolArguments = "{\"path\":\"README.md\"}",
                    MetadataJson = "{\"CallId\":\"read_1\"}"
                },
                new ConversationTurn
                {
                    TurnIndex = 3,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Read,
                    ToolResult = "# README",
                    DurationMs = 15,
                    MetadataJson = "{\"CallId\":\"read_1\"}"
                },
                new ConversationTurn
                {
                    TurnIndex = 4,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"prompt\":\"Target net10?\"}",
                    MetadataJson = "{\"CallId\":\"ask_1\"}"
                },
                new ConversationTurn
                {
                    TurnIndex = 5,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "User answered: Yes",
                    MetadataJson = "{\"CallId\":\"ask_1\",\"Answers\":[{\"Question\":\"Target net10?\",\"Answer\":\"Yes\"}]}"
                },
                new ConversationTurn
                {
                    TurnIndex = 6,
                    Role = StageTurnRole.Optimization,
                    Content = "[CONTEXT COMPACTION CHECKPOINT]\nCompacted earlier tool output.",
                    MetadataJson = checkpointMeta,
                    DurationMs = 146
                },
                new ConversationTurn
                {
                    TurnIndex = 7,
                    Role = StageTurnRole.Assistant,
                    Content = "Here is the plan.",
                    ThinkingTrace = "Inspecting repository layout...",
                    MetadataJson = JsonSerializer.Serialize(new[] { proposal })
                }
            ]
        };

        var projected = IdeationTurnRehydrationMapper.ProjectConversation(
            conv,
            [attachment],
            Array.Empty<ForgeTask>(),
            maxTurnIndexInclusive: null,
            NullLogger.Instance);

        Assert.Equal(2, projected.Messages.Count);

        var userMsg = projected.Messages[0];
        Assert.Equal("user", userMsg.Role);
        Assert.Contains("arch-spec.md", userMsg.AttachmentChips!.Select(c => c.Title));

        var assistantMsg = projected.Messages[1];
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.Equal("Here is the plan.", assistantMsg.Content);
        Assert.Equal("Inspecting repository layout...", assistantMsg.ThinkingTrace);
        Assert.NotNull(assistantMsg.Parts);

        // Exactly 1 ThinkingContentPart (no duplication from Assistant.ThinkingTrace)
        var thinkPart = Assert.Single(assistantMsg.Parts.OfType<ThinkingContentPart>());
        Assert.Equal("Inspecting repository layout...", thinkPart.Thinking);

        var toolPart = Assert.Single(assistantMsg.Parts.OfType<ToolCallContentPart>());
        Assert.Equal("read_1", toolPart.ToolCall.ToolCallId);
        Assert.True(toolPart.ToolCall.IsCompleted);
        Assert.Equal("# README", toolPart.ToolCall.Result);
        Assert.Equal(15, toolPart.ToolCall.DurationMs);

        var qPart = Assert.Single(assistantMsg.Parts.OfType<QuestionContentPart>());
        Assert.Equal("Yes", qPart.Answer);
        Assert.Contains("Target net10?", qPart.MetadataJson);

        var optPart = Assert.Single(assistantMsg.Parts.OfType<OptimizationContentPart>());
        Assert.Equal(48_000, optPart.PreCompactionTokens);
        Assert.Equal(8_200, optPart.PostCompactionTokens);
        Assert.Equal(145.5, optPart.DurationMs);

        var textPart = Assert.Single(assistantMsg.Parts.OfType<TextContentPart>());
        Assert.Equal("Here is the plan.", textPart.Text);

        var prop = Assert.Single(projected.Proposals);
        Assert.Equal("Extract Pipeline Service", prop.Title);
    }

    [Fact]
    public void ProjectConversation_HandlesLegacyToolResultOnly_LegacyThinkingTrace_AndMaxTurnCutoff()
    {
        var conv = new Conversation
        {
            Id = 11,
            WorkspaceId = 1,
            Turns =
            [
                new ConversationTurn { TurnIndex = 0, Role = StageTurnRole.User, Content = "First prompt" },
                new ConversationTurn
                {
                    TurnIndex = 1,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Bash,
                    ToolArguments = "{\"command\":\"ls\"}",
                    ToolResult = "Program.cs",
                    DurationMs = 9
                },
                new ConversationTurn
                {
                    TurnIndex = 2,
                    Role = StageTurnRole.Assistant,
                    Content = "Found Program.cs",
                    ThinkingTrace = "Legacy embedded trace"
                },
                new ConversationTurn { TurnIndex = 3, Role = StageTurnRole.User, Content = "In-flight prompt" },
                new ConversationTurn { TurnIndex = 4, Role = StageTurnRole.Thinking, ThinkingTrace = "Unfinished thought" }
            ]
        };

        var projected = IdeationTurnRehydrationMapper.ProjectConversation(
            conv,
            Array.Empty<TaskAttachment>(),
            Array.Empty<ForgeTask>(),
            maxTurnIndexInclusive: 3,
            NullLogger.Instance);

        Assert.Equal(3, projected.Messages.Count);
        Assert.Equal("First prompt", projected.Messages[0].Content);

        var firstAssistant = projected.Messages[1];
        var think = Assert.Single(firstAssistant.Parts!.OfType<ThinkingContentPart>());
        Assert.Equal("Legacy embedded trace", think.Thinking);
        var tool = Assert.Single(firstAssistant.Parts!.OfType<ToolCallContentPart>());
        Assert.Equal(CanonicalToolNames.Bash, tool.ToolCall.ToolName);
        Assert.True(tool.ToolCall.IsCompleted);
        Assert.Equal("Program.cs", tool.ToolCall.Result);

        Assert.Equal("In-flight prompt", projected.Messages[2].Content);
        Assert.DoesNotContain(projected.Messages, m => m.Content.Contains("Unfinished thought") || (m.ThinkingTrace?.Contains("Unfinished thought") ?? false));
    }

    [Fact]
    public void ProjectInFlightSnapshot_MapsAllLivePartKindsAndPendingQuestions()
    {
        var q = new IdeationQuestion("Which DB?", "Need storage", ["Postgres", "SQLite"]);
        var groupId = Guid.NewGuid();
        var runningTool = new IdeationToolCallInfo("c1", CanonicalToolNames.Read, "{\"path\":\"App.cs\"}");

        var snapshot = new IdeationInFlightTurnSnapshot(
            WorkspaceId: 1,
            ConversationId: 22,
            IsGenerating: true,
            ActiveUserTurnIndex: 3,
            CurrentThinkingBuffer: "Checking App.cs...",
            CurrentMessageBuffer: "Drafting reply...",
            CurrentToolCalls: [new IdeationInFlightToolCall(runningTool, IsCompleted: false)],
            CurrentParts:
            [
                new IdeationInFlightPart(IdeationInFlightPartKind.Thinking, Text: "Checking App.cs..."),
                new IdeationInFlightPart(IdeationInFlightPartKind.ToolCall, ToolCall: runningTool, IsToolCompleted: false),
                new IdeationInFlightPart(IdeationInFlightPartKind.Optimization, Text: "Compacted", PreCompactionTokens: 50_000, PostCompactionTokens: 9_000, DurationMs: 95),
                new IdeationInFlightPart(IdeationInFlightPartKind.Question, QuestionGroupId: groupId, Questions: [q]),
                new IdeationInFlightPart(IdeationInFlightPartKind.Text, Text: "Drafting reply...")
            ],
            DiscoveredProposals: Array.Empty<IdeationProposal>(),
            LatestTranscriptPath: null,
            LatestFullTranscriptMarkdown: null,
            Options: new IdeationSessionOptions(OllamaRoutingTarget.Cloud, "override-model", "Be concise"));

        var state = IdeationTurnRehydrationMapper.ProjectInFlightSnapshot(snapshot);

        Assert.Equal("Checking App.cs...", state.ThinkingBuffer);
        Assert.Equal("Drafting reply...", state.MessageBuffer);
        var tc = Assert.Single(state.ToolCalls);
        Assert.False(tc.IsCompleted);
        Assert.Equal(5, state.Parts.Count);
        Assert.IsType<ThinkingContentPart>(state.Parts[0]);
        Assert.IsType<ToolCallContentPart>(state.Parts[1]);
        var opt = Assert.IsType<OptimizationContentPart>(state.Parts[2]);
        Assert.Equal(50_000, opt.PreCompactionTokens);
        var qp = Assert.IsType<QuestionContentPart>(state.Parts[3]);
        Assert.Equal(groupId, qp.GroupId);
        Assert.False(qp.IsAnswered);
        Assert.IsType<TextContentPart>(state.Parts[4]);
        Assert.Same(q, Assert.Single(state.PendingQuestions));
    }

    [Fact]
    public void ProjectConversation_ExtractsCleanAskAnswer_FromStructuredMetadataOverRawLlmToolText()
    {
        var singleMeta = JsonSerializer.Serialize(new
        {
            CallId = "ask_single",
            Answers = new[]
            {
                new { QuestionIndex = 0, SelectedOptionIndex = 0, SelectedOption = "JWT", AnswerText = "JWT" }
            }
        });
        var multiMeta = JsonSerializer.Serialize(new
        {
            CallId = "ask_multi",
            Answers = new[]
            {
                new { QuestionIndex = 0, SelectedOptionIndex = 0, SelectedOption = "Postgres", AnswerText = "Postgres" },
                new { QuestionIndex = 1, SelectedOptionIndex = 0, SelectedOption = "Redis", AnswerText = "Redis" }
            }
        });

        var conv = new Conversation
        {
            Id = 12,
            WorkspaceId = 1,
            Turns =
            [
                new ConversationTurn { TurnIndex = 0, Role = StageTurnRole.User, Content = "Configure stack" },
                new ConversationTurn
                {
                    TurnIndex = 1,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Which auth mode?\",\"options\":[\"JWT\",\"Cookie\"]}]}",
                    MetadataJson = "{\"CallId\":\"ask_single\"}"
                },
                new ConversationTurn
                {
                    TurnIndex = 2,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "1. Question: Which auth mode?\n   Answer: JWT",
                    MetadataJson = singleMeta
                },
                new ConversationTurn
                {
                    TurnIndex = 3,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"DB?\"},{\"prompt\":\"Cache?\"}]}",
                    MetadataJson = "{\"CallId\":\"ask_multi\"}"
                },
                new ConversationTurn
                {
                    TurnIndex = 4,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "1. Question: DB?\n   Answer: Postgres\n\n2. Question: Cache?\n   Answer: Redis",
                    MetadataJson = multiMeta
                },
                new ConversationTurn { TurnIndex = 5, Role = StageTurnRole.Assistant, Content = "Stack configured." }
            ]
        };

        var projected = IdeationTurnRehydrationMapper.ProjectConversation(
            conv,
            Array.Empty<TaskAttachment>(),
            Array.Empty<ForgeTask>(),
            maxTurnIndexInclusive: null,
            NullLogger.Instance);

        var assistant = projected.Messages[1];
        var questionParts = assistant.Parts!.OfType<QuestionContentPart>().ToList();
        Assert.Equal(2, questionParts.Count);
        Assert.Equal("JWT", questionParts[0].Answer);
        Assert.Equal("Q1: Postgres\n\nQ2: Redis", questionParts[1].Answer);
    }

    [Fact]
    public void ProjectConversation_MatchesMultipleCallIdLessAskResults_InFifoOrder()
    {
        var conv = new Conversation
        {
            Id = 13,
            WorkspaceId = 1,
            Turns =
            [
                new ConversationTurn { TurnIndex = 0, Role = StageTurnRole.User, Content = "Two questions" },
                new ConversationTurn
                {
                    TurnIndex = 1,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"First question?\"}]}",
                    MetadataJson = null
                },
                new ConversationTurn
                {
                    TurnIndex = 2,
                    Role = StageTurnRole.Tool,
                    ToolName = CanonicalToolNames.Ask,
                    ToolArguments = "{\"questions\":[{\"prompt\":\"Second question?\"}]}",
                    MetadataJson = null
                },
                new ConversationTurn
                {
                    TurnIndex = 3,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "1. Question: First question?\n   Answer: First answer",
                    MetadataJson = null
                },
                new ConversationTurn
                {
                    TurnIndex = 4,
                    Role = StageTurnRole.ToolResult,
                    ToolName = CanonicalToolNames.Ask,
                    ToolResult = "1. Question: Second question?\n   Answer: Second answer",
                    MetadataJson = null
                },
                new ConversationTurn { TurnIndex = 5, Role = StageTurnRole.Assistant, Content = "All answered." }
            ]
        };

        var projected = IdeationTurnRehydrationMapper.ProjectConversation(
            conv,
            Array.Empty<TaskAttachment>(),
            Array.Empty<ForgeTask>(),
            maxTurnIndexInclusive: null,
            NullLogger.Instance);

        var assistant = projected.Messages[1];
        var questionParts = assistant.Parts!.OfType<QuestionContentPart>().ToList();
        Assert.Equal(2, questionParts.Count);
        Assert.Equal("1. Question: First question?\n   Answer: First answer", questionParts[0].Answer);
        Assert.Equal("1. Question: Second question?\n   Answer: Second answer", questionParts[1].Answer);
    }

    [Fact]
    public void ProjectInFlightSnapshot_HandlesNullOrEmptyToolCallId_WithoutThrowing()
    {
        var emptyCall = new IdeationToolCallInfo(string.Empty, CanonicalToolNames.Read, "{\"path\":\"A.cs\"}", "content A", DurationMs: 5);
        var nullCall = new IdeationToolCallInfo(null!, CanonicalToolNames.Bash, "{\"command\":\"ls\"}", "B.cs", DurationMs: 7);

        var snapshot = new IdeationInFlightTurnSnapshot(
            WorkspaceId: 1,
            ConversationId: 23,
            IsGenerating: true,
            ActiveUserTurnIndex: 0,
            CurrentThinkingBuffer: string.Empty,
            CurrentMessageBuffer: string.Empty,
            CurrentToolCalls:
            [
                new IdeationInFlightToolCall(emptyCall, IsCompleted: true),
                new IdeationInFlightToolCall(nullCall, IsCompleted: false)
            ],
            CurrentParts:
            [
                new IdeationInFlightPart(IdeationInFlightPartKind.ToolCall, ToolCall: emptyCall, IsToolCompleted: true),
                new IdeationInFlightPart(IdeationInFlightPartKind.ToolCall, ToolCall: nullCall, IsToolCompleted: false)
            ],
            DiscoveredProposals: Array.Empty<IdeationProposal>(),
            LatestTranscriptPath: null,
            LatestFullTranscriptMarkdown: null,
            Options: new IdeationSessionOptions());

        var state = IdeationTurnRehydrationMapper.ProjectInFlightSnapshot(snapshot);

        Assert.Equal(2, state.ToolCalls.Count);
        var toolParts = state.Parts.OfType<ToolCallContentPart>().ToList();
        Assert.Equal(2, toolParts.Count);
        Assert.Equal(CanonicalToolNames.Read, toolParts[0].ToolCall.ToolName);
        Assert.True(toolParts[0].ToolCall.IsCompleted);
        Assert.Equal(CanonicalToolNames.Bash, toolParts[1].ToolCall.ToolName);
        Assert.False(toolParts[1].ToolCall.IsCompleted);
    }

    [Fact]
    public void ProjectConversation_DeserializesLegacyProposalMetadataContainingMergeMode_WithoutDroppingFields()
    {
        var legacyMetadataJson = """
        [
          {
            "Title": "Legacy Proposal With MergeMode",
            "Description": "Implement feature per @attachment:7",
            "Priority": 2,
            "DependsOnTitles": ["Base Migration"],
            "TaskId": 42,
            "DependsOnIds": [10, 11],
            "MergeMode": "Automatic",
            "Difficulty": "difficult",
            "AttachmentIds": [7]
          }
        ]
        """;

        var conv = new Conversation
        {
            Id = 14,
            WorkspaceId = 1,
            Turns =
            [
                new ConversationTurn { TurnIndex = 0, Role = StageTurnRole.User, Content = "Propose work" },
                new ConversationTurn
                {
                    TurnIndex = 1,
                    Role = StageTurnRole.Assistant,
                    Content = "Proposed one task.",
                    MetadataJson = legacyMetadataJson
                }
            ]
        };

        var projected = IdeationTurnRehydrationMapper.ProjectConversation(
            conv,
            Array.Empty<TaskAttachment>(),
            Array.Empty<ForgeTask>(),
            maxTurnIndexInclusive: null,
            NullLogger.Instance);

        var prop = Assert.Single(projected.Proposals);
        Assert.Equal("Legacy Proposal With MergeMode", prop.Title);
        Assert.Equal("Implement feature per @attachment:7", prop.Description);
        Assert.Equal(2, prop.Priority);
        Assert.Equal(["Base Migration"], prop.DependsOnTitles);
        Assert.Equal(42, prop.TaskId);
        Assert.Equal([10, 11], prop.DependsOnIds);
        Assert.Equal("difficult", prop.Difficulty);
        Assert.Equal([7], prop.AttachmentIds);
    }
}
