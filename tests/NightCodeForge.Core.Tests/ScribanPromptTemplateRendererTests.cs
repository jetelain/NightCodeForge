using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ScribanPromptTemplateRendererTests
{
    private readonly ScribanPromptTemplateRenderer _renderer = new();

    [Fact]
    public void Render_NullOrEmptyTemplate_ReturnsEmptyString()
    {
        Assert.Empty(_renderer.Render(null!, new { }));
        Assert.Empty(_renderer.Render(string.Empty, new { }));
        Assert.Empty(_renderer.Render("   \n\t", new { }));
    }

    [Fact]
    public void Render_BasicModel_ReplacesPropertiesCorrectly()
    {
        var template = "Task #{{ task.id }}: {{ task.title }}";
        var model = new
        {
            task = new { id = 42, title = "Implement Scriban" }
        };

        var result = _renderer.Render(template, model);

        Assert.Equal("Task #42: Implement Scriban", result);
    }

    [Fact]
    public void Render_WhitespaceTrimming_OmitsEmptySectionsWithoutBlankLines()
    {
        var template = """
        Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}
        {{ task.description }}
        {{- if context.task_markdown_path }}

        [ForgeTask Specification Document]:
        {{ context.task_markdown_path }}
        {{- end }}
        {{- if context.reviewer_feedback }}

        [Reviewer Requested Changes / Feedback]:
        {{ context.reviewer_feedback }}
        {{- end }}
        """.Replace("\r\n", "\n");

        var modelWithoutOptionalSections = new
        {
            task = new { id = 10, title = "Feature X", description = "Details about feature X." },
            stage = new { display_name = "Implementation" },
            context = new
            {
                task_markdown_path = (string?)null,
                reviewer_feedback = (string?)null
            }
        };

        var result = _renderer.Render(template, modelWithoutOptionalSections).Replace("\r\n", "\n").Trim();

        Assert.Equal("Execute stage 'Implementation' for task #10: Feature X\nDetails about feature X.", result);
        Assert.DoesNotContain("[ForgeTask Specification Document]", result);
        Assert.DoesNotContain("[Reviewer Requested Changes / Feedback]", result);
    }

    [Fact]
    public void Render_WithCollection_RendersLoopCorrectly()
    {
        var template = """
        [Human Clarifications & Answers]:
        {{ for qa in context.answered_questions }}
        Q: {{ qa.prompt }}
        A: {{ qa.answer }}
        {{ end }}
        """.Replace("\r\n", "\n").Trim();

        var model = new
        {
            context = new
            {
                answered_questions = new[]
                {
                    new { prompt = "Use PostgreSQL or SQLite?", answer = "SQLite" },
                    new { prompt = "Is async required?", answer = "Yes" }
                }
            }
        };

        var result = _renderer.Render(template, model).Replace("\r\n", "\n").Trim();

        Assert.Contains("Q: Use PostgreSQL or SQLite?\nA: SQLite", result);
        Assert.Contains("Q: Is async required?\nA: Yes", result);
    }

    [Fact]
    public void Render_MalformedTemplate_ThrowsInvalidOperationException()
    {
        var invalidTemplate = "Hello {{ if unclosed tag ";
        var ex = Assert.Throws<InvalidOperationException>(() => _renderer.Render(invalidTemplate, new { }));

        Assert.Contains("Failed parsing Scriban prompt template", ex.Message);
    }
}
