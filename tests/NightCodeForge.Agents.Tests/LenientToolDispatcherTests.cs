using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using Pmad.AI.Healing;
using Pmad.AI.Tools;

namespace NightCodeForge.Agents.Tests;

public class LenientToolDispatcherTests
{
    private readonly LenientToolDispatcher _dispatcher;

    public LenientToolDispatcherTests()
    {
        var canonical = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        _dispatcher = NightCodeForgeToolDispatcher.Create(canonical);
    }

    [Fact]
    public void Validate_WithAllCanonicalToolsRegistered_ReturnsZeroErrors()
    {
        var report = _dispatcher.Validate(throwOnError: false);
        Assert.DoesNotContain(report.Issues, i => i.Severity == ToolValidationSeverity.Error);
    }

    [Theory]
    [InlineData("read_file", "read")]
    [InlineData("ReadFile", "read")]
    [InlineData("cat", "read")]
    [InlineData("replace_file_content", "edit")]
    [InlineData("ReplaceFileContent", "edit")]
    [InlineData("str_replace", "edit")]
    [InlineData("write_file", "write")]
    [InlineData("create_file", "write")]
    [InlineData("run_command", "bash")]
    [InlineData("RunCommand", "bash")]
    [InlineData("exec", "bash")]
    [InlineData("ask_human_question", "ask")]
    [InlineData("ask_question", "ask")]
    [InlineData("askhumanquestion", "ask")]
    [InlineData("askhuman", "ask")]
    [InlineData("ask_questions", "ask")]
    [InlineData("search_web", "web_search")]
    [InlineData("loadskill", "load_skill")]
    [InlineData("useskill", "load_skill")]
    [InlineData("requestchanges", "request_changes")]
    [InlineData("rejectimplementation", "request_changes")]
    [InlineData("send_back", "request_changes")]
    [InlineData("sendback", "request_changes")]
    [InlineData("reject_stage", "request_changes")]
    [InlineData("loopback", "request_changes")]
    [InlineData("return_to_implementation", "request_changes")]
    [InlineData("view_task", "get_task")]
    [InlineData("gettask", "get_task")]
    [InlineData("update_task", "edit_task")]
    [InlineData("edittask", "edit_task")]
    [InlineData("updatetask", "edit_task")]
    [InlineData("prioritize_task", "set_task_priority")]
    [InlineData("settaskpriority", "set_task_priority")]
    [InlineData("set_priority", "set_task_priority")]
    [InlineData("reorder_tasks", "rework_priorities")]
    [InlineData("reworkpriorities", "rework_priorities")]
    [InlineData("rework_priority", "rework_priorities")]
    [InlineData("set_task_priorities", "rework_priorities")]
    [InlineData("batch_set_priorities", "rework_priorities")]
    [InlineData("reprioritize", "rework_priorities")]
    [InlineData("cancel_task", "remove_task")]
    [InlineData("removetask", "remove_task")]
    [InlineData("deletetask", "remove_task")]
    [InlineData("archive_task", "remove_task")]
    public void ResolveCanonicalToolName_Aliases_MapToCanonical(string rawName, string expectedCanonical)
    {
        var resolved = _dispatcher.ResolveCanonicalToolName(rawName);
        Assert.Equal(expectedCanonical, resolved);
    }

    [Fact]
    public void NormalizeCall_TranslatesListDir_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("list_dir", "{\"path\": \"src/Models\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("ls -la 'src/Models'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesGrepSearch_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("grep_search", "{\"query\": \"OrderService\", \"path\": \"src\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("rg -n -- 'OrderService' 'src'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesGetFileOutline_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("get_file_outline", "{\"file_path\": \"src/Program.cs\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("rg -n \"^(public|private|protected|internal|class|interface|record|def|function)\" 'src/Program.cs'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesFindFiles_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("find_files", "{\"dir\": \"src\", \"glob\": \"*.cs\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("find 'src' -maxdepth 4 -iname '*.cs'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesGetFileOutline_WithTargetFile()
    {
        var call = _dispatcher.NormalizeCall("get_file_outline", "{\"targetFile\": \"src/App.cs\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("rg -n \"^(public|private|protected|internal|class|interface|record|def|function)\" 'src/App.cs'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesFindFiles_WithRootAndFilePattern()
    {
        var call = _dispatcher.NormalizeCall("find_files", "{\"root\": \"tests\", \"file_pattern\": \"*Tests.cs\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.True(call.WasTranslated);
        Assert.Equal("find 'tests' -maxdepth 4 -iname '*Tests.cs'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_ReadWithLineOffset()
    {
        var json = "{\"file\": \"doc.md\", \"line_offset\": 5, \"count\": 20}";
        var call = _dispatcher.NormalizeCall("read", json);

        Assert.Equal("read", call.CanonicalToolName);
        Assert.Equal("doc.md", call.Arguments["path"]);
        Assert.Equal(5, call.Arguments["offset"]);
        Assert.Equal(20, call.Arguments["limit"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_EditWithOldStrAndNewStrAndUpdated()
    {
        var json = "{\"targetFile\": \"foo.txt\", \"old_str\": \"foo\", \"new_str\": \"bar\", \"replace_all_occurrences\": true}";
        var call = _dispatcher.NormalizeCall("edit", json);

        Assert.Equal("edit", call.CanonicalToolName);
        Assert.Equal("foo.txt", call.Arguments["path"]);
        Assert.True(call.Arguments.ContainsKey("edits"));
        var edits = Assert.IsAssignableFrom<IEnumerable<object?>>(call.Arguments["edits"]);
        var first = Assert.IsAssignableFrom<IDictionary<string, object?>>(edits.First());
        Assert.Equal("foo", first["oldText"]);
        Assert.Equal("bar", first["newText"]);
        Assert.Equal(true, first["replaceAll"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_EditWithReplacementsAndOperations()
    {
        var json = "{\"path\": \"file.txt\", \"replacements\": [{\"old_str\": \"a\", \"new_str\": \"b\"}]}";
        var call = _dispatcher.NormalizeCall("edit", json);

        Assert.Equal("edit", call.CanonicalToolName);
        Assert.True(call.Arguments.ContainsKey("edits"));
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_BashWithExec()
    {
        var json = "{\"exec\": \"dotnet test\"}";
        var call = _dispatcher.NormalizeCall("bash", json);

        Assert.Equal("bash", call.CanonicalToolName);
        Assert.Equal("dotnet test", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_AskWithCandidatesAndOptionsJson()
    {
        var json = "{\"questions\": [{\"prompt\": \"Choice?\", \"candidates\": [\"1\", \"2\"], \"options_json\": [\"A\", \"B\"]}]}";
        var call = _dispatcher.NormalizeCall("ask", json);

        Assert.Equal("ask", call.CanonicalToolName);
        Assert.True(call.Arguments.ContainsKey("questions"));
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_RequestChangesWithFindingsAndReview()
    {
        var json = "{\"findings\": \"Unit test missing\", \"why\": \"Quality gate\"}";
        var call = _dispatcher.NormalizeCall("request_changes", json);

        Assert.Equal("request_changes", call.CanonicalToolName);
        Assert.Equal("Unit test missing", call.Arguments["feedback"]);
        Assert.Equal("Quality gate", call.Arguments["reason"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_RangeLinesToOffsetAndLimit()
    {
        var json = JsonSerializer.Serialize(new
        {
            file_path = "test.txt",
            start_line = 10,
            end_line = 25
        });

        var call = _dispatcher.NormalizeCall("view_file", json);

        Assert.Equal("read", call.CanonicalToolName);
        Assert.Equal("test.txt", call.Arguments["path"]);
        Assert.Equal(10, call.Arguments["offset"]);
        Assert.Equal(16, call.Arguments["limit"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_FilePathAndTargetContent()
    {
        var json = @"{
            ""filePath"": ""src/Program.cs"",
            ""targetContent"": ""int x = 1;"",
            ""replacementContent"": ""int x = 2;""
        }";

        var call = _dispatcher.NormalizeCall("replace_file_content", json);
        Assert.Equal("edit", call.CanonicalToolName);
        Assert.Equal("src/Program.cs", call.Arguments["path"]);

        Assert.True(call.Arguments.ContainsKey("edits"));
        var edits = Assert.IsAssignableFrom<IEnumerable<object?>>(call.Arguments["edits"]);
        var first = Assert.IsAssignableFrom<IDictionary<string, object?>>(edits.First());
        Assert.Equal("int x = 1;", first["oldText"]);
        Assert.Equal("int x = 2;", first["newText"]);
    }

    [Fact]
    public void NormalizeCall_StripsMarkdownFences_FromPayload()
    {
        var fencedJson = "```json\n{\n  \"filePath\": \"test.txt\",\n  \"offset\": \"10\",\n  \"limit\": \"20\"\n}\n```";
        var call = _dispatcher.NormalizeCall("read_file", fencedJson);

        Assert.Equal("read", call.CanonicalToolName);
        Assert.Equal("test.txt", call.Arguments["path"]);
        Assert.Equal(10, call.Arguments["offset"]);
        Assert.Equal(20, call.Arguments["limit"]);
    }

    [Fact]
    public void SanitizeArgumentsJson_RepairsUnescapedNewlines_InCodeStrings()
    {
        var brokenJson = "{\n  \"command\": \"echo hello\necho world\"\n}";
        var sanitized = ToolCallHealingTransform.SanitizeArgumentsJson(brokenJson);

        Assert.DoesNotContain("\necho world", sanitized);
        Assert.Contains("\\necho world", sanitized);
    }

    [Fact]
    public async Task RegisterTool_CustomToolWithAliases_DispatchesSuccessfully()
    {
        bool toolExecuted = false;
        var customTools = new[]
        {
            new CustomToolConfig
            {
                Name = "deploy_service",
                Description = "Deploys a specified service",
                Aliases = ["deploy", "ship_it"],
                ParameterAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["svc"] = "service"
                }
            }
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(customTools: customTools);
        dispatcher.RegisterTool(AIFunctionFactory.Create(
            (string service) =>
            {
                toolExecuted = true;
                return "Deployment triggered: " + service;
            },
            "deploy_service",
            "Deploys a specified service"));

        Assert.Equal("deploy_service", dispatcher.ResolveCanonicalToolName("deploy"));
        Assert.Equal("deploy_service", dispatcher.ResolveCanonicalToolName("ship_it"));

        var result = await dispatcher.ExecuteAsync("ship_it", "{\"svc\": \"auth-api\"}");
        Assert.True(toolExecuted);
        Assert.Equal("Deployment triggered: auth-api", result?.ToString());
    }

    [Fact]
    public void CustomTranslationRule_TranslatesArguments()
    {
        var config = NightCodeForgeToolDispatcher.CreateConfiguration();
        config.Translations.Add(new ToolTranslation
        {
            SourceTool = "query_database",
            TargetTool = "bash",
            CommandTemplate = "psql -c {sql:SELECT 1}",
            EscapeMethod = ParameterEscapeMethod.Bash
        });

        var options = NightCodeForgeToolDispatcher.CreateOptions();
        options.ValidateTranslationsWithReadOnlyValidator = false;
        var dispatcher = new LenientToolDispatcher(config, options);
        dispatcher.RegisterTools(CanonicalAIFunctionFactory.CreateAllCanonicalFunctions());

        var call = dispatcher.NormalizeCall("query_database", "{\"sql\": \"SELECT * FROM users;\"}");
        Assert.Equal("bash", call.CanonicalToolName);
        Assert.Equal("psql -c 'SELECT * FROM users;'", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_SnakeCaseEditsArray()
    {
        var json = @"{
            ""filePath"": ""src/Program.cs"",
            ""edits"": [
                {
                    ""old_text"": ""int x = 1;"",
                    ""new_text"": ""int x = 2;""
                }
            ]
        }";

        var call = _dispatcher.NormalizeCall("edit", json);
        Assert.Equal("edit", call.CanonicalToolName);
        Assert.Equal("src/Program.cs", call.Arguments["path"]);

        Assert.True(call.Arguments.ContainsKey("edits"));
        var edits = Assert.IsAssignableFrom<IEnumerable<object?>>(call.Arguments["edits"]);
        var first = Assert.IsAssignableFrom<IDictionary<string, object?>>(edits.First());
        Assert.Equal("int x = 1;", first["oldText"]);
        Assert.Equal("int x = 2;", first["newText"]);
    }

    [Fact]
    public async Task ExecuteAsync_EditWithSnakeCaseShortcut_AppliesEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = "{\"path\": \"sample.txt\", \"old_text\": \"bar\", \"new_text\": \"qux\"}";
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString() ?? string.Empty);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditWithCamelCaseShortcut_AppliesEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = "{\"path\": \"sample.txt\", \"oldText\": \"bar\", \"newText\": \"qux\"}";
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString() ?? string.Empty);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditWithMissingParameters_ReturnsUnifiedErrorMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = "{\"path\": \"sample.txt\"}";
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Error: Edit failed:", result?.ToString() ?? string.Empty);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditTool_WhenNewTextIsNull_CoercesToEmptyStringAndDeletesMatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "REMOVE keep");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = """{"path": "sample.txt", "old_text": "REMOVE ", "new_text": null}""";
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString() ?? string.Empty);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("keep", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EditTool_WithNestedEditsArray_PropagatesTopLevelReplaceAll()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo and foo");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = """
            {
                "path": "sample.txt",
                "replace_all": true,
                "edits": [
                    { "old_text": "foo", "new_text": "bar" }
                ]
            }
            """;
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString() ?? string.Empty);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("bar and bar", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_WorkingDirectoryAndSkillName()
    {
        var jsonBash = "{\"command\": \"pwd\", \"workingDirectory\": \"src/Web\"}";
        var callBash = _dispatcher.NormalizeCall("bash", jsonBash);
        Assert.Equal("bash", callBash.CanonicalToolName);
        Assert.Equal("src/Web", callBash.Arguments["working_directory"]);

        var jsonSkill = "{\"skillName\": \"csharp-refactoring\"}";
        var callSkill = _dispatcher.NormalizeCall("load_skill", jsonSkill);
        Assert.Equal("load_skill", callSkill.CanonicalToolName);
        Assert.Equal("csharp-refactoring", callSkill.Arguments["skill_name"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_WriteCodeAlias()
    {
        var json = "{\"path\": \"src/Test.cs\", \"code\": \"public class Test {}\"}";
        var call = _dispatcher.NormalizeCall("write", json);
        Assert.Equal("write", call.CanonicalToolName);
        Assert.Equal("src/Test.cs", call.Arguments["path"]);
        Assert.Equal("public class Test {}", call.Arguments["content"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_AskQuestionItemsAndItems()
    {
        var json1 = "{\"question_items\": [{\"prompt\": \"Question 1\"}]}";
        var call1 = _dispatcher.NormalizeCall("ask", json1);
        Assert.Equal("ask", call1.CanonicalToolName);
        Assert.True(call1.Arguments.ContainsKey("questions"));

        var json2 = "{\"items\": [{\"prompt\": \"Question 2\"}]}";
        var call2 = _dispatcher.NormalizeCall("ask", json2);
        Assert.Equal("ask", call2.CanonicalToolName);
        Assert.True(call2.Arguments.ContainsKey("questions"));
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_RequestChangesCritiqueAndComment()
    {
        var json = "{\"critique\": \"Code does not compile\", \"comment\": \"Syntax error on line 42\"}";
        var call = _dispatcher.NormalizeCall("request_changes", json);
        Assert.Equal("request_changes", call.CanonicalToolName);
        Assert.Equal("Code does not compile", call.Arguments["feedback"]);
        Assert.Equal("Syntax error on line 42", call.Arguments["reason"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_WebSearchQAndCount()
    {
        var json = "{\"q\": \"aspnet core 10 minimal apis\", \"count\": 8}";
        var call = _dispatcher.NormalizeCall("web_search", json);
        Assert.Equal("web_search", call.CanonicalToolName);
        Assert.Equal("aspnet core 10 minimal apis", call.Arguments["query"]);
        Assert.Equal(8, call.Arguments["max_results"]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithQuestionItems_InvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = "{\"question_items\": [{\"prompt\": \"Use Redis or Postgres?\"}]}";
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Use Redis or Postgres?", captured[0].Prompt);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithStringifiedQuestionsArray_ParsesAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """
        {
            "questions": "[{\"prompt\": \"Select database engine\", \"rationale\": \"Storage design\", \"options\": [\"PostgreSQL\", \"SQLite\"]}]"
        }
        """;
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Select database engine", captured[0].Prompt);
        Assert.Equal("Storage design", captured[0].Rationale);
        Assert.Equal(2, captured[0].Options?.Count);
        Assert.Equal("PostgreSQL", captured[0].Options?[0]);
        Assert.Equal("SQLite", captured[0].Options?[1]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithStringifiedSingleQuestionObject_ParsesAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """
        {
            "questions": "{\"prompt\": \"Confirm deploy to staging?\", \"options\": [\"Yes\", \"No\"]}"
        }
        """;
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Confirm deploy to staging?", captured[0].Prompt);
        Assert.Equal(2, captured[0].Options?.Count);
        Assert.Equal("Yes", captured[0].Options?[0]);
        Assert.Equal("No", captured[0].Options?[1]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithBoxedJsonElementString_ParsesAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        using var doc = JsonDocument.Parse("""
        "[{\"prompt\": \"Choose environment\", \"options\": [\"Dev\", \"Prod\"]}]"
        """);

        var args = new Dictionary<string, object?>
        {
            ["questions"] = doc.RootElement.Clone()
        };

        var result = await dispatcher.ExecuteAsync("ask", args);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Choose environment", captured[0].Prompt);
        Assert.Equal(2, captured[0].Options?.Count);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithStringifiedOptionsArray_ParsesOptionsAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """
        {
            "questions": [
                {
                    "prompt": "Pick database engine",
                    "options": "[\"PostgreSQL\", \"SQLite\"]"
                }
            ]
        }
        """;
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Pick database engine", captured[0].Prompt);
        Assert.NotNull(captured[0].Options);
        Assert.Equal(2, captured[0].Options!.Count);
        Assert.Equal("PostgreSQL", captured[0].Options![0]);
        Assert.Equal("SQLite", captured[0].Options![1]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithDictionaryBoxedJsonElementOptionsArray_ParsesOptionsAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        using var doc = JsonDocument.Parse("""["Blue", "Green"]""");

        var args = new Dictionary<string, object?>
        {
            ["questions"] = new Dictionary<string, object?>
            {
                ["prompt"] = "Select deployment style",
                ["options"] = doc.RootElement.Clone()
            }
        };

        var result = await dispatcher.ExecuteAsync("ask", args);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Select deployment style", captured[0].Prompt);
        Assert.NotNull(captured[0].Options);
        Assert.Equal(2, captured[0].Options!.Count);
        Assert.Equal("Blue", captured[0].Options![0]);
        Assert.Equal("Green", captured[0].Options![1]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithOptionsJson_ParsesOptionsAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select caching layer",
                    "optionsJson": "[\"Redis\", \"Memory\"]"
                }
            ]
        }
        """;
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Select caching layer", captured[0].Prompt);
        Assert.NotNull(captured[0].Options);
        Assert.Equal(2, captured[0].Options!.Count);
        Assert.Equal("Redis", captured[0].Options![0]);
        Assert.Equal("Memory", captured[0].Options![1]);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithTask31Task41Pattern_MissingClosingBracket_AutoFixesAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """
        {
            "questions": "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\", \"D\"]}"
        }
        """;
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        Assert.Equal("Q1", captured[0].Prompt);
        Assert.Equal("Q2", captured[1].Prompt);
        Assert.Equal(2, captured[1].Options?.Count);
    }

    [Fact]
    public async Task ExecuteAsync_AskWithTruncatedRawArgumentsJson_AutoFixesAndInvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = """{"questions": [{"prompt": "Proceed?"}""";
        var result = await dispatcher.ExecuteAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured!);
        Assert.Equal("Proceed?", captured[0].Prompt);
    }

    [Fact]
    public async Task ExecuteAsync_RequestChangesWithCritiqueAndComment_ThrowsDomainException()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        string? fb = null;
        string? reason = null;
        tools.RequestChangesHandler = (f, r) =>
        {
            fb = f;
            reason = r;
        };

        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var json = "{\"critique\": \"Tests failed\", \"comment\": \"Check logs\"}";

        var ex = await Assert.ThrowsAsync<NightCodeForge.Core.Workflow.RequestChangesException>(() =>
            dispatcher.ExecuteAsync("request_changes", json));

        Assert.Equal("Tests failed", ex.Feedback);
        Assert.Equal("Check logs", ex.Reason);
        Assert.Equal("Tests failed", fb);
        Assert.Equal("Check logs", reason);
    }

    [Fact]
    public async Task ExecuteAsync_EditWithBlankEditsAndShortcut_AppliesShortcutEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = NightCodeForgeToolDispatcher.Create(canonical);

            var json = "{\"path\": \"sample.txt\", \"edits\": [{}], \"old_text\": \"bar\", \"new_text\": \"qux\"}";
            var result = await dispatcher.ExecuteAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result?.ToString() ?? string.Empty);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task executeAsync_ProposeTasks_WhenTitleIsNullAndNameIsProvided_UsesNameAlias()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);

        var json = "{\"title\": null, \"name\": \"Fallback Name\", \"description\": \"Valid description\"}";
        await dispatcher.ExecuteAsync("propose_tasks", json);

        var proposed = Assert.Single(tools.PendingProposals);
        Assert.Equal("Fallback Name", proposed.Title);
    }

    [Fact]
    public async Task LenientToolDispatchChatClient_GetResponseAsync_RewritesHallucinatedToolAndParameterAliases()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools, dispatcher: dispatcher).Cast<AITool>().ToList();

        var fakeInner = new FakeHallucinatingChatClient(
            new FunctionCallContent(
                "call-1",
                "replace_file_content",
                new Dictionary<string, object?>
                {
                    ["file_path"] = "src/App.cs",
                    ["targetContent"] = "oldVal",
                    ["replacementContent"] = "newVal"
                }),
            new FunctionCallContent(
                "call-2",
                "list_dir",
                new Dictionary<string, object?>
                {
                    ["dir"] = "src"
                }));

        var client = new LenientToolDispatchChatClient(fakeInner, dispatcher);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Update App.cs")],
            new ChatOptions { Tools = aiTools });

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);

        Assert.Equal("edit", calls[0].Name);
        Assert.Equal("src/App.cs", calls[0].Arguments?["path"]);
        Assert.True(calls[0].Arguments?.ContainsKey("edits"));

        Assert.Equal("bash", calls[1].Name);
        Assert.Equal("ls -la 'src'", calls[1].Arguments?["command"]);
    }

    [Fact]
    public async Task LenientToolDispatchChatClient_GetStreamingResponseAsync_RewritesHallucinatedBacklogAliases()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        var dispatcher = NightCodeForgeToolDispatcher.Create(tools);
        var aiTools = CanonicalAIFunctionFactory.CreateTools(tools, dispatcher: dispatcher).Cast<AITool>().ToList();

        var fakeInner = new FakeHallucinatingChatClient(
            new FunctionCallContent(
                "call-1",
                "update_task",
                new Dictionary<string, object?>
                {
                    ["id"] = "#42",
                    ["name"] = "Refined task"
                }),
            new FunctionCallContent(
                "call-2",
                "reorder_tasks",
                new Dictionary<string, object?>
                {
                    ["items"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["id"] = "#7", ["prio"] = "1" }
                    }
                }));

        var client = new LenientToolDispatchChatClient(fakeInner, dispatcher);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Update backlog")],
            new ChatOptions { Tools = aiTools }))
        {
            updates.Add(update);
        }

        var calls = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);

        Assert.Equal("edit_task", calls[0].Name);
        Assert.Equal(42, calls[0].Arguments?["task_id"]);
        Assert.Equal("Refined task", calls[0].Arguments?["title"]);

        Assert.Equal("rework_priorities", calls[1].Name);
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(calls[1].Arguments?["updates"]);
        var first = Assert.IsAssignableFrom<IDictionary<string, object?>>(items.First());
        Assert.Equal(7, first["taskId"]);
        Assert.Equal(1, first["priority"]);
    }

    private sealed class FakeHallucinatingChatClient(params FunctionCallContent[] toolCalls) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("FakeHallucinatingChatClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var msg = new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList());
            return Task.FromResult(new ChatResponse([msg]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList());
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
