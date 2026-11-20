using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Vision;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NightCodeForge.Agents.Tests.Vision;

public class VisionImageNormalizerAndReadToolTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workspaceDir;
    private readonly string _attachmentsDir;

    public VisionImageNormalizerAndReadToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-vision-read-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempRoot, "repo");
        _attachmentsDir = Path.Combine(_tempRoot, "attachments");
        Directory.CreateDirectory(_workspaceDir);
        Directory.CreateDirectory(_attachmentsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static void WriteSolidPng(string path, int width, int height)
    {
        using var img = new Image<Rgba32>(width, height, new Rgba32(100, 149, 237));
        img.SaveAsPng(path);
    }

    [Fact]
    public void CanonicalTools_SchemaAndInventory_AddZeroNewTools_AndAllowNullModuleSchemaInspection()
    {
        var functions = CanonicalAIFunctionFactory.CreateAllCanonicalFunctions();
        var names = functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(14, functions.Count);
        Assert.Subset(CanonicalToolNames.All.ToHashSet(StringComparer.Ordinal), names);
        Assert.DoesNotContain("read_image", names);
        Assert.Contains("inspect", string.Concat(functions.Single(f => f.Name == CanonicalToolNames.Read).Description), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VisionImageNormalizer_DownscalesOversizedImagesProportionally_AndRejectsOversizeOrCorrupt()
    {
        var bigPath = Path.Combine(_workspaceDir, "oversized.png");
        WriteSolidPng(bigPath, 2400, 1200);

        var normalized = await VisionImageNormalizer.LoadAndNormalizeAsync(bigPath, "oversized.png");

        Assert.Equal("image/png", normalized.MediaType);
        Assert.Equal(VisionConstants.MaxVisionImageDimensionPx, normalized.WidthPx);
        Assert.Equal(VisionConstants.MaxVisionImageDimensionPx / 2, normalized.HeightPx);
        Assert.Equal(VisionConstants.EstimateVisionTokens(normalized.WidthPx, normalized.HeightPx), normalized.EstimatedTokens);

        // Corrupt bytes with .png extension must throw AttachmentConversionException
        var corruptPath = Path.Combine(_workspaceDir, "corrupt.png");
        await File.WriteAllBytesAsync(corruptPath, "not really a png"u8.ToArray());
        await Assert.ThrowsAsync<AttachmentConversionException>(() =>
            VisionImageNormalizer.LoadAndNormalizeAsync(corruptPath, "corrupt.png"));

        // Bytes exceeding MaxVisionImageBytes must throw AttachmentConversionException
        var tooLarge = new byte[VisionConstants.MaxVisionImageBytes + 1];
        tooLarge[0] = 0x89;
        tooLarge[1] = 0x50;
        tooLarge[2] = 0x4E;
        tooLarge[3] = 0x47;
        Assert.Throws<AttachmentConversionException>(() =>
            VisionImageNormalizer.NormalizeBytes(tooLarge, ".png", "huge.png"));
    }

    [Fact]
    public async Task ReadAsync_LoadsWorkspaceAndAttachmentImagesIntoQueue_AndThrowsForTextOnlyModels()
    {
        var attachmentImg = Path.Combine(_attachmentsDir, "001-bug.png");
        WriteSolidPng(attachmentImg, 640, 480);
        var repoImg = Path.Combine(_workspaceDir, "screenshot.png");
        WriteSolidPng(repoImg, 320, 240);

        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _workspaceDir,
            hostAttachmentsDirectory: _attachmentsDir);
        var sandboxResolver = new SandboxPathResolver(
            SandboxScopeDescriptor.ForWorkspace(
                new Workspace { Id = 1, Name = "W", RepositoryPath = _workspaceDir },
                SandboxSecurityTier.Standard,
                taskId: null,
                mountTable: mountTable));

        var queue = new MultimodalAttachmentQueue();
        var visionModule = new WorkspaceFileToolModule(
            new CanonicalToolScope(
                _workspaceDir,
                SandboxPathResolver: sandboxResolver,
                SupportsVision: true,
                ActiveModelId: "qwen3-vl:8b",
                MultimodalAttachmentQueue: queue),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        var attachResult = await visionModule.ReadAsync("/workspace/attachments/001-bug.png");
        var repoResult = await visionModule.ReadAsync("screenshot.png");

        Assert.StartsWith(VisionConstants.LoadedImageToolResultPrefix, attachResult);
        Assert.Contains("640x480", attachResult);
        Assert.StartsWith(VisionConstants.LoadedImageToolResultPrefix, repoResult);
        Assert.Contains("320x240", repoResult);

        var resolved = queue.ResolveForToolRun([("c1", attachResult), ("c2", repoResult)]);
        Assert.Equal(2, resolved.Count);
        Assert.Equal("/workspace/attachments/001-bug.png", resolved[0].SourcePath);
        Assert.Equal("screenshot.png", resolved[1].SourcePath);

        // Text-only module must throw ModelVisionNotSupportedException
        var textOnlyModule = new WorkspaceFileToolModule(
            new CanonicalToolScope(
                _workspaceDir,
                SandboxPathResolver: sandboxResolver,
                SupportsVision: false,
                ActiveModelId: "qwen2.5-coder:32b"),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<ModelVisionNotSupportedException>(() =>
            textOnlyModule.ReadAsync("/workspace/attachments/001-bug.png"));
    }

    [Fact]
    public async Task CreateFunctions_ReadLambda_ReturnsActionableErrorStringThroughFunctionInvokingChatClient()
    {
        var repoImg = Path.Combine(_workspaceDir, "ui.png");
        WriteSolidPng(repoImg, 120, 120);
        var corruptImg = Path.Combine(_workspaceDir, "bad.png");
        await File.WriteAllBytesAsync(corruptImg, "garbage"u8.ToArray());

        var mountTable = SandboxMountTable.ForWorkspace(_workspaceDir);
        var sandboxResolver = new SandboxPathResolver(
            SandboxScopeDescriptor.ForWorkspace(
                new Workspace { Id = 1, Name = "W", RepositoryPath = _workspaceDir },
                SandboxSecurityTier.Standard,
                taskId: null,
                mountTable: mountTable));

        var module = new WorkspaceFileToolModule(
            new CanonicalToolScope(
                _workspaceDir,
                SandboxPathResolver: sandboxResolver,
                SupportsVision: false,
                ActiveModelId: "text-only-model"),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        var readFn = module.CreateFunctions().Single(f => f.Name == CanonicalToolNames.Read);

        // 5 consecutive vision/corrupt errors through FICC (exceeding default MaximumConsecutiveErrorsPerRequest = 3)
        // must NOT throw or mask as "Error: Function failed."
        int callTurn = 0;
        var seenToolResults = new List<string>();
        IChatClient innerClient = new DelegateTestChatClient((msgs, _, _) =>
        {
            foreach (var frc in msgs.SelectMany(m => m.Contents ?? []).OfType<FunctionResultContent>())
            {
                var str = frc.Result?.ToString() ?? string.Empty;
                if (!seenToolResults.Contains(str))
                {
                    seenToolResults.Add(str);
                }
            }

            callTurn++;
            if (callTurn <= 5)
            {
                var path = callTurn % 2 == 1 ? "ui.png" : "../outside-secret.png";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent($"call_{callTurn}", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = path })
                ])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Handled errors gracefully.")));
        });

        using var wrapped = innerClient.AsBuilder().UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [readFn] };
        var final = await wrapped.GetResponseAsync([new ChatMessage(ChatRole.User, "Inspect images")], options);

        Assert.Equal("Handled errors gracefully.", final.Text);
        Assert.Equal(2, seenToolResults.Count);
        Assert.Contains(seenToolResults, s => s.StartsWith("Error:") && s.Contains("does not support vision"));
        Assert.Contains(seenToolResults, s => s.StartsWith("Error:"));
        Assert.DoesNotContain(seenToolResults, s => s.Contains("Function failed.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CrucibleAgentHarness_TriageStage_ForcesVisionDisabled_AndOmitsAttachmentsMount()
    {
        var repoImg = Path.Combine(_workspaceDir, "repro.png");
        WriteSolidPng(repoImg, 128, 128);
        var attachImg = Path.Combine(_attachmentsDir, "001-untrusted.png");
        WriteSolidPng(attachImg, 128, 128);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(65536);
        // Even if the gateway advertises vision on the Triage model, Stage 0 Triage MUST force SupportsVision = false.
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var capturedToolResults = new List<string>();
        int turn = 0;
        var baseClient = new DelegateTestChatClient((msgs, _, _) =>
        {
            foreach (var frc in msgs.SelectMany(m => m.Contents ?? []).OfType<FunctionResultContent>())
            {
                capturedToolResults.Add(frc.Result?.ToString() ?? string.Empty);
            }

            turn++;
            if (turn == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("triage_read_1", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "repro.png" })
                ])));
            }
            if (turn == 2)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("triage_read_2", CanonicalToolNames.Read, new Dictionary<string, object?> { ["path"] = "/workspace/attachments/001-untrusted.png" })
                ])));
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Triage finished without binary image inspection.")));
        });

        var builder = new TestCrucibleHarnessBuilder
        {
            GatewayService = gatewayMock.Object,
            BaseChatClient = baseClient
        };
        var harness = builder.Build();

        var mountTable = SandboxMountTable.ForWorkspace(_workspaceDir, hostAttachmentsDirectory: null);
        var workspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _workspaceDir };
        var scope = SandboxScopeDescriptor.ForWorkspace(
            workspace,
            SandboxSecurityTier.RestrictedReadOnly,
            taskId: null,
            mountTable: mountTable);

        var context = new AgentSessionContext
        {
            Workspace = workspace,
            ForgeTask = new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Untrusted Bug", Description = "Desc", Status = TaskOverallStatus.Triage },
            StageId = StandardStageIds.Triage,
            WorkspacePath = _workspaceDir,
            SandboxScope = scope,
            Profile = new AgentProfile
            {
                Id = StandardStageIds.Triage,
                ModelName = "qwen3-vl:8b",
                Mode = AgentMode.ReadOnly,
                SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
                PermittedTools = [CanonicalToolNames.Read]
            },
            TaskAttachments =
            [
                new TaskAttachment { Id = 1, WorkspaceId = 1, Title = "001-untrusted.png", ContentKind = TaskAttachmentContentKind.Image, RelativePath = "001-untrusted.png" }
            ]
        };

        var result = await harness.RunStageAsync(context);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.DoesNotContain(DefaultAgentTools.Triage, t => string.Equals(t, "read_image", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedToolResults, r => r.StartsWith("Error:") && r.Contains("does not support vision"));
    }

    [Fact]
    public void VisionImageNormalizer_RejectsDecompressionBombDimensionsBeforeDecoding()
    {
        var bombHeaderPng = new byte[32];
        byte[] pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        pngSig.CopyTo(bombHeaderPng, 0);
        "IHDR"u8.CopyTo(bombHeaderPng.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bombHeaderPng.AsSpan(16), 20_000);
        BinaryPrimitives.WriteUInt32BigEndian(bombHeaderPng.AsSpan(20), 20_000);

        var ex = Assert.Throws<AttachmentConversionException>(() =>
            VisionImageNormalizer.NormalizeBytes(bombHeaderPng, ".png", "bomb.png"));
        Assert.Contains("exceed maximum allowed source dimensions", ex.Message);
    }

    [Fact]
    public async Task VisionImageNormalizer_LoadAndNormalizeAsync_ExtensionlessHostFileValidatesUsingLogicalSourceExtension()
    {
        var extensionlessPath = Path.Combine(_workspaceDir, "blob-without-ext");
        await File.WriteAllBytesAsync(extensionlessPath, "not actually a png"u8.ToArray());

        await Assert.ThrowsAsync<AttachmentConversionException>(() =>
            VisionImageNormalizer.LoadAndNormalizeAsync(extensionlessPath, "logical-ui.png"));
    }

    [Fact]
    public void VisionImageNormalizer_TryCreateThumbnailBytes_DownscalesOversizedAndPreservesSmallNativeImage()
    {
        var bigPath = Path.Combine(_workspaceDir, "big-thumb.png");
        WriteSolidPng(bigPath, 640, 320);
        var bigBytes = File.ReadAllBytes(bigPath);

        var thumb = VisionImageNormalizer.TryCreateThumbnailBytes(bigBytes, VisionConstants.ThumbnailMaxDimensionPx);
        Assert.NotNull(thumb);
        Assert.Equal("image/png", thumb!.Value.MediaType);
        Assert.True(Core.Services.ImageHeaderInspector.TryInspect(thumb.Value.Bytes, out var thumbInfo));
        Assert.Equal(VisionConstants.ThumbnailMaxDimensionPx, thumbInfo.WidthPx);
        Assert.Equal(64, thumbInfo.HeightPx);

        var smallPath = Path.Combine(_workspaceDir, "small-thumb.png");
        WriteSolidPng(smallPath, 64, 64);
        var smallBytes = File.ReadAllBytes(smallPath);

        var passThrough = VisionImageNormalizer.TryCreateThumbnailBytes(smallBytes, VisionConstants.ThumbnailMaxDimensionPx);
        Assert.NotNull(passThrough);
        Assert.Same(smallBytes, passThrough!.Value.Bytes);
    }

    [Fact]
    public async Task ReadAsync_WhenSupportsVisionTrueAndQueueNull_ThrowsInvalidOperationException()
    {
        var imgPath = Path.Combine(_workspaceDir, "ui-no-queue.png");
        WriteSolidPng(imgPath, 100, 100);

        var mountTable = SandboxMountTable.ForWorkspace(_workspaceDir);
        var sandboxResolver = new SandboxPathResolver(
            SandboxScopeDescriptor.ForWorkspace(
                new Workspace { Id = 1, Name = "W", RepositoryPath = _workspaceDir },
                SandboxSecurityTier.Standard,
                taskId: null,
                mountTable: mountTable));

        var module = new WorkspaceFileToolModule(
            new CanonicalToolScope(
                _workspaceDir,
                SandboxPathResolver: sandboxResolver,
                SupportsVision: true,
                ActiveModelId: "qwen3-vl:8b",
                MultimodalAttachmentQueue: null),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => module.ReadAsync("ui-no-queue.png"));
    }

    [Fact]
    public async Task ReadAsync_WhenSupportsVisionFalseAndMissingImageAttachment_ThrowsModelVisionNotSupportedFirst()
    {
        var mountTable = SandboxMountTable.ForWorkspace(
            hostWorkingDirectory: _workspaceDir,
            hostAttachmentsDirectory: _attachmentsDir);
        var sandboxResolver = new SandboxPathResolver(
            SandboxScopeDescriptor.ForWorkspace(
                new Workspace { Id = 1, Name = "W", RepositoryPath = _workspaceDir },
                SandboxSecurityTier.Standard,
                taskId: null,
                mountTable: mountTable));

        // Write a manifest entry claiming a symlink to a missing host file; text-only model must still fail fast with ModelVisionNotSupportedException
        await File.WriteAllTextAsync(
            Path.Combine(_attachmentsDir, ".manifest.json"),
            """[{"Id":1,"Title":"dangling.png","RelativePath":"dangling.png","IsSymlink":true,"SourceRef":"/nonexistent/host/dangling.png"}]""");

        var module = new WorkspaceFileToolModule(
            new CanonicalToolScope(
                _workspaceDir,
                SandboxPathResolver: sandboxResolver,
                SupportsVision: false,
                ActiveModelId: "text-only-coder"),
            sandboxResolver,
            new EditorConfigService(),
            new EditorConfigFileApplier(),
            NullLogger.Instance);

        await Assert.ThrowsAsync<ModelVisionNotSupportedException>(() =>
            module.ReadAsync("/workspace/attachments/dangling.png"));
    }

    internal sealed class DelegateTestChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => handler(messages, options, cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var msg in response.Messages)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(DelegateTestChatClient) ? this : null;

        public void Dispose() { }
    }
}
