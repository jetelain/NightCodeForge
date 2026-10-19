using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class LenientToolDispatcherTests
{
    private readonly LenientToolDispatcher _dispatcher;

    public LenientToolDispatcherTests()
    {
        var canonical = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        _dispatcher = new LenientToolDispatcher(canonical);
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
    [InlineData("search_web", "web_search")]
    public void ResolveToolName_Aliases_MapToCanonical(string rawName, string expectedCanonical)
    {
        var resolved = _dispatcher.ResolveToolName(rawName);
        Assert.Equal(expectedCanonical, resolved);
    }

    [Fact]
    public void NormalizeCall_TranslatesListDir_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("list_dir", "{\"path\": \"src/Models\"}");
        Assert.Equal("bash", call.CanonicalName);
        Assert.Equal("ls -la src/Models", call.Arguments["command"]);
    }

    [Fact]
    public void NormalizeCall_TranslatesGrepSearch_ToBashCommand()
    {
        var call = _dispatcher.NormalizeCall("grep_search", "{\"query\": \"OrderService\", \"path\": \"src\"}");
        Assert.Equal("bash", call.CanonicalName);
        Assert.Equal("rg -n \"OrderService\" src", call.Arguments["command"]);
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
        Assert.Equal("edit", call.CanonicalName);
        Assert.Equal("src/Program.cs", call.Arguments["path"]);

        Assert.True(call.Arguments.ContainsKey("edits"));
        var edits = Assert.IsAssignableFrom<IEnumerable<object?>>(call.Arguments["edits"]);
        var first = Assert.IsAssignableFrom<Dictionary<string, object?>>(edits.First());
        Assert.Equal("int x = 1;", first["oldText"]);
        Assert.Equal("int x = 2;", first["newText"]);
    }

    [Fact]
    public void NormalizeCall_StripsMarkdownFences_FromPayload()
    {
        var fencedJson = "```json\n{\n  \"filePath\": \"test.txt\",\n  \"offset\": \"10\",\n  \"limit\": \"20\"\n}\n```";
        var call = _dispatcher.NormalizeCall("read_file", fencedJson);

        Assert.Equal("read", call.CanonicalName);
        Assert.Equal("test.txt", call.Arguments["path"]);
        Assert.Equal(10, call.Arguments["offset"]);
        Assert.Equal(20, call.Arguments["limit"]);
    }

    [Fact]
    public void SanitizeJsonPayload_RepairsUnescapedNewlines_InCodeStrings()
    {
        var brokenJson = "{\n  \"command\": \"echo hello\necho world\"\n}";
        var sanitized = LenientToolDispatcher.SanitizeJsonPayload(brokenJson);

        Assert.DoesNotContain("\necho world", sanitized);
        Assert.Contains("\\necho world", sanitized);
    }

    [Fact]
    public async Task RegisterTool_CustomMcpTool_DispatchesSuccessfully()
    {
        bool toolExecuted = false;
        var customDef = new ToolDefinition(
            name: "deploy_service",
            description: "Deploys a specified service",
            handler: (args, ct) =>
            {
                toolExecuted = true;
                return Task.FromResult("Deployment triggered: " + args["service"]);
            },
            aliases: new[] { "deploy", "ship_it" });

        _dispatcher.RegisterTool(customDef);

        Assert.Equal("deploy_service", _dispatcher.ResolveToolName("deploy"));
        Assert.Equal("deploy_service", _dispatcher.ResolveToolName("ship_it"));

        var result = await _dispatcher.DispatchAsync("ship_it", "{\"service\": \"auth-api\"}");
        Assert.True(toolExecuted);
        Assert.Equal("Deployment triggered: auth-api", result);
    }

    [Fact]
    public void ToolDefinition_Constructors_InitializeCorrectly()
    {
        var def3 = new ToolDefinition("t1", "desc1", (args, ct) => Task.FromResult("ok"));
        Assert.Equal("t1", def3.Name);
        Assert.Equal("desc1", def3.Description);
        Assert.Empty(def3.Aliases);
        Assert.Empty(def3.ParameterAliases);

        var def4 = new ToolDefinition("t2", "desc2", (args, ct) => Task.FromResult("ok"), new[] { "alias1" });
        Assert.Single(def4.Aliases);
        Assert.Equal("alias1", def4.Aliases[0]);

        var paramAliases = new Dictionary<string, string> { ["src"] = "source" };
        var def5 = new ToolDefinition("t3", "desc3", (args, ct) => Task.FromResult("ok"), new[] { "a1", "a2" }, paramAliases);
        Assert.Equal(2, def5.Aliases.Count);
        Assert.True(def5.ParameterAliases.ContainsKey("src"));
        Assert.Equal("source", def5.ParameterAliases["src"]);
    }

    [Fact]
    public void RegisterTranslation_CustomRule_TranslatesArguments()
    {
        _dispatcher.RegisterTranslation(new ToolTranslationRule(
            sourceTool: "query_database",
            targetTool: "bash",
            transform: args =>
            {
                var sql = args.TryGetValue("sql", out var s) ? s?.ToString() : "SELECT 1";
                return new Dictionary<string, object?> { ["command"] = $"psql -c \"{sql}\"" };
            }));

        var call = _dispatcher.NormalizeCall("query_database", "{\"sql\": \"SELECT * FROM users;\"}");
        Assert.Equal("bash", call.CanonicalName);
        Assert.Equal("psql -c \"SELECT * FROM users;\"", call.Arguments["command"]);
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
        Assert.Equal("edit", call.CanonicalName);
        Assert.Equal("src/Program.cs", call.Arguments["path"]);

        Assert.True(call.Arguments.ContainsKey("edits"));
        var edits = Assert.IsAssignableFrom<IEnumerable<object?>>(call.Arguments["edits"]);
        var first = Assert.IsAssignableFrom<Dictionary<string, object?>>(edits.First());
        Assert.Equal("int x = 1;", first["old_text"]);
        Assert.Equal("int x = 2;", first["new_text"]);
    }

    [Fact]
    public async Task DispatchAsync_EditWithSnakeCaseShortcut_AppliesEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = new LenientToolDispatcher(canonical);

            var json = "{\"path\": \"sample.txt\", \"old_text\": \"bar\", \"new_text\": \"qux\"}";
            var result = await dispatcher.DispatchAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DispatchAsync_EditWithCamelCaseShortcut_AppliesEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = new LenientToolDispatcher(canonical);

            var json = "{\"path\": \"sample.txt\", \"oldText\": \"bar\", \"newText\": \"qux\"}";
            var result = await dispatcher.DispatchAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DispatchAsync_EditWithMissingParameters_ReturnsUnifiedErrorMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = new LenientToolDispatcher(canonical);

            var json = "{\"path\": \"sample.txt\"}";
            var result = await dispatcher.DispatchAsync("edit", json);

            Assert.Equal("Error: provide 'edits' (array of { oldText, newText }) or single-edit replacement ('oldText'/'newText' or 'old_text'/'new_text').", result);
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
        Assert.Equal("bash", callBash.CanonicalName);
        Assert.Equal("src/Web", callBash.Arguments["working_directory"]);

        var jsonSkill = "{\"skillName\": \"csharp-refactoring\"}";
        var callSkill = _dispatcher.NormalizeCall("load_skill", jsonSkill);
        Assert.Equal("load_skill", callSkill.CanonicalName);
        Assert.Equal("csharp-refactoring", callSkill.Arguments["skill_name"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_WriteCodeAlias()
    {
        var json = "{\"path\": \"src/Test.cs\", \"code\": \"public class Test {}\"}";
        var call = _dispatcher.NormalizeCall("write", json);
        Assert.Equal("write", call.CanonicalName);
        Assert.Equal("src/Test.cs", call.Arguments["path"]);
        Assert.Equal("public class Test {}", call.Arguments["content"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_AskQuestionItemsAndItems()
    {
        var json1 = "{\"question_items\": [{\"prompt\": \"Question 1\"}]}";
        var call1 = _dispatcher.NormalizeCall("ask", json1);
        Assert.Equal("ask", call1.CanonicalName);
        Assert.True(call1.Arguments.ContainsKey("questions"));

        var json2 = "{\"items\": [{\"prompt\": \"Question 2\"}]}";
        var call2 = _dispatcher.NormalizeCall("ask", json2);
        Assert.Equal("ask", call2.CanonicalName);
        Assert.True(call2.Arguments.ContainsKey("questions"));
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_RequestChangesCritiqueAndComment()
    {
        var json = "{\"critique\": \"Code does not compile\", \"comment\": \"Syntax error on line 42\"}";
        var call = _dispatcher.NormalizeCall("request_changes", json);
        Assert.Equal("request_changes", call.CanonicalName);
        Assert.Equal("Code does not compile", call.Arguments["feedback"]);
        Assert.Equal("Syntax error on line 42", call.Arguments["reason"]);
    }

    [Fact]
    public void NormalizeCall_NormalizesParameters_WebSearchQAndCount()
    {
        var json = "{\"q\": \"aspnet core 10 minimal apis\", \"count\": 8}";
        var call = _dispatcher.NormalizeCall("web_search", json);
        Assert.Equal("web_search", call.CanonicalName);
        Assert.Equal("aspnet core 10 minimal apis", call.Arguments["query"]);
        Assert.Equal(8, call.Arguments["max_results"]);
    }

    [Fact]
    public async Task DispatchAsync_AskWithQuestionItems_InvokesAskHandler()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        IReadOnlyList<QuestionItem>? captured = null;
        tools.AskHandler = (questions, ct) =>
        {
            captured = questions;
            return Task.FromResult("Clarified");
        };

        var dispatcher = new LenientToolDispatcher(tools);
        var json = "{\"question_items\": [{\"prompt\": \"Use Redis or Postgres?\"}]}";
        var result = await dispatcher.DispatchAsync("ask", json);

        Assert.Equal("Clarified", result);
        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal("Use Redis or Postgres?", captured[0].Prompt);
    }

    [Fact]
    public async Task DispatchAsync_RequestChangesWithCritiqueAndComment_ThrowsDomainException()
    {
        var tools = TestCanonicalToolsBuilder.Create(Path.GetTempPath(), pathResolver: TestPathResolver.Create());
        string? fb = null;
        string? reason = null;
        tools.RequestChangesHandler = (f, r) =>
        {
            fb = f;
            reason = r;
        };

        var dispatcher = new LenientToolDispatcher(tools);
        var json = "{\"critique\": \"Tests failed\", \"comment\": \"Check logs\"}";

        var ex = await Assert.ThrowsAsync<NightCodeForge.Core.Workflow.RequestChangesException>(() =>
            dispatcher.DispatchAsync("request_changes", json));

        Assert.Equal("Tests failed", ex.Feedback);
        Assert.Equal("Check logs", ex.Reason);
        Assert.Equal("Tests failed", fb);
        Assert.Equal("Check logs", reason);
    }

    [Fact]
    public async Task DispatchAsync_EditWithBlankEditsAndShortcut_AppliesShortcutEdit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "nc_disp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "foo bar baz");

            var canonical = TestCanonicalToolsBuilder.Create(tempDir, pathResolver: TestPathResolver.Create());
            var dispatcher = new LenientToolDispatcher(canonical);

            var json = "{\"path\": \"sample.txt\", \"edits\": [{}], \"old_text\": \"bar\", \"new_text\": \"qux\"}";
            var result = await dispatcher.DispatchAsync("edit", json);

            Assert.Contains("Successfully applied 1 edit(s)", result);
            var content = await File.ReadAllTextAsync(filePath);
            Assert.Equal("foo qux baz", content.Trim());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

