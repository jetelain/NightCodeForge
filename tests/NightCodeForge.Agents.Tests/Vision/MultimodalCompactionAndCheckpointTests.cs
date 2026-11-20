using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NightCodeForge.Agents.Tests.Vision;

public class MultimodalCompactionAndCheckpointTests : IDisposable
{
    private readonly string _tempDir;

    public MultimodalCompactionAndCheckpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ncf-compaction-vision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height, new Rgba32(72, 61, 139));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public void ModelCalibratedTokenEstimator_MatchesMafComputeContentByteCountParity_AndUsesTileEstimate()
    {
        // Create a large raw byte buffer (e.g. 200 KB) carrying a valid 1024x1024 PNG header
        // MAF's raw ByteCount / 4 would estimate ~50,000 tokens if ComputeActualMessageBytes didn't match!
        var basePng = CreatePngBytes(1024, 1024);
        var padded = new byte[200_000];
        basePng.CopyTo(padded, 0);

        var dataContent = new DataContent(padded, "image/png")
        {
            Name = "large-ui.png",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [VisionConstants.PropSourcePath] = "large-ui.png",
                [VisionConstants.PropWidthPx] = 1024,
                [VisionConstants.PropHeightPx] = 1024
            }
        };

        var msg = new ChatMessage(ChatRole.User, [dataContent]);
        var index = PiStyleCrucibleCompactionStrategy.CreateIndex([msg]);
        var group = Assert.Single(index.Groups);

        int expectedTilesTokens = ModelCalibratedTokenEstimator.PerMessageFramingTokens
            + VisionConstants.EstimateVisionTokens(1024, 1024);
        int estimated = ModelCalibratedTokenEstimator.EstimateGroupTokens(group, ModelCalibratedTokenEstimator.Resolve(null));

        // Must NOT fall back to ~50,000 raw byteCount / 4 tokens
        Assert.Equal(expectedTilesTokens, estimated);
        Assert.True(estimated < 1000);
    }

    [Fact]
    public void ConversationSerializer_RendersImageAsCompactTextPlaceholderWithoutRawBytes()
    {
        var data = new DataContent(CreatePngBytes(1280, 720), "image/png")
        {
            Name = "layout.png",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [VisionConstants.PropSourcePath] = "/workspace/attachments/layout.png",
                [VisionConstants.PropWidthPx] = 1280,
                [VisionConstants.PropHeightPx] = 720
            }
        };

        var transcript = ConversationSerializer.SerializeConversation(
            [new ChatMessage(ChatRole.User, [new TextContent("Compare header."), data])]);

        Assert.Contains("[Visual Attachment: /workspace/attachments/layout.png (1280x720, image/png)]", transcript);
        Assert.Contains("Compare header.", transcript);
        Assert.DoesNotContain("iVBOR", transcript);
    }

    [Fact]
    public async Task PiStyleCrucibleCompactionStrategy_EvictsExcessImagesAndPrunesQueue()
    {
        var summarizer = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "## Goal\nDone\n## Constraints & Preferences\n- None\n## Progress\n### Done\n- Checked screenshots\n### In Progress\n- Fix\n### Blocked\n- None\n## Key Decisions\n- None\n## Next Steps\n1. Finish\n## Critical Context\n- None"))));

        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 1600,
            ReserveTokens = 700,
            SafetyMarginTokens = 200,
            KeepRecentTokens = 400,
            SummarizerModelId = "qwen3-vl:8b"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, summarizer, NullLogger.Instance);
        var queue = new MultimodalAttachmentQueue();
        strategy.AttachMultimodalAttachmentQueue(queue);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('A', 2000))
        };

        for (int i = 1; i <= 6; i++)
        {
            var callId = $"call_img_{i}";
            var png = CreatePngBytes(512, 512);
            var staged = new StagedVisualContent(
                callId,
                $"/workspace/attachments/shot-{i}.png",
                "image/png",
                512,
                512,
                VisionImageNormalizer.ComputeSha256Hex(png),
                VisionConstants.EstimateVisionTokens(512, 512),
                png);
            queue.Stage(staged);

            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, CanonicalToolNames.Read)]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, VisionConstants.FormatReadImageToolResult(staged.SourcePath, 512, 512, "image/png", staged.EstimatedTokens))]));
            messages.Add(new ChatMessage(ChatRole.User, [new TextContent(VisionConstants.FormatVisualHeader(staged.SourcePath, callId, 512, 512)), staged.ToDataContent()])
            {
                AdditionalProperties = new AdditionalPropertiesDictionary { [VisionConstants.PropIsInjectedVisual] = true }
            });
        }

        // Trigger compaction using MAF index
        var index = PiStyleCrucibleCompactionStrategy.CreateIndex(messages);
        await strategy.CompactAsync(index, NullLogger.Instance);

        var retainedImages = index.Groups
            .Where(g => !g.IsExcluded)
            .SelectMany(g => g.Messages)
            .SelectMany(m => m.Contents ?? [])
            .OfType<DataContent>()
            .Count();

        Assert.True(retainedImages <= VisionConstants.MaxImagesInActiveWindow);
        // Queue was pruned down to only call IDs surviving in included messages
        Assert.True(queue.ResolveForToolRun([("call_img_1", VisionConstants.LoadedImageToolResultPrefix + "x")]).Count == 0);
    }

    [Fact]
    public async Task CrucibleAgentHarness_SerializeAndRehydrateSession_ZeroBase64_RestoresFromDiskAndHandlesDeletedFiles()
    {
        var imgPath = Path.Combine(_tempDir, "mockup.png");
        var rawBytes = CreatePngBytes(640, 480);
        await File.WriteAllBytesAsync(imgPath, rawBytes);
        var sha = VisionImageNormalizer.ComputeSha256Hex(rawBytes);

        var data = new DataContent(rawBytes, "image/png")
        {
            Name = "mockup.png",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [VisionConstants.PropSourcePath] = "mockup.png",
                [VisionConstants.PropSha256] = sha,
                [VisionConstants.PropWidthPx] = 640,
                [VisionConstants.PropHeightPx] = 480,
                [VisionConstants.PropCallId] = "call_mock_1"
            }
        };

        var original = new List<ChatMessage>
        {
            new(ChatRole.User, [new TextContent("Review mockup"), data])
        };

        var stateJson = CrucibleAgentHarness.SerializeSession(original);
        Assert.DoesNotContain(Convert.ToBase64String(rawBytes), stateJson);
        Assert.Contains($"\"{ConversationHistorySerializer.ContentTypeImageRef}\"", stateJson);

        // Deferred rehydration with trackUnresolvedRefs + WorkspaceFileToolModule
        var mountTable = SandboxMountTable.ForWorkspace(_tempDir);
        var sandboxResolver = new SandboxPathResolver(
            SandboxScopeDescriptor.ForWorkspace(
                new Workspace { Id = 1, Name = "W", RepositoryPath = _tempDir },
                SandboxSecurityTier.Standard,
                taskId: null,
                mountTable: mountTable));
        var fileModule = new WorkspaceFileToolModule(
            new CanonicalToolScope(_tempDir, SandboxPathResolver: sandboxResolver, SupportsVision: true),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        var restored = CrucibleAgentHarness.DeserializeSession(stateJson, resolveHostPath: null, trackUnresolvedRefs: true, NullLogger.Instance);
        await CrucibleAgentHarness.RehydrateDeferredImageRefsAsync(restored, fileModule, NullLogger.Instance, CancellationToken.None);

        var restoredDc = Assert.Single(restored[0].Contents.OfType<DataContent>());
        Assert.Equal("image/png", restoredDc.MediaType);

        // Delete file and rehydrate again -> deterministic missing placeholder without crash
        File.Delete(imgPath);
        var restoredMissing = CrucibleAgentHarness.DeserializeSession(stateJson, resolveHostPath: null, trackUnresolvedRefs: true, NullLogger.Instance);
        await CrucibleAgentHarness.RehydrateDeferredImageRefsAsync(restoredMissing, fileModule, NullLogger.Instance, CancellationToken.None);

        Assert.Empty(restoredMissing[0].Contents.OfType<DataContent>());
        var text = string.Concat(restoredMissing[0].Contents.OfType<TextContent>().Select(t => t.Text));
        Assert.Contains(VisionConstants.FormatMissingImageMarker("mockup.png"), text);
    }

    [Fact]
    public void ModelCalibratedTokenEstimator_EstimateGroupTokens_UsesCalibratedTextTokensAfterInPlaceImageEviction()
    {
        var basePng = CreatePngBytes(1024, 1024);
        var padded = new byte[200_000];
        basePng.CopyTo(padded, 0);

        var dataContent = new DataContent(padded, "image/png")
        {
            Name = "evicted-ui.png",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [VisionConstants.PropSourcePath] = "/workspace/attachments/evicted-ui.png"
            }
        };

        var msg = new ChatMessage(ChatRole.User, [new TextContent("Header"), dataContent]);
        var index = PiStyleCrucibleCompactionStrategy.CreateIndex([msg]);
        var group = Assert.Single(index.Groups);
        Assert.True(group.TokenCount > 40_000);

        // Simulate EnforceActiveImageWindowOnGroups mutating the content slot in-place
        group.Messages[0].Contents[1] = new TextContent(
            VisionConstants.FormatEvictedImageMarker("/workspace/attachments/evicted-ui.png"));

        int estimated = ModelCalibratedTokenEstimator.EstimateGroupTokens(group, ModelCalibratedTokenEstimator.Resolve(null));
        Assert.True(estimated < 200, $"Expected small text token count after image eviction, got {estimated}");
    }

    [Fact]
    public async Task PiStyleCrucibleCompactionStrategy_EmergencyRollback_RestoresEvictedImages()
    {
        var oversizedSummary = string.Join(' ', Enumerable.Repeat("summaryword", 2000));
        var summarizer = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, oversizedSummary))));

        // Preserve all MaxImagesInActiveWindow + 2 recent image turns past a small older turn so active-window
        // eviction runs before an oversized summary forces emergency rollback of those evicted images.
        var options = new CrucibleCompactionOptions
        {
            ContextWindowTokens = 200_000,
            ReserveTokens = 1_000,
            SafetyMarginTokens = 500,
            KeepRecentTokens = 1_650,
            SummarizerModelId = "qwen3-vl:8b"
        };

        var strategy = new PiStyleCrucibleCompactionStrategy(options, summarizer, NullLogger.Instance);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Anchor task prompt"),
            new(ChatRole.Assistant, "Brief earlier reply to summarize")
        };

        for (int i = 1; i <= VisionConstants.MaxImagesInActiveWindow + 2; i++)
        {
            var png = CreatePngBytes(64, 64);
            var dc = new DataContent(png, "image/png")
            {
                Name = $"img-{i}.png",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [VisionConstants.PropSourcePath] = $"/workspace/attachments/img-{i}.png"
                }
            };
            messages.Add(new ChatMessage(ChatRole.User, [dc]));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"Ok {i}"));
        }

        var index = PiStyleCrucibleCompactionStrategy.CreateIndex(messages);
        bool compacted = await strategy.CompactEmergencyAsync(index, NullLogger.Instance);

        Assert.False(compacted);
        int liveImagesAfterRollback = index.Groups
            .Where(g => !g.IsExcluded)
            .SelectMany(g => g.Messages)
            .SelectMany(m => m.Contents ?? [])
            .OfType<DataContent>()
            .Count();
        Assert.Equal(VisionConstants.MaxImagesInActiveWindow + 2, liveImagesAfterRollback);
        Assert.DoesNotContain(
            index.Groups.Where(g => !g.IsExcluded).SelectMany(g => g.Messages).SelectMany(m => m.Contents ?? []).OfType<TextContent>(),
            tc => tc.Text.Contains(VisionConstants.EvictedImageMarkerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckpointRehydration_RejectsOutOfSandboxRootedPaths_AndOmitsImagesForTextOnlyModels()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"ncf-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        try
        {
            var outsideImg = Path.Combine(outsideDir, "secret.png");
            var outsideBytes = CreatePngBytes(64, 64);
            await File.WriteAllBytesAsync(outsideImg, outsideBytes);
            var sha = VisionImageNormalizer.ComputeSha256Hex(outsideBytes);

            var data = new DataContent(outsideBytes, "image/png")
            {
                Name = "secret.png",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [VisionConstants.PropSourcePath] = outsideImg,
                    [VisionConstants.PropSha256] = sha,
                    [VisionConstants.PropWidthPx] = 64,
                    [VisionConstants.PropHeightPx] = 64
                }
            };

            var stateJson = CrucibleAgentHarness.SerializeSession([new ChatMessage(ChatRole.User, [data])]);

            // DeserializeSession without resolver MUST NOT load arbitrary rooted paths directly from disk
            var restoredWithoutResolver = CrucibleAgentHarness.DeserializeSession(stateJson, resolveHostPath: null, NullLogger.Instance);
            Assert.Empty(restoredWithoutResolver[0].Contents.OfType<DataContent>());
            Assert.Contains(VisionConstants.FormatMissingImageMarker(outsideImg), restoredWithoutResolver[0].Text);

            // Coordinator restore path resolver must reject rooted paths and '../' traversal outside WorkspacePath
            var ctx = new AgentSessionContext
            {
                ForgeTask = new ForgeTask { Id = 1, Title = "T", Description = "D" },
                Profile = new AgentProfile { Id = StandardStageIds.Implementation },
                StageId = StandardStageIds.Implementation,
                WorkspacePath = _tempDir
            };
            var resolver = CrucibleConversationCoordinator.BuildRestoreHostPathResolver(ctx);
            Assert.Null(resolver(outsideImg));
            Assert.Null(resolver($"../{Path.GetFileName(outsideDir)}/secret.png"));

            // RehydrateDeferredImageRefsAsync when fileModule.SupportsVision == false must omit images cleanly without disk read
            var insideImg = Path.Combine(_tempDir, "inside.png");
            var insideBytes = CreatePngBytes(64, 64);
            await File.WriteAllBytesAsync(insideImg, insideBytes);
            var insideData = new DataContent(insideBytes, "image/png")
            {
                Name = "inside.png",
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [VisionConstants.PropSourcePath] = "inside.png",
                    [VisionConstants.PropSha256] = VisionImageNormalizer.ComputeSha256Hex(insideBytes),
                    [VisionConstants.PropWidthPx] = 64,
                    [VisionConstants.PropHeightPx] = 64
                }
            };
            var insideStateJson = CrucibleAgentHarness.SerializeSession([new ChatMessage(ChatRole.User, [insideData])]);

            var mountTable = SandboxMountTable.ForWorkspace(_tempDir);
            var sandboxResolver = new SandboxPathResolver(
                SandboxScopeDescriptor.ForWorkspace(
                    new Workspace { Id = 1, Name = "W", RepositoryPath = _tempDir },
                    SandboxSecurityTier.Standard,
                    taskId: null,
                    mountTable: mountTable));
            var textOnlyModule = new WorkspaceFileToolModule(
                new CanonicalToolScope(_tempDir, SandboxPathResolver: sandboxResolver, SupportsVision: false),
                sandboxResolver,
                new EditorConfigService(),
                new EditorConfigFileApplier(),
                NullLogger.Instance);

            var deferredTextOnly = CrucibleAgentHarness.DeserializeSession(insideStateJson, resolveHostPath: null, trackUnresolvedRefs: true, NullLogger.Instance);
            await CrucibleAgentHarness.RehydrateDeferredImageRefsAsync(deferredTextOnly, textOnlyModule, NullLogger.Instance, CancellationToken.None);

            Assert.Empty(deferredTextOnly[0].Contents.OfType<DataContent>());
            Assert.Contains(
                VisionConstants.FormatVisionDisabledOmittedImageMarker("inside.png"),
                string.Concat(deferredTextOnly[0].Contents.OfType<TextContent>().Select(t => t.Text)));
        }
        finally
        {
            if (Directory.Exists(outsideDir))
            {
                Directory.Delete(outsideDir, recursive: true);
            }
        }
    }
}
