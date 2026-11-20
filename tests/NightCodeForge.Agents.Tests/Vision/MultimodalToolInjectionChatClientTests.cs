using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Agents.Tests.Vision;

public class MultimodalToolInjectionChatClientTests
{
    private static StagedVisualContent MakeVisual(string callId, string sourcePath, byte seed = 1) =>
        new(
            CallId: callId,
            SourcePath: sourcePath,
            MediaType: "image/png",
            WidthPx: 800,
            HeightPx: 600,
            Sha256: $"{seed:D2}{new string('a', 62)}",
            EstimatedTokens: VisionConstants.EstimateVisionTokens(800, 600),
            Data: new byte[] { 0x89, 0x50, 0x4E, 0x47, seed });

    [Fact]
    public async Task TransformMessages_GroupsParallelToolRunIntoSingleVisualUserMessage_AndPreservesKvPrefix()
    {
        var queue = new MultimodalAttachmentQueue();
        var v1 = MakeVisual("call_1", "/workspace/attachments/001-a.png", 1);
        var v2 = MakeVisual("call_2", "/workspace/attachments/002-b.png", 2);
        var v3 = MakeVisual("call_3", "docs/diagram.png", 3);
        queue.Stage(v1);
        queue.Stage(v2);

        var capturedCalls = new List<List<ChatMessage>>();
        int recordedSentCount = -1;

        var innerClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((msgs, _, _) =>
        {
            capturedCalls.Add(msgs.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Observed."))
            {
                Usage = new UsageDetails { InputTokenCount = 500, OutputTokenCount = 20 }
            });
        });

        var tracer = new ThinkingAndActivityTracingChatClient(innerClient);
        tracer.SetMeasuredUsageCallback(u => recordedSentCount = u.SentMessageCount);
        using var middleware = new MultimodalToolInjectionChatClient(tracer, queue);

        var turn1History = new List<ChatMessage>
        {
            new(ChatRole.User, "Inspect both images"),
            new(ChatRole.Assistant,
            [
                new FunctionCallContent("call_1", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = v1.SourcePath }),
                new FunctionCallContent("call_2", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = v2.SourcePath })
            ]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", VisionConstants.FormatReadImageToolResult(v1.SourcePath, 800, 600, "image/png", v1.EstimatedTokens))]),
            new(ChatRole.Tool, [new FunctionResultContent("call_2", VisionConstants.FormatReadImageToolResult(v2.SourcePath, 800, 600, "image/png", v2.EstimatedTokens))])
        };

        await middleware.GetResponseAsync(turn1History);

        // SentMessageCount reported back to CompactionProvider must compensate for the 1 injected visual message
        Assert.Equal(turn1History.Count, recordedSentCount);

        var call1Msgs = capturedCalls[0];
        Assert.Equal(5, call1Msgs.Count);
        Assert.Equal(ChatRole.User, call1Msgs[4].Role);
        Assert.Equal(2, call1Msgs[4].Contents.OfType<DataContent>().Count());
        // PropNewlyInjectedInMiddleware is tagged on initial middleware injection so tracer subtracts it from SentMessageCount
        Assert.True(call1Msgs[4].AdditionalProperties?.ContainsKey(MultimodalToolInjectionChatClient.PropNewlyInjectedInMiddleware) == true);

        // Turn 2: Simulate persisted visual turn followed by a new read tool call; prefix [0..4] must remain byte/instance identical!
        var persistedInjected = call1Msgs[4];
        queue.Stage(v3);

        var turn2History = new List<ChatMessage>(call1Msgs)
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call_3", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = v3.SourcePath })]),
            new(ChatRole.Tool, [new FunctionResultContent("call_3", VisionConstants.FormatReadImageToolResult(v3.SourcePath, 800, 600, "image/png", v3.EstimatedTokens))])
        };

        await middleware.GetResponseAsync(turn2History);

        var call2Msgs = capturedCalls[1];
        Assert.Equal(8, call2Msgs.Count);
        Assert.False(call2Msgs[4].AdditionalProperties?.ContainsKey(MultimodalToolInjectionChatClient.PropNewlyInjectedInMiddleware) == true);
        for (int i = 0; i < 4; i++)
        {
            Assert.Same(call1Msgs[i], call2Msgs[i]);
        }
        // Index 4 stayed the single restored/persisted visual message (never duplicated!)
        Assert.Single(call2Msgs[4].Contents.OfType<DataContent>(), dc => ((string?)dc.AdditionalProperties?[VisionConstants.PropCallId]) == "call_1");
        Assert.Equal(2, call2Msgs[4].Contents.OfType<DataContent>().Count());
        Assert.Single(call2Msgs[7].Contents.OfType<DataContent>());
    }

    [Fact]
    public void SlidingWindow_EvictsOldestDataContentBeyondCapWithDeterministicMarker()
    {
        var queue = new MultimodalAttachmentQueue();
        var history = new List<ChatMessage>();

        for (int i = 1; i <= VisionConstants.MaxImagesInActiveWindow + 2; i++)
        {
            var callId = $"call_{i}";
            var path = $"/workspace/attachments/img-{i}.png";
            var visual = MakeVisual(callId, path, (byte)i);
            queue.Stage(visual);

            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, CanonicalToolNames.Read)]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, VisionConstants.FormatReadImageToolResult(path, 800, 600, "image/png", visual.EstimatedTokens))]));
        }

        var transformed = MultimodalToolInjectionChatClient.TransformMessages(history, queue, VisionConstants.MaxImagesInActiveWindow);

        var liveImages = transformed
            .SelectMany(m => m.Contents ?? [])
            .OfType<DataContent>()
            .Where(dc => dc.HasTopLevelMediaType(VisionConstants.TopLevelImageMediaType))
            .ToList();

        Assert.Equal(VisionConstants.MaxImagesInActiveWindow, liveImages.Count);
        Assert.Equal("/workspace/attachments/img-3.png", liveImages[0].AdditionalProperties?[VisionConstants.PropSourcePath]);
        Assert.Equal("/workspace/attachments/img-7.png", liveImages[^1].AdditionalProperties?[VisionConstants.PropSourcePath]);

        var allTexts = transformed.SelectMany(m => m.Contents ?? []).OfType<TextContent>().Select(t => t.Text).ToList();
        Assert.Contains(allTexts, t => t.Contains(VisionConstants.FormatEvictedImageMarker("/workspace/attachments/img-1.png")));
        Assert.Contains(allTexts, t => t.Contains(VisionConstants.FormatEvictedImageMarker("/workspace/attachments/img-2.png")));
    }

    [Fact]
    public void ThinkingAndActivityTracingChatClient_DescribeMessage_FormatsRichImageSummary()
    {
        var visual = MakeVisual("call_desc", "/workspace/ui.png", 9);
        var msg = new ChatMessage(ChatRole.User, [visual.ToDataContent()]);

        var described = ThinkingAndActivityTracingChatClient.DescribeMessage(msg);

        Assert.Contains("[image: image/png, 800x600,", described);
        Assert.Contains("sha256:09aaaaaaaaaa]", described);
        Assert.DoesNotContain("[DataContent]", described);
    }

    [Fact]
    public async Task TracingClient_RecordNewlyInjectedVisualTurns_IgnoresRestoredVisualWithoutMiddlewareMarker()
    {
        var recordedVisualEntries = new List<Transcripts.TranscriptEntry>();
        var queue = new MultimodalAttachmentQueue();
        var freshVisual = MakeVisual("call_fresh", "/workspace/attachments/fresh.png", 4);
        var restoredVisual = MakeVisual("call_old", "/workspace/attachments/old.png", 5);
        queue.Stage(freshVisual);

        var inner = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."))));

        using var tracer = new ThinkingAndActivityTracingChatClient(
            inner,
            onEntry: e =>
            {
                if (!string.IsNullOrWhiteSpace(e.MetadataJson) &&
                    Core.Workflow.ConversationHistorySerializer.IsInjectedVisualTurn(e.MetadataJson))
                {
                    recordedVisualEntries.Add(e);
                }
            },
            activityStreamer: null,
            sessionId: null,
            taskId: 0,
            stageId: StandardStageIds.Implementation);

        using var middleware = new MultimodalToolInjectionChatClient(tracer, queue);

        var restoredHistoryMsg = new ChatMessage(
            ChatRole.User,
            [new TextContent(VisionConstants.FormatVisualHeader(restoredVisual.SourcePath, restoredVisual.CallId, 800, 600)), restoredVisual.ToDataContent()])
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [VisionConstants.PropIsInjectedVisual] = true,
                [VisionConstants.PropInjectedCallIds] = new[] { "call_old" }
            }
        };

        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call_old", CanonicalToolNames.Read)]),
            new(ChatRole.Tool, [new FunctionResultContent("call_old", VisionConstants.FormatReadImageToolResult(restoredVisual.SourcePath, 800, 600, "image/png", restoredVisual.EstimatedTokens))]),
            restoredHistoryMsg,
            new(ChatRole.Assistant, [new FunctionCallContent("call_fresh", CanonicalToolNames.Read)]),
            new(ChatRole.Tool, [new FunctionResultContent("call_fresh", VisionConstants.FormatReadImageToolResult(freshVisual.SourcePath, 800, 600, "image/png", freshVisual.EstimatedTokens))])
        };

        await middleware.GetResponseAsync(messages);

        var singleEntry = Assert.Single(recordedVisualEntries);
        var recordedRef = Assert.Single(Core.Workflow.ConversationHistorySerializer.ExtractImageRefs(singleEntry.MetadataJson));
        Assert.Equal("/workspace/attachments/fresh.png", recordedRef.SourcePath);
    }

    [Fact]
    public void MultimodalAttachmentQueue_ResolveForToolRun_DoesNotStealUnrelatedImage_AndReplaysUnkeyedAcrossTurns()
    {
        var queue = new MultimodalAttachmentQueue();
        var newImg = new StagedVisualContent(null, "/workspace/new.png", "image/png", 100, 100, new string('b', 64), 255, new byte[] { 1, 2 });
        queue.Stage(newImg);

        // Asking for an unrelated unkeyed source path must NOT steal new.png from the queue
        var unrelated = queue.ResolveForToolRun([(null, VisionConstants.FormatReadImageToolResult("/workspace/old.png", 100, 100, "image/png", 255))]);
        Assert.Empty(unrelated);

        // Turn 1 unkeyed match resolves and preserves new.png for Turn 2 replay
        var turn1 = queue.ResolveForToolRun([(null, VisionConstants.FormatReadImageToolResult("/workspace/new.png", 100, 100, "image/png", 255))]);
        Assert.Single(turn1);
        Assert.Equal("/workspace/new.png", turn1[0].SourcePath);

        var turn2 = queue.ResolveForToolRun([(null, VisionConstants.FormatReadImageToolResult("/workspace/new.png", 100, 100, "image/png", 255))]);
        Assert.Single(turn2);
        Assert.Equal("/workspace/new.png", turn2[0].SourcePath);
    }

    [Fact]
    public void StripImagesForTextOnlyModel_ReplacesImagesWithOmittedMarkerWithoutMutatingOriginals()
    {
        var visual = MakeVisual("call_strip", "/workspace/attachments/chart.png", 7);
        var originalMsg = new ChatMessage(ChatRole.User, [new TextContent("Analyze chart"), visual.ToDataContent()]);
        var list = new List<ChatMessage> { originalMsg };

        int omittedCount = MultimodalToolInjectionChatClient.StripImagesForTextOnlyModel(list);

        Assert.Equal(1, omittedCount);
        Assert.Single(originalMsg.Contents.OfType<DataContent>());
        Assert.Empty(list[0].Contents.OfType<DataContent>());
        var texts = list[0].Contents.OfType<TextContent>().Select(t => t.Text).ToList();
        Assert.Contains(VisionConstants.FormatVisionDisabledOmittedImageMarker("/workspace/attachments/chart.png"), texts);
    }
}
