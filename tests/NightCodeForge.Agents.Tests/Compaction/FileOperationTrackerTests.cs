using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Compaction;
using Xunit;

namespace NightCodeForge.Agents.Tests.Compaction;

public class FileOperationTrackerTests
{
    [Fact]
    public void RecordToolCall_ExtractsReadWriteAndEditedFiles()
    {
        var tracker = new FileOperationTracker();

        tracker.RecordToolCall("read", new Dictionary<string, object?> { ["path"] = "src/Core/Service.cs" });
        tracker.RecordToolCall("read_file", new Dictionary<string, object?> { ["filePath"] = "src/Core/Model.cs" });
        tracker.RecordToolCall("write", new Dictionary<string, object?> { ["target_file"] = "src/Core/NewFile.cs" });
        tracker.RecordToolCall("edit", new Dictionary<string, object?> { ["TargetFile"] = "src/Core/Service.cs" });

        var (readFiles, modifiedFiles) = tracker.ComputeFileLists();

        // Service.cs was both read and edited, so it should be categorized under modified
        Assert.Single(readFiles);
        Assert.Equal("src/Core/Model.cs", readFiles[0]);

        Assert.Equal(2, modifiedFiles.Count);
        Assert.Contains("src/Core/NewFile.cs", modifiedFiles);
        Assert.Contains("src/Core/Service.cs", modifiedFiles);
    }

    [Fact]
    public void FormatFileOperationsXml_RendersFormattedXmlBlocks()
    {
        var tracker = new FileOperationTracker();
        tracker.RecordToolCall("read", new Dictionary<string, object?> { ["path"] = "docs/README.md" });
        tracker.RecordToolCall("edit", new Dictionary<string, object?> { ["path"] = "src/Program.cs" });

        var xml = tracker.FormatFileOperationsXml();

        Assert.Contains("<read-files>\ndocs/README.md\n</read-files>", xml);
        Assert.Contains("<modified-files>\nsrc/Program.cs\n</modified-files>", xml);
    }

    [Fact]
    public void RecordMessages_ScansAllFunctionCallsInChatHistory()
    {
        var tracker = new FileOperationTracker();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Read foo.cs and edit bar.cs"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read", new Dictionary<string, object?> { ["path"] = "foo.cs" }),
                new FunctionCallContent("c2", "edit", new Dictionary<string, object?> { ["path"] = "bar.cs" })
            })
        };

        tracker.RecordMessages(messages);
        var (readFiles, modifiedFiles) = tracker.ComputeFileLists();

        Assert.Single(readFiles);
        Assert.Equal("foo.cs", readFiles[0]);
        Assert.Single(modifiedFiles);
        Assert.Equal("bar.cs", modifiedFiles[0]);
    }

    [Fact]
    public void ExtractFilePath_NormalizesMixedSeparators_AndTrimsQuotes()
    {
        var tracker = new FileOperationTracker();

        // Read using forward slash
        tracker.RecordToolCall("read", new Dictionary<string, object?> { ["path"] = "src/Domain/Entity.cs" });

        // Edit using Windows backslash and surrounding quotes
        tracker.RecordToolCall("edit", new Dictionary<string, object?> { ["path"] = "\"src\\Domain\\Entity.cs\"" });

        var (readFiles, modifiedFiles) = tracker.ComputeFileLists();

        // Entity.cs must be recognized as the same file and categorized ONLY under modified, not duplicated in read
        Assert.Empty(readFiles);
        Assert.Single(modifiedFiles);
        Assert.Equal("src/Domain/Entity.cs", modifiedFiles[0]);
    }
}
