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

    [Fact]
    public void ExtractQuestions_WithQuestionsStringContainingJsonArray_ParsesItems()
    {
        var json = """
        {
            "questions": "[{\"prompt\": \"Select database engine\", \"rationale\": \"Storage design\", \"options\": [\"PostgreSQL\", \"SQLite\"]}]"
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Select database engine", questions[0].Prompt);
        Assert.Equal("Storage design", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);
        Assert.Equal("SQLite", questions[0].Options![1]);
    }

    [Fact]
    public void ExtractQuestions_WithQuestionsStringContainingJsonObject_ParsesItem()
    {
        var json = """
        {
            "questions": "{\"prompt\": \"Confirm deploy to staging?\", \"options\": [\"Yes\", \"No\"]}"
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Confirm deploy to staging?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Yes", questions[0].Options![0]);
        Assert.Equal("No", questions[0].Options![1]);
    }

    [Fact]
    public void ExtractQuestions_WithQuestionsStringNotJson_ParsesAsPrompt()
    {
        var json = """
        {
            "questions": "Should we proceed with the migration?"
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Should we proceed with the migration?", questions[0].Prompt);
    }

    [Fact]
    public void ExtractQuestions_WithNestedStringifiedOptions_ParsesOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select target environment",
                    "options": "[\"Staging\", \"Prod\"]"
                }
            ]
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Select target environment", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Staging", questions[0].Options![0]);
        Assert.Equal("Prod", questions[0].Options![1]);
    }

    [Fact]
    public void ExtractQuestions_WithQuestionObjectProperty_ParsesPrompt()
    {
        var json = """
        {
            "question": {
                "prompt": "Confirm database rollback?",
                "options": ["Proceed", "Abort"]
            }
        }
        """;

        var turn = new ConversationTurn { ToolArguments = json };
        var questions = StageConversationParser.ExtractQuestions(turn);

        Assert.Single(questions);
        Assert.Equal("Confirm database rollback?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Proceed", questions[0].Options![0]);
        Assert.Equal("Abort", questions[0].Options![1]);
    }

    [Theory]
    [InlineData("ask", StageTurnRole.Tool, true)]
    [InlineData("Ask", StageTurnRole.Tool, true)]
    [InlineData("ASK", StageTurnRole.Tool, true)]
    [InlineData("ask", StageTurnRole.ToolResult, false)]
    [InlineData("bash", StageTurnRole.Tool, false)]
    [InlineData(null, StageTurnRole.Tool, false)]
    public void IsAskToolTurn_ReturnsExpected(string? toolName, StageTurnRole role, bool expected)
    {
        var turn = new ConversationTurn { ToolName = toolName, Role = role };
        Assert.Equal(expected, StageConversationParser.IsAskToolTurn(turn));
    }

    [Fact]
    public void IsAskError_WhenTurnIsError_ReturnsTrue()
    {
        var turn = new ConversationTurn { IsError = true, ToolName = "ask" };
        Assert.True(StageConversationParser.IsAskError(turn, null));
    }

    [Fact]
    public void IsAskError_WhenPairedResultIsError_ReturnsTrue()
    {
        var turn = new ConversationTurn { IsError = false, ToolName = "ask" };
        var paired = new ConversationTurn { IsError = true, ToolName = "ask" };
        Assert.True(StageConversationParser.IsAskError(turn, paired));
    }

    [Fact]
    public void IsAskError_WhenPairedResultStartsWithError_ReturnsTrue()
    {
        var turn = new ConversationTurn { IsError = false, ToolName = "ask" };
        var paired = new ConversationTurn { IsError = false, ToolName = "ask", ToolResult = "Error: At least one question must be provided" };
        Assert.True(StageConversationParser.IsAskError(turn, paired));
    }

    [Fact]
    public void IsAskError_WhenTurnResultStartsWithError_ReturnsTrue()
    {
        var turn = new ConversationTurn { IsError = false, ToolName = "ask", ToolResult = "Error: Invalid argument" };
        Assert.True(StageConversationParser.IsAskError(turn, null));
    }

    [Fact]
    public void IsAskError_WhenNoError_ReturnsFalse()
    {
        var turn = new ConversationTurn { IsError = false, ToolName = "ask" };
        var paired = new ConversationTurn { IsError = false, ToolName = "ask", ToolResult = "Clarified" };
        Assert.False(StageConversationParser.IsAskError(turn, paired));
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

    #region ResolveAnswerFromTask & IsStageWaitingForAnswer Tests

    [Fact]
    public void ResolveAnswerFromTask_WhenDifferentStageHasAnsweredQuestions_ReturnsNull()
    {
        var task = new ForgeTask
        {
            Id = 1,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 10,
                    StageId = "task-review",
                    Prompt = "Choose database:",
                    Answer = "PostgreSQL",
                    Status = QuestionStatus.Answered
                }
            }
        };

        // When resolving for a different stage (e.g. implementation), it must NOT pick up task-review's answer
        var (answer, meta) = StageConversationParser.ResolveAnswerFromTask(task, "implementation");
        Assert.Null(answer);
        Assert.Null(meta);
    }

    [Fact]
    public void ResolveAnswerFromTask_WhenStageHasPendingQuestions_ReturnsNull()
    {
        var task = new ForgeTask
        {
            Id = 2,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 20,
                    StageId = "task-review",
                    Prompt = "Choose database:",
                    Answer = "PostgreSQL",
                    Status = QuestionStatus.Answered
                },
                new()
                {
                    Id = 21,
                    StageId = "task-review",
                    Prompt = "Choose cache:",
                    Status = QuestionStatus.Pending
                }
            }
        };

        // Without turn-specific questions, if the stage has ANY pending questions, it is actively waiting
        var (answer, meta) = StageConversationParser.ResolveAnswerFromTask(task, "task-review");
        Assert.Null(answer);
        Assert.Null(meta);
    }

    [Fact]
    public void ResolveAnswerFromTask_WhenTurnQuestionsMatchAnsweredQuestions_ReturnsFormattedAnswers()
    {
        var task = new ForgeTask
        {
            Id = 3,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 30,
                    StageId = "task-review",
                    Prompt = "Choose database:",
                    Answer = "PostgreSQL",
                    OptionsJson = "[\"PostgreSQL\",\"SQLite\"]",
                    Status = QuestionStatus.Answered
                },
                new()
                {
                    Id = 31,
                    StageId = "task-review",
                    Prompt = "Choose cache:",
                    Status = QuestionStatus.Pending
                }
            }
        };

        var turnQuestions = new List<QuestionItem>
        {
            new("Choose database:", null, new[] { "PostgreSQL", "SQLite" })
        };

        // When turn question specifically matches the answered question, it resolves cleanly
        var (answer, meta) = StageConversationParser.ResolveAnswerFromTask(task, "task-review", turnQuestions);
        Assert.Equal("PostgreSQL", answer);
        Assert.NotNull(meta);
        Assert.Contains("PostgreSQL", meta);
    }

    [Fact]
    public void ResolveAnswerFromTask_WhenTurnQuestionPromptDoesNotMatchAnsweredPrompt_ReturnsNull()
    {
        var task = new ForgeTask
        {
            Id = 4,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 40,
                    StageId = "task-review",
                    Prompt = "Choose database:",
                    Answer = "PostgreSQL",
                    Status = QuestionStatus.Answered
                }
            }
        };

        var pendingTurnQuestions = new List<QuestionItem>
        {
            new("Choose cache:", null, new[] { "Redis", "Memory" })
        };

        // Asking about cache when only database was answered must return null
        var (answer, meta) = StageConversationParser.ResolveAnswerFromTask(task, "task-review", pendingTurnQuestions);
        Assert.Null(answer);
        Assert.Null(meta);
    }

    [Fact]
    public void IsStageWaitingForAnswer_WhenConversationNotWaiting_ReturnsFalse()
    {
        var conv = new Conversation { Status = StageExecutionStatus.Completed };
        var task = new ForgeTask { IsWaitingForHuman = false };

        Assert.False(StageConversationParser.IsStageWaitingForAnswer(conv, task));
    }

    [Fact]
    public void IsStageWaitingForAnswer_WhenTaskNotWaitingForHuman_ReturnsFalse()
    {
        var conv = new Conversation { Status = StageExecutionStatus.WaitingForHuman };
        var task = new ForgeTask { IsWaitingForHuman = false };

        Assert.False(StageConversationParser.IsStageWaitingForAnswer(conv, task));
    }

    [Fact]
    public void IsStageWaitingForAnswer_WhenTaskHasPendingQuestions_ReturnsTrue()
    {
        var conv = new Conversation
        {
            Status = StageExecutionStatus.WaitingForHuman,
            StageId = "task-review"
        };
        var task = new ForgeTask
        {
            IsWaitingForHuman = true,
            Questions = new List<Question>
            {
                new() { StageId = "task-review", Status = QuestionStatus.Pending }
            }
        };

        Assert.True(StageConversationParser.IsStageWaitingForAnswer(conv, task));
    }

    [Fact]
    public void IsStageWaitingForAnswer_WhenLastAskTurnHasNoMatchingToolResult_EvenIfPreviousAskHadResult_ReturnsTrue()
    {
        var conv = new Conversation
        {
            Status = StageExecutionStatus.WaitingForHuman,
            StageId = "task-review",
            Turns = new List<ConversationTurn>
            {
                new() { TurnIndex = 0, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Ask, MetadataJson = "{\"CallId\":\"call-1\"}" },
                new() { TurnIndex = 1, Role = StageTurnRole.ToolResult, ToolName = CanonicalToolNames.Ask, ToolResult = "Answer 1", MetadataJson = "{\"CallId\":\"call-1\"}" },
                new() { TurnIndex = 2, Role = StageTurnRole.Tool, ToolName = CanonicalToolNames.Ask, MetadataJson = "{\"CallId\":\"call-2\"}" }
            }
        };
        var task = new ForgeTask { IsWaitingForHuman = true };

        // Last ask turn (call-2) does NOT have a matching ToolResult turn, so must be waiting
        Assert.True(StageConversationParser.IsStageWaitingForAnswer(conv, task));
    }

    [Fact]
    public void IsRunningToolCompleted_WhenTurnsNullOrEmpty_ReturnsFalse()
    {
        Assert.False(StageConversationParser.IsRunningToolCompleted(null, "bash"));
        Assert.False(StageConversationParser.IsRunningToolCompleted(new List<ConversationTurn>(), "bash"));
        Assert.False(StageConversationParser.IsRunningToolCompleted(new List<ConversationTurn> { new() { ToolName = "bash" } }, null));
        Assert.False(StageConversationParser.IsRunningToolCompleted(new List<ConversationTurn> { new() { ToolName = "bash" } }, "  "));
    }

    [Fact]
    public void IsRunningToolCompleted_WhenRepeatedToolCallStarts_DoesNotMatchPriorResult()
    {
        var t0 = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(5);
        var t2 = t0.AddMinutes(5); // 2nd call started
        var t3 = t2.AddSeconds(8); // 2nd call finished

        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "bash", Timestamp = t0 },
            new() { Id = 2, TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = "bash", ToolResult = "ok 1", Timestamp = t1 }
        };

        // 2nd call running, but turns in DB only have the first call's result
        Assert.False(StageConversationParser.IsRunningToolCompleted(turns, "bash", runningToolStartedAt: t2));

        // 2nd call's Tool turn added to DB
        turns.Add(new ConversationTurn { Id = 3, TurnIndex = 3, Role = StageTurnRole.Tool, ToolName = "bash", Timestamp = t2 });
        Assert.False(StageConversationParser.IsRunningToolCompleted(turns, "bash", runningToolStartedAt: t2));

        // 2nd call's ToolResult turn added to DB
        turns.Add(new ConversationTurn { Id = 4, TurnIndex = 4, Role = StageTurnRole.ToolResult, ToolName = "bash", ToolResult = "ok 2", Timestamp = t3 });
        Assert.True(StageConversationParser.IsRunningToolCompleted(turns, "bash", runningToolStartedAt: t2));
    }

    [Fact]
    public void IsRunningToolCompleted_WhenLatestTurnIsToolCall_ReturnsFalse()
    {
        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "dotnet-test", Timestamp = DateTime.UtcNow }
        };

        Assert.False(StageConversationParser.IsRunningToolCompleted(turns, "dotnet-test"));
    }

    [Fact]
    public void IsRunningToolCompleted_WhenFastConsecutiveCallsWithinOneSecond_DoesNotMatchPriorResult()
    {
        var t0 = new DateTime(2026, 10, 24, 12, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMilliseconds(500); // 1st call finished
        var t2 = t1.AddMilliseconds(200); // 2nd call started 200ms later (less than 1s)

        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "bash", Timestamp = t0 },
            new() { Id = 2, TurnIndex = 2, Role = StageTurnRole.ToolResult, ToolName = "bash", ToolResult = "ok 1", Timestamp = t1 }
        };

        // 2nd call is running. DB still only has the 1st call's result.
        // It should NOT match the 1st result as completed for the 2nd call despite being within 1 second.
        Assert.False(StageConversationParser.IsRunningToolCompleted(turns, "bash", runningToolStartedAt: t2));

        // When 2nd call completes at t3 (>= t2):
        var t3 = t2.AddMilliseconds(300);
        turns.Add(new ConversationTurn { Id = 3, TurnIndex = 3, Role = StageTurnRole.ToolResult, ToolName = "bash", ToolResult = "ok 2", Timestamp = t3 });
        Assert.True(StageConversationParser.IsRunningToolCompleted(turns, "bash", runningToolStartedAt: t2));
    }

    [Fact]
    public void IsRunningToolCompleted_WhenToolTurnContainsEmbeddedResult_ReturnsTrue()
    {
        var t0 = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(2);

        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, TurnIndex = 1, Role = StageTurnRole.Tool, ToolName = "read_file", ToolResult = "file contents", Timestamp = t1 }
        };

        Assert.True(StageConversationParser.IsRunningToolCompleted(turns, "read_file", runningToolStartedAt: t0));
    }

    #endregion

    #region Synthetic Priming Tests

    [Fact]
    public void IsPrimedTurn_ReturnsTrue_WhenIsPrimedMetadataIsTrue()
    {
        var turn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"prime_123\",\"IsPrimed\":true,\"SourceStageId\":\"deterministic-pre-impl\",\"PrimingType\":\"ForwardPass\"}"
        };

        Assert.True(StageConversationParser.IsPrimedTurn(turn));
    }

    [Fact]
    public void IsPrimedTurn_ReturnsFalse_WhenIsPrimedIsMissingOrFalse()
    {
        Assert.False(StageConversationParser.IsPrimedTurn(null));
        Assert.False(StageConversationParser.IsPrimedTurn(new ConversationTurn()));
        Assert.False(StageConversationParser.IsPrimedTurn(new ConversationTurn { MetadataJson = "{\"CallId\":\"call-1\"}" }));
        Assert.False(StageConversationParser.IsPrimedTurn(new ConversationTurn { MetadataJson = "{\"IsPrimed\":false}" }));
        Assert.False(StageConversationParser.IsPrimedTurn(new ConversationTurn { MetadataJson = "not-json" }));
    }

    [Fact]
    public void GetPrimingBadge_ReturnsCorrectBadge_ForForwardPassAndLoopbackDiagnostic()
    {
        var forwardTurn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"p1\",\"IsPrimed\":true,\"PrimingType\":\"ForwardPass\"}"
        };
        var loopbackTurn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"p2\",\"IsPrimed\":true,\"PrimingType\":\"LoopbackDiagnostic\"}"
        };
        var unprimedTurn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"p3\"}"
        };

        Assert.Equal("⚡ Quality Gate Handover", StageConversationParser.GetPrimingBadge(forwardTurn));
        Assert.Equal("🔁 Quality Gate Diagnostic", StageConversationParser.GetPrimingBadge(loopbackTurn));
        Assert.Null(StageConversationParser.GetPrimingBadge(unprimedTurn));
        Assert.Null(StageConversationParser.GetPrimingBadge(null));
    }

    [Fact]
    public void GetPrimingTooltip_ReturnsInformativeExplanation()
    {
        var forwardTurn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"p1\",\"IsPrimed\":true,\"PrimingType\":\"ForwardPass\"}"
        };
        var loopbackTurn = new ConversationTurn
        {
            MetadataJson = "{\"CallId\":\"p2\",\"IsPrimed\":true,\"PrimingType\":\"LoopbackDiagnostic\"}"
        };

        Assert.Contains("without GPU inference", StageConversationParser.GetPrimingTooltip(forwardTurn));
        Assert.Contains("failure diagnostic", StageConversationParser.GetPrimingTooltip(loopbackTurn));
        Assert.Null(StageConversationParser.GetPrimingTooltip(null));
    }

    [Fact]
    public void PairToolResults_CorrectlyPairsPrimedToolCallAndResult()
    {
        const string callId = "prime_abc123";
        var meta = $"{{\"CallId\":\"{callId}\",\"IsPrimed\":true,\"SourceStageId\":\"deterministic-pre-impl\",\"PrimingType\":\"ForwardPass\"}}";

        var turns = new List<ConversationTurn>
        {
            new() { Id = 1, Role = StageTurnRole.Tool, ToolName = "bash", ToolArguments = "dotnet test", MetadataJson = meta },
            new() { Id = 2, Role = StageTurnRole.ToolResult, ToolName = "bash", ToolResult = "Exit Code: 0\nPassed: 10", MetadataJson = meta }
        };

        var (merged, hidden) = StageConversationParser.PairToolResults(turns);

        Assert.Single(merged);
        Assert.Equal("Exit Code: 0\nPassed: 10", merged[1].ToolResult);
        Assert.Contains(2, hidden);
    }

    [Theory]
    [InlineData("{\"command\":\"dotnet test\"}", "(dotnet test)")]
    [InlineData("dotnet test", "(dotnet test)")]
    [InlineData("{\"CommandLine\":\"git status\"}", "(git status)")]
    [InlineData("{\"path\":\"src/Program.cs\"}", "(src/Program.cs)")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ChatPresentationHelper_GetToolSummary_FormatsArgumentsCleanly(string? input, string expected)
    {
        var summary = ChatPresentationHelper.GetToolSummary(input);
        Assert.Equal(expected, summary);
    }

    #endregion
}

