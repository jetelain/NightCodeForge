using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Execution;

namespace NightCodeForge.Agents.Tests;

public class CanonicalAIFunctionFactoryTests
{
    [Fact]
    public void CreateTools_DoesNotAdvertiseWebSearch_WhenWebSearchIsNotConfigured()
    {
        var unconfiguredSearch = new MockWebSearchService(isConfigured: false);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: unconfiguredSearch);

        Assert.False(tools.IsWebSearchConfigured);

        // Even when permittedTools specifically requests web_search or is null
        var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
        Assert.DoesNotContain(allTools, t => t.Name == CanonicalToolNames.WebSearch);

        var permittedExplicitly = CanonicalAIFunctionFactory.CreateTools(tools, new[] { "read", "bash", "web_search" });
        Assert.DoesNotContain(permittedExplicitly, t => t.Name == CanonicalToolNames.WebSearch);
        Assert.Contains(permittedExplicitly, t => t.Name == "read");
        Assert.Contains(permittedExplicitly, t => t.Name == "bash");
    }

    [Fact]
    public void CreateTools_AdvertisesWebSearch_WhenWebSearchIsConfigured()
    {
        var configuredSearch = new MockWebSearchService(isConfigured: true);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: configuredSearch);

        Assert.True(tools.IsWebSearchConfigured);

        var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
        Assert.Contains(allTools, t => t.Name == CanonicalToolNames.WebSearch);

        var permittedExplicitly = CanonicalAIFunctionFactory.CreateTools(tools, new[] { "read", "web_search" });
        Assert.Contains(permittedExplicitly, t => t.Name == CanonicalToolNames.WebSearch);
        Assert.Equal(2, permittedExplicitly.Count);
    }

    [Fact]
    public async Task CanonicalTools_WebSearchAsync_ReturnsDisabledMessage_WhenUnconfigured()
    {
        var unconfiguredSearch = new MockWebSearchService(isConfigured: false);
        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: unconfiguredSearch);

        var result = await tools.WebSearchAsync("how to run local models");

        Assert.Contains("Web search is disabled because no API key is configured", result);
    }

    [Fact]
    public async Task CanonicalTools_WebSearchAsync_ExecutesSearch_WhenConfigured()
    {
        var configuredSearch = new MockWebSearchService(
            searchHandler: (q, max) => Task.FromResult(new WebSearchResponse(new List<WebSearchResultItem>
            {
                new("Result 1", "https://example.com/1", "Sample content 1")
            })),
            isConfigured: true);

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: configuredSearch);

        var result = await tools.WebSearchAsync("test query");

        Assert.Contains("Web search results for: 'test query'", result);
        Assert.Contains("Result 1", result);
        Assert.Contains("https://example.com/1", result);
    }

    [Fact]
    public async Task EditTool_WithCamelCaseShortcut_AppliesEditSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "Hello World\nLine 2");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("edit", new Dictionary<string, object?>
            {
                ["path"] = "file.txt",
                ["oldText"] = "Hello World",
                ["newText"] = "Hello Universe"
            });

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString());
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Hello Universe", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditTool_WithSnakeCaseShortcut_AppliesEditSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "Hello World\nLine 2");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
            var editTool = allTools.OfType<AIFunction>().Single(t => t.Name == "edit");

            var result = await editTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "file.txt",
                ["old_text"] = "Hello World",
                ["new_text"] = "Hello Universe"
            }));

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString());
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Hello Universe", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditTool_WithCamelCaseEditsArray_AppliesEditSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "Hello World\nLine 2");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
            var editTool = allTools.OfType<AIFunction>().Single(t => t.Name == "edit");

            var editsJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "[{\"oldText\": \"Hello World\", \"newText\": \"Hello Multiverse\"}]");
            var result = await editTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "file.txt",
                ["edits"] = editsJson
            }));

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString());
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Hello Multiverse", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditTool_WithSnakeCaseEditsArray_AppliesEditSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "Hello World\nLine 2");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var editsJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                "[{\"old_text\": \"Hello World\", \"new_text\": \"Hello Galaxy\"}]");
            var result = await dispatcher.ExecuteAsync("edit", new Dictionary<string, object?>
            {
                ["path"] = "file.txt",
                ["edits"] = editsJson
            });

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString());
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Contains("Hello Galaxy", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditTool_MissingAllEditParameters_ReturnsUnifiedNonConfusingErrorMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "Hello World\nLine 2");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
            var editTool = allTools.OfType<AIFunction>().Single(t => t.Name == "edit");

            var result = await editTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["path"] = "file.txt"
            }));

            Assert.StartsWith("Error: Edit failed:", result?.ToString() ?? string.Empty);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BashTool_WithWorkingDirectoryCamelCase_InvokesWithEffectiveDirectory()
    {
        string? passedWorkDir = null;
        var mockEnv = new Moq.Mock<NightCodeForge.Core.Interfaces.IExecutionEnvironment>();
        mockEnv.Setup(e => e.ExecuteAsync(
            Moq.It.IsAny<string>(),
            Moq.It.IsAny<string?>(),
            Moq.It.IsAny<int?>(),
            Moq.It.IsAny<IReadOnlyDictionary<string, string>?>(),
            Moq.It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, wd, timeout, env, ct) =>
            {
                passedWorkDir = wd;
                return Task.FromResult(new NightCodeForge.Core.Interfaces.CommandResult(0, "output", "", TimeSpan.FromSeconds(10)));
            });

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            executionEnv: mockEnv.Object,
            pathResolver: TestPathResolver.Create());

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("bash", new Dictionary<string, object?>
        {
            ["command"] = "echo test",
            ["workingDirectory"] = "sub/folder"
        });

        Assert.NotNull(passedWorkDir);
        Assert.Contains("sub", passedWorkDir);
        Assert.Contains("folder", passedWorkDir);
    }

    [Fact]
    public async Task LoadSkillTool_WithSkillNameCamelCase_LoadsSkillSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(tempDir, ".agents", "skills", "test-skill");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "---\nname: test-skill\ndescription: A test skill\n---\n# Test Skill Instructions");

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("load_skill", new Dictionary<string, object?>
            {
                ["skillName"] = "test-skill"
            });

            Assert.Contains("Test Skill Instructions", result?.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WebSearchTool_WithMaxResultsCamelCase_InvokesWithCorrectCount()
    {
        int? receivedMax = null;
        var configuredSearch = new MockWebSearchService(
            searchHandler: (q, max) =>
            {
                receivedMax = max;
                return Task.FromResult(new WebSearchResponse(new List<WebSearchResultItem>
                {
                    new("Title", "https://example.com", "Snippet")
                }));
            },
            isConfigured: true);

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: configuredSearch);

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var result = await dispatcher.ExecuteAsync(CanonicalToolNames.WebSearch, new Dictionary<string, object?>
        {
            ["query"] = "c# 13 params collections",
            ["maxResults"] = 7
        });

        Assert.Equal(7, receivedMax);
        Assert.Contains("Title", result?.ToString());
    }

    [Fact]
    public async Task ReadTool_WithStartLineAndEndLine_ReadsSlice()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllLinesAsync(filePath, new[] { "Line 1", "Line 2", "Line 3", "Line 4", "Line 5" });

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("read", new Dictionary<string, object?>
            {
                ["path"] = "sample.txt",
                ["start_line"] = 2,
                ["end_line"] = 4
            });

            var text = result?.ToString() ?? "";
            Assert.Contains("Line 2", text);
            Assert.Contains("Line 3", text);
            Assert.Contains("Line 4", text);
            Assert.DoesNotContain("Line 1", text);
            Assert.DoesNotContain("Line 5", text);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteTool_WithFilePathAndTextAliases_WritesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("write", new Dictionary<string, object?>
            {
                ["filePath"] = "written.txt",
                ["text"] = "Hello from text alias"
            });

            Assert.Contains("Successfully wrote", result?.ToString());
            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, "written.txt"));
            Assert.Equal("Hello from text alias", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteTool_WithCodeAlias_WritesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("write", new Dictionary<string, object?>
            {
                ["path"] = "code.cs",
                ["code"] = "public class Foo {}"
            });

            Assert.Contains("Successfully wrote", result?.ToString());
            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, "code.cs"));
            Assert.Equal("public class Foo {}", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AskTool_WithQuestionItemsAlias_InvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? capturedQuestions = null;
        tools.AskHandler = (questions, ct) =>
        {
            capturedQuestions = questions;
            return Task.FromResult("Q1: Answered");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var questionsJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "[{\"prompt\": \"Which database?\", \"options\": [\"PostgreSQL\", \"SQLite\"]}]");

        var result = await dispatcher.ExecuteAsync("ask", new Dictionary<string, object?>
        {
            ["question_items"] = questionsJson
        });

        Assert.Equal("Q1: Answered", result?.ToString());
        Assert.NotNull(capturedQuestions);
        Assert.Single(capturedQuestions);
        Assert.Equal("Which database?", capturedQuestions[0].Prompt);
    }

    [Fact]
    public async Task AskTool_WithItemsAlias_InvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? capturedQuestions = null;
        tools.AskHandler = (questions, ct) =>
        {
            capturedQuestions = questions;
            return Task.FromResult("Items: Answered");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var itemsJson = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            "[{\"prompt\": \"Proceed with plan?\"}]");

        var result = await dispatcher.ExecuteAsync("ask", new Dictionary<string, object?>
        {
            ["items"] = itemsJson
        });

        Assert.Equal("Items: Answered", result?.ToString());
        Assert.NotNull(capturedQuestions);
        Assert.Single(capturedQuestions);
        Assert.Equal("Proceed with plan?", capturedQuestions[0].Prompt);
    }

    [Fact]
    public async Task RequestChangesTool_WithCritiqueAndCommentAliases_ThrowsDomainException()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        string? passedFeedback = null;
        string? passedReason = null;
        tools.RequestChangesHandler = (fb, r) =>
        {
            passedFeedback = fb;
            passedReason = r;
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var ex = await Assert.ThrowsAsync<NightCodeForge.Core.Workflow.RequestChangesException>(async () =>
            await dispatcher.ExecuteAsync("request_changes", new Dictionary<string, object?>
            {
                ["critique"] = "Missing unit tests for edge cases",
                ["comment"] = "TDD requirement not met"
            }));

        Assert.Equal("Missing unit tests for edge cases", ex.Feedback);
        Assert.Equal("TDD requirement not met", ex.Reason);
        Assert.Equal("Missing unit tests for edge cases", passedFeedback);
        Assert.Equal("TDD requirement not met", passedReason);
    }

    [Fact]
    public async Task WebSearchTool_WithQAndCountAliases_InvokesWithExpectedValues()
    {
        string? passedQuery = null;
        int? passedMax = null;
        var configuredSearch = new MockWebSearchService(
            searchHandler: (q, max) =>
            {
                passedQuery = q;
                passedMax = max;
                return Task.FromResult(new WebSearchResponse(new List<WebSearchResultItem>
                {
                    new("Title", "https://example.com", "Snippet")
                }));
            },
            isConfigured: true);

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            pathResolver: TestPathResolver.Create(),
            webSearchService: configuredSearch);

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var result = await dispatcher.ExecuteAsync(CanonicalToolNames.WebSearch, new Dictionary<string, object?>
        {
            ["q"] = "system.text.json source generator",
            ["count"] = 3
        });

        Assert.Equal("system.text.json source generator", passedQuery);
        Assert.Equal(3, passedMax);
        Assert.Contains("Title", result?.ToString());
    }

    [Fact]
    public async Task BashTool_WithCwdAlias_InvokesWithEffectiveDirectory()
    {
        string? passedWorkDir = null;
        var mockEnv = new Moq.Mock<NightCodeForge.Core.Interfaces.IExecutionEnvironment>();
        mockEnv.Setup(e => e.ExecuteAsync(
            Moq.It.IsAny<string>(),
            Moq.It.IsAny<string?>(),
            Moq.It.IsAny<int?>(),
            Moq.It.IsAny<IReadOnlyDictionary<string, string>?>(),
            Moq.It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, wd, timeout, env, ct) =>
            {
                passedWorkDir = wd;
                return Task.FromResult(new NightCodeForge.Core.Interfaces.CommandResult(0, "output", "", TimeSpan.FromSeconds(10)));
            });

        var tools = TestCanonicalToolsBuilder.Create(
            AppContext.BaseDirectory,
            executionEnv: mockEnv.Object,
            pathResolver: TestPathResolver.Create());

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        await dispatcher.ExecuteAsync("bash", new Dictionary<string, object?>
        {
            ["command"] = "ls",
            ["cwd"] = "src/Domain"
        });

        Assert.NotNull(passedWorkDir);
        Assert.Contains("Domain", passedWorkDir);
    }

    [Fact]
    public async Task LoadSkillTool_WithSkillAlias_LoadsSkillSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(tempDir, ".agents", "skills", "dotnet-migration");
        Directory.CreateDirectory(skillDir);
        await File.WriteAllTextAsync(Path.Combine(skillDir, "SKILL.md"), "---\nname: dotnet-migration\ndescription: Migration skill\n---\n# Migration Instructions");

        try
        {
            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("load_skill", new Dictionary<string, object?>
            {
                ["skill"] = "dotnet-migration"
            });

            Assert.Contains("Migration Instructions", result?.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task EditTool_WithJsonStringEdits_AppliesEditSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.txt");
            await File.WriteAllTextAsync(filePath, "alpha beta gamma");

            var tools = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

            var result = await dispatcher.ExecuteAsync("edit", new Dictionary<string, object?>
            {
                ["path"] = "file.txt",
                ["edits"] = "[{\"oldText\": \"beta\", \"newText\": \"DELTA\"}]"
            });

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString());
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("alpha DELTA gamma", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EditTool_JsonSchema_ExposesCleanNonDuplicatedProperties()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        var allTools = CanonicalAIFunctionFactory.CreateTools(tools);
        var editTool = allTools.OfType<AIFunction>().Single(t => t.Name == "edit");

        var schema = editTool.JsonSchema.GetRawText();

        // The parameter list exposes edits, old_text, new_text (snake_case)
        Assert.Contains("\"edits\"", schema);
        Assert.Contains("\"old_text\"", schema);
        Assert.Contains("\"new_text\"", schema);

        // EditChunk record itself only has OldText and NewText (reflecting as oldText, newText)
        Assert.Contains("\"oldText\"", schema);
        Assert.Contains("\"newText\"", schema);
    }
}
