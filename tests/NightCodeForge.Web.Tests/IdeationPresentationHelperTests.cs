using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class IdeationPresentationHelperTests
{
    [Fact]
    public void MapToQuestionItems_ConvertsIdeationQuestionsCorrectly()
    {
        var ideationQuestions = new List<IdeationQuestion>
        {
            new("Which framework?", "Choose UI tech", new[] { "Blazor", "React" }),
            new("Target platform?", null, new[] { "Web", "Desktop" })
        };

        var items = IdeationPresentationHelper.MapToQuestionItems(ideationQuestions);

        Assert.Equal(2, items.Count);
        Assert.Equal("Which framework?", items[0].Prompt);
        Assert.Equal("Choose UI tech", items[0].Rationale);
        Assert.NotNull(items[0].Options);
        Assert.Equal(2, items[0].Options!.Count);
        Assert.Equal("Blazor", items[0].Options![0]);
        Assert.Equal("React", items[0].Options![1]);

        Assert.Equal("Target platform?", items[1].Prompt);
        Assert.Null(items[1].Rationale);
        Assert.NotNull(items[1].Options);
        Assert.Equal(2, items[1].Options!.Count);
    }

    [Fact]
    public void MapToQuestionItems_NullOrEmpty_ReturnsEmptyList()
    {
        var itemsNull = IdeationPresentationHelper.MapToQuestionItems(null);
        Assert.Empty(itemsNull);

        var itemsEmpty = IdeationPresentationHelper.MapToQuestionItems(Array.Empty<IdeationQuestion>());
        Assert.Empty(itemsEmpty);
    }

    [Fact]
    public async Task SubmitAnswersToPendingQuestions_CompletesAllTasks_WithGivenAnswers()
    {
        var q1 = new IdeationQuestion("Database?", null, new[] { "Postgres", "SQLite" });
        var q2 = new IdeationQuestion("ORM?", null, new[] { "EF Core", "Dapper" });
        var pendingQuestions = new List<IdeationQuestion> { q1, q2 };

        var answers = new Dictionary<int, string>
        {
            [0] = "Postgres",
            [1] = "EF Core"
        };

        IdeationPresentationHelper.SubmitAnswersToPendingQuestions(pendingQuestions, answers);

        var res1 = await q1.AnswerTask;
        var res2 = await q2.AnswerTask;

        Assert.Equal("Postgres", res1);
        Assert.Equal("EF Core", res2);
    }

    [Fact]
    public async Task SubmitAnswersToPendingQuestions_WhenAnswerMissing_UsesFallbackToAvoidHanging()
    {
        var q1 = new IdeationQuestion("Database?", null, new[] { "Postgres", "SQLite" });
        var q2 = new IdeationQuestion("ORM?", null, new[] { "EF Core", "Dapper" });
        var pendingQuestions = new List<IdeationQuestion> { q1, q2 };

        // Only answer q1
        var answers = new Dictionary<int, string>
        {
            [0] = "Postgres"
        };

        IdeationPresentationHelper.SubmitAnswersToPendingQuestions(pendingQuestions, answers);

        var res1 = await q1.AnswerTask;
        var res2 = await q2.AnswerTask;

        Assert.Equal("Postgres", res1);
        Assert.Equal("(No answer provided)", res2);
    }

    [Fact]
    public void FormatSubmission_ReturnsExpectedStrings()
    {
        var dict = new Dictionary<int, string>
        {
            [0] = "Postgres"
        };

        var (formatted, metadata) = IdeationPresentationHelper.FormatSubmission(dict);
        Assert.Equal("Postgres", formatted);
        Assert.Contains("Postgres", metadata);

        var multiDict = new Dictionary<int, string>
        {
            [0] = "Postgres",
            [1] = "EF Core"
        };
        var (multiFormatted, multiMeta) = IdeationPresentationHelper.FormatSubmission(multiDict);
        Assert.Contains("Q1: Postgres", multiFormatted);
        Assert.Contains("Q2: EF Core", multiFormatted);
        Assert.Contains("EF Core", multiMeta);
    }

    [Fact]
    public void GetSubmitButtonLabel_ReturnsExpectedLabels()
    {
        Assert.Equal("Submit & Continue Ideation ➔", IdeationPresentationHelper.GetSubmitButtonLabel(1));
        Assert.Equal("Submit & Continue Ideation ➔", IdeationPresentationHelper.GetSubmitButtonLabel(0));
        Assert.Equal("Submit All Clarifications (2) ➔", IdeationPresentationHelper.GetSubmitButtonLabel(2));
        Assert.Equal("Submit All Clarifications (5) ➔", IdeationPresentationHelper.GetSubmitButtonLabel(5));
    }

    [Fact]
    public void ExtractStructuredAnswers_WithCamelCaseJson_ParsesCorrectly()
    {
        var json = "{\"answers\":[{\"questionIndex\":0,\"selectedOptionIndex\":1,\"selectedOption\":\"Postgres\",\"answerText\":\"Postgres\"}]}";
        var answers = StageConversationParser.ExtractStructuredAnswers(json);
        Assert.Single(answers);
        Assert.Equal(0, answers[0].QuestionIndex);
        Assert.Equal(1, answers[0].SelectedOptionIndex);
        Assert.Equal("Postgres", answers[0].SelectedOption);
        Assert.Equal("Postgres", answers[0].AnswerText);
    }

    [Fact]
    public void BuildStructuredAnswersJson_WithNullOptionsOrAnswer_HandlesSafelyWithoutException()
    {
        var questions = new List<QuestionItem>
        {
            new("Q1?", null, new string?[] { null, "OptionA" }!)
        };
        var answers = new Dictionary<int, string>
        {
            [0] = null!
        };
        var json = StageConversationParser.BuildStructuredAnswersJson(answers, questions);
        Assert.NotNull(json);
        Assert.Contains("Answers", json);
    }

    [Fact]
    public void QuestionContentPart_PromptProperty_PrioritizesQuestionsWhenPopulated()
    {
        var qPart = new QuestionContentPart
        {
            Prompt = "Initial Prompt",
            Rationale = "Initial Rationale",
            Options = new[] { "InitOpt" }
        };
        Assert.Equal("Initial Prompt", qPart.Prompt);
        Assert.Single(qPart.EffectiveQuestions);

        // Populate Questions
        qPart.Questions = new[]
        {
            new QuestionItem("Updated Question Prompt", "Updated Rationale", new[] { "Opt1", "Opt2" })
        };

        Assert.Equal("Updated Question Prompt", qPart.Prompt);
        Assert.Equal("Updated Rationale", qPart.Rationale);
        Assert.Equal(2, qPart.Options.Count);
        Assert.Equal("Updated Question Prompt", qPart.EffectiveQuestions[0].Prompt);
    }

    [Fact]
    public void IdeationSessionState_CancelPendingQuestions_CancelsAllInBatch()
    {
        var session = new IdeationSessionState();
        var q1 = new IdeationQuestion("Q1?", null, null);
        var q2 = new IdeationQuestion("Q2?", null, null);
        session.PendingQuestions.AddRange(new[] { q1, q2 });

        session.CancelPendingQuestions();

        Assert.Empty(session.PendingQuestions);
        Assert.True(q1.AnswerTask.IsCanceled);
        Assert.True(q2.AnswerTask.IsCanceled);
    }
}
