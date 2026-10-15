using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Tools;

namespace NightCodeForge.Agents.Tests;

public class LenientToolDispatcherTests
{
    private readonly LenientToolDispatcher _dispatcher;

    public LenientToolDispatcherTests()
    {
        var canonical = new CanonicalTools(Path.GetTempPath(), new NightCodeForge.Infrastructure.Execution.WorkspacePathResolver());
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
}
