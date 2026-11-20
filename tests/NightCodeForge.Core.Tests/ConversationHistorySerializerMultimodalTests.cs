using System.Text.Json;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ConversationHistorySerializerMultimodalTests
{
    [Fact]
    public void BuildVisualTurnMetadataJson_AndExtractors_RoundTripMetadata()
    {
        var imageRefs = new List<ConversationHistorySerializer.SerializedImageRef>
        {
            new("/workspace/attachments/001-ui.png", "image/png", "abcdef0123456789", 1280, 720, "call_img_1")
        };

        var json = ConversationHistorySerializer.BuildVisualTurnMetadataJson(
            imageRefs,
            isInjectedVisual: true,
            attachmentIds: [12, 12, 15],
            callId: "call_img_1");

        Assert.True(ConversationHistorySerializer.IsInjectedVisualTurn(json));
        Assert.Equal("call_img_1", ConversationHistorySerializer.ExtractCallId(json));
        Assert.Equal([12, 15], ConversationHistorySerializer.ExtractAttachmentIds(json));

        var parsedRefs = ConversationHistorySerializer.ExtractImageRefs(json);
        var parsed = Assert.Single(parsedRefs);
        Assert.Equal("/workspace/attachments/001-ui.png", parsed.SourcePath);
        Assert.Equal("image/png", parsed.MediaType);
        Assert.Equal("abcdef0123456789", parsed.Sha256);
        Assert.Equal(1280, parsed.WidthPx);
        Assert.Equal(720, parsed.HeightPx);
        Assert.Equal("call_img_1", parsed.CallId);
    }

    [Fact]
    public void Serialize_UserTurnWithImageRefs_EmitsImageRefWithoutBase64Payloads()
    {
        var upfrontMeta = ConversationHistorySerializer.BuildVisualTurnMetadataJson(
            [new ConversationHistorySerializer.SerializedImageRef("/workspace/attachments/001-bug.png", "image/png", "11223344", 1024, 768)],
            isInjectedVisual: false,
            attachmentIds: [1]);

        var injectedMeta = ConversationHistorySerializer.BuildVisualTurnMetadataJson(
            [new ConversationHistorySerializer.SerializedImageRef("/workspace/mockups/target.jpg", "image/jpeg", "99887766", 1568, 882, "call_r_2")],
            isInjectedVisual: true,
            callId: "call_r_2");

        var turns = new List<ConversationTurn>
        {
            new()
            {
                TurnIndex = 0,
                Role = StageTurnRole.User,
                Content = "Check this bug screenshot",
                MetadataJson = upfrontMeta
            },
            new()
            {
                TurnIndex = 1,
                Role = StageTurnRole.Tool,
                ToolName = CanonicalToolNames.Read,
                ToolArguments = "{\"path\":\"/workspace/mockups/target.jpg\"}",
                MetadataJson = "{\"CallId\":\"call_r_2\"}"
            },
            new()
            {
                TurnIndex = 2,
                Role = StageTurnRole.ToolResult,
                ToolName = CanonicalToolNames.Read,
                ToolResult = "Loaded image '/workspace/mockups/target.jpg' (1568x882, image/jpeg, ~1105 vision tokens).",
                MetadataJson = "{\"CallId\":\"call_r_2\"}"
            },
            new()
            {
                TurnIndex = 3,
                Role = StageTurnRole.User,
                Content = "[Visual Attachment: /workspace/mockups/target.jpg]",
                MetadataJson = injectedMeta
            }
        };

        var json = ConversationHistorySerializer.Serialize(turns);

        Assert.DoesNotContain("data:image", json);
        Assert.DoesNotContain("iVBOR", json);

        var items = JsonSerializer.Deserialize<List<ConversationHistorySerializer.SerializedMessageItem>>(json);
        Assert.NotNull(items);
        Assert.Equal(4, items!.Count);

        var upfrontImgRef = items[0].Contents?.Single(c => c.Type == ConversationHistorySerializer.ContentTypeImageRef);
        Assert.NotNull(upfrontImgRef);
        Assert.Equal("/workspace/attachments/001-bug.png", upfrontImgRef!.SourcePath);
        Assert.Equal("image/png", upfrontImgRef.MediaType);
        Assert.Equal("11223344", upfrontImgRef.Sha256);
        Assert.Equal(1024, upfrontImgRef.WidthPx);
        Assert.Equal(768, upfrontImgRef.HeightPx);

        var injectedImgRef = items[3].Contents?.Single(c => c.Type == ConversationHistorySerializer.ContentTypeImageRef);
        Assert.NotNull(injectedImgRef);
        Assert.Equal("/workspace/mockups/target.jpg", injectedImgRef!.SourcePath);
        Assert.Equal("image/jpeg", injectedImgRef.MediaType);
        Assert.Equal("99887766", injectedImgRef.Sha256);
    }
}
