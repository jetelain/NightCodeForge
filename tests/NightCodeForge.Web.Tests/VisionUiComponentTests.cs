using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Acp;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Components.Workspaces.Steps;
using NightCodeForge.Web.Models;
using NightCodeForge.Web.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class VisionUiComponentTests : BunitContext
{
    private const string Valid1x1PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    public VisionUiComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task VisionCapabilityUiService_EvaluatesOverridesProbesAndAcpCorrectly()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(
        [
            new AgentProfile
            {
                Id = StandardStageIds.Ideation,
                Name = "Ideation",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = "text-coder"
            },
            new AgentProfile
            {
                Id = StandardStageIds.Implementation,
                Name = "Implement",
                HarnessId = StandardHarnessIds.Crucible,
                ModelName = "static-vision"
            },
            new AgentProfile
            {
                Id = StandardStageIds.TaskReview,
                Name = "Review",
                HarnessId = StandardHarnessIds.Acp,
                ModelName = "gemini-pro"
            }
        ]);

        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 1, Name = "Repo", RepositoryPath = "/repo" });

        var llmOptions = Options.Create(new LlmProviderOptions
        {
            ModelProfiles = new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["static-vision"] = new() { SupportsVision = true }
            }
        });

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("text-coder", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("qwen3-vl:8b", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var capMock = new Mock<ILlmCapabilityResolver>();
        capMock.Setup(c => c.SupportsVision(It.IsAny<string?>())).Returns(false);

        var service = new VisionCapabilityUiService(
            workspaceRepo,
            llmOptions,
            gatewayMock.Object,
            capMock.Object,
            NullLogger<VisionCapabilityUiService>.Instance);

        var textOnly = await service.EvaluateWorkspaceStageVisionAsync(ws.Id, StandardStageIds.Ideation);
        Assert.False(textOnly.SupportsVision);
        Assert.Equal("text-coder", textOnly.ModelName);
        Assert.Contains("does not support vision", textOnly.WarningMessage);

        var overrideVision = await service.EvaluateWorkspaceStageVisionAsync(ws.Id, StandardStageIds.Ideation, modelOverride: "qwen3-vl:8b");
        Assert.True(overrideVision.SupportsVision);
        Assert.Null(overrideVision.WarningMessage);

        var taskVision = await service.EvaluateWorkspaceTaskVisionAsync(ws.Id, TaskDifficulty.Standard);
        Assert.True(taskVision.SupportsVision);

        var acpVision = await service.EvaluateWorkspaceStageVisionAsync(ws.Id, StandardStageIds.TaskReview);
        Assert.True(acpVision.SupportsVision);
        Assert.True(acpVision.IsAcpHarness);
    }

    [Fact]
    public void AttachmentPresentationHelper_ClipboardImageAndComposerSubmitHelpers_WorkAsSpecified()
    {
        Assert.True(AttachmentPresentationHelper.TryDecodeClipboardImage(
            $"data:image/png;base64,{Valid1x1PngBase64}",
            "shot",
            "image/png",
            out var bytes,
            out var normalizedName,
            out var error));
        Assert.NotEmpty(bytes);
        Assert.Equal("shot.png", normalizedName);
        Assert.Null(error);

        Assert.False(AttachmentPresentationHelper.TryDecodeClipboardImage(
            Convert.ToBase64String("not an image"u8),
            "bad.png",
            "image/png",
            out _,
            out _,
            out var badErr));
        Assert.NotNull(badErr);

        var readyImgChip = new ConversationAttachmentChipViewModel
        {
            AttachmentId = 12,
            Title = "bug.png",
            ContentKind = TaskAttachmentContentKind.Image,
            Status = AttachmentIntakeStatus.Stored
        };

        Assert.True(AttachmentPresentationHelper.CanSubmitIdeationComposer(null, [readyImgChip], hasActiveWorkspace: true, isGenerating: false));
        Assert.False(AttachmentPresentationHelper.CanSubmitIdeationComposer(null, [], hasActiveWorkspace: true, isGenerating: false));

        var imgOnlyPrompt = AttachmentPresentationHelper.BuildEffectivePromptWithCitations(null, [readyImgChip]);
        Assert.StartsWith(VisionConstants.DefaultImageOnlyPrompt, imgOnlyPrompt);
        Assert.Contains("@attachment:12", imgOnlyPrompt);

        // Existing typed text must stay verbatim
        Assert.Equal("Inspect @attachment:12", AttachmentPresentationHelper.BuildEffectivePromptWithCitations("Inspect @attachment:12", [readyImgChip]));
    }

    [Fact]
    public async Task IdeationSessionState_PasteImageFromClipboard_UploadsAndSupportsImageOnlySend_WhileRejectingTextOnlyModels()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);

        var ws = new Workspace { Id = 11, Name = "ClipWS", RepositoryPath = "/clip" };
        var storedAttachment = new TaskAttachment
        {
            Id = 51,
            WorkspaceId = ws.Id,
            Title = "pasted.png",
            SourceRef = "pasted.png",
            MediaType = "image/png",
            ContentKind = TaskAttachmentContentKind.Image,
            SizeBytes = 68,
            Sha256 = new string('c', 64)
        };
        await attachRepo.CreateAsync(storedAttachment);

        var visionEnabled = true;
        var attachmentUiMock = new Mock<IAttachmentUiService>();
        attachmentUiMock.Setup(a => a.UploadFileAsync(ws.Id, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedAttachment);
        attachmentUiMock.Setup(a => a.LoadWorkspaceAttachmentsAsync(ws.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([storedAttachment]);
        attachmentUiMock.Setup(a => a.LoadImageThumbnailDataUriAsync(storedAttachment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync($"data:image/png;base64,{Valid1x1PngBase64}");
        attachmentUiMock.Setup(a => a.ExtractMessageCitations(It.IsAny<string?>()))
            .Returns<string?>(s => AttachmentPresentationHelper.ExtractMessageCitations(s));
        attachmentUiMock.Setup(a => a.IngestMessageCitationsAsync(ws.Id, It.IsAny<string?>(), It.IsAny<Action<ConversationAttachmentChipViewModel>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        attachmentUiMock.Setup(a => a.EvaluateIdeationVisionAsync(ws.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => visionEnabled
                ? new VisionCapabilityStatus(true, "qwen3-vl:8b", false, null)
                : new VisionCapabilityStatus(false, "text-model", false, "Model 'text-model' does not support vision."));

        var session = TestServices.CreateIdeationSessionState(
            coordinator: coordinator,
            taskRepository: taskRepo,
            workflowRepository: workflowRepo,
            attachmentRepository: attachRepo,
            attachmentUiService: attachmentUiMock.Object);

        await session.PasteImageFromClipboardAsync(ws, "pasted", "image/png", Valid1x1PngBase64);
        var chip = Assert.Single(session.PendingComposerChips);
        Assert.Equal(51, chip.AttachmentId);
        Assert.StartsWith("data:image/png;base64,", chip.ThumbnailDataUri);

        // Text-only gating prevents dispatch when model lacks vision
        visionEnabled = false;
        await session.RefreshVisionStatusAsync(ws.Id);
        Assert.Contains("does not support vision", session.VisionWarningMessage);
        Assert.False(await session.SendMessageAsync(ws, null));
        Assert.Single(session.PendingComposerChips);

        // Vision model accepts image-only submission cleanly
        visionEnabled = true;
        await session.RefreshVisionStatusAsync(ws.Id);
        Assert.True(await session.SendMessageAsync(ws, null));
        Assert.Empty(session.PendingComposerChips);
        Assert.Contains("@attachment:51", session.Messages[0].Content);
    }

    [Fact]
    public void AllowedModelsTable_DisplaysAndEditsSupportsVisionOverride()
    {
        List<OpenAiAllowedModel> models =
        [
            new() { ModelId = "gpt-4o", ContextWindowTokens = 128000, SupportsVision = true },
            new() { ModelId = "deepseek-chat", ContextWindowTokens = 64000, SupportsVision = false }
        ];

        var cut = Render<AllowedModelsTable>(ps => ps
            .Add(p => p.Models, models)
            .Add(p => p.ModelsChanged, EventCallback.Factory.Create<List<OpenAiAllowedModel>>(this, updated => models = updated)));

        Assert.Contains(VisionConstants.VisionBadgeLabel, cut.Markup);
        Assert.Contains("Text only", cut.Markup);

        cut.Find("#btn-add-allowed-model").Click();
        cut.Find("#input-model-id").Change("custom-vl");
        cut.Find("#select-supports-vision").Change("true");
        cut.Find("#btn-save-model").Click();

        var added = Assert.Single(models, m => m.ModelId == "custom-vl");
        Assert.True(added.SupportsVision);
    }

    [Fact]
    public void GatewayCardAndWizardEnvironment_RenderVisionCapabilityBadges()
    {
        var models = new List<AvailableModelInfo>
        {
            new("qwen3-vl:8b", "qwen3-vl:8b", 131072, SupportsVision: true),
            new("qwen2.5-coder:32b", "qwen2.5-coder:32b", 65536, SupportsVision: false)
        };

        var catalogMock = new Mock<ILlmModelCatalogService>();
        catalogMock.Setup(c => c.GetAvailableModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(models);
        catalogMock.Setup(c => c.VerifyEndpointAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EndpointVerificationResult(true, true, models.Count, models));

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.SetupGet(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        gatewayMock.Setup(g => g.GetGatewayStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewayStatusReport(true, LlmProviderType.OllamaRouter, "http://localhost:11434", "0.6.0", []));
        gatewayMock.Setup(g => g.GetResidentModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Services.AddSingleton(catalogMock.Object);
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(Mock.Of<ILlmConfigurationWriter>());
        Services.AddSingleton(Options.Create(new LlmProviderOptions()));
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(Mock.Of<IWslMetadataService>());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var gwCut = Render<OllamaRouterGatewayCard>();
        Assert.Contains(VisionConstants.VisionBadgeLabel, gwCut.Markup);

        var wizardCut = Render<WizardStep3Environment>(ps => ps
            .Add(p => p.Model, new WorkspaceCreationModel { UseDedicatedAgentProfiles = true, DedicatedModelName = "qwen3-vl:8b" }));
        Assert.Contains(VisionConstants.VisionBadgeLabel, wizardCut.Markup);
    }

    [Fact]
    public void StageConversationViewer_And_CreateTask_RenderVisualBadgesAndTextOnlyWarnings()
    {
        Services.AddSingleton<ITaskRepository>(new InMemoryTaskRepository());
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());

        var meta = ConversationHistorySerializer.BuildVisualTurnMetadataJson(
            [new ConversationHistorySerializer.SerializedImageRef("/workspace/attachments/bug.png", "image/png", "1234567890abcdef", 1024, 768, "call_vis_1")],
            isInjectedVisual: true,
            callId: "call_vis_1");

        var conv = new Conversation
        {
            Id = 1,
            StageId = StandardStageIds.Implementation,
            StartedAt = DateTime.UtcNow,
            Turns =
            [
                new ConversationTurn
                {
                    Id = 1,
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "[Visual Attachment: /workspace/attachments/bug.png (call_id: call_vis_1, dimensions: 1024x768)]",
                    MetadataJson = meta,
                    Timestamp = DateTime.UtcNow
                }
            ]
        };

        var viewerCut = Render<StageConversationViewer>(ps => ps.Add(p => p.Conversation, conv));
        Assert.Contains("visual-inspection-badge", viewerCut.Markup);
        Assert.Contains("/workspace/attachments/bug.png", viewerCut.Markup);
        Assert.Contains("1024x768", viewerCut.Markup);
    }

    [Fact]
    public async Task CreateTask_WhenTextOnlyModelAndImageSelected_RendersVisionWarning()
    {
        var workspaceState = new WorkspaceStateService();
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 20, Name = "WarnWS", RepositoryPath = "/repo" });
        await attachRepo.CreateAsync(new TaskAttachment
        {
            Id = 71,
            WorkspaceId = ws.Id,
            Title = "error-shot.png",
            ContentKind = TaskAttachmentContentKind.Image,
            MediaType = "image/png",
            SizeBytes = 1234
        });
        workspaceState.SetActiveWorkspace(ws);

        var visionMock = new Mock<IVisionCapabilityUiService>();
        visionMock.Setup(v => v.EvaluateWorkspaceTaskVisionAsync(ws.Id, It.IsAny<TaskDifficulty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(false, "qwen2.5-coder:32b", false, "Active model 'qwen2.5-coder:32b' does not support vision."));

        Services.AddSingleton(workspaceState);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<IWorkflowRepository>(workflowRepo);
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskAttachmentRepository>(attachRepo);
        Services.AddSingleton(visionMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        TestServices.AddDefaultWebUiServices(Services);

        var cut = Render<CreateTask>();
        cut.WaitForState(() => cut.FindAll(".attachment-option-item").Count > 0);
        cut.Find(".attachment-option-item").Click();

        var warning = cut.Find("#create-task-vision-warning");
        Assert.Contains("does not support vision", warning.TextContent);
    }

    [Fact]
    public async Task Ideation_RendersVisionWarningOnTextOnlyModel_AndRendersRehydratedVisualInspectionParts()
    {
        var workspaceState = new WorkspaceStateService();
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 30, Name = "IdeationVisionWS", RepositoryPath = "/repo" });
        workspaceState.SetActiveWorkspace(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var attachmentUiMock = new Mock<IAttachmentUiService>();
        attachmentUiMock.Setup(a => a.LoadWorkspaceAttachmentsAsync(ws.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TaskAttachment>());
        attachmentUiMock.Setup(a => a.ExtractMessageCitations(It.IsAny<string?>()))
            .Returns<string?>(s => AttachmentPresentationHelper.ExtractMessageCitations(s));
        attachmentUiMock.Setup(a => a.EvaluateIdeationVisionAsync(ws.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(false, "text-model", false, "Active model 'text-model' does not support vision."));

        var session = TestServices.CreateIdeationSessionState(
            coordinator: coordinator,
            taskRepository: taskRepo,
            workflowRepository: workflowRepo,
            attachmentRepository: attachRepo,
            attachmentUiService: attachmentUiMock.Object);

        await session.RefreshVisionStatusAsync(ws.Id);

        Services.AddSingleton(workspaceState);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<IWorkflowRepository>(workflowRepo);
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskAttachmentRepository>(attachRepo);
        Services.AddSingleton<IIdeationConversationCoordinator>(coordinator);
        Services.AddSingleton(session);
        Services.AddSingleton(TestServices.CreateToastService());
        TestServices.AddDefaultWebUiServices(Services);

        var cut = Render<Ideation>();
        await cut.InvokeAsync(() =>
        {
            session.PendingComposerChips.Add(new ConversationAttachmentChipViewModel
            {
                AttachmentId = 90,
                Title = "screen.png",
                ContentKind = TaskAttachmentContentKind.Image,
                Status = AttachmentIntakeStatus.Stored
            });
            session.AddMessage(new ChatMessage(
                "assistant",
                string.Empty,
                Parts:
                [
                    new VisualInspectionContentPart(
                        [new ConversationHistorySerializer.SerializedImageRef("/workspace/attachments/screen.png", "image/png", "sha256abc", 1024, 768, "call_vis")],
                        "Visual inspection complete")
                ]));
        });

        Assert.Contains("does not support vision", cut.Find("#ideation-vision-warning").TextContent);
        Assert.Contains("visual-inspection-badge", cut.Markup);
        Assert.Contains("/workspace/attachments/screen.png", cut.Markup);
        Assert.False(cut.Find("button.send-btn").HasAttribute("disabled"));

        // Verify TurnRehydrationMapper projects persisted StageTurnRole.User (IsInjectedVisual) into VisualInspectionContentPart
        var conv = new Conversation
        {
            Turns =
            [
                new ConversationTurn
                {
                    TurnIndex = 0,
                    Role = StageTurnRole.User,
                    Content = "[Visual Attachment: /workspace/attachments/screen.png]",
                    MetadataJson = ConversationHistorySerializer.BuildVisualTurnMetadataJson(
                        [new ConversationHistorySerializer.SerializedImageRef("/workspace/attachments/screen.png", "image/png", "sha256abc", 1024, 768, "call_vis")],
                        isInjectedVisual: true,
                        callId: "call_vis")
                }
            ]
        };
        var projected = IdeationTurnRehydrationMapper.ProjectConversation(conv, null, null, null, NullLogger.Instance);
        var projectedMsg = Assert.Single(projected.Messages);
        Assert.Equal("assistant", projectedMsg.Role);
        Assert.Contains(projectedMsg.Parts!, p => p is VisualInspectionContentPart vp && vp.Images.Any(i => i.SourcePath == "/workspace/attachments/screen.png"));
    }

    [Fact]
    public void AttachmentPresentationHelper_PreservesTypedTextWithReadyChips_AndFormatsAllowedModelsAndCitationDetectors()
    {
        var readyImgChip = new ConversationAttachmentChipViewModel
        {
            AttachmentId = 12,
            Title = "bug.png",
            ContentKind = TaskAttachmentContentKind.Image,
            Status = AttachmentIntakeStatus.Stored
        };

        var preserved = AttachmentPresentationHelper.BuildEffectivePromptWithCitations("Explain this UI bug", [readyImgChip]);
        Assert.Equal("Explain this UI bug", preserved);

        var metadataJson = IdeationTurnRehydrationMapper.SerializeUserAttachmentMetadata([readyImgChip]);
        Assert.NotNull(metadataJson);
        Assert.Contains(12, ConversationHistorySerializer.ExtractAttachmentIds(metadataJson));

        var allowedVision = new OpenAiAllowedModel { ModelId = "gpt-4o", DisplayName = "GPT-4o", ContextWindowTokens = 128000, SupportsVision = true };
        var allowedText = new OpenAiAllowedModel { ModelId = "deepseek-chat", ContextWindowTokens = 64000, SupportsVision = false };
        Assert.Contains(VisionConstants.VisionBadgeLabel, AttachmentPresentationHelper.FormatAllowedModelOptionLabel(allowedVision));
        Assert.DoesNotContain(VisionConstants.VisionBadgeLabel, AttachmentPresentationHelper.FormatAllowedModelOptionLabel(allowedText));

        var imgAtt = new TaskAttachment { Id = 21, Title = "shot.png", ContentKind = TaskAttachmentContentKind.Image };
        var txtAtt = new TaskAttachment { Id = 22, Title = "notes.txt", ContentKind = TaskAttachmentContentKind.PlainText };
        Assert.True(AttachmentPresentationHelper.HasPendingOrCitedImageAttachments([], [imgAtt, txtAtt], "Look at @attachment:21"));
        Assert.False(AttachmentPresentationHelper.HasPendingOrCitedImageAttachments([], [imgAtt, txtAtt], "Look at @attachment:22"));
        Assert.True(AttachmentPresentationHelper.HasSelectedOrCitedImageAttachments([imgAtt, txtAtt], [], "Ref @attachment:21"));
        Assert.False(AttachmentPresentationHelper.HasSelectedOrCitedImageAttachments([imgAtt, txtAtt], [22], "Ref @attachment:22"));
    }

    [Fact]
    public async Task VisionCapabilityUiService_NativeCliHarness_IsNotFlaggedAsAcpHarness()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(
        [
            new AgentProfile
            {
                Id = StandardStageIds.Implementation,
                Name = "NativeCli",
                HarnessId = StandardHarnessIds.NativeCli,
                ModelName = "cpu-only"
            }
        ]);
        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 41, Name = "CliWS", RepositoryPath = "/cli" });

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("cpu-only", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var capMock = new Mock<ILlmCapabilityResolver>();
        capMock.Setup(c => c.SupportsVision(It.IsAny<string?>())).Returns(false);

        var service = new VisionCapabilityUiService(
            workspaceRepo,
            Options.Create(new LlmProviderOptions()),
            gatewayMock.Object,
            capMock.Object,
            NullLogger<VisionCapabilityUiService>.Instance);

        var status = await service.EvaluateWorkspaceStageVisionAsync(ws.Id, StandardStageIds.Implementation);
        Assert.False(status.IsAcpHarness);
        Assert.False(status.SupportsVision);
    }

    [Fact]
    public async Task IdeationSessionState_ResetSessionAsync_RefreshesVisionStatus_AndReportClipboardImageErrorNotifies()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var ws = new Workspace { Id = 55, Name = "ResetVisionWS", RepositoryPath = "/reset" };

        var attachmentUiMock = new Mock<IAttachmentUiService>();
        attachmentUiMock.Setup(a => a.LoadWorkspaceAttachmentsAsync(ws.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TaskAttachment>());
        attachmentUiMock.Setup(a => a.ExtractMessageCitations(It.IsAny<string?>()))
            .Returns<string?>(s => AttachmentPresentationHelper.ExtractMessageCitations(s));
        attachmentUiMock.Setup(a => a.EvaluateIdeationVisionAsync(ws.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, string? ov, CancellationToken _) => string.IsNullOrWhiteSpace(ov)
                ? new VisionCapabilityStatus(true, "default-vision", false, null)
                : new VisionCapabilityStatus(false, ov, false, "Text-only override"));

        var session = TestServices.CreateIdeationSessionState(
            coordinator: coordinator,
            taskRepository: taskRepo,
            workflowRepository: workflowRepo,
            attachmentRepository: attachRepo,
            attachmentUiService: attachmentUiMock.Object);

        string? notifiedTitle = null;
        string? notifiedMessage = null;
        ToastLevel? notifiedLevel = null;
        session.OnNotification += (t, m, l) =>
        {
            notifiedTitle = t;
            notifiedMessage = m;
            notifiedLevel = l;
        };

        session.SelectedModelOverride = "text-override";
        await session.RefreshVisionStatusAsync(ws.Id);
        Assert.False(session.ActiveVisionStatus?.SupportsVision);

        await session.ResetSessionAsync(ws);
        Assert.Null(session.SelectedModelOverride);
        Assert.True(session.ActiveVisionStatus?.SupportsVision);
        Assert.Equal("default-vision", session.ActiveVisionStatus?.ModelName);

        session.ReportClipboardImageError("huge.png", "Pasted image exceeds maximum allowed vision image size.");
        var failedChip = Assert.Single(session.PendingComposerChips);
        Assert.Equal(AttachmentIntakeStatus.Failed, failedChip.Status);
        Assert.Equal("Clipboard Image Error", notifiedTitle);
        Assert.Contains("exceeds maximum", notifiedMessage);
        Assert.Equal(ToastLevel.Error, notifiedLevel);
    }

    [Fact]
    public async Task AttachmentUiService_LoadImageThumbnailDataUriAsync_DownscalesOversizedImageToThumbnailCap()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ncf-web-thumb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var img = new Image<Rgba32>(512, 256, new Rgba32(46, 139, 87));
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            var rawBytes = ms.ToArray();

            var filePath = Path.Combine(tempDir, "wide-ui.png");
            await File.WriteAllBytesAsync(filePath, rawBytes);

            var taskRepo = new InMemoryTaskRepository();
            var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);
            var wsRepo = new InMemoryWorkspaceRepository();
            var att = await attachRepo.CreateAsync(new TaskAttachment
            {
                Id = 91,
                WorkspaceId = 1,
                Title = "wide-ui.png",
                RelativePath = "wide-ui.png",
                MediaType = "image/png",
                ContentKind = TaskAttachmentContentKind.Image,
                WidthPx = 512,
                HeightPx = 256,
                SizeBytes = rawBytes.Length
            });

            var pathResolverMock = new Mock<IWorkspacePathResolver>();
            pathResolverMock.Setup(p => p.GetAttachmentsPath(1)).Returns(tempDir);
            pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);

            var uiService = new AttachmentUiService(
                Mock.Of<IAttachmentIntakeService>(),
                attachRepo,
                taskRepo,
                wsRepo,
                pathResolverMock.Object,
                Mock.Of<IIdeationConversationCoordinator>(),
                Mock.Of<IVisionCapabilityUiService>(),
                NullLogger<AttachmentUiService>.Instance);

            var dataUri = await uiService.LoadImageThumbnailDataUriAsync(att.Id);
            Assert.NotNull(dataUri);
            Assert.StartsWith("data:image/png;base64,", dataUri);

            var decoded = Convert.FromBase64String(dataUri!["data:image/png;base64,".Length..]);
            Assert.True(ImageHeaderInspector.TryInspect(decoded, out var thumbHeader));
            Assert.Equal(VisionConstants.ThumbnailMaxDimensionPx, thumbHeader.WidthPx);
            Assert.Equal(64, thumbHeader.HeightPx);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Ideation_And_CreateTask_RenderVisionWarningWhenImageCitedInText()
    {
        var workspaceState = new WorkspaceStateService();
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var attachRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 60, Name = "CiteWarnWS", RepositoryPath = "/repo" });
        var citedImg = await attachRepo.CreateAsync(new TaskAttachment
        {
            Id = 92,
            WorkspaceId = ws.Id,
            Title = "cited-bug.png",
            ContentKind = TaskAttachmentContentKind.Image,
            MediaType = "image/png",
            SizeBytes = 1024
        });
        workspaceState.SetActiveWorkspace(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var attachmentUiMock = new Mock<IAttachmentUiService>();
        attachmentUiMock.Setup(a => a.LoadWorkspaceAttachmentsAsync(ws.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([citedImg]);
        attachmentUiMock.Setup(a => a.ExtractMessageCitations(It.IsAny<string?>()))
            .Returns<string?>(s => AttachmentPresentationHelper.ExtractMessageCitations(s));
        attachmentUiMock.Setup(a => a.EvaluateIdeationVisionAsync(ws.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(false, "text-model", false, "Active model 'text-model' does not support vision."));

        var session = TestServices.CreateIdeationSessionState(
            coordinator: coordinator,
            taskRepository: taskRepo,
            workflowRepository: workflowRepo,
            attachmentRepository: attachRepo,
            attachmentUiService: attachmentUiMock.Object);

        var visionMock = new Mock<IVisionCapabilityUiService>();
        visionMock.Setup(v => v.EvaluateWorkspaceTaskVisionAsync(ws.Id, It.IsAny<TaskDifficulty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(false, "text-model", false, "Active model 'text-model' does not support vision."));

        Services.AddSingleton(workspaceState);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<IWorkflowRepository>(workflowRepo);
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        Services.AddSingleton<ITaskAttachmentRepository>(attachRepo);
        Services.AddSingleton<IIdeationConversationCoordinator>(coordinator);
        Services.AddSingleton(session);
        Services.AddSingleton(visionMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        TestServices.AddDefaultWebUiServices(Services);

        var ideationCut = Render<Ideation>();
        ideationCut.Find("#ideation-chat-textarea").Input("Investigate @attachment:92");
        Assert.Contains("does not support vision", ideationCut.Find("#ideation-vision-warning").TextContent);

        var createCut = Render<CreateTask>();
        createCut.WaitForState(() => createCut.FindAll("#task-desc-input").Count > 0);
        createCut.Find("#task-desc-input").Input("Fix layout shown in @attachment:92");
        Assert.Contains("does not support vision", createCut.Find("#create-task-vision-warning").TextContent);
    }

    [Fact]
    public async Task AgentProfilesTab_OpenAiCompatibleModel_RendersVisionBadgeFromGatewayDiscovery()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var initDb = new AppDbContext(dbOptions))
        {
            await initDb.Database.EnsureCreatedAsync();
            await initDb.EnsureDefaultGlobalAgentProfilesAsync("openrouter/vision-pro");
        }

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.SetupGet(g => g.ProviderType).Returns(LlmProviderType.OpenAiCompatible);
        gatewayMock.Setup(g => g.ResolveContextWindowAsync("openrouter/vision-pro", It.IsAny<CancellationToken>()))
            .ReturnsAsync(128000);
        gatewayMock.Setup(g => g.ResolveVisionCapabilityAsync("openrouter/vision-pro", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var catalogMock = new Mock<ILlmModelCatalogService>();
        catalogMock.Setup(c => c.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AvailableModelInfo>());

        Services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(connection));
        Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        Services.AddSingleton(catalogMock.Object);
        Services.AddSingleton(gatewayMock.Object);
        // EmptyLlmContextWindowCache simulates OpenAI discovery where ContextWindowCache isn't pre-populated
        Services.AddSingleton<ILlmContextWindowCache>(EmptyLlmContextWindowCache.Instance);
        Services.AddSingleton(Mock.Of<IAcpHarnessCatalogService>());
        Services.AddSingleton(Options.Create(new LlmProviderOptions { ProviderType = LlmProviderType.OpenAiCompatible }));
        Services.AddSingleton(TestServices.CreateToastService());

        var cut = Render<AgentProfilesTab>(ps => ps.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));
        cut.WaitForState(() => cut.Markup.Contains(VisionConstants.VisionBadgeLabel, StringComparison.Ordinal));

        Assert.Contains(VisionConstants.VisionBadgeLabel, cut.Markup);
    }
}
