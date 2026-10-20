using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class StageConversationParserTests
{
    #region ExtractCallId Tests

    [Fact]
    public void ExtractCallId_WithValidJson_ReturnsCallId()
    {
        var turn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"call-abc-123\"}"
        };

        var result = StageConversationParser.ExtractCallId(turn);

        Assert.Equal("call-abc-123", result);
    }

    [Fact]
    public void ExtractCallId_WithNullOrEmptyMetadata_ReturnsNull()
    {
        Assert.Null(StageConversationParser.ExtractCallId(null));
        Assert.Null(StageConversationParser.ExtractCallId(new ConversationTurn { MetadataJson = null }));
        Assert.Null(StageConversationParser.ExtractCallId(new ConversationTurn { MetadataJson = "" }));
        Assert.Null(StageConversationParser.ExtractCallId(new ConversationTurn { MetadataJson = "   " }));
    }

    [Fact]
    public void ExtractCallId_WithMalformedJson_ReturnsNull()
    {
        var turn = new ConversationTurn { MetadataJson = "not-json" };
        var result = StageConversationParser.ExtractCallId(turn);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractCallId_WithoutCallIdProperty_ReturnsNull()
    {
        var turn = new ConversationTurn { MetadataJson = "{\"Other\":\"value\"}" };
        var result = StageConversationParser.ExtractCallId(turn);
        Assert.Null(result);
    }

    #endregion

    #region PairToolResults Tests

    [Fact]
    public void PairToolResults_PairsByCallIdAccurately()
    {
        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, Role = StageTurnRole.Tool, ToolName = "grep_search", MetadataJson = "{\"CallId\":\"call-1\"}" },
            new() { Id = 2, Role = StageTurnRole.Tool, ToolName = "grep_search", MetadataJson = "{\"CallId\":\"call-2\"}" },
            new() { Id = 3, Role = StageTurnRole.ToolResult, ToolName = "grep_search", MetadataJson = "{\"CallId\":\"call-2\"}", ToolResult = "Result 2" },
            new() { Id = 4, Role = StageTurnRole.ToolResult, ToolName = "grep_search", MetadataJson = "{\"CallId\":\"call-1\"}", ToolResult = "Result 1" }
        };

        var (merged, hidden) = StageConversationParser.PairToolResults(turns);

        Assert.Equal(2, merged.Count);
        Assert.Equal("Result 1", merged[1].ToolResult);
        Assert.Equal("Result 2", merged[2].ToolResult);
        Assert.Contains(3, hidden);
        Assert.Contains(4, hidden);
    }

    [Fact]
    public void PairToolResults_PairsByFifoWhenNoCallId()
    {
        var turns = new List<ConversationTurn>
        {
            new() { Id = 10, Role = StageTurnRole.Tool, ToolName = "run_command" },
            new() { Id = 11, Role = StageTurnRole.Tool, ToolName = "run_command" },
            new() { Id = 20, Role = StageTurnRole.ToolResult, ToolName = "run_command", ToolResult = "First output" },
            new() { Id = 21, Role = StageTurnRole.ToolResult, ToolName = "run_command", ToolResult = "Second output" }
        };

        var (merged, hidden) = StageConversationParser.PairToolResults(turns);

        Assert.Equal(2, merged.Count);
        Assert.Equal("First output", merged[10].ToolResult);
        Assert.Equal("Second output", merged[11].ToolResult);
        Assert.Contains(20, hidden);
        Assert.Contains(21, hidden);
    }

    [Fact]
    public void PairToolResults_WithEmptyOrNull_ReturnsEmpty()
    {
        var (mergedNull, hiddenNull) = StageConversationParser.PairToolResults(null);
        Assert.Empty(mergedNull);
        Assert.Empty(hiddenNull);

        var (mergedEmpty, hiddenEmpty) = StageConversationParser.PairToolResults(new List<ConversationTurn>());
        Assert.Empty(mergedEmpty);
        Assert.Empty(hiddenEmpty);
    }

    #endregion

    #region FormatStageName Tests

    [Theory]
    [InlineData(StandardStageIds.TaskReview, "ForgeTask Review")]
    [InlineData(StandardStageIds.PreImplementationBaseline, "Pre-Implementation Baseline")]
    [InlineData(StandardStageIds.Implementation, "Implementation")]
    [InlineData(StandardStageIds.PostImplementationGates, "Post-Implementation Gates")]
    [InlineData(StandardStageIds.CodeReviewAndFix, "Code Review & Fix")]
    [InlineData(StandardStageIds.PostReviewVerification, "Post-Review Verification")]
    [InlineData("custom-stage-id", "custom-stage-id")]
    public void FormatStageName_WithoutDefinitions_ReturnsStandardOrId(string stageId, string expected)
    {
        var result = StageConversationParser.FormatStageName(stageId);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatStageName_WithCustomDefinitions_UsesDisplayNameAndOrder()
    {
        var definitions = new List<StageDefinition>
        {
            new() { StageId = "my-stage", DisplayName = "Architecture Planning", SequenceOrder = 1 },
            new() { StageId = "my-stage-no-order", DisplayName = "Post-Processing", SequenceOrder = 0 }
        };

        Assert.Equal("1. Architecture Planning", StageConversationParser.FormatStageName("my-stage", definitions));
        Assert.Equal("Post-Processing", StageConversationParser.FormatStageName("my-stage-no-order", definitions));
        Assert.Equal("unknown-stage", StageConversationParser.FormatStageName("unknown-stage", definitions));
    }

    #endregion

    #region GetStatusClass Tests

    [Theory]
    [InlineData(StageExecutionStatus.Completed, "badge-emerald")]
    [InlineData(StageExecutionStatus.Running, "badge-amber")]
    [InlineData(StageExecutionStatus.WaitingForHuman, "badge-amber")]
    [InlineData(StageExecutionStatus.Failed, "badge-crimson")]
    [InlineData(StageExecutionStatus.Skipped, "badge-zinc")]
    [InlineData(StageExecutionStatus.Pending, "badge-zinc")]
    public void GetStatusClass_ReturnsExpectedBadge(StageExecutionStatus status, string expectedClass)
    {
        Assert.Equal(expectedClass, StageConversationParser.GetStatusClass(status));
    }

    #endregion

    #region ParseOptimizationPayload Tests

    [Fact]
    public void ParseOptimizationPayload_WithTokensSavedMetadata_ParsesCorrectly()
    {
        var turn = new ConversationTurn
        {
            Content = "Pruned older messages",
            MetadataJson = "{\"ActionType\":\"Compaction\",\"TokensSaved\":1240}"
        };

        var (actionType, desc, tokensSaved, details) = StageConversationParser.ParseOptimizationPayload(turn);

        Assert.Equal("Compaction", actionType);
        Assert.Equal("Pruned older messages", desc);
        Assert.Equal(1240, tokensSaved);
        Assert.Equal(turn.MetadataJson, details);
    }

    [Fact]
    public void ParseOptimizationPayload_WithCharactersSaved_ParsesCorrectly()
    {
        var turn = new ConversationTurn
        {
            Content = "Trimmed long diff",
            MetadataJson = "{\"ActionType\":\"ToolOutputTruncation\",\"CharactersSaved\":5000}"
        };

        var (actionType, desc, tokensSaved, details) = StageConversationParser.ParseOptimizationPayload(turn);

        Assert.Equal("ToolOutputTruncation", actionType);
        Assert.Equal("Trimmed long diff", desc);
        Assert.Equal(5000, tokensSaved);
        Assert.Equal(turn.MetadataJson, details);
    }

    [Fact]
    public void ParseOptimizationPayload_WithoutMetadata_UsesDefaults()
    {
        var turn = new ConversationTurn
        {
            Content = "Optimized conversation memory"
        };

        var (actionType, desc, tokensSaved, details) = StageConversationParser.ParseOptimizationPayload(turn);

        Assert.Equal("ContextOptimization", actionType);
        Assert.Equal("Optimized conversation memory", desc);
        Assert.Equal(0, tokensSaved);
        Assert.Null(details);
    }

    #endregion

    #region ExtractQuestions Tests

    [Fact]
    public void ExtractQuestions_WithQuestionsArray_ParsesItemsWithRationaleAndOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Which DB should we target?",
                    "rationale": "Schema requirements",
                    "options": ["PostgreSQL", "SQLite", "SQL Server"]
                },
                {
                    "prompt": "Provide staging URL:"
                }
            ]
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Which DB should we target?", questions[0].Prompt);
        Assert.Equal("Schema requirements", questions[0].Rationale);
        Assert.Equal(3, questions[0].Options?.Count);
        Assert.Equal("PostgreSQL", questions[0].Options?[0]);

        Assert.Equal("Provide staging URL:", questions[1].Prompt);
        Assert.Null(questions[1].Rationale);
        Assert.Null(questions[1].Options);
    }

    [Fact]
    public void ExtractQuestions_WithSinglePromptObject_ParsesSingleQuestion()
    {
        var json = """
        {
            "prompt": "Should we proceed with migration?",
            "options": ["Yes", "No"]
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Should we proceed with migration?", questions[0].Prompt);
        Assert.Equal(2, questions[0].Options?.Count);
    }

    [Fact]
    public void ExtractQuestions_WithStringArray_ParsesStringPrompts()
    {
        var json = "[\"First question?\", \"Second question?\"]";
        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Equal(2, questions.Count);
        Assert.Equal("First question?", questions[0].Prompt);
        Assert.Equal("Second question?", questions[1].Prompt);
    }

    [Fact]
    public void ExtractQuestions_WithInvalidJson_FallsBackToContent()
    {
        var turn = new ConversationTurn
        {
            ToolArguments = "not a valid json",
            Content = "Can you confirm the API key?"
        };

        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Can you confirm the API key?", questions[0].Prompt);
    }

    [Fact]
    public void ExtractQuestions_WithOptionsJsonProperty_ParsesOptionsFromJsonString()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select target database:",
                    "rationale": "Configuration",
                    "optionsJson": "[\"PostgreSQL\", \"SQLite\", \"MySQL\"]"
                }
            ]
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Select target database:", questions[0].Prompt);
        Assert.Equal("Configuration", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(3, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);
        Assert.Equal("SQLite", questions[0].Options![1]);
        Assert.Equal("MySQL", questions[0].Options![2]);
    }

    [Fact]
    public void ExtractQuestions_WithSingleQuestionOptionsJson_ParsesOptions()
    {
        var json = """
        {
            "prompt": "Enable telemetry?",
            "OptionsJson": "[\"Yes\", \"No\"]"
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Enable telemetry?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Yes", questions[0].Options![0]);
        Assert.Equal("No", questions[0].Options![1]);
    }

    #endregion

    #region ResolvePendingBatchQuestions Tests

    [Fact]
    public void ResolvePendingBatchQuestions_FiltersByStageAndBatchId()
    {
        var questions = new List<Question>
        {
            new() { Id = 1, StageId = "task-review", BatchId = "batch-1", Status = QuestionStatus.Answered, Prompt = "Q1" },
            new() { Id = 2, StageId = "task-review", BatchId = "batch-2", Status = QuestionStatus.Pending, Prompt = "Q2" },
            new() { Id = 3, StageId = "task-review", BatchId = "batch-2", Status = QuestionStatus.Pending, Prompt = "Q3" },
            new() { Id = 4, StageId = "impl", BatchId = "batch-3", Status = QuestionStatus.Pending, Prompt = "Q4" }
        };

        var resolved = StageConversationParser.ResolvePendingBatchQuestions(questions, "task-review");

        Assert.Equal(2, resolved.Count);
        Assert.Equal(2, resolved[0].Id);
        Assert.Equal(3, resolved[1].Id);
    }

    [Fact]
    public void ResolvePendingBatchQuestions_FallbackToAllWhenStageHasNoMatches()
    {
        var questions = new List<Question>
        {
            new() { Id = 1, StageId = "other-stage", BatchId = "batch-x", Status = QuestionStatus.Pending, Prompt = "Q1" }
        };

        var resolved = StageConversationParser.ResolvePendingBatchQuestions(questions, "non-existent-stage");

        Assert.Single(resolved);
        Assert.Equal(1, resolved[0].Id);
    }

    [Fact]
    public void ResolvePendingBatchQuestions_EmptyOrNullReturnsEmpty()
    {
        Assert.Empty(StageConversationParser.ResolvePendingBatchQuestions(null, "task-review"));
        Assert.Empty(StageConversationParser.ResolvePendingBatchQuestions(new List<Question>(), "task-review"));
    }

    #endregion

    #region MapAnswersToQuestionIds Tests

    [Fact]
    public void MapAnswersToQuestionIds_MapsIndexToQuestionId()
    {
        var batchQuestions = new List<Question>
        {
            new() { Id = 10, Status = QuestionStatus.Pending, Prompt = "Q1" },
            new() { Id = 20, Status = QuestionStatus.Pending, Prompt = "Q2" }
        };

        var answers = new Dictionary<int, string>
        {
            [0] = "PostgreSQL",
            [1] = "Standard tier",
            [2] = "Ignored extra"
        };

        var mapped = StageConversationParser.MapAnswersToQuestionIds(batchQuestions, answers);

        Assert.Equal(2, mapped.Count);
        Assert.Equal("PostgreSQL", mapped[10]);
        Assert.Equal("Standard tier", mapped[20]);
    }

    [Fact]
    public void MapAnswersToQuestionIds_SkipsWhitespaceAndNonPending()
    {
        var batchQuestions = new List<Question>
        {
            new() { Id = 10, Status = QuestionStatus.Answered, Prompt = "Q1" },
            new() { Id = 20, Status = QuestionStatus.Pending, Prompt = "Q2" }
        };

        var answers = new Dictionary<int, string>
        {
            [0] = "Answer 1", // target question 10 is Answered, so skipped
            [1] = "   ",     // whitespace, skipped
        };

        var mapped = StageConversationParser.MapAnswersToQuestionIds(batchQuestions, answers);

        Assert.Empty(mapped);
    }

    #endregion

    #region IsOptionSelected Tests

    [Fact]
    public void IsOptionSelected_WithStructuredAnswerMatchingIndex_ReturnsTrue()
    {
        var structured = new Dictionary<int, QuestionAnswerEntry>
        {
            [0] = new(QuestionIndex: 0, QuestionId: 10, SelectedOptionIndex: 1, SelectedOption: "SQLite", AnswerText: "SQLite")
        };

        Assert.True(StageConversationParser.IsOptionSelected(structured, null, 0, 1, "SQLite"));
        Assert.False(StageConversationParser.IsOptionSelected(structured, null, 0, 0, "PostgreSQL"));
    }

    [Fact]
    public void IsOptionSelected_WithStructuredAnswerMatchingText_ReturnsTrue()
    {
        var structured = new Dictionary<int, QuestionAnswerEntry>
        {
            [0] = new(QuestionIndex: 0, QuestionId: 10, SelectedOptionIndex: null, SelectedOption: "PostgreSQL", AnswerText: "PostgreSQL")
        };

        Assert.True(StageConversationParser.IsOptionSelected(structured, null, 0, 0, "PostgreSQL"));
        Assert.False(StageConversationParser.IsOptionSelected(structured, null, 0, 1, "SQLite"));
    }

    [Fact]
    public void IsOptionSelected_WithStructuredAnswerMatchingAnswerText_ReturnsTrue()
    {
        var structured = new Dictionary<int, QuestionAnswerEntry>
        {
            [0] = new(QuestionIndex: 0, QuestionId: 10, SelectedOptionIndex: null, SelectedOption: null, AnswerText: "Staging")
        };

        Assert.True(StageConversationParser.IsOptionSelected(structured, null, 0, 0, "Staging"));
        Assert.False(StageConversationParser.IsOptionSelected(structured, null, 0, 1, "Production"));
    }

    [Fact]
    public void IsOptionSelected_WithCleanNormalizedFallbackMatching_ReturnsTrue()
    {
        // When structured answers are missing, exact normalized match on rawAnswer works
        Assert.True(StageConversationParser.IsOptionSelected(null, "PostgreSQL", 0, 0, "PostgreSQL"));
        Assert.True(StageConversationParser.IsOptionSelected(null, " postgresql ", 0, 0, "PostgreSQL"));
        Assert.False(StageConversationParser.IsOptionSelected(null, "SQLite", 0, 0, "PostgreSQL"));
    }

    [Fact]
    public void IsOptionSelected_WithNullOrMissingEntry_ReturnsFalse()
    {
        Assert.False(StageConversationParser.IsOptionSelected(null, null, 0, 0, "PostgreSQL"));

        var structured = new Dictionary<int, QuestionAnswerEntry>
        {
            [1] = new(QuestionIndex: 1, QuestionId: 11, SelectedOptionIndex: 0, SelectedOption: "Prod", AnswerText: "Prod")
        };
        Assert.False(StageConversationParser.IsOptionSelected(structured, null, 0, 0, "PostgreSQL"));
    }

    #endregion

    #region ExtractStructuredAnswers & FormatSingleSubmissionAnswer Tests

    [Fact]
    public void ExtractStructuredAnswers_WithValidMetadataJson_ParsesEntries()
    {
        var meta = """
        {
            "CallId": "call-123",
            "Answers": [
                {
                    "QuestionIndex": 0,
                    "QuestionId": 101,
                    "SelectedOptionIndex": 1,
                    "SelectedOption": "SQLite",
                    "AnswerText": "SQLite"
                },
                {
                    "QuestionIndex": 1,
                    "QuestionId": null,
                    "SelectedOptionIndex": null,
                    "SelectedOption": null,
                    "AnswerText": "Staging environment"
                }
            ]
        }
        """;

        var result = StageConversationParser.ExtractStructuredAnswers(meta);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(0));
        Assert.Equal(101, result[0].QuestionId);
        Assert.Equal(1, result[0].SelectedOptionIndex);
        Assert.Equal("SQLite", result[0].SelectedOption);
        Assert.Equal("SQLite", result[0].AnswerText);

        Assert.True(result.ContainsKey(1));
        Assert.Null(result[1].QuestionId);
        Assert.Null(result[1].SelectedOptionIndex);
        Assert.Null(result[1].SelectedOption);
        Assert.Equal("Staging environment", result[1].AnswerText);
    }

    [Fact]
    public void ExtractStructuredAnswers_WithInvalidOrMissingAnswers_ReturnsEmpty()
    {
        Assert.Empty(StageConversationParser.ExtractStructuredAnswers(null));
        Assert.Empty(StageConversationParser.ExtractStructuredAnswers(""));
        Assert.Empty(StageConversationParser.ExtractStructuredAnswers("not valid json"));
        Assert.Empty(StageConversationParser.ExtractStructuredAnswers("""{"CallId":"123"}"""));
    }

    [Fact]
    public void FormatSingleSubmissionAnswer_WithSingleAnswer_ReturnsDirectAnswer()
    {
        var dict = new Dictionary<int, string> { [0] = "PostgreSQL" };
        var formatted = StageConversationParser.FormatSingleSubmissionAnswer(dict);
        Assert.Equal("PostgreSQL", formatted);
    }

    [Fact]
    public void FormatSingleSubmissionAnswer_WithMultipleAnswers_FormatsWithQuestionPrefixes()
    {
        var dict = new Dictionary<int, string>
        {
            [1] = "Staging",
            [0] = "PostgreSQL"
        };
        var formatted = StageConversationParser.FormatSingleSubmissionAnswer(dict);
        Assert.Equal("Q1: PostgreSQL\n\nQ2: Staging", formatted);
    }

    #endregion
}
