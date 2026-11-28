using System.Text.Json;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class QuestionItemParserTests
{
    [Fact]
    public void Parse_WithNullOrWhitespace_ReturnsFallback()
    {
        var items1 = QuestionItemParser.Parse(null, "Please choose an option");
        Assert.Single(items1);
        Assert.Equal("Please choose an option", items1[0].Prompt);

        var items2 = QuestionItemParser.Parse("", null);
        Assert.Single(items2);
        Assert.Equal("Clarification requested", items2[0].Prompt);
    }

    [Fact]
    public void Parse_WithInvalidJson_ReturnsFallbackOrRawContent()
    {
        var items1 = QuestionItemParser.Parse("not-a-valid-json", "Fallback prompt");
        Assert.Single(items1);
        Assert.Equal("Fallback prompt", items1[0].Prompt);

        var items2 = QuestionItemParser.Parse("just a raw string prompt", null);
        Assert.Single(items2);
        Assert.Equal("just a raw string prompt", items2[0].Prompt);
    }

    [Fact]
    public void Parse_WithQuestionsArray_ParsesItemsWithRationaleAndOptions()
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

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Which DB should we target?", questions[0].Prompt);
        Assert.Equal("Schema requirements", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(3, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);

        Assert.Equal("Provide staging URL:", questions[1].Prompt);
        Assert.Null(questions[1].Rationale);
        Assert.Null(questions[1].Options);
    }

    [Fact]
    public void Parse_WithSinglePromptObject_ParsesSingleQuestion()
    {
        var json = """
        {
            "prompt": "Should we proceed with migration?",
            "options": ["Yes", "No"]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Should we proceed with migration?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
    }

    [Fact]
    public void Parse_WithStringifiedQuestionsArray_ParsesCorrectly()
    {
        var json = """
        {
            "questions": "[{\"prompt\": \"Select database engine\", \"rationale\": \"Storage design\", \"options\": [\"PostgreSQL\", \"SQLite\"]}]"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select database engine", questions[0].Prompt);
        Assert.Equal("Storage design", questions[0].Rationale);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("PostgreSQL", questions[0].Options![0]);
        Assert.Equal("SQLite", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithStringifiedSingleQuestionObject_ParsesCorrectly()
    {
        var json = """
        {
            "questions": "{\"prompt\": \"Confirm deploy to staging?\", \"options\": [\"Yes\", \"No\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm deploy to staging?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Yes", questions[0].Options![0]);
        Assert.Equal("No", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithStringifiedNestedOptions_ParsesOptions()
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

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select target environment", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Staging", questions[0].Options![0]);
        Assert.Equal("Prod", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithOptionsJsonProperty_ParsesOptions()
    {
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

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select caching layer", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Redis", questions[0].Options![0]);
        Assert.Equal("Memory", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithQuestionObjectProperty_ParsesPrompt()
    {
        var json = """
        {
            "question": {
                "prompt": "Confirm database rollback?",
                "options": ["Proceed", "Abort"]
            }
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm database rollback?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Proceed", questions[0].Options![0]);
        Assert.Equal("Abort", questions[0].Options![1]);
    }

    [Fact]
    public void Parse_WithQuestionStringContainingJsonObject_ParsesPrompt()
    {
        var json = """
        {
            "question": "{\"prompt\": \"Confirm database rollback?\", \"options\": [\"Proceed\", \"Abort\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Confirm database rollback?", questions[0].Prompt);
        Assert.NotNull(questions[0].Options);
        Assert.Equal(2, questions[0].Options!.Count);
        Assert.Equal("Proceed", questions[0].Options![0]);
        Assert.Equal("Abort", questions[0].Options![1]);
    }

    [Fact]
    public void ParseFromObject_WithTypedList_ReturnsList()
    {
        var original = new List<QuestionItem> { new("Prompt A") };
        var result = QuestionItemParser.ParseFromObject(original);
        Assert.Same(original, result);
    }

    [Fact]
    public void ParseFromObject_WithJsonElementArray_ReturnsItems()
    {
        using var doc = JsonDocument.Parse("""[{"prompt": "Pick one", "options": ["A", "B"]}]""");
        var result = QuestionItemParser.ParseFromObject(doc.RootElement);
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("Pick one", result[0].Prompt);
        Assert.Equal(2, result[0].Options?.Count);
    }

    [Fact]
    public void ExtractOptionsFromObject_WithVariousInputs_ReturnsExpected()
    {
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject(null));
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject(new List<string>()));
        Assert.Null(QuestionItemParser.ExtractOptionsFromObject("not a json array"));

        var list = QuestionItemParser.ExtractOptionsFromObject(new List<string> { "X", "Y" });
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);

        var fromString = QuestionItemParser.ExtractOptionsFromObject("[\"Option1\", \"Option2\"]");
        Assert.NotNull(fromString);
        Assert.Equal(2, fromString.Count);
        Assert.Equal("Option1", fromString[0]);

        using var doc = JsonDocument.Parse("[\"Alpha\", \"Beta\"]");
        var fromElement = QuestionItemParser.ExtractOptionsFromObject(doc.RootElement);
        Assert.NotNull(fromElement);
        Assert.Equal(2, fromElement.Count);
    }

    [Fact]
    public void Parse_WithStringifiedQuestionsArrayMissingClosingBracket_AutoFixesAndParsesAllQuestions()
    {
        // Repro Task 31 / Task 41
        var json = """
        {
            "questions": "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\", \"D\"]}"
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Q1", questions[0].Prompt);
        Assert.Equal("Q2", questions[1].Prompt);
        Assert.Equal(2, questions[1].Options?.Count);
    }

    [Fact]
    public void ParseFromObject_WithStringMissingClosingSquareBracket_AutoFixesAndParses()
    {
        var rawStr = "[{\"prompt\": \"Q1\"}, {\"prompt\": \"Q2\"}";
        var questions = QuestionItemParser.ParseFromObject(rawStr);

        Assert.NotNull(questions);
        Assert.Equal(2, questions!.Count);
        Assert.Equal("Q1", questions[0].Prompt);
        Assert.Equal("Q2", questions[1].Prompt);
    }

    [Fact]
    public void Parse_WithTruncatedStringifiedOptionsArray_RepairsAndExtractsOptions()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select target environment",
                    "options": "[\"Staging\", \"Prod\""
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Single(questions);
        Assert.Equal("Select target environment", questions[0].Prompt);
        Assert.Equal(["Staging", "Prod"], questions[0].Options);
    }

    [Fact]
    public void Parse_WithTruncatedDanglingKeyInQuestionObject_RepairsAndParsesQuestion()
    {
        var truncated = """{"questions": [{"prompt": "Which DB?", "options": ["SQLite", "Postgres"], "ration""";

        var questions = QuestionItemParser.Parse(truncated);

        Assert.Single(questions);
        Assert.Equal("Which DB?", questions[0].Prompt);
        Assert.Equal(["SQLite", "Postgres"], questions[0].Options);
    }

    [Fact]
    public void Parse_WithDoubleNestedJsonArrayInPrompt_PreservesAllQuestions()
    {
        // Live repro: Task 66 double-nested JSON prompt
        var json = """
        {
            "questions": [
                {
                    "prompt": "[{\"prompt\": \"Where should recommended.json persist?\", \"rationale\": \"Persistence design\", \"options\": [\"Repo root\", \"Crucible dir\"]}, {\"prompt\": \"What should the semantics of default be?\", \"rationale\": \"Behavior\", \"options\": [\"Opt A\", \"Opt B\"]}]"
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        Assert.Equal(2, questions.Count);
        Assert.Equal("Where should recommended.json persist?", questions[0].Prompt);
        Assert.Equal("Persistence design", questions[0].Rationale);
        Assert.Equal(["Repo root", "Crucible dir"], questions[0].Options);

        Assert.Equal("What should the semantics of default be?", questions[1].Prompt);
        Assert.Equal("Behavior", questions[1].Rationale);
        Assert.Equal(["Opt A", "Opt B"], questions[1].Options);
    }

    [Fact]
    public void Normalize_WithNestedJsonPrompt_FlattensIntoDistinctQuestions()
    {
        var rawPrompt = "[{\"prompt\": \"Q1\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Q2\", \"options\": [\"C\"]}]";
        var input = new List<QuestionItem>
        {
            new(rawPrompt)
        };

        var normalized = QuestionItemParser.Normalize(input);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("Q1", normalized[0].Prompt);
        Assert.Equal(["A", "B"], normalized[0].Options);
        Assert.Equal("Q2", normalized[1].Prompt);
        Assert.Equal(["C"], normalized[1].Options);
    }

    [Fact]
    public void Normalize_WithCleanItems_ReturnsSameListWithoutAllocation()
    {
        var input = new List<QuestionItem>
        {
            new("Clean prompt 1", null, new[] { "A", "B" }),
            new("Clean prompt 2")
        };

        var normalized = QuestionItemParser.Normalize(input);

        Assert.Same(input, normalized);
    }

    [Fact]
    public void ToQuestionItems_WithDoubleNestedPromptEntity_UnpacksBothQuestions()
    {
        var entity = new Question
        {
            Id = 129,
            TaskId = 66,
            Prompt = "[{\"prompt\": \"Where to persist?\", \"rationale\": \"Design\", \"options\": [\"A\", \"B\"]}, {\"prompt\": \"Semantics?\", \"options\": [\"1\", \"2\"]}]",
            Rationale = "",
            OptionsJson = null
        };

        var items = QuestionItemParser.ToQuestionItems(entity);

        Assert.Equal(2, items.Count);
        Assert.Equal("Where to persist?", items[0].Prompt);
        Assert.Equal("Design", items[0].Rationale);
        Assert.Equal(["A", "B"], items[0].Options);

        Assert.Equal("Semantics?", items[1].Prompt);
        Assert.Equal(["1", "2"], items[1].Options);
    }

    [Fact]
    public void ToQuestionItems_WithCleanEntity_ReturnsMappedItem()
    {
        var entity = new Question
        {
            Id = 1,
            TaskId = 10,
            Prompt = "Target runtime?",
            Rationale = "Configuration",
            OptionsJson = "[\"net9.0\", \"net10.0\"]"
        };

        var items = QuestionItemParser.ToQuestionItems(entity);

        Assert.Single(items);
        Assert.Equal("Target runtime?", items[0].Prompt);
        Assert.Equal("Configuration", items[0].Rationale);
        Assert.Equal(["net9.0", "net10.0"], items[0].Options);
    }

    [Fact]
    public void Parse_WithObjectOptionsContainingLabelAndDescription_FormatsHumanReadableStrings()
    {
        var json = """
        {
            "questions": [
                {
                    "prompt": "Select request timeout",
                    "rationale": "Retry budget",
                    "options": [
                        { "label": "5 seconds", "description": "5000 ms" },
                        { "label": "10 seconds", "description": "" },
                        { "label": "30 seconds" },
                        "Custom value"
                    ]
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        var q = Assert.Single(questions);
        Assert.Equal("Select request timeout", q.Prompt);
        Assert.Equal("Retry budget", q.Rationale);
        Assert.Equal(
            ["5 seconds — 5000 ms", "10 seconds", "30 seconds", "Custom value"],
            q.Options);
    }

    [Fact]
    public void ParseFromObject_WithMixedObjectListOptionsAndBlanks_NormalizesCleanly()
    {
        var raw = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = "Which verbs?",
                ["rationale"] = "HTTP safety",
                ["options"] = new List<object?>
                {
                    "  Idempotent verbs only  ",
                    "   ",
                    new Dictionary<string, object?> { ["label"] = "All verbs", ["description"] = "Includes POST/PATCH" },
                    new Dictionary<string, object?> { ["label"] = "GET only", ["description"] = null }
                }
            }
        };

        var parsed = QuestionItemParser.ParseFromObject(raw);

        Assert.NotNull(parsed);
        var q = Assert.Single(parsed);
        Assert.Equal("Which verbs?", q.Prompt);
        Assert.Equal("HTTP safety", q.Rationale);
        Assert.Equal(
            ["Idempotent verbs only", "All verbs — Includes POST/PATCH", "GET only"],
            q.Options);
    }

    [Fact]
    public void Normalize_WithWhitespaceAndBlankOptions_TrimsAndFiltersWhilePreservingPromptAndRationale()
    {
        var input = new List<QuestionItem>
        {
            new("Choose mode", "Stability", ["  Fast  ", "", "  Safe ", "   "]),
            new("Blank options", "Notes", ["   "])
        };

        var normalized = QuestionItemParser.Normalize(input);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("Choose mode", normalized[0].Prompt);
        Assert.Equal("Stability", normalized[0].Rationale);
        Assert.Equal(["Fast", "Safe"], normalized[0].Options);

        Assert.Equal("Blank options", normalized[1].Prompt);
        Assert.Equal("Notes", normalized[1].Rationale);
        Assert.Null(normalized[1].Options);
    }

    [Fact]
    public void Parse_WithUpperCaseKeysAndQuestionFieldWithOptions_ParsesCaseInsensitivelyAndPreservesOptions()
    {
        var json = """
        {
            "Questions": [
                {
                    "QUESTION": "Which database should we use?",
                    "RATIONALE": "Persistence",
                    "OPTIONS": [
                        { "LABEL": "SQLite", "DESCRIPTION": "Embedded" },
                        { "Label": "Postgres", "Desc": "Server" }
                    ]
                }
            ]
        }
        """;

        var questions = QuestionItemParser.Parse(json);

        var q = Assert.Single(questions);
        Assert.Equal("Which database should we use?", q.Prompt);
        Assert.Equal("Persistence", q.Rationale);
        Assert.Equal(["SQLite — Embedded", "Postgres — Server"], q.Options);
    }

    [Fact]
    public void ParseAndParseFromObject_SingleObjectWithQuestionAndOptions_PreservesRationaleAndOptions()
    {
        var json = """
        {
            "question": "Select deployment target",
            "rationale": "Environment",
            "options": ["Staging", "Production"]
        }
        """;

        var fromJson = QuestionItemParser.Parse(json);
        var qJson = Assert.Single(fromJson);
        Assert.Equal("Select deployment target", qJson.Prompt);
        Assert.Equal("Environment", qJson.Rationale);
        Assert.Equal(["Staging", "Production"], qJson.Options);

        var dict = new Dictionary<string, object?>
        {
            ["question"] = "Select deployment target",
            ["rationale"] = "Environment",
            ["options"] = new List<object?> { "Staging", "Production" }
        };

        var fromDict = QuestionItemParser.ParseFromObject(dict);
        Assert.NotNull(fromDict);
        var qDict = Assert.Single(fromDict);
        Assert.Equal("Select deployment target", qDict.Prompt);
        Assert.Equal("Environment", qDict.Rationale);
        Assert.Equal(["Staging", "Production"], qDict.Options);
    }

    [Fact]
    public void ParseFromObject_WithStringDictionaryOptionsAndNestedNonScalars_FormatsWithoutTypeNames()
    {
        var raw = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = "Choose tier",
                ["options"] = new List<object?>
                {
                    new Dictionary<string, string> { ["label"] = "Fast", ["description"] = "Low latency" },
                    new Dictionary<string, object?>
                    {
                        ["label"] = "Balanced",
                        ["description"] = new Dictionary<string, object?> { ["nested"] = 1 }
                    },
                    new List<string> { "unexpected-nested-list" }
                }
            }
        };

        var parsed = QuestionItemParser.ParseFromObject(raw);

        Assert.NotNull(parsed);
        var q = Assert.Single(parsed);
        Assert.Equal(["Fast — Low latency", "Balanced"], q.Options);
    }

    [Fact]
    public void Normalize_WithEmptyOptionsCollection_NormalizesOptionsToNull()
    {
        var input = new List<QuestionItem>
        {
            new("Empty options", "Notes", Array.Empty<string>())
        };

        var normalized = QuestionItemParser.Normalize(input);

        var q = Assert.Single(normalized);
        Assert.Equal("Empty options", q.Prompt);
        Assert.Equal("Notes", q.Rationale);
        Assert.Null(q.Options);
    }

    [Fact]
    public void ParseFromObject_WithNonScalarPromptOrRationaleAndJsonNull_DoesNotEmitClrTypeNames()
    {
        using var nullDoc = System.Text.Json.JsonDocument.Parse("null");
        using var objDoc = System.Text.Json.JsonDocument.Parse("""{"nested": true}""");

        var invalidDict = new Dictionary<string, object?>
        {
            ["prompt"] = new Dictionary<string, object?> { ["bad"] = 1 },
            ["rationale"] = new List<object?> { "nested" }
        };
        Assert.Null(QuestionItemParser.ParseFromObject(invalidDict));

        var validWithJsonNullRationale = new Dictionary<string, object?>
        {
            ["prompt"] = "Select cache backend",
            ["rationale"] = nullDoc.RootElement,
            ["options"] = new List<object?>
            {
                new Dictionary<string, object?> { ["choice"] = "Redis", ["reason"] = "Shared cluster" },
                new Dictionary<string, object?> { ["mode"] = "InMemory" },
                new Dictionary<string, object?> { ["nestedOnly"] = objDoc.RootElement }
            }
        };

        var parsed = QuestionItemParser.ParseFromObject(validWithJsonNullRationale);
        Assert.NotNull(parsed);
        var item = Assert.Single(parsed);
        Assert.Equal("Select cache backend", item.Prompt);
        Assert.Null(item.Rationale);
        Assert.Equal(["Redis — Shared cluster", "InMemory"], item.Options);
    }

    [Fact]
    public void TryBindAskQuestions_PreservesBlankPromptEntries_AndRejectsMalformedQuestionItems()
    {
        var withBlankFirst = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = "   ",
                ["rationale"] = "Missing text",
                ["options"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["label"] = "5s", ["description"] = "Fast" }
                }
            },
            new Dictionary<string, object?>
            {
                ["prompt"] = "Which DB?",
                ["options"] = new List<object?> { "SQLite", "Postgres" }
            }
        };

        Assert.True(QuestionItemParser.TryBindAskQuestions(withBlankFirst, out var bound));
        Assert.NotNull(bound);
        Assert.Equal(2, bound.Count);
        Assert.Equal("   ", bound[0].Prompt);
        Assert.Equal(["5s — Fast"], bound[0].Options);
        Assert.Equal("Which DB?", bound[1].Prompt);

        var mixedInvalid = new List<object?>
        {
            new Dictionary<string, object?> { ["prompt"] = "Valid?" },
            123
        };
        Assert.False(QuestionItemParser.TryBindAskQuestions(mixedInvalid, out _));

        var invalidOptionsScalar = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = "Valid?",
                ["options"] = 123
            }
        };
        Assert.False(QuestionItemParser.TryBindAskQuestions(invalidOptionsScalar, out _));
    }

    [Fact]
    public void TryBindAskQuestions_AcceptsBracketOrBracePrefixedProse_AndRejectsMalformedJsonAndUnknownObjects()
    {
        Assert.True(QuestionItemParser.TryBindAskQuestions("[Auth] Should we use JWT or cookies?", out var rootProse));
        Assert.Equal("[Auth] Should we use JWT or cookies?", Assert.Single(rootProse!).Prompt);

        var arrayProse = new List<object?> { "[1] Which DB?", "{tenantId} route param: UUID or int?" };
        Assert.True(QuestionItemParser.TryBindAskQuestions(arrayProse, out var arrayBound));
        Assert.Equal(["[1] Which DB?", "{tenantId} route param: UUID or int?"], arrayBound!.Select(q => q.Prompt).ToArray());

        Assert.False(QuestionItemParser.TryBindAskQuestions("{\"prompt\": broken", out _));
        Assert.False(QuestionItemParser.TryBindAskQuestions("[{bad", out _));
        Assert.False(QuestionItemParser.TryBindAskQuestions("```json\nnot-json\n```", out _));

        using var unknownObjJson = JsonDocument.Parse("""[{"foo": "bar"}]""");
        Assert.False(QuestionItemParser.TryBindAskQuestions(unknownObjJson.RootElement, out _));

        var unknownDict = new List<object?> { new Dictionary<string, object?> { ["foo"] = "bar" } };
        Assert.False(QuestionItemParser.TryBindAskQuestions(unknownDict, out _));
    }

    [Fact]
    public void TryBindAskQuestions_And_ParseFromObject_SupportClrValueTypeOptionSequences()
    {
        var rawWithArray = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = "Pick retry count",
                ["options"] = new[] { 1, 2, 3 }
            },
            new Dictionary<string, object?>
            {
                ["prompt"] = "Pick timeout seconds",
                ["options"] = new List<int> { 5, 10 }
            }
        };

        Assert.True(QuestionItemParser.TryBindAskQuestions(rawWithArray, out var bound));
        Assert.NotNull(bound);
        Assert.Equal(2, bound.Count);
        Assert.Equal(["1", "2", "3"], bound[0].Options);
        Assert.Equal(["5", "10"], bound[1].Options);

        var parsed = QuestionItemParser.ParseFromObject(rawWithArray);
        Assert.NotNull(parsed);
        Assert.Equal(["1", "2", "3"], parsed[0].Options);
        Assert.Equal(["5", "10"], parsed[1].Options);
    }

    [Fact]
    public void RootQuestionProse_And_NullQuestionWithSiblingPrompt_PreserveSiblingsAcrossParsers()
    {
        // 1. Root "question" with bracketed prose preserves sibling options and rationale
        const string bracketedJson = """{"question": "[Auth] Use JWT or cookies?", "options": ["JWT", "Cookies"], "rationale": "Session design"}""";
        using var bracketedDoc = JsonDocument.Parse(bracketedJson);

        Assert.True(QuestionItemParser.TryBindAskQuestions(bracketedDoc.RootElement, out var boundFromEl));
        var boundItem = Assert.Single(boundFromEl!);
        Assert.Equal("[Auth] Use JWT or cookies?", boundItem.Prompt);
        Assert.Equal("Session design", boundItem.Rationale);
        Assert.Equal(["JWT", "Cookies"], boundItem.Options);

        var bracketedDict = new Dictionary<string, object?>
        {
            ["question"] = "[Auth] Use JWT or cookies?",
            ["options"] = new[] { "JWT", "Cookies" },
            ["rationale"] = "Session design"
        };
        Assert.True(QuestionItemParser.TryBindAskQuestions(bracketedDict, out var boundFromDict));
        Assert.Equal(boundItem.Prompt, Assert.Single(boundFromDict!).Prompt);
        Assert.Equal(["JWT", "Cookies"], boundFromDict![0].Options);
        Assert.Equal("Session design", boundFromDict[0].Rationale);

        var parsedFromObj = QuestionItemParser.ParseFromObject(bracketedDict);
        Assert.Single(parsedFromObj!);
        Assert.Equal(["JWT", "Cookies"], parsedFromObj![0].Options);
        Assert.Equal("Session design", parsedFromObj[0].Rationale);

        var parsedFromJson = QuestionItemParser.ParseFromJsonElement(bracketedDoc.RootElement);
        Assert.Single(parsedFromJson);
        Assert.Equal(["JWT", "Cookies"], parsedFromJson[0].Options);
        Assert.Equal("Session design", parsedFromJson[0].Rationale);

        // 2. Root "question" with braced prose preserves sibling options
        using var bracedDoc = JsonDocument.Parse("""{"question": "{env} default to prod?", "options": ["Yes", "No"]}""");
        Assert.True(QuestionItemParser.TryBindAskQuestions(bracedDoc.RootElement, out var bracedBound));
        Assert.Equal("{env} default to prod?", Assert.Single(bracedBound!).Prompt);
        Assert.Equal(["Yes", "No"], bracedBound![0].Options);

        // 3. Single-item JSON encoded inside "question" merges enclosing options and rationale
        using var nestedDoc = JsonDocument.Parse("""{"question": "{\"prompt\": \"Use JWT?\"}", "options": ["JWT", "Cookies"], "rationale": "Session"}""");
        Assert.True(QuestionItemParser.TryBindAskQuestions(nestedDoc.RootElement, out var nestedBound));
        var nestedItem = Assert.Single(nestedBound!);
        Assert.Equal("Use JWT?", nestedItem.Prompt);
        Assert.Equal("Session", nestedItem.Rationale);
        Assert.Equal(["JWT", "Cookies"], nestedItem.Options);

        var nestedObj = new Dictionary<string, object?>
        {
            ["question"] = """{"prompt": "Use JWT?"}""",
            ["options"] = new[] { "JWT", "Cookies" },
            ["rationale"] = "Session"
        };
        var parsedNestedObj = QuestionItemParser.ParseFromObject(nestedObj);
        Assert.Single(parsedNestedObj!);
        Assert.Equal(["JWT", "Cookies"], parsedNestedObj![0].Options);
        Assert.Equal("Session", parsedNestedObj[0].Rationale);

        // 4. "question": null with sibling "prompt" must not shadow "prompt"
        using var nullQuestionDoc = JsonDocument.Parse("""{"question": null, "prompt": "Real question?"}""");
        Assert.True(QuestionItemParser.TryBindAskQuestions(nullQuestionDoc.RootElement, out var nullQuestionBound));
        Assert.Equal("Real question?", Assert.Single(nullQuestionBound!).Prompt);

        var nullQuestionDict = new Dictionary<string, object?>
        {
            ["question"] = null,
            ["prompt"] = "Real question?"
        };
        Assert.True(QuestionItemParser.TryBindAskQuestions(nullQuestionDict, out var nullQuestionDictBound));
        Assert.Equal("Real question?", Assert.Single(nullQuestionDictBound!).Prompt);

        // 5. "question": null with sibling "items" must not shadow "items"
        using var nullQuestionItemsDoc = JsonDocument.Parse("""{"question": null, "items": [{"prompt": "From items?"}]}""");
        Assert.True(QuestionItemParser.TryBindAskQuestions(nullQuestionItemsDoc.RootElement, out var nullItemsBound));
        Assert.Equal("From items?", Assert.Single(nullItemsBound!).Prompt);

        var nullQuestionItemsDict = new Dictionary<string, object?>
        {
            ["question"] = null,
            ["items"] = new[] { new Dictionary<string, object?> { ["prompt"] = "From items?" } }
        };
        Assert.True(QuestionItemParser.TryBindAskQuestions(nullQuestionItemsDict, out var nullItemsDictBound));
        Assert.Equal("From items?", Assert.Single(nullItemsDictBound!).Prompt);

        // 6. List of dictionaries with single-item nested JSON merges outer options/rationale,
        //    and blank inner rationale/empty inner options fall back to outer values.
        var listWithNestedDict = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["prompt"] = """{"prompt": "Nested Q?", "rationale": "", "options": []}""",
                ["options"] = new[] { "Opt1", "Opt2" },
                ["rationale"] = "Outer rationale"
            }
        };
        var parsedListObj = QuestionItemParser.ParseFromObject(listWithNestedDict);
        var mergedListItem = Assert.Single(parsedListObj!);
        Assert.Equal("Nested Q?", mergedListItem.Prompt);
        Assert.Equal("Outer rationale", mergedListItem.Rationale);
        Assert.Equal(["Opt1", "Opt2"], mergedListItem.Options);

        using var blankInnerDoc = JsonDocument.Parse("""{"question": "{\"prompt\": \"Use JWT?\", \"rationale\": \"\", \"options\": []}", "options": ["JWT", "Cookies"], "rationale": "Session"}""");
        Assert.True(QuestionItemParser.TryBindAskQuestions(blankInnerDoc.RootElement, out var blankInnerBound));
        var blankInnerItem = Assert.Single(blankInnerBound!);
        Assert.Equal("Session", blankInnerItem.Rationale);
        Assert.Equal(["JWT", "Cookies"], blankInnerItem.Options);
    }
}
