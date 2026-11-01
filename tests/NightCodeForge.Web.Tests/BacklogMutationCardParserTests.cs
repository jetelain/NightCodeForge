using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class BacklogMutationCardParserTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("ask")]
    [InlineData(null)]
    public void Parse_NonBacklogTool_ReturnsNull(string? toolName)
    {
        Assert.Null(BacklogMutationCardParser.Parse(toolName, "{}", "ok", false, false));
    }

    [Fact]
    public void Parse_GetTask_ShowsTitleAndPriority()
    {
        var result = "{ \"id\": 14, \"title\": \"Add retry\", \"priority\": 2 }";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.GetTask, "{\"task_id\":14}", result, false, false);

        Assert.NotNull(card);
        Assert.Equal(BacklogMutationCardKind.Loaded, card.Kind);
        Assert.Equal("Loaded Task #14", card.Heading);
        Assert.Equal(14, card.TaskId);
        Assert.Contains("Add retry", card.Subtitle);
        Assert.Contains("P2", card.Subtitle);
    }

    [Fact]
    public void Parse_GetTask_WhileRunning_DegradesToArgumentsOnly()
    {
        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.GetTask, "{\"task_id\":14}", null, false, true);

        Assert.NotNull(card);
        Assert.True(card.IsRunning);
        Assert.Equal(14, card.TaskId);
        Assert.Null(card.Subtitle);
    }

    [Fact]
    public void Parse_EditTask_HighlightsModifiedFieldsFromResult()
    {
        var result = "Task #14 updated (description; priority P3 -> P2).";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.EditTask, "{\"task_id\":14,\"description\":\"x\",\"priority\":2}", result, false, false);

        Assert.NotNull(card);
        Assert.Equal(BacklogMutationCardKind.Updated, card.Kind);
        Assert.Equal("Updated Task #14", card.Heading);
        Assert.Equal(["Description updated", "Priority: P3 -> P2"], card.Highlights);
    }

    [Fact]
    public void Parse_EditTask_WithoutResult_FallsBackToArgumentFields()
    {
        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.EditTask, "{\"task_id\":3,\"title\":\"n\"}", null, false, true);

        Assert.NotNull(card);
        Assert.Equal(["Title updated"], card.Highlights);
    }

    [Fact]
    public void Parse_EditTask_Error_ExposesMessageAndNoHighlights()
    {
        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.EditTask, "{\"task_id\":3}", "Task #3 cannot be modified: it has already entered pipeline execution.", true, false);

        Assert.NotNull(card);
        Assert.True(card.IsError);
        Assert.Empty(card.Highlights);
        Assert.Contains("cannot be modified", card.ErrorMessage);
    }

    [Fact]
    public void Parse_SetTaskPriority_BuildsSingleChip()
    {
        var result = "Priorities updated for 1 task(s): #7: P3 -> P1.";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.SetTaskPriority, "{\"task_id\":7,\"priority\":1}", result, false, false);

        Assert.NotNull(card);
        Assert.Equal(BacklogMutationCardKind.Reprioritized, card.Kind);
        var chip = Assert.Single(card.Chips);
        Assert.Equal(7, chip.TaskId);
        Assert.Equal("#7 P3 -> P1", chip.Label);
        Assert.Equal(7, card.TaskId);
    }

    [Fact]
    public void Parse_ReworkPriorities_BuildsChipPerTask()
    {
        var result = "Priorities updated for 2 task(s): #1: P3 -> P2; #2: already P4.";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.ReworkPriorities, "{}", result, false, false);

        Assert.NotNull(card);
        Assert.Equal(["#1 P3 -> P2", "#2 already P4"], card.Chips.Select(c => c.Label));
        Assert.Null(card.TaskId);
    }

    [Fact]
    public void Parse_ReworkPriorities_WhileRunning_UsesUpdatesArgument()
    {
        var args = "{\"updates\":[{\"task_id\":1,\"priority\":2},{\"task_id\":5,\"priority\":4}]}";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.ReworkPriorities, args, null, false, true);

        Assert.NotNull(card);
        Assert.Equal(2, card.Chips.Count);
        Assert.Equal(5, card.Chips[1].TaskId);
    }

    [Fact]
    public void Parse_RemoveTask_ShowsReason()
    {
        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.RemoveTask, "{\"task_id\":18,\"reason\":\" duplicate of #4 \"}", "Task #18 removed (status Cancelled).", false, false);

        Assert.NotNull(card);
        Assert.Equal(BacklogMutationCardKind.Removed, card.Kind);
        Assert.Equal("Removed Task #18", card.Heading);
        Assert.Equal("duplicate of #4", card.Reason);
    }

    [Fact]
    public void Parse_MalformedArguments_DoesNotThrow()
    {
        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.RemoveTask, "{\"task_id\":1", null, false, true);

        Assert.NotNull(card);
        Assert.Null(card.TaskId);
        Assert.Equal("Removed Task (unknown)", card.Heading);
    }

    [Fact]
    public void Parse_EditTask_WithoutResult_IgnoresNullPropertiesAndSupportsPascalCaseTaskId()
    {
        var args = "{\"TaskId\": 9, \"title\": \"Renamed\", \"description\": null, \"priority\": null, \"depends_on_ids\": null}";

        var card = BacklogMutationCardParser.Parse(CanonicalToolNames.EditTask, args, null, false, true);

        Assert.NotNull(card);
        Assert.Equal(9, card.TaskId);
        Assert.Equal(["Title updated"], card.Highlights);
    }

    [Theory]
    [InlineData("read_task", BacklogMutationCardKind.Loaded)]
    [InlineData("update_task", BacklogMutationCardKind.Updated)]
    [InlineData("set_priority", BacklogMutationCardKind.Reprioritized)]
    [InlineData("rework_priority", BacklogMutationCardKind.Reprioritized)]
    [InlineData("delete_task", BacklogMutationCardKind.Removed)]
    public void Parse_SupportsBacklogToolAliases(string alias, BacklogMutationCardKind expectedKind)
    {
        Assert.True(BacklogMutationCardParser.IsBacklogMutationTool(alias));

        var card = BacklogMutationCardParser.Parse(alias, "{\"task_id\": 4, \"priority\": 2}", null, false, true);

        Assert.NotNull(card);
        Assert.Equal(expectedKind, card.Kind);
    }

    [Fact]
    public void Parse_DetectsErrorFromResultPrefix_EvenWhenIsErrorFlagIsFalse()
    {
        var card = BacklogMutationCardParser.Parse(
            CanonicalToolNames.EditTask,
            "{\"task_id\": 14, \"title\": \"New\"}",
            "Error: Task #14 cannot be modified: it has already entered pipeline execution.",
            isError: false,
            isRunning: false);

        Assert.NotNull(card);
        Assert.True(card.IsError);
        Assert.Empty(card.Highlights);
        Assert.Contains("cannot be modified", card.ErrorMessage);
    }

    [Fact]
    public void Parse_SupportsAliasPropertyNamesForReasonAndUpdates()
    {
        var removeCard = BacklogMutationCardParser.Parse(
            CanonicalToolNames.RemoveTask,
            "{\"TaskId\": 18, \"comment\": \"superseded by #20\"}",
            "Task #18 removed (status Cancelled).",
            isError: false,
            isRunning: false);
        Assert.NotNull(removeCard);
        Assert.Equal("superseded by #20", removeCard.Reason);

        var reprioritizeCard = BacklogMutationCardParser.Parse(
            CanonicalToolNames.ReworkPriorities,
            "{\"priorities\":[{\"taskId\":2,\"priority\":1}]}",
            null,
            isError: false,
            isRunning: true);
        Assert.NotNull(reprioritizeCard);
        var chip = Assert.Single(reprioritizeCard.Chips);
        Assert.Equal(2, chip.TaskId);
    }
}

