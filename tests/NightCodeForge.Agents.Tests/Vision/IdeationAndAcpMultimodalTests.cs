using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NightCodeForge.Agents.Tests.Vision;

public class IdeationAndAcpMultimodalTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _repoDir;
    private readonly string _attachmentsDir;

    public IdeationAndAcpMultimodalTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-ideation-acp-vision-{Guid.NewGuid():N}");
        _repoDir = Path.Combine(_tempRoot, "repo");
        _attachmentsDir = Path.Combine(_tempRoot, "attachments");
        Directory.CreateDirectory(_repoDir);
        Directory.CreateDirectory(_attachmentsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static byte[] CreatePngBytes(int width, int height)
    {
        using var img = new Image<Rgba32>(width, height, new Rgba32(147, 112, 219));
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task IdeationCoordinator_UpfrontImageTurn_PersistsImageRefAndRehydratesAcrossColdCoordinatorRestart()
    {
        var imgBytes = CreatePngBytes(640, 480);
        var relPath = "001-screen.png";
        var fullImgPath = Path.Combine(_attachmentsDir, relPath);
        await File.WriteAllBytesAsync(fullImgPath, imgBytes);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(131072);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("qwen3-vl:8b", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        gatewayMock.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 5,
            Name = "VisionIdeationWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen3-vl:8b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });

        var att = await attachmentRepo.CreateAsync(new TaskAttachment
        {
            Id = 9,
            WorkspaceId = ws.Id,
            Title = "screen.png",
            SourceRef = "screen.png",
            RelativePath = relPath,
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            WidthPx = 640,
            HeightPx = 480,
            SizeBytes = imgBytes.Length
        });

        var call1Captured = new List<List<ChatMessage>>();
        using var builder1 = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((msgs, _, _) =>
            {
                call1Captured.Add(msgs.ToList());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Saw purple screen.")));
            })
        };

        var coordinator1 = builder1.Build();
        var handle = await coordinator1.StartConversationAsync(ws.Id, "Vision Session");

        var completionTcs = new TaskCompletionSource<IdeationStreamUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (coordinator1.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind is IdeationStreamUpdateKind.Completed or IdeationStreamUpdateKind.Error)
            {
                completionTcs.TrySetResult(u);
            }
        }))
        {
            var meta = JsonSerializer.Serialize(new { AttachmentIds = new[] { att.Id } });
            Assert.True(await coordinator1.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, VisionConstants.DefaultImageOnlyPrompt, null, meta)));
            var done = await completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(IdeationStreamUpdateKind.Completed, done.Kind);
        }

        // First call had user message carrying DataContent image
        var sentUserMsg = Assert.Single(call1Captured[0], m => m.Role == ChatRole.User);
        Assert.Single(sentUserMsg.Contents.OfType<DataContent>());

        // Persisted StageTurnRole.User turn has ImageRefs and zero raw Base64
        var savedConv = await taskRepo.GetConversationWithTurnsAsync(handle.Conversation.Id);
        var userTurn = Assert.Single(savedConv!.Turns, t => t.Role == StageTurnRole.User);
        Assert.Single(ConversationHistorySerializer.ExtractImageRefs(userTurn.MetadataJson));
        Assert.DoesNotContain(Convert.ToBase64String(imgBytes), userTurn.MetadataJson ?? string.Empty);

        // Cold restart: fresh coordinator instance reloads from SQLite/repository turns and rehydrates DataContent for follow-up turn
        var call2Captured = new List<List<ChatMessage>>();
        using var builder2 = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((msgs, _, _) =>
            {
                call2Captured.Add(msgs.ToList());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Follow-up answered.")));
            })
        };

        var coordinator2 = builder2.Build();
        var completion2Tcs = new TaskCompletionSource<IdeationStreamUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (coordinator2.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind is IdeationStreamUpdateKind.Completed or IdeationStreamUpdateKind.Error)
            {
                completion2Tcs.TrySetResult(u);
            }
        }))
        {
            Assert.True(await coordinator2.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Follow up question")));
            var done2 = await completion2Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(IdeationStreamUpdateKind.Completed, done2.Kind);
        }

        var rehydratedHistory = call2Captured[0];
        var restoredTurn1 = rehydratedHistory.First(m => m.Role == ChatRole.User);
        Assert.Single(restoredTurn1.Contents.OfType<DataContent>());
    }

    [Fact]
    public async Task IdeationCoordinator_TextOnlyModel_FailsFastWithModelVisionNotSupported()
    {
        var imgBytes = CreatePngBytes(120, 120);
        await File.WriteAllBytesAsync(Path.Combine(_attachmentsDir, "002-ui.png"), imgBytes);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(65536);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 6,
            Name = "TextOnlyIdeationWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen2.5-coder:32b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });

        await attachmentRepo.CreateAsync(new TaskAttachment
        {
            Id = 10,
            WorkspaceId = ws.Id,
            Title = "ui.png",
            SourceRef = "ui.png",
            RelativePath = "002-ui.png",
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            WidthPx = 120,
            HeightPx = 120,
            SizeBytes = imgBytes.Length
        });

        bool llmInvoked = false;
        using var builder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((_, _, _) =>
            {
                llmInvoked = true;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should not run")));
            })
        };

        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Fail-fast vision test");

        await Assert.ThrowsAsync<ModelVisionNotSupportedException>(() =>
            coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Check @attachment:10")));
        Assert.False(llmInvoked);
    }

    [Fact]
    public async Task AcpAgentHarness_NegotiateProtocolAndBuildPromptBlocks_SupportsNativeImageAndFailsFastWithoutCapability()
    {
        var initResponse = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":2,\"agentCapabilities\":{\"promptCapabilities\":{\"image\":true}}}}\n";
        using var stdinMs = new MemoryStream();
        using var stdinWriter = new StreamWriter(stdinMs, new UTF8Encoding(false), leaveOpen: true);
        using var stdoutReader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(initResponse)));

        var negotiation = await AcpAgentHarness.NegotiateProtocolWithCapabilitiesAsync(stdinWriter, stdoutReader, CancellationToken.None);
        Assert.Equal(2, negotiation.ProtocolVersion);
        Assert.True(negotiation.SupportsImagePrompt);

        var imgBytes = CreatePngBytes(400, 300);
        await File.WriteAllBytesAsync(Path.Combine(_attachmentsDir, "acp-ui.png"), imgBytes);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);

        var harness = new TestAcpHarnessBuilder { PathResolver = pathResolverMock.Object }.Build();
        var context = new AgentSessionContext
        {
            Workspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _repoDir },
            ForgeTask = new ForgeTask { Id = 42, WorkspaceId = 1, Title = "ACP Vision Task", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            WorkspacePath = _repoDir,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "ACP", HarnessId = StandardHarnessIds.Acp, ModelName = "gemini-pro" },
            TaskAttachments =
            [
                new TaskAttachment
                {
                    Id = 1,
                    WorkspaceId = 1,
                    Title = "acp-ui.png",
                    RelativePath = "acp-ui.png",
                    MediaType = "image/png",
                    ContentKind = TaskAttachmentContentKind.Image
                }
            ]
        };

        var blocks = await harness.BuildPromptContentBlocksAsync(context, "Solve the layout bug", supportsImagePrompt: true, sandboxPathResolver: null, CancellationToken.None);
        var serialized = JsonSerializer.Serialize(blocks);
        using var parsedDoc = JsonDocument.Parse(serialized);
        var arr = parsedDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);
        Assert.Equal("text", arr[0].GetProperty("type").GetString());
        Assert.Equal("image", arr[1].GetProperty("type").GetString());
        Assert.Equal("image/png", arr[1].GetProperty("mimeType").GetString());
        Assert.Equal(Convert.ToBase64String(imgBytes), arr[1].GetProperty("data").GetString());

        await Assert.ThrowsAsync<ModelVisionNotSupportedException>(() =>
            harness.BuildPromptContentBlocksAsync(context, "Solve the layout bug", supportsImagePrompt: false, sandboxPathResolver: null, CancellationToken.None));
    }

    [Fact]
    public async Task AcpHarness_And_IdeationCoordinator_RejectRelativePathTraversalInAttachments()
    {
        var outsideFile = Path.Combine(_tempRoot, "outside-secret.png");
        await File.WriteAllBytesAsync(outsideFile, CreatePngBytes(64, 64));

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);

        // 1. AcpAgentHarness must reject '../outside-secret.png'
        var acpHarness = new TestAcpHarnessBuilder { PathResolver = pathResolverMock.Object }.Build();
        var acpContext = new AgentSessionContext
        {
            Workspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _repoDir },
            ForgeTask = new ForgeTask { Id = 43, WorkspaceId = 1, Title = "Traversal", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            WorkspacePath = _repoDir,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "ACP", HarnessId = StandardHarnessIds.Acp },
            TaskAttachments =
            [
                new TaskAttachment
                {
                    Id = 77,
                    WorkspaceId = 1,
                    Title = "escape.png",
                    RelativePath = "../outside-secret.png",
                    MediaType = "image/png",
                    ContentKind = TaskAttachmentContentKind.Image
                }
            ]
        };

        // 1. AcpAgentHarness skips invalid traversal attachment without throwing so tasks never fail due to bad/missing attachments
        var traversalBlocks = await acpHarness.BuildPromptContentBlocksAsync(
            acpContext,
            "Prompt",
            supportsImagePrompt: true,
            sandboxPathResolver: null,
            CancellationToken.None);
        Assert.Single(traversalBlocks);

        // 2. IdeationConversationCoordinator must reject '../outside-secret.png'
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(65536);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 7,
            Name = "TraversalWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen3-vl:8b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });

        await attachmentRepo.CreateAsync(new TaskAttachment
        {
            Id = 78,
            WorkspaceId = ws.Id,
            Title = "escape.png",
            RelativePath = "../outside-secret.png",
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image
        });

        using var builder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo
        };

        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Traversal Test");

        await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
            coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Check @attachment:78")));
    }

    [Fact]
    public async Task IdeationCoordinator_WarmSessionSwitchingFromVisionToTextOnlyModel_StripsPriorImages()
    {
        var imgBytes = CreatePngBytes(200, 200);
        await File.WriteAllBytesAsync(Path.Combine(_attachmentsDir, "001-warm.png"), imgBytes);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(131072);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("qwen3-vl:8b", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("qwen2.5-coder:32b", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        gatewayMock.Setup(g => g.ReleaseSessionAffinityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 8,
            Name = "WarmSwitchWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen3-vl:8b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });

        await attachmentRepo.CreateAsync(new TaskAttachment
        {
            Id = 81,
            WorkspaceId = ws.Id,
            Title = "warm.png",
            RelativePath = "001-warm.png",
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            WidthPx = 200,
            HeightPx = 200,
            SizeBytes = imgBytes.Length
        });

        var capturedCalls = new List<List<ChatMessage>>();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((msgs, _, _) =>
            {
                capturedCalls.Add(msgs.ToList());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Ack.")));
            })
        };

        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Warm Switch");

        // Turn 1: vision model receives image DataContent
        var tcs1 = new TaskCompletionSource<IdeationStreamUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind is IdeationStreamUpdateKind.Completed or IdeationStreamUpdateKind.Error)
                tcs1.TrySetResult(u);
        }))
        {
            Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Inspect @attachment:81")));
            Assert.Equal(IdeationStreamUpdateKind.Completed, (await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        }
        Assert.Contains(capturedCalls[0], m => m.Contents.OfType<DataContent>().Any());

        // Turn 2: switch warm session to text-only ModelOverride -> historical DataContent must be stripped from outbound request
        var tcs2 = new TaskCompletionSource<IdeationStreamUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind is IdeationStreamUpdateKind.Completed or IdeationStreamUpdateKind.Error)
                tcs2.TrySetResult(u);
        }))
        {
            var textOptions = new IdeationSessionOptions(ModelOverride: "qwen2.5-coder:32b");
            Assert.True(await StartTurnWhenIdleAsync(coordinator, new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Summarize findings", textOptions)));
            Assert.Equal(IdeationStreamUpdateKind.Completed, (await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        }

        Assert.DoesNotContain(capturedCalls[1], m => m.Contents.OfType<DataContent>().Any());
        Assert.Contains(capturedCalls[1], m => m.Contents.OfType<TextContent>().Any(t => t.Text.Contains("omitted: active model does not support vision")));

        // Turn 3: sending a new image on text-only ModelOverride must fail fast with ModelVisionNotSupportedException
        await Assert.ThrowsAsync<ModelVisionNotSupportedException>(() =>
            StartTurnWhenIdleAsync(coordinator, new IdeationSendRequest(
                ws.Id,
                handle.Conversation.Id,
                "Check @attachment:81",
                new IdeationSessionOptions(ModelOverride: "qwen2.5-coder:32b"))));

        static async Task<bool> StartTurnWhenIdleAsync(IIdeationConversationCoordinator coord, IdeationSendRequest req)
        {
            for (int attempt = 0; attempt < 250; attempt++)
            {
                if (await coord.StartTurnAsync(req))
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }
    }

    [Fact]
    public async Task AcpHarness_And_IdeationCoordinator_SymlinkedImageAttachment_ReadsDirectHostTargetInsteadOfAttachmentsSymlink()
    {
        var realHostImage = Path.Combine(_tempRoot, "real-host-capture.png");
        var imgBytes = CreatePngBytes(120, 80);
        await File.WriteAllBytesAsync(realHostImage, imgBytes);

        // Simulate unreadable/corrupt symlink entry inside attachments/ (as seen over Windows \\wsl.localhost)
        var placeholderRel = "009-capture.png";
        await File.WriteAllBytesAsync(Path.Combine(_attachmentsDir, placeholderRel), [0x00, 0x01, 0x02]);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);

        var symlinkImgAtt = new TaskAttachment
        {
            Id = 9,
            WorkspaceId = 1,
            Title = "capture.png",
            RelativePath = placeholderRel,
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = realHostImage,
            IsSymlink = true,
            WidthPx = 120,
            HeightPx = 80,
            SizeBytes = imgBytes.Length
        };

        // 1. AcpAgentHarness reads directly from realHostImage (even when SandboxPathResolver maps virtualPath -> placeholder)
        var acpWorkspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(
            _repoDir,
            isReadOnly: false,
            hostAttachmentsDirectory: _attachmentsDir);
        var sandboxScope = SandboxScopeDescriptor.ForWorkspace(
            acpWorkspace,
            SandboxSecurityTier.Standard,
            taskId: 44,
            mountTable);
        var sandboxResolver = new SandboxPathResolver(sandboxScope);

        var acpHarness = new TestAcpHarnessBuilder { PathResolver = pathResolverMock.Object }.Build();
        var acpContext = new AgentSessionContext
        {
            Workspace = acpWorkspace,
            ForgeTask = new ForgeTask { Id = 44, WorkspaceId = 1, Title = "Symlink Image", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            WorkspacePath = _repoDir,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "ACP", HarnessId = StandardHarnessIds.Acp },
            TaskAttachments = [symlinkImgAtt]
        };

        var blocks = await acpHarness.BuildPromptContentBlocksAsync(
            acpContext,
            "Inspect screenshot",
            supportsImagePrompt: true,
            sandboxPathResolver: sandboxResolver,
            CancellationToken.None);
        var json = JsonSerializer.Serialize(blocks);
        Assert.Contains(Convert.ToBase64String(imgBytes), json);

        // 2. IdeationConversationCoordinator reads directly from realHostImage
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "SymlinkVisionWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen3-vl:8b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });
        await attachmentRepo.CreateAsync(symlinkImgAtt);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(65536);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var captured = new List<List<ChatMessage>>();
        using var builder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((msgs, _, _) =>
            {
                captured.Add(msgs.ToList());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Saw host image.")));
            })
        };

        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Symlink Image Turn");
        var tcs = new TaskCompletionSource<IdeationStreamUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (coordinator.Subscribe(handle.Conversation.Id, u =>
        {
            if (u.Kind is IdeationStreamUpdateKind.Completed or IdeationStreamUpdateKind.Error)
                tcs.TrySetResult(u);
        }))
        {
            Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Inspect @attachment:9")));
            var done = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(IdeationStreamUpdateKind.Completed, done.Kind);
        }

        var userMsg = Assert.Single(captured[0], m => m.Role == ChatRole.User);
        var data = Assert.Single(userMsg.Contents.OfType<DataContent>());
        Assert.Equal(imgBytes, data.Data.ToArray());
    }

    [Fact]
    public async Task AcpHarnessSkips_And_IdeationCoordinatorThrows_ForSymlinkedImageWithMissingHostTargetEvenWhenPlaceholderExists()
    {
        var missingHostImage = Path.Combine(_tempRoot, "missing-host-capture.png");
        var placeholderRel = "010-capture.png";
        await File.WriteAllBytesAsync(Path.Combine(_attachmentsDir, placeholderRel), [0x00, 0x01, 0x02]);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(r => r.GetAgentWorkingClonePath(It.IsAny<int>())).Returns(_repoDir);

        var symlinkImgAtt = new TaskAttachment
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "capture.png",
            RelativePath = placeholderRel,
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = missingHostImage,
            IsSymlink = true,
            WidthPx = 120,
            HeightPx = 80,
            SizeBytes = 123
        };

        var acpWorkspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _repoDir };
        var mountTable = SandboxMountTable.ForWorkspace(
            _repoDir,
            isReadOnly: false,
            hostAttachmentsDirectory: _attachmentsDir);
        var sandboxScope = SandboxScopeDescriptor.ForWorkspace(
            acpWorkspace,
            SandboxSecurityTier.Standard,
            taskId: 45,
            mountTable);
        var sandboxResolver = new SandboxPathResolver(sandboxScope);

        var acpHarness = new TestAcpHarnessBuilder { PathResolver = pathResolverMock.Object }.Build();
        var acpContext = new AgentSessionContext
        {
            Workspace = acpWorkspace,
            ForgeTask = new ForgeTask { Id = 45, WorkspaceId = 1, Title = "Missing Symlink Image", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            WorkspacePath = _repoDir,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "ACP", HarnessId = StandardHarnessIds.Acp },
            TaskAttachments = [symlinkImgAtt]
        };

        var missingSymlinkBlocks = await acpHarness.BuildPromptContentBlocksAsync(
            acpContext,
            "Inspect screenshot",
            supportsImagePrompt: true,
            sandboxPathResolver: sandboxResolver,
            CancellationToken.None);
        Assert.Single(missingSymlinkBlocks);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var ws = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "MissingSymlinkVisionWS",
            RepositoryPath = _repoDir,
            AgentProfiles =
            [
                new AgentProfile
                {
                    Id = StandardStageIds.Ideation,
                    Name = "Ideation",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "qwen3-vl:8b",
                    Mode = AgentMode.ReadOnly,
                    PermittedTools = DefaultAgentTools.Ideation
                }
            ]
        });
        await attachmentRepo.CreateAsync(symlinkImgAtt);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(65536);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        using var builder = new TestIdeationCoordinatorBuilder
        {
            PathResolver = pathResolverMock.Object,
            GatewayService = gatewayMock.Object,
            WorkspaceRepository = workspaceRepo,
            TaskRepository = taskRepo,
            TaskAttachmentRepository = attachmentRepo,
            BaseChatClient = new VisionImageNormalizerAndReadToolTests.DelegateTestChatClient((_, _, _) =>
                Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Should not run."))))
        };

        var coordinator = builder.Build();
        var handle = await coordinator.StartConversationAsync(ws.Id, "Missing Symlink Image Turn");

        await Assert.ThrowsAsync<AttachmentTargetNotFoundException>(() =>
            coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, handle.Conversation.Id, "Inspect @attachment:10")));
    }

    [Fact]
    public async Task AcpHarness_BuildPromptContentBlocksAsync_SkipsCorruptOrEmptyImageAttachmentAndKeepsValidImages()
    {
        var corruptImgPath = Path.Combine(_attachmentsDir, "011-corrupt.png");
        var emptyImgPath = Path.Combine(_attachmentsDir, "012-empty.png");
        var validImgPath = Path.Combine(_attachmentsDir, "013-valid.png");
        var validBytes = CreatePngBytes(80, 60);
        await File.WriteAllBytesAsync(corruptImgPath, [0x00, 0x01, 0x02]);
        await File.WriteAllBytesAsync(emptyImgPath, []);
        await File.WriteAllBytesAsync(validImgPath, validBytes);

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.GetAttachmentsPath(It.IsAny<int>())).Returns(_attachmentsDir);
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);

        var acpHarness = new TestAcpHarnessBuilder { PathResolver = pathResolverMock.Object }.Build();
        var acpContext = new AgentSessionContext
        {
            Workspace = new Workspace { Id = 1, Name = "W", RepositoryPath = _repoDir },
            ForgeTask = new ForgeTask { Id = 46, WorkspaceId = 1, Title = "Corrupt Image", Description = "Desc" },
            StageId = StandardStageIds.Implementation,
            WorkspacePath = _repoDir,
            Profile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "ACP", HarnessId = StandardHarnessIds.Acp },
            TaskAttachments =
            [
                new TaskAttachment
                {
                    Id = 11,
                    WorkspaceId = 1,
                    Title = "corrupt.png",
                    RelativePath = "011-corrupt.png",
                    MediaType = "image/png",
                    ContentKind = TaskAttachmentContentKind.Image
                },
                new TaskAttachment
                {
                    Id = 12,
                    WorkspaceId = 1,
                    Title = "empty.png",
                    RelativePath = "012-empty.png",
                    MediaType = "image/png",
                    ContentKind = TaskAttachmentContentKind.Image
                },
                new TaskAttachment
                {
                    Id = 13,
                    WorkspaceId = 1,
                    Title = "valid.png",
                    RelativePath = "013-valid.png",
                    MediaType = "image/png",
                    ContentKind = TaskAttachmentContentKind.Image
                }
            ]
        };

        var blocks = await acpHarness.BuildPromptContentBlocksAsync(
            acpContext,
            "Inspect images",
            supportsImagePrompt: true,
            sandboxPathResolver: null,
            CancellationToken.None);

        var serialized = JsonSerializer.Serialize(blocks);
        using var parsedDoc = JsonDocument.Parse(serialized);
        var arr = parsedDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);
        Assert.Equal("text", arr[0].GetProperty("type").GetString());
        Assert.Equal("image", arr[1].GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(validBytes), arr[1].GetProperty("data").GetString());
    }
}
