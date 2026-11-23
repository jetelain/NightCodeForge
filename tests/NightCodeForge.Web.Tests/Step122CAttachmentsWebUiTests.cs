using System.Buffers.Binary;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Ideation;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Pmad.Markitdown;

namespace NightCodeForge.Web.Tests;

public class Step122CAttachmentsWebUiTests : BunitContext, IDisposable
{
    private readonly string _tempRoot;

    public Step122CAttachmentsWebUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ncf-step122c-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public new void Dispose()
    {
        try
        {
            base.Dispose();
        }
        finally
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AttachmentPresentationHelper_ResolvesExplicitCitationsFormatsStatusSizeSymlinkReachabilityAndFilters()
    {
        // 1. Supported format helpers
        Assert.True(AttachmentConstants.IsSupportedFileName("spec.md"));
        Assert.True(AttachmentConstants.IsSupportedFileName(@"C:\path\shot.png"));
        Assert.False(AttachmentConstants.IsSupportedFileName("ComicGen.App.exe"));
        Assert.False(AttachmentConstants.IsSupportedFileName("extensionless"));
        Assert.False(AttachmentConstants.IsSupportedFileName(null));
        Assert.Contains(".md", AttachmentConstants.HtmlAcceptExtensions);
        Assert.Contains(".png", AttachmentConstants.HtmlAcceptExtensions);
        Assert.DoesNotContain(".exe", AttachmentConstants.HtmlAcceptExtensions);

        // 2. Status, symlink, and reachability formatting
        Assert.Equal("stored", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Stored));
        Assert.Equal("symlinked", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Symlinked));
        Assert.Equal("converting", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Converting));
        Assert.Equal("badge-teal", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Stored));
        Assert.Equal("badge-indigo", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Symlinked));
        Assert.Equal("badge-amber", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Converting));

        var symlinkAtt = new TaskAttachment
        {
            Id = 2,
            Title = "router-spikes.md",
            Slug = "router-spikes",
            RelativePath = "002-router-spikes.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = @"C:\dev\poc\router-spikes.md",
            IsSymlink = true,
            SizeBytes = 1536
        };

        Assert.Equal(AttachmentIntakeStatus.Symlinked, AttachmentPresentationHelper.ResolveIntakeStatus(symlinkAtt));
        Assert.Equal("Reachable", AttachmentPresentationHelper.FormatTargetReachabilityLabel(true));
        Assert.Equal("Target Missing", AttachmentPresentationHelper.FormatTargetReachabilityLabel(false));
        Assert.Equal("badge-emerald", AttachmentPresentationHelper.GetTargetReachabilityBadgeClass(true));
        Assert.Equal("badge-crimson", AttachmentPresentationHelper.GetTargetReachabilityBadgeClass(false));

        // 3. Size & dimension formatting
        Assert.Equal("0 B", AttachmentPresentationHelper.FormatSizeBytes(0));
        Assert.Equal("512 B", AttachmentPresentationHelper.FormatSizeBytes(512));
        Assert.Equal("1.5 KB", AttachmentPresentationHelper.FormatSizeBytes(1536));
        Assert.Equal("2.5 MB", AttachmentPresentationHelper.FormatSizeBytes(2621440));
        Assert.Equal("1.5 GB", AttachmentPresentationHelper.FormatSizeBytes(1610612736));
        Assert.Equal("1280 x 720", AttachmentPresentationHelper.FormatDimensions(1280, 720));

        // 4. Citation snippet & composer insertion (without duplicating)
        Assert.Equal("@attachment:2 router-spikes.md", AttachmentPresentationHelper.FormatCitationSnippet(symlinkAtt));
        var appended = AttachmentPresentationHelper.AppendCitationToPrompt("Review", symlinkAtt);
        Assert.Equal("Review @attachment:2 router-spikes.md", appended);
        Assert.Equal(appended, AttachmentPresentationHelper.AppendCitationToPrompt(appended, symlinkAtt));

        // 5. Restored message chips resolve ONLY explicit @attachment:{id} citations (never raw host paths or URLs)
        var restoredFromRaw = AttachmentPresentationHelper.ResolveRestoredMessageChips(
            @"Inspect C:\dev\poc\router-spikes.md and https://example.com/rfc",
            [symlinkAtt]);
        Assert.Empty(restoredFromRaw);

        var restoredExplicit = AttachmentPresentationHelper.ResolveRestoredMessageChips(
            @"Inspect @attachment:2 router-spikes.md alongside C:\dev\poc\router-spikes.md",
            [symlinkAtt]);
        var restoredChip = Assert.Single(restoredExplicit);
        Assert.Equal(2, restoredChip.AttachmentId);

        // 6. Filtering by title, source ref, and #id
        var att1 = new TaskAttachment { Id = 1, Title = "OpenAI API Spec", Slug = "openai", SourceRef = "openai.pdf", SizeBytes = 100 };
        var att2 = symlinkAtt;
        var all = new[] { att1, att2 };
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "#2"), a => a.Id == 2);
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "router-spikes"), a => a.Id == 2);
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "OpenAI"), a => a.Id == 1);
        Assert.Equal(2, AttachmentPresentationHelper.FilterAttachments(all, "").Count);
    }

    [Fact]
    public async Task AttachmentUiService_CoordinatesExplicitIngestionPreviewReachabilityAndSafeDeletion()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 10);
        var hostFile = Path.Combine(_tempRoot, "host-spikes.md");
        await File.WriteAllTextAsync(hostFile, "# Host Spikes\nImportant routing rules.");
        var unattachedHostFile = Path.Combine(_tempRoot, "unattached-host.md");
        await File.WriteAllTextAsync(unattachedHostFile, "# Unattached\nShould never be auto-ingested.");

        // 1. Upload file drop + image file drop + explicit host path attach
        await using var mdStream = new MemoryStream(Encoding.UTF8.GetBytes("# Design Doc\nToken rotation spec."));
        var uploadedMd = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "design-doc.md", mdStream);

        await using var pngStream = new MemoryStream(CreateValidPngBytes(1280, 720));
        var uploadedImg = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "ui-mockup.png", pngStream);

        var symlinkAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, hostFile);

        // 2. ResolveCitedAttachmentChipsAsync resolves ONLY explicit @attachment:{id} and never auto-ingests paths or URLs
        var resolvedChips = await harness.UiService.ResolveCitedAttachmentChipsAsync(
            harness.Workspace.Id,
            $"Check \"{unattachedHostFile}\", C:\\app\\Tool.exe, https://example.com/rfc, and @attachment:{uploadedMd.Id} plus @attachment:{symlinkAtt.Id}");

        Assert.Equal(2, resolvedChips.Count);
        Assert.Contains(resolvedChips, c => c.AttachmentId == uploadedMd.Id && c.Status == AttachmentIntakeStatus.Stored);
        Assert.Contains(resolvedChips, c => c.AttachmentId == symlinkAtt.Id && c.Status == AttachmentIntakeStatus.Symlinked);
        Assert.Equal(3, (await harness.AttachmentRepo.GetByWorkspaceIdAsync(harness.Workspace.Id)).Count);

        // 3. Preview loading (markdown HTML + image DataUri + symlink reachability)
        var mdPreview = await harness.UiService.LoadPreviewAsync(uploadedMd.Id);
        Assert.Contains("<h1", mdPreview.RenderedHtml);
        Assert.Contains("Token rotation spec.", mdPreview.RenderedHtml);
        Assert.True(mdPreview.IsTargetReachable);
        Assert.Null(mdPreview.ContentReadError);

        var imgPreview = await harness.UiService.LoadPreviewAsync(uploadedImg.Id);
        Assert.NotNull(imgPreview.ImageDataUri);
        Assert.StartsWith("data:image/png;base64,", imgPreview.ImageDataUri);
        Assert.Equal(1280, imgPreview.Attachment.WidthPx);
        Assert.Equal(720, imgPreview.Attachment.HeightPx);

        // Even when the POSIX symlink inside attachments/ isn't directly readable on Windows,
        // LoadPreviewAsync resolves the direct host target without failing.
        var symlinkPreviewOk = await harness.UiService.LoadPreviewAsync(symlinkAtt.Id);
        Assert.True(symlinkPreviewOk.IsTargetReachable);
        Assert.Contains("Important routing rules.", symlinkPreviewOk.RenderedHtml);
        Assert.Null(symlinkPreviewOk.ContentReadError);

        // 4. Delete impact + safe deletion preserves host file
        var task = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Task Linking Host File",
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task.Id, [symlinkAtt.Id]);

        var impact = await harness.UiService.GetDeleteImpactAsync(symlinkAtt.Id);
        Assert.Single(impact.LinkedTasks, t => t.Id == task.Id);

        await harness.UiService.DeleteAttachmentAsync(symlinkAtt.Id);
        Assert.Null(await harness.AttachmentRepo.GetByIdAsync(symlinkAtt.Id));
        Assert.Empty(await harness.AttachmentRepo.GetByTaskIdAsync(task.Id));
        Assert.True(File.Exists(hostFile));

        // 5. Target Missing detection when host target disappears
        var disappearingHost = Path.Combine(_tempRoot, "temp-host.md");
        await File.WriteAllTextAsync(disappearingHost, "# Temp");
        var tempSymlink = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, disappearingHost);
        File.Delete(disappearingHost);

        var missingPreview = await harness.UiService.LoadPreviewAsync(tempSymlink.Id);
        Assert.False(missingPreview.IsTargetReachable);
    }

    [Fact]
    public async Task TaskCreationAndProposalPromotion_PersistAttachmentLinksFromSelectionAndCitations_AndFailFastOnUnknownId()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 20);
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("Spec 1"));
        await using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("Spec 2"));
        var att1 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "spec1.md", s1);
        var att2 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "spec2.md", s2);

        // 1. Ideation proposal promotion unions SelectedAttachmentIds + @attachment:{id} citations
        var proposalVm = IdeationProposalViewModel.FromProposal(
            new IdeationProposal(
                Title: "Proposed Routing Task",
                Description: $"Implement per @attachment:{att1.Id}.",
                AttachmentIds: [att2.Id]));
        Assert.Equal([att2.Id, att1.Id], proposalVm.SelectedAttachmentIds);

        var promoted = await IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync(
            harness.Workspace,
            proposalVm,
            harness.TaskRepo,
            harness.WorkflowRepo,
            harness.AttachmentRepo);

        var promotedLinks = await harness.AttachmentRepo.GetByTaskIdAsync(promoted.Id);
        Assert.Equal([att1.Id, att2.Id], promotedLinks.Select(a => a.Id).OrderBy(id => id).ToArray());

        // 2. TaskCreationUiService persists unioned AttachmentIds + @attachment:{id} and syncs markdown
        var markdownSyncMock = new Mock<ITaskMarkdownSyncService>();
        var creationService = new TaskCreationUiService(
            harness.TaskRepo,
            harness.WorkspaceRepo,
            harness.WorkflowRepo,
            markdownSyncMock.Object,
            new Mock<IGitHubTaskMirrorService>().Object,
            new Mock<IGitHubPullRequestDeliveryService>().Object,
            new Mock<ITaskJobQueue>().Object,
            harness.AttachmentRepo,
            Options.Create(new SecurityOptions()),
            NullLogger<TaskCreationUiService>.Instance);

        var createResult = await creationService.CreateTaskAsync(new TaskCreationRequest(
            WorkspaceId: harness.Workspace.Id,
            Workspace: harness.Workspace,
            RepositoryPath: harness.RepoDir,
            Title: "Created With Attachments",
            Description: $"Use @attachment:{att1.Id} specification.",
            WorkflowId: null,
            Priority: TaskPriority.High,
            MergeMode: MergeMode.ManualReviewRequired,
            BaseBranch: "main",
            FeatureBranch: null,
            InitialStatus: TaskOverallStatus.Open,
            PrerequisiteIds: null,
            LaunchImmediately: false,
            AuditReason: "UI creation test",
            AttachmentIds: [att2.Id]));

        var createdLinks = await harness.AttachmentRepo.GetByTaskIdAsync(createResult.Task.Id);
        Assert.Equal([att1.Id, att2.Id], createdLinks.Select(a => a.Id).OrderBy(id => id).ToArray());

        // Unknown attachment ID fails fast without creating task
        var beforeCount = (await harness.TaskRepo.GetByWorkspaceIdAsync(harness.Workspace.Id)).Count;
        await Assert.ThrowsAsync<AttachmentNotFoundException>(() => creationService.CreateTaskAsync(new TaskCreationRequest(
            WorkspaceId: harness.Workspace.Id,
            Workspace: harness.Workspace,
            RepositoryPath: harness.RepoDir,
            Title: "Invalid Attachment Task",
            Description: "Invalid",
            WorkflowId: null,
            Priority: TaskPriority.Normal,
            MergeMode: MergeMode.ManualReviewRequired,
            BaseBranch: "main",
            FeatureBranch: null,
            InitialStatus: TaskOverallStatus.Open,
            PrerequisiteIds: null,
            LaunchImmediately: false,
            AuditReason: "Should fail",
            AttachmentIds: [999999])));
        Assert.Equal(beforeCount, (await harness.TaskRepo.GetByWorkspaceIdAsync(harness.Workspace.Id)).Count);
    }

    [Fact]
    public async Task IdeationPage_ManagesAttachmentsInSidePaneTabs_RejectsUnsupportedFilesAndDirs_AndDropsAutoAttachmentFromChat()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 30);
        harness.WorkspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);

        var mockIdeation = new MockIdeationConversationCoordinator(harness.TaskRepo);
        var sessionState = TestServices.CreateIdeationSessionState(
            coordinator: mockIdeation,
            taskRepository: harness.TaskRepo,
            workflowRepository: harness.WorkflowRepo,
            attachmentRepository: harness.AttachmentRepo,
            attachmentUiService: harness.UiService);
        sessionState.BoundWorkspaceId = harness.Workspace.Id;
        mockIdeation.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Reviewed cited attachments."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Reviewed cited attachments.", []))
        ];

        var hostFile1 = Path.Combine(_tempRoot, "router-spikes.md");
        var unattachedDoc = Path.Combine(_tempRoot, "ignored-in-chat.md");
        var unsupportedExe = Path.Combine(_tempRoot, "ComicGen.App.exe");
        var subDirectory = Path.Combine(_tempRoot, "some-directory");
        await File.WriteAllTextAsync(hostFile1, "# Router Spikes\nSpike content.");
        await File.WriteAllTextAsync(unattachedDoc, "# Ignored\nMust not be auto-attached from message text.");
        await File.WriteAllBytesAsync(unsupportedExe, [0x4D, 0x5A, 0x90, 0x00]);
        Directory.CreateDirectory(subDirectory);

        RegisterHarnessServices(harness, wsState, sessionState, mockIdeation);

        var cut = Render<Ideation>();

        // 0. Side pane tabs: Attachments live on the right pane, NOT inline inside .chat-pane
        Assert.Empty(cut.Find(".chat-pane").QuerySelectorAll(".workspace-attachments-panel"));
        Assert.Empty(cut.FindAll("#btn-toggle-workspace-attachments"));
        Assert.NotNull(cut.Find("#ideation-side-tab-proposals"));
        Assert.NotNull(cut.Find("#ideation-side-tab-attachments"));
        Assert.Equal("true", cut.Find("#ideation-side-tab-proposals").GetAttribute("aria-selected"));

        // 1. File drop via <InputFile> (valid markdown + rejected .exe)
        var inputFile = cut.FindComponent<InputFile>();
        Assert.Contains(".md", inputFile.Instance.AdditionalAttributes!["accept"]?.ToString());
        inputFile.UploadFiles(
            InputFileContent.CreateFromText("# Drop Spec\nDropped markdown.", "drop-spec.md", contentType: "text/markdown"),
            InputFileContent.CreateFromText("MZ", "payload.exe", contentType: "application/octet-stream"));

        cut.WaitForAssertion(() =>
        {
            var chipsText = cut.Find(".composer-attachment-chips").TextContent;
            Assert.Contains("drop-spec.md", chipsText);
            Assert.Contains("stored", chipsText);
            Assert.DoesNotContain("payload.exe", chipsText);
            Assert.Contains("📎 Attachments", cut.Find("#ideation-side-tab-attachments").TextContent);
        });

        // 2. Attach Host File path input (directory & .exe rejected without leaving failed chips; valid file succeeds)
        cut.Find("#ideation-host-file-input").Input(subDirectory);
        cut.Find("#btn-attach-host-file").Click();
        cut.Find("#ideation-host-file-input").Input(unsupportedExe);
        cut.Find("#btn-attach-host-file").Click();
        Assert.DoesNotContain("ComicGen.App.exe", cut.Find(".composer-attachment-chips").TextContent);
        Assert.DoesNotContain("some-directory", cut.Find(".composer-attachment-chips").TextContent);

        cut.Find("#ideation-host-file-input").Input(hostFile1);
        cut.Find("#btn-attach-host-file").Click();

        cut.WaitForAssertion(() =>
        {
            var chipsText = cut.Find(".composer-attachment-chips").TextContent;
            Assert.Contains("router-spikes.md", chipsText);
            Assert.Contains("symlinked", chipsText);
            Assert.Empty(cut.FindAll(".composer-attachment-chips .attachment-symlink-badge"));
            Assert.DoesNotContain(VirtualMountEntry.ToDefaultWslPath(hostFile1), chipsText);
        });

        // 3. Switch right side pane to Attachments tab -> lists attachments, filters, and cites in chat (including mobile pane switch)
        await cut.InvokeAsync(() => cut.Find("#ideation-side-tab-attachments").Click());
        await cut.InvokeAsync(() => cut.Find("#ideation-tab-attachments").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("#ideation-side-tab-attachments").GetAttribute("aria-selected"));
            Assert.Equal("true", cut.Find("#ideation-tab-attachments").GetAttribute("aria-selected"));
            Assert.Equal("false", cut.Find("#ideation-tab-chat").GetAttribute("aria-selected"));
            var listText = cut.Find(".workspace-attachments-list").TextContent;
            Assert.Contains("drop-spec.md", listText);
            Assert.Contains("router-spikes.md", listText);
            Assert.Empty(cut.FindAll(".workspace-attachment-title-line .attachment-symlink-badge"));
        });

        await cut.InvokeAsync(() =>
        {
            var dropRow = cut.FindAll(".workspace-attachment-row").First(r => r.TextContent.Contains("drop-spec.md"));
            var dropAttId = dropRow.GetAttribute("data-attachment-id");
            cut.Find($".workspace-attachment-row[data-attachment-id='{dropAttId}'] .attachment-cite-btn").Click();
        });
        cut.WaitForAssertion(() =>
        {
            var textareaVal = cut.Find("textarea.chat-textarea").GetAttribute("value");
            Assert.Contains("@attachment:", textareaVal);
            Assert.Equal("true", cut.Find("#ideation-tab-chat").GetAttribute("aria-selected"));
        });

        // Filter workspace attachments panel in side pane
        cut.Find("#ideation-attachment-search-input").Input("router-spikes");
        var filteredRows = cut.FindAll(".workspace-attachment-row");
        Assert.Single(filteredRows);
        Assert.Contains("router-spikes.md", filteredRows[0].TextContent);
        cut.Find("#ideation-attachment-search-input").Input(string.Empty);

        var dropRowId = int.Parse(
            cut.FindAll(".workspace-attachment-row")
                .First(r => r.TextContent.Contains("drop-spec.md"))
                .GetAttribute("data-attachment-id")!,
            System.Globalization.CultureInfo.InvariantCulture);

        // 4. Send message with raw .exe path, directory, unattached doc, URL, AND explicit @attachment:{id}
        // -> NO auto-attachment for paths or URLs; ONLY explicit @attachment:{id} and composer chips appear
        var beforeCount = (await harness.AttachmentRepo.GetByWorkspaceIdAsync(harness.Workspace.Id)).Count;
        Assert.Equal(2, beforeCount);

        cut.Find("textarea.chat-textarea").Input(
            $"Please analyze \"{unsupportedExe}\", \"{subDirectory}\", \"{unattachedDoc}\", https://example.com/docs and @attachment:{dropRowId}");
        cut.Find("button.send-btn").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(sessionState.IsGenerating);
            Assert.Equal(2, sessionState.Messages.Count);
            var msgChips = cut.FindAll(".message-attachment-chips .attachment-chip");
            Assert.Equal(2, msgChips.Count);
            Assert.Contains(msgChips, c => c.TextContent.Contains("drop-spec.md"));
            Assert.Contains(msgChips, c => c.TextContent.Contains("router-spikes.md"));
            Assert.DoesNotContain(msgChips, c => c.TextContent.Contains("ComicGen.App.exe"));
            Assert.DoesNotContain(msgChips, c => c.TextContent.Contains("ignored-in-chat.md"));
            Assert.DoesNotContain(msgChips, c => c.TextContent.Contains("failed"));
            Assert.DoesNotContain(msgChips, c => c.TextContent.Contains(VirtualMountEntry.ToDefaultWslPath(hostFile1)));
            Assert.Empty(cut.FindAll(".message-attachment-chips .attachment-symlink-badge"));
        });
        Assert.Equal(2, (await harness.AttachmentRepo.GetByWorkspaceIdAsync(harness.Workspace.Id)).Count);

        // 5. Clicking a conversation chip opens AttachmentPreviewDrawer
        await cut.InvokeAsync(() =>
        {
            var clickableMsgChip = cut.FindAll(".message-attachment-chips .attachment-chip")
                .First(c => c.TextContent.Contains("router-spikes.md"));
            clickableMsgChip.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var drawer = cut.Find(".attachment-preview-drawer");
            Assert.Contains("router-spikes.md", drawer.TextContent);
            Assert.Contains("Spike content.", drawer.InnerHtml);
            Assert.Contains("Reachable", cut.Find("#attachment-reachability-badge").TextContent);
            Assert.Empty(cut.FindAll(".attachment-symlink-badge"));
        });

        // 6. Close preview, switch back to Proposals tab, add proposal with linked attachment to backlog
        await cut.InvokeAsync(() => cut.Find("#btn-close-attachment-preview").Click());
        Assert.Empty(cut.FindAll(".attachment-preview-drawer"));

        cut.Find("#ideation-side-tab-proposals").Click();
        sessionState.AddProposal(new IdeationProposalViewModel
        {
            Title = "Proposal With Linked Drop Spec",
            Description = $"Implement per @attachment:{dropRowId}",
            SelectedAttachmentIds = [dropRowId]
        });

        await cut.InvokeAsync(() => cut.Find("button.add-backlog-btn").Click());

        cut.WaitForAssertion(() => Assert.Contains("Added as ForgeTask", cut.Markup));
        var createdTask = Assert.Single(
            await harness.TaskRepo.GetByWorkspaceIdAsync(harness.Workspace.Id),
            t => t.Title == "Proposal With Linked Drop Spec");
        var linkedForCreatedTask = await harness.AttachmentRepo.GetByTaskIdAsync(createdTask.Id);
        Assert.Single(linkedForCreatedTask, a => a.Id == dropRowId);

        // 7. Side-pane Attachments tab Preview & Delete buttons open drawer and delete safely
        cut.Find("#ideation-side-tab-attachments").Click();
        var routerRowId = cut.FindAll(".workspace-attachment-row")
            .First(r => r.TextContent.Contains("router-spikes.md"))
            .GetAttribute("data-attachment-id");
        await cut.InvokeAsync(() =>
            cut.Find($".workspace-attachment-row[data-attachment-id='{routerRowId}'] .attachment-preview-btn").Click());
        cut.WaitForAssertion(() => Assert.Contains("router-spikes.md", cut.Find(".attachment-preview-drawer").TextContent));
        await cut.InvokeAsync(() => cut.Find("#btn-close-attachment-preview").Click());

        await cut.InvokeAsync(() =>
            cut.Find($".workspace-attachment-row[data-attachment-id='{routerRowId}'] .attachment-delete-btn").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".attachment-delete-confirm-modal")));
        await cut.InvokeAsync(() => cut.Find("#btn-confirm-delete-attachment").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".attachment-preview-drawer"));
            Assert.DoesNotContain("router-spikes.md", cut.Find(".workspace-attachments-list").TextContent);
        });
        Assert.True(File.Exists(hostFile1));
    }

    [Fact]
    public async Task IdeationProposalCard_RendersClickableAttachmentChips_AndOpensPreviewDrawer()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 35);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# API Spec\nEndpoints specification."));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "api-spec.md", stream);
        var hostFile = Path.Combine(_tempRoot, "host-proposal.md");
        await File.WriteAllTextAsync(hostFile, "# Host Proposal\nSymlinked proposal context.");
        var symlinkAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, hostFile);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        var proposal = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Implement Endpoints", $"Follow @attachment:{att.Id} guidelines.", AttachmentIds: [symlinkAtt.Id]));

        var cut = Render<IdeationProposalCard>(p => p
            .Add(c => c.Proposal, proposal)
            .Add(c => c.WorkspaceAttachments, [att, symlinkAtt]));

        Assert.Empty(cut.FindAll(".attachment-symlink-badge"));
        var chips = cut.FindAll(".proposal-attachment-chip");
        Assert.Equal(2, chips.Count);

        var apiChip = Assert.Single(chips, c => c.TextContent.Contains("api-spec.md"));
        Assert.Contains($"#{att.Id}", apiChip.TextContent);

        var symlinkChip = Assert.Single(chips, c => c.TextContent.Contains("host-proposal.md"));
        Assert.Contains($"#{symlinkAtt.Id}", symlinkChip.TextContent);
        Assert.Equal(symlinkAtt.SourceRef, symlinkChip.GetAttribute("title"));
        Assert.DoesNotContain(symlinkAtt.SourceRef, symlinkChip.TextContent);

        apiChip.Click();

        cut.WaitForAssertion(() =>
        {
            var drawer = cut.Find(".attachment-preview-drawer");
            Assert.Contains("Endpoints specification.", drawer.InnerHtml);
        });
    }

    [Fact]
    public async Task CreateTaskPage_PickerSelectsExistingUploadsFileMapsHostPathAndPersistsLinks()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 40);
        await using var existingStream = new MemoryStream(Encoding.UTF8.GetBytes("# Existing Context\nPre-uploaded doc."));
        var existingAtt = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "existing-context.md", existingStream);

        var hostFile = Path.Combine(_tempRoot, "create-task-host.md");
        await File.WriteAllTextAsync(hostFile, "# Host Context\nSymlinked during creation.");

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        var cut = Render<CreateTask>();

        // 1. Search and select existing workspace attachment
        cut.Find("#attachment-search-input").Input("existing-context");
        var options = cut.FindAll(".attachment-option-item");
        Assert.Single(options);
        options[0].Click();
        Assert.Contains("existing-context.md", cut.Find(".selected-attachments-chips").TextContent);
        cut.Find("#attachment-search-input").Input(string.Empty);

        // 2. Upload file via InputFile in picker
        var inputFile = cut.FindComponent<InputFile>();
        inputFile.UploadFiles(
            InputFileContent.CreateFromText("New uploaded doc", "picker-upload.md", contentType: "text/markdown"));

        cut.WaitForAssertion(() =>
            Assert.Contains("picker-upload.md", cut.Find(".selected-attachments-chips").TextContent));

        // 3. Attach host file in picker
        cut.Find("#create-task-host-file-input").Input(hostFile);
        cut.Find("#btn-create-task-attach-host-file").Click();

        cut.WaitForAssertion(() =>
        {
            var selectedChips = cut.Find(".selected-attachments-chips").TextContent;
            Assert.Contains("create-task-host.md", selectedChips);
            Assert.Empty(cut.FindAll(".attachment-symlink-badge"));
            Assert.DoesNotContain(VirtualMountEntry.ToDefaultWslPath(hostFile), selectedChips);
        });

        // 4. Submit task creation and verify TaskAttachmentLinks persisted
        cut.Find("#task-title-input").Input("Task With Picked Attachments");
        cut.Find("#btn-save-as-open").Click();

        ForgeTask? created = null;
        cut.WaitForAssertion(() =>
        {
            var tasks = harness.TaskRepo.GetByWorkspaceIdAsync(harness.Workspace.Id).GetAwaiter().GetResult();
            created = tasks.FirstOrDefault(t => t.Title == "Task With Picked Attachments");
            Assert.NotNull(created);
        });

        var linked = await harness.AttachmentRepo.GetByTaskIdAsync(created!.Id);
        Assert.Equal(3, linked.Count);
        Assert.Contains(linked, a => a.Id == existingAtt.Id);
        Assert.Contains(linked, a => a.Title == "picker-upload.md");
        Assert.Contains(linked, a => a.Title == "create-task-host.md" && a.IsSymlink);
    }

    [Fact]
    public async Task TaskDetailAndDrawer_RenderLinkedAttachmentChips_AndOpenPreviewDrawer()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 50);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Architecture Spec\nDetailed layout rules."));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "arch-spec.md", stream);
        var hostDetailFile = Path.Combine(_tempRoot, "host-detail.md");
        await File.WriteAllTextAsync(hostDetailFile, "# Host Detail\nSymlinked task context.");
        var symlinkAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, hostDetailFile);

        var task = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Task With Linked Spec",
            Description = "Implement per attachment.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task.Id, [att.Id, symlinkAtt.Id]);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        // 1. TaskDetail.razor
        var detailCut = Render<TaskDetail>(p => p.Add(t => t.TaskId, task.Id));
        Assert.Empty(detailCut.FindAll(".attachment-symlink-badge"));
        var detailChips = detailCut.FindAll(".task-attachments-section .task-attachment-chip");
        Assert.Equal(2, detailChips.Count);
        var detailChip = Assert.Single(detailChips, c => c.TextContent.Contains("arch-spec.md"));
        Assert.Contains($"#{att.Id}", detailChip.TextContent);
        var detailSymlinkChip = Assert.Single(detailChips, c => c.TextContent.Contains("host-detail.md"));
        Assert.Equal(symlinkAtt.SourceRef, detailSymlinkChip.GetAttribute("title"));
        Assert.DoesNotContain(symlinkAtt.SourceRef, detailSymlinkChip.TextContent);

        detailChip.Click();
        detailCut.WaitForAssertion(() =>
        {
            var drawer = detailCut.Find(".attachment-preview-drawer");
            Assert.Contains("Detailed layout rules.", drawer.InnerHtml);
        });

        // 2. TaskDetailDrawer.razor
        var drawerCut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        Assert.Empty(drawerCut.FindAll(".attachment-symlink-badge"));
        var drawerChips = drawerCut.FindAll(".task-attachments-section .task-attachment-chip");
        Assert.Equal(2, drawerChips.Count);
        var drawerChip = Assert.Single(drawerChips, c => c.TextContent.Contains("arch-spec.md"));
        var drawerSymlinkChip = Assert.Single(drawerChips, c => c.TextContent.Contains("host-detail.md"));
        Assert.Equal(symlinkAtt.SourceRef, drawerSymlinkChip.GetAttribute("title"));
        Assert.DoesNotContain(symlinkAtt.SourceRef, drawerSymlinkChip.TextContent);

        drawerChip.Click();
        drawerCut.WaitForAssertion(() =>
        {
            var previewDrawer = drawerCut.Find(".attachment-preview-drawer");
            Assert.Contains("Detailed layout rules.", previewDrawer.InnerHtml);
        });
    }

    [Fact]
    public async Task AttachmentPreviewDrawer_RendersImageAndMarkdownProvenanceReachabilityAndSafeDeleteConfirmation()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 60);
        var hostFile = Path.Combine(_tempRoot, "preserved-host-notes.md");
        await File.WriteAllTextAsync(hostFile, "# Host Notes\nMust never be deleted by UI.");

        var symlinkAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, hostFile);

        await using var pngStream = new MemoryStream(CreateValidPngBytes(1024, 768));
        var imgAtt = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "diagram.png", pngStream);

        var linkedTask = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Impacted Downstream Task",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(linkedTask.Id, [symlinkAtt.Id]);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        // 1. Image attachment preview renders <img>, dimensions, and MediaType badge
        var imgCut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, imgAtt.Id));

        imgCut.WaitForAssertion(() =>
        {
            var imgEl = imgCut.Find("img.attachment-preview-image");
            Assert.StartsWith("data:image/png;base64,", imgEl.GetAttribute("src"));
            Assert.Contains("1024 x 768", imgCut.Find(".attachment-dimensions-badge").TextContent);
            Assert.Contains("image/png", imgCut.Find(".attachment-mediatype-badge").TextContent);
            Assert.Contains(imgAtt.Sha256, imgCut.Find(".attachment-provenance-grid").TextContent);
        });

        // 2. Symlink preview shows Reachable, linked tasks, and safe delete confirmation preserving host file
        int? deletedCallbackId = null;
        var symlinkCut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, symlinkAtt.Id)
            .Add(d => d.OnDeleted, EventCallback.Factory.Create<int>(this, id => deletedCallbackId = id)));

        symlinkCut.WaitForAssertion(() =>
        {
            Assert.Empty(symlinkCut.FindAll(".attachment-symlink-badge"));
            var headerText = symlinkCut.Find(".attachment-drawer-header").TextContent;
            Assert.Contains("preserved-host-notes.md", headerText);
            Assert.DoesNotContain(symlinkAtt.SourceRef, headerText);
            Assert.Equal("Reachable", symlinkCut.Find("#attachment-reachability-badge").TextContent.Trim());
            Assert.Contains(symlinkAtt.SourceRef, symlinkCut.Find(".attachment-provenance-grid").TextContent);
            Assert.Contains("Must never be deleted by UI.", symlinkCut.Find(".attachment-markdown-preview").InnerHtml);
            Assert.Contains("Impacted Downstream Task", symlinkCut.Find(".attachment-linked-tasks-section").TextContent);
        });

        symlinkCut.Find("#btn-open-delete-attachment").Click();

        symlinkCut.WaitForAssertion(() =>
        {
            var confirmModal = symlinkCut.Find(".attachment-delete-confirm-modal");
            Assert.Contains($"#{linkedTask.Id}", confirmModal.TextContent);
            Assert.Contains("Impacted Downstream Task", confirmModal.TextContent);
            Assert.Contains("strictly never be deleted or modified", confirmModal.TextContent);
        });

        symlinkCut.Find("#btn-confirm-delete-attachment").Click();

        symlinkCut.WaitForAssertion(() => Assert.Equal(symlinkAtt.Id, deletedCallbackId));
        Assert.Null(await harness.AttachmentRepo.GetByIdAsync(symlinkAtt.Id));
        Assert.Empty(await harness.AttachmentRepo.GetByTaskIdAsync(linkedTask.Id));
        Assert.True(File.Exists(hostFile));
        Assert.False(File.Exists(Path.Combine(harness.AttachmentsDir, symlinkAtt.RelativePath)));

        // 3. Target Missing badge when host target is removed
        var vanishingHost = Path.Combine(_tempRoot, "vanishing-host.md");
        await File.WriteAllTextAsync(vanishingHost, "# Vanishing");
        var missingAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, vanishingHost);
        File.Delete(vanishingHost);

        var missingCut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, missingAtt.Id));

        missingCut.WaitForAssertion(() =>
        {
            Assert.Equal("Target Missing", missingCut.Find("#attachment-reachability-badge").TextContent.Trim());
            Assert.Contains("Target Missing", missingCut.Find(".attachment-target-missing-alert").TextContent);
        });
    }

    [Fact]
    public async Task RemoveAttachmentCitation_AndDeletingCitedAttachmentInProposalAndCreateTask_AllowsCleanTaskCreation()
    {
        // 1. Unit assertions for RemoveAttachmentCitation & FilterAttachments null-safety
        Assert.Equal(string.Empty, AttachmentPresentationHelper.RemoveAttachmentCitation(null, 5));
        Assert.Equal("Keep text", AttachmentPresentationHelper.RemoveAttachmentCitation("Keep text", 0));
        Assert.Equal(
            "Review\n  - Keep indent\nNext",
            AttachmentPresentationHelper.RemoveAttachmentCitation(
                "Review @attachment:7 router-spikes.md\n  - Keep indent\nNext",
                7,
                "router-spikes.md"));
        Assert.Equal(
            "Follow guidelines.",
            AttachmentPresentationHelper.RemoveAttachmentCitation("Follow @attachment:#7 guidelines.", 7));
        Assert.Equal(
            "Check and @attachment:3 other.md",
            AttachmentPresentationHelper.RemoveAttachmentCitation(
                "Check @attachment:2 spec.md and @attachment:3 other.md",
                2,
                "spec.md"));

        var nullFieldAttachments = new[]
        {
            new TaskAttachment { Id = 1, Title = null!, Slug = null!, SourceRef = null!, RelativePath = null! },
            new TaskAttachment { Id = 2, Title = "Alpha.md", Slug = "alpha", SourceRef = "alpha.md", RelativePath = "002-alpha.md" }
        };
        var filtered = AttachmentPresentationHelper.FilterAttachments(nullFieldAttachments, "alpha");
        Assert.Single(filtered, a => a.Id == 2);

        // 2. Deleting a cited attachment from IdeationProposalCard standalone drawer strips citation and permits promotion
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 70);
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("# Proposal Spec\nDetails."));
        var att1 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "proposal-spec.md", s1);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        var proposal = IdeationProposalViewModel.FromProposal(
            new IdeationProposal(
                "Cited Proposal",
                $"Implement per @attachment:{att1.Id} {att1.Title} carefully."));

        var proposalCut = Render<IdeationProposalCard>(p => p
            .Add(c => c.Proposal, proposal)
            .Add(c => c.WorkspaceAttachments, [att1]));

        proposalCut.Find(".proposal-attachment-chip").Click();
        proposalCut.WaitForAssertion(() => proposalCut.Find("#btn-open-delete-attachment"));
        proposalCut.Find("#btn-open-delete-attachment").Click();
        proposalCut.WaitForAssertion(() => proposalCut.Find("#btn-confirm-delete-attachment"));
        proposalCut.Find("#btn-confirm-delete-attachment").Click();

        proposalCut.WaitForAssertion(() =>
        {
            Assert.Empty(proposalCut.FindAll(".proposal-attachments-section"));
            Assert.DoesNotContain($"@attachment:{att1.Id}", proposal.Description);
            Assert.DoesNotContain(att1.Id, proposal.SelectedAttachmentIds);
        });

        var promoted = await IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync(
            harness.Workspace,
            proposal,
            harness.TaskRepo,
            harness.WorkflowRepo,
            harness.AttachmentRepo);
        Assert.Equal("Cited Proposal", promoted.Title);

        // 3. Deleting a cited attachment in CreateTask strips citation from description so saving succeeds
        await using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("# Create Spec\nDetails."));
        var att2 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "create-cited.md", s2);

        var createCut = Render<CreateTask>();
        createCut.Find("#task-title-input").Input("Task After Deleting Cited Attachment");
        createCut.Find("#task-desc-input").Input($"Follow @attachment:{att2.Id} {att2.Title} rules.");

        createCut.WaitForAssertion(() => createCut.Find(".attachment-option-item .attachment-preview-btn"));
        createCut.Find(".attachment-option-item .attachment-preview-btn").Click();
        createCut.WaitForAssertion(() => createCut.Find("#btn-open-delete-attachment"));
        createCut.Find("#btn-open-delete-attachment").Click();
        createCut.WaitForAssertion(() => createCut.Find("#btn-confirm-delete-attachment"));
        createCut.Find("#btn-confirm-delete-attachment").Click();

        createCut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("create-cited.md", createCut.Find(".attachment-options-container").TextContent);
            var descVal = createCut.Find("#task-desc-input").GetAttribute("value") ?? string.Empty;
            Assert.DoesNotContain($"@attachment:{att2.Id}", descVal);
        });

        createCut.Find("#btn-save-as-open").Click();
        createCut.WaitForAssertion(() =>
        {
            var tasks = harness.TaskRepo.GetByWorkspaceIdAsync(harness.Workspace.Id).GetAwaiter().GetResult();
            Assert.Contains(tasks, t => t.Title == "Task After Deleting Cited Attachment");
        });
    }

    [Fact]
    public async Task IdeationChips_DeduplicateAcrossComposerAndMessageCitations_AndRestoreOnSessionLoad()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 71);
        harness.WorkspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));

        var hostFile = Path.Combine(_tempRoot, "dedup-host.md");
        await File.WriteAllTextAsync(hostFile, "# Dedup Host\nContent.");
        var existingAtt = await harness.UiService.AttachHostFileAsync(harness.Workspace.Id, hostFile);

        // 1. ResolveCitedAttachmentChipsAsync deduplicates duplicate explicit citations resolving to the same AttachmentId
        var serviceChips = await harness.UiService.ResolveCitedAttachmentChipsAsync(
            harness.Workspace.Id,
            $"Check @attachment:{existingAtt.Id} and @attachment:#{existingAtt.Id}");
        Assert.Single(serviceChips, c => c.AttachmentId == existingAtt.Id);

        // 2. ResolveRestoredMessageChips resolves user-turn chips from saved history
        var restored = AttachmentPresentationHelper.ResolveRestoredMessageChips(
            $"Check \"{hostFile}\" and @attachment:{existingAtt.Id}",
            [existingAtt]);
        var restoredChip = Assert.Single(restored);
        Assert.Equal(existingAtt.Id, restoredChip.AttachmentId);
        Assert.Equal(AttachmentIntakeStatus.Symlinked, restoredChip.Status);

        // 3. Ideation page deduplicates composer chip + message citation chip and restores user chips on session load
        var conv = await harness.TaskRepo.StartIdeationConversationAsync(harness.Workspace.Id, "Restored Session");
        await harness.TaskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.User,
            content: $"Initial review of @attachment:{existingAtt.Id}");

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        var mockIdeation = new MockIdeationConversationCoordinator(harness.TaskRepo)
        {
            StreamUpdatesToYield =
            [
                new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Done."),
                new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Done.", []))
            ]
        };
        var sessionState = TestServices.CreateIdeationSessionState(
            coordinator: mockIdeation,
            taskRepository: harness.TaskRepo,
            workflowRepository: harness.WorkflowRepo,
            attachmentRepository: harness.AttachmentRepo,
            attachmentUiService: harness.UiService);
        RegisterHarnessServices(harness, wsState, sessionState, mockIdeation);

        var cut = Render<Ideation>();

        // Restored user turn displays attachment chip
        cut.WaitForAssertion(() =>
        {
            var initialChips = cut.FindAll(".message-attachment-chips .attachment-chip");
            Assert.Single(initialChips, c => c.TextContent.Contains($"#{existingAtt.Id}"));
        });

        // Attach same host file in composer AND cite both host path & @attachment:{id} in next message
        await cut.InvokeAsync(() => cut.Find("#ideation-host-file-input").Input(hostFile));
        await cut.InvokeAsync(() => cut.Find("#btn-attach-host-file").Click());
        cut.WaitForAssertion(() =>
        {
            var composerChips = cut.Find(".composer-attachment-chips").TextContent;
            Assert.Contains("dedup-host.md", composerChips);
            Assert.Contains("symlinked", composerChips);
        }, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find("textarea.chat-textarea").Input($"Re-check \"{hostFile}\" and @attachment:{existingAtt.Id}"));
        await cut.InvokeAsync(() => cut.Find("button.send-btn").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.False(sessionState.IsGenerating);
            var userMessages = sessionState.Messages.Where(m => m.Role == "user").ToList();
            Assert.Equal(2, userMessages.Count);
            Assert.Single(userMessages[1].AttachmentChips!, c => c.AttachmentId == existingAtt.Id);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AttachmentUiService_BlocksPathTraversalOutsideWorkspaceAttachmentsDirectory()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 72);
        var outsideSecretPath = Path.Combine(Path.GetDirectoryName(harness.AttachmentsDir)!, "outside-secret.md");
        await File.WriteAllTextAsync(outsideSecretPath, "TOP_SECRET_OUTSIDE_CONTENT");

        var traversalAtt = await harness.AttachmentRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "outside-secret.md",
            Slug = "outside-secret",
            RelativePath = "../outside-secret.md",
            MarkdownPath = "../outside-secret.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "outside-secret.md",
            IsSymlink = false,
            Sha256 = "0000",
            SizeBytes = 26
        });

        var preview = await harness.UiService.LoadPreviewAsync(traversalAtt.Id);
        Assert.DoesNotContain("TOP_SECRET_OUTSIDE_CONTENT", preview.MarkdownContent);
        Assert.DoesNotContain("TOP_SECRET_OUTSIDE_CONTENT", preview.RenderedHtml);
        Assert.True(string.IsNullOrEmpty(preview.MarkdownContent));
    }

    [Fact]
    public async Task IdeationProposalViewModel_SyncFromTask_ReplacesAttachmentsFromPersistedDraftTaskAndCitations()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 73);
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("Doc 1"));
        await using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("Doc 2"));
        await using var s3 = new MemoryStream(Encoding.UTF8.GetBytes("Doc 3"));
        var att1 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "doc1.md", s1);
        var att2 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "doc2.md", s2);
        var att3 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "doc3.md", s3);

        var draftTask = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Draft Proposal Task",
            Description = $"Draft body citing @attachment:{att3.Id}",
            Status = TaskOverallStatus.Draft,
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(draftTask.Id, [att1.Id]);
        draftTask.AttachmentLinks =
        [
            new TaskAttachmentLink
            {
                TaskId = draftTask.Id,
                TaskAttachmentId = att1.Id,
                TaskAttachment = att1
            }
        ];

        var proposalVm = IdeationProposalViewModel.FromProposal(
            new IdeationProposal(
                "Draft Proposal Task",
                "Draft body",
                TaskId: draftTask.Id,
                AttachmentIds: [att1.Id, att2.Id]));

        var sessionState = TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService);
        sessionState.Proposals.Add(proposalVm);
        sessionState.SyncProposalsWithWorkspaceTasks([draftTask]);

        Assert.Contains(att1.Id, proposalVm.SelectedAttachmentIds);
        Assert.Contains(att3.Id, proposalVm.SelectedAttachmentIds);
        Assert.DoesNotContain(att2.Id, proposalVm.SelectedAttachmentIds);
    }

    [Fact]
    public async Task AttachmentPreviewDrawer_CancelDeleteConfirmation_RemainsClosedAcrossParentRerenders()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 74);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# Cancel Test\nContent."));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "cancel-test.md", stream);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        var cut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, att.Id)
            .Add(d => d.StartInDeleteConfirmation, true));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".attachment-delete-confirm-modal")));

        cut.Find("#btn-cancel-delete-attachment").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".attachment-delete-confirm-modal")));

        // Simulate a parent re-render passing the same StartInDeleteConfirmation=true parameter
        cut.Render(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, att.Id)
            .Add(d => d.StartInDeleteConfirmation, true));

        Assert.Empty(cut.FindAll(".attachment-delete-confirm-modal"));
    }

    [Fact]
    public async Task TaskDetailDrawer_DeletesLastLinkedAttachmentCleanly_AndResolvesCitationsAfterDescriptionEdit()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 75);
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("# Initial Spec\nFirst."));
        await using var s2 = new MemoryStream(Encoding.UTF8.GetBytes("# Added Spec\nSecond."));
        var att1 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "drawer-doc.md", s1);
        var att2 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "cited-after-edit.md", s2);

        var task = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Drawer Attachment Lifecycle Task",
            Description = $"See @attachment:{att1.Id} {att1.Title}.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task.Id, [att1.Id]);
        task.AttachmentLinks =
        [
            new TaskAttachmentLink
            {
                TaskId = task.Id,
                TaskAttachmentId = att1.Id,
                TaskAttachment = att1
            }
        ];

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: harness.UiService), new MockIdeationConversationCoordinator(harness.TaskRepo));

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        cut.WaitForAssertion(() =>
            Assert.Contains("drawer-doc.md", cut.Find(".task-attachments-section").TextContent));

        // 1. Delete the last attachment via preview drawer -> chip disappears immediately
        cut.Find(".task-attachments-section .task-attachment-chip").Click();
        cut.WaitForAssertion(() => cut.Find("#btn-open-delete-attachment"));
        cut.Find("#btn-open-delete-attachment").Click();
        cut.WaitForAssertion(() => cut.Find("#btn-confirm-delete-attachment"));
        cut.Find("#btn-confirm-delete-attachment").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".task-attachments-section .task-attachment-chip"));
            Assert.Contains("No context attachments linked to this task.", cut.Find(".task-attachments-section").TextContent);
            Assert.DoesNotContain($"@attachment:{att1.Id}", task.Description);
        });

        // 2. Edit description to cite att2 -> chip surfaces immediately
        var editDescBtn = cut.FindAll(".drawer-section")
            .First(s => s.TextContent.Contains("Description & Specification"))
            .QuerySelector("button")!;
        editDescBtn.Click();

        cut.Find(".edit-description-box textarea").Change($"Updated spec referencing @attachment:{att2.Id}.");
        cut.Find(".edit-description-box button.forge-btn-primary").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains("cited-after-edit.md", cut.Find(".task-attachments-section").TextContent));
    }

    [Fact]
    public async Task AttachmentPreviewDrawer_WhenLoadPreviewFailsWithPathError_StillAllowsDeletion()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 76);
        var brokenAtt = new TaskAttachment
        {
            Id = 202,
            WorkspaceId = harness.Workspace.Id,
            Title = "config.json",
            Slug = "config",
            RelativePath = "002-config.json",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "application/json",
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = "/mnt/c/Users/Test/config.json",
            IsSymlink = true,
            Sha256 = new string('a', 64),
            SizeBytes = 2700
        };

        var uiMock = new Mock<IAttachmentUiService>();
        uiMock.Setup(u => u.LoadPreviewAsync(brokenAtt.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DirectoryNotFoundException(
                @"Could not find a part of the path '\\wsl.localhost\Ubuntu\home\gruearbre\.nightcodeforge\wks\1\attachments\002-config.json'."));
        uiMock.Setup(u => u.GetDeleteImpactAsync(brokenAtt.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AttachmentDeleteImpact(brokenAtt, Array.Empty<ForgeTask>()));
        uiMock.Setup(u => u.DeleteAttachmentAsync(brokenAtt.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, TestServices.CreateIdeationSessionState(taskRepository: harness.TaskRepo, workflowRepository: harness.WorkflowRepo, attachmentRepository: harness.AttachmentRepo, attachmentUiService: uiMock.Object), new MockIdeationConversationCoordinator(harness.TaskRepo));
        Services.AddSingleton<IAttachmentUiService>(uiMock.Object);

        int? deletedId = null;
        var closed = false;
        var cut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, brokenAtt.Id)
            .Add(d => d.StartInDeleteConfirmation, true)
            .Add(d => d.OnClose, EventCallback.Factory.Create(this, () => closed = true))
            .Add(d => d.OnDeleted, EventCallback.Factory.Create<int>(this, id => deletedId = id)));

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".attachment-delete-confirm-modal"));
            Assert.Contains("Unable to read preview content", cut.Find(".attachment-content-read-error").TextContent);
            Assert.Empty(cut.FindAll("#attachment-reachability-badge"));
            Assert.Empty(cut.FindAll(".attachment-target-missing-alert"));
            Assert.NotNull(cut.Find("#btn-confirm-delete-attachment"));
        });

        await cut.InvokeAsync(() => cut.Find("#btn-confirm-delete-attachment").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(brokenAtt.Id, deletedId);
            Assert.True(closed);
        });
        uiMock.Verify(u => u.DeleteAttachmentAsync(brokenAtt.Id, It.IsAny<CancellationToken>()), Times.Once);

        // When even GetDeleteImpactAsync fails, fallback delete requires a two-step confirmation before deleting
        uiMock.Setup(u => u.GetDeleteImpactAsync(brokenAtt.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Metadata lookup failed"));

        int? fallbackDeletedId = null;
        var fallbackClosed = false;
        var fallbackCut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, brokenAtt.Id)
            .Add(d => d.OnClose, EventCallback.Factory.Create(this, () => fallbackClosed = true))
            .Add(d => d.OnDeleted, EventCallback.Factory.Create<int>(this, id => fallbackDeletedId = id)));

        fallbackCut.WaitForAssertion(() => Assert.NotNull(fallbackCut.Find("#btn-fallback-delete-attachment")));
        await fallbackCut.InvokeAsync(() => fallbackCut.Find("#btn-fallback-delete-attachment").Click());

        // Clicking the initial fallback delete button only reveals confirmation, without deleting yet
        fallbackCut.WaitForAssertion(() =>
        {
            Assert.NotNull(fallbackCut.Find("#btn-cancel-fallback-delete-attachment"));
            Assert.NotNull(fallbackCut.Find("#btn-confirm-fallback-delete-attachment"));
        });
        uiMock.Verify(u => u.DeleteAttachmentAsync(brokenAtt.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(fallbackDeletedId);
        Assert.False(fallbackClosed);

        // Cancelling returns to the initial fallback button
        await fallbackCut.InvokeAsync(() => fallbackCut.Find("#btn-cancel-fallback-delete-attachment").Click());
        fallbackCut.WaitForAssertion(() =>
        {
            Assert.NotNull(fallbackCut.Find("#btn-fallback-delete-attachment"));
            Assert.Empty(fallbackCut.FindAll("#btn-confirm-fallback-delete-attachment"));
        });
        uiMock.Verify(u => u.DeleteAttachmentAsync(brokenAtt.Id, It.IsAny<CancellationToken>()), Times.Once);

        // Confirming deletes the attachment and closes the drawer
        await fallbackCut.InvokeAsync(() => fallbackCut.Find("#btn-fallback-delete-attachment").Click());
        fallbackCut.WaitForAssertion(() => Assert.NotNull(fallbackCut.Find("#btn-confirm-fallback-delete-attachment")));
        await fallbackCut.InvokeAsync(() => fallbackCut.Find("#btn-confirm-fallback-delete-attachment").Click());
        fallbackCut.WaitForAssertion(() =>
        {
            Assert.Equal(brokenAtt.Id, fallbackDeletedId);
            Assert.True(fallbackClosed);
        });
        uiMock.Verify(u => u.DeleteAttachmentAsync(brokenAtt.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));

        // When GetDeleteImpactAsync throws AttachmentNotFoundException, do not offer fallback delete for a non-existent attachment
        uiMock.Setup(u => u.GetDeleteImpactAsync(brokenAtt.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AttachmentNotFoundException(brokenAtt.Id));

        var missingFallbackCut = Render<AttachmentPreviewDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.AttachmentId, brokenAtt.Id));

        missingFallbackCut.WaitForAssertion(() =>
        {
            var errText = missingFallbackCut.Find(".attachment-load-error").TextContent;
            Assert.Contains($"#{brokenAtt.Id}", errText);
            Assert.Empty(missingFallbackCut.FindAll("#btn-fallback-delete-attachment"));
        });
    }

    [Fact]
    public async Task TaskDetailDrawer_DeleteAttachment_PersistsCitationCleanupAcrossWorkspaceTasksAndSyncsMarkdown()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 75);
        await using var imgStream = new MemoryStream(CreateValidPngBytes(32, 32));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "bug-capture.png", imgStream);

        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var syncServices = new ServiceCollection();
        syncServices.AddSingleton<ITaskRepository>(harness.TaskRepo);
        syncServices.AddSingleton<ITaskAttachmentRepository>(harness.AttachmentRepo);
        syncServices.AddSingleton<ITaskDependencyStore>(depStore);
        syncServices.AddSingleton<IDependencyResolutionEngine>(depEngine);
        using var syncSp = syncServices.BuildServiceProvider();

        var realSyncService = new TaskMarkdownSyncService(
            syncSp.GetRequiredService<IServiceScopeFactory>(),
            harness.PathResolver,
            NullLogger<TaskMarkdownSyncService>.Instance);

        var visionMock = new Mock<IVisionCapabilityUiService>();
        var realUiService = new AttachmentUiService(
            harness.IntakeService,
            harness.AttachmentRepo,
            harness.TaskRepo,
            harness.WorkspaceRepo,
            harness.PathResolver,
            Mock.Of<IIdeationConversationCoordinator>(),
            visionMock.Object,
            realSyncService,
            harness.LockProvider,
            NullLogger<AttachmentUiService>.Instance);

        var task1 = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Drawer Task One",
            Description = $"Fix bug shown in @attachment:{att.Id} {att.Title} right away.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task1.Id, [att.Id]);

        var task2 = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Sibling Task Two",
            Description = $"Also check (@attachment:{att.Id} {att.Title}).",
            Status = TaskOverallStatus.Suggested,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task2.Id, [att.Id]);

        var closedTask = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Archived Closed Task",
            Description = $"Historical reference @attachment:{att.Id}",
            Status = TaskOverallStatus.Closed,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");
        var closedInitialVersion = closedTask.Version;

        await realSyncService.SyncAllTasksToDiskAsync(harness.Workspace, harness.RepoDir);
        var tasksDir = Path.Combine(harness.RepoDir, "docs", "tasks");
        Assert.Contains(Directory.GetFiles(tasksDir, "*.md"), f => File.ReadAllText(f).Contains($"@attachment:{att.Id}"));

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        var mockCoordinator = new MockIdeationConversationCoordinator(harness.TaskRepo);
        RegisterHarnessServices(
            harness,
            wsState,
            TestServices.CreateIdeationSessionState(
                taskRepository: harness.TaskRepo,
                workflowRepository: harness.WorkflowRepo,
                attachmentRepository: harness.AttachmentRepo,
                attachmentUiService: realUiService),
            mockCoordinator);
        Services.AddSingleton<ITaskMarkdownSyncService>(realSyncService);
        Services.AddSingleton<IAttachmentUiService>(realUiService);

        ForgeTask? callbackTask = null;
        var drawerCut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task1)
            .Add(d => d.OnTaskUpdated, EventCallback.Factory.Create<ForgeTask>(this, t => callbackTask = t)));

        drawerCut.WaitForAssertion(() => Assert.NotNull(drawerCut.Find(".task-attachment-chip")), TimeSpan.FromSeconds(10));
        await drawerCut.InvokeAsync(() => drawerCut.Find(".task-attachment-chip").Click());

        drawerCut.WaitForAssertion(() => Assert.NotNull(drawerCut.Find("#btn-open-delete-attachment")), TimeSpan.FromSeconds(10));
        await drawerCut.InvokeAsync(() => drawerCut.Find("#btn-open-delete-attachment").Click());

        drawerCut.WaitForAssertion(() => Assert.NotNull(drawerCut.Find("#btn-confirm-delete-attachment")), TimeSpan.FromSeconds(10));
        await drawerCut.InvokeAsync(() => drawerCut.Find("#btn-confirm-delete-attachment").Click());

        drawerCut.WaitForAssertion(() => Assert.NotNull(callbackTask), TimeSpan.FromSeconds(10));

        Assert.Null(await harness.AttachmentRepo.GetByIdAsync(att.Id));

        var task1Persisted = await harness.TaskRepo.GetByIdAsync(task1.Id);
        Assert.NotNull(task1Persisted);
        Assert.DoesNotContain($"@attachment:{att.Id}", task1Persisted!.Description);
        Assert.Equal("Fix bug shown in right away.", task1Persisted.Description);
        Assert.True(task1Persisted.Version > 1);
        Assert.Equal(task1Persisted.Version, callbackTask!.Version);
        Assert.Empty(await harness.AttachmentRepo.GetByTaskIdAsync(task1.Id));

        var task2Persisted = await harness.TaskRepo.GetByIdAsync(task2.Id);
        Assert.NotNull(task2Persisted);
        Assert.DoesNotContain($"@attachment:{att.Id}", task2Persisted!.Description);
        Assert.True(task2Persisted.Version > 1);
        Assert.Empty(await harness.AttachmentRepo.GetByTaskIdAsync(task2.Id));

        var closedPersisted = await harness.TaskRepo.GetByIdAsync(closedTask.Id);
        Assert.Equal(closedInitialVersion, closedPersisted!.Version);

        // Disk markdown for active tasks must no longer contain @attachment:{id} or attachment_ids
        var task1MdPath = Directory.GetFiles(tasksDir, "*drawer-task-one*.md").Single();
        var task2MdPath = Directory.GetFiles(tasksDir, "*sibling-task-two*.md").Single();
        Assert.DoesNotContain("@attachment:", await File.ReadAllTextAsync(task1MdPath));
        Assert.DoesNotContain("attachment_ids:", await File.ReadAllTextAsync(task1MdPath));
        Assert.DoesNotContain("@attachment:", await File.ReadAllTextAsync(task2MdPath));
        Assert.DoesNotContain("attachment_ids:", await File.ReadAllTextAsync(task2MdPath));

        // Syncing back from disk afterwards must succeed cleanly
        await realSyncService.SyncAllTasksFromDiskAsync(
            harness.Workspace,
            harness.RepoDir,
            hostRepositoryPath: null,
            baselineSnapshot: null,
            changeActor: AuditActors.User,
            changeReason: "test sync after delete");
    }

    [Fact]
    public async Task AttachmentUiService_LoadPreviewAsync_ReturnsDegradedPreviewModelWhenPathResolverThrows()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 77);
        var att = await harness.AttachmentRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "broken-symlink.json",
            Slug = "broken-symlink",
            RelativePath = "001-broken-symlink.json",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "application/json",
            SourceKind = TaskAttachmentSourceKind.HostPath,
            SourceRef = "/mnt/c/Users/Test/broken-symlink.json",
            IsSymlink = true,
            Sha256 = new string('a', 64),
            SizeBytes = 128
        });

        var linkedTask = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Task Linking Broken Preview",
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(linkedTask.Id, [att.Id]);

        var throwingResolverMock = new Mock<IWorkspacePathResolver>();
        throwingResolverMock.Setup(p => p.GetAttachmentsPath(harness.Workspace.Id))
            .Throws(new DirectoryNotFoundException(@"Could not find a part of the path '\\wsl.localhost\Ubuntu\home\missing'."));

        var uiService = new AttachmentUiService(
            harness.IntakeService,
            harness.AttachmentRepo,
            harness.TaskRepo,
            harness.WorkspaceRepo,
            throwingResolverMock.Object,
            Mock.Of<IIdeationConversationCoordinator>(),
            Mock.Of<IVisionCapabilityUiService>(),
            Mock.Of<ITaskMarkdownSyncService>(),
            harness.LockProvider,
            NullLogger<AttachmentUiService>.Instance);

        var preview = await uiService.LoadPreviewAsync(att.Id);

        Assert.Equal(att.Id, preview.Attachment.Id);
        Assert.Contains("Could not find a part of the path", preview.ContentReadError);
        Assert.True(preview.IsReachabilityUnknown);
        Assert.True(preview.IsTargetReachable);
        Assert.Single(preview.LinkedTasks, t => t.Id == linkedTask.Id);
    }

    [Fact]
    public async Task AttachmentUiService_DeleteAttachment_SkipsLockedOrInFlightTasks_IsolatesTaskUpdateFailure_AndMarksContextDirty()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 78);
        await using var imgStream = new MemoryStream(CreateValidPngBytes(16, 16));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "shared.png", imgStream);

        var lockedTask = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Locked Task",
            Description = $"Keep while locked @attachment:{att.Id}.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");

        var inFlightTask = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "InFlight Task",
            Description = $"Keep while running @attachment:{att.Id}.",
            Status = TaskOverallStatus.Reviewed,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");

        var failingTask = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Failing Update Task",
            Description = $"Will fail update @attachment:{att.Id}.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");

        var siblingTask = await harness.TaskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Sibling Open Task",
            Description = $"Clean up @attachment:{att.Id} right now.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        }, null, AuditActors.User, "initial");

        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(lockedTask.Id, [att.Id]);
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(inFlightTask.Id, [att.Id]);
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(failingTask.Id, [att.Id]);
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(siblingTask.Id, [att.Id]);

        var taskRepoMock = new Mock<ITaskRepository>();
        taskRepoMock.Setup(r => r.GetByWorkspaceIdAsync(harness.Workspace.Id, It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((wsId, ct) => harness.TaskRepo.GetByWorkspaceIdAsync(wsId, ct));
        taskRepoMock.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (id, ct) =>
            {
                var found = await harness.TaskRepo.GetByIdAsync(id, ct);
                if (found != null && id == failingTask.Id)
                {
                    // Match EfCoreTaskRepository.GetByIdAsync AsNoTracking semantics so failed updates never mutate stored state.
                    return new ForgeTask
                    {
                        Id = found.Id,
                        WorkspaceId = found.WorkspaceId,
                        Title = found.Title,
                        Description = found.Description,
                        Status = found.Status,
                        BaseBranch = found.BaseBranch,
                        Version = found.Version
                    };
                }
                return found;
            });
        taskRepoMock.Setup(r => r.GetDependenciesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, ct) => harness.TaskRepo.GetDependenciesAsync(id, ct));
        taskRepoMock.Setup(r => r.UpdateWithHistoryAsync(
                It.IsAny<ForgeTask>(),
                It.IsAny<IEnumerable<int>?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<ForgeTask, IEnumerable<int>?, string, string?, CancellationToken>((t, deps, actor, reason, ct) =>
            {
                if (t.Id == failingTask.Id)
                {
                    throw new InvalidOperationException("Simulated DB failure");
                }
                return harness.TaskRepo.UpdateWithHistoryAsync(t, deps, actor, reason, ct);
            });

        var coordinatorMock = new Mock<IIdeationConversationCoordinator>();
        var uiService = new AttachmentUiService(
            harness.IntakeService,
            harness.AttachmentRepo,
            taskRepoMock.Object,
            harness.WorkspaceRepo,
            harness.PathResolver,
            coordinatorMock.Object,
            Mock.Of<IVisionCapabilityUiService>(),
            Mock.Of<ITaskMarkdownSyncService>(),
            harness.LockProvider,
            NullLogger<AttachmentUiService>.Instance);

        Assert.True(harness.LockProvider.TryAcquireTaskLock(harness.Workspace.Id, lockedTask.Id, out var activeLock));
        try
        {
            await uiService.DeleteAttachmentAsync(att.Id);
        }
        finally
        {
            activeLock!.Dispose();
        }

        Assert.Null(await harness.AttachmentRepo.GetByIdAsync(att.Id));
        Assert.Contains($"@attachment:{att.Id}", (await harness.TaskRepo.GetByIdAsync(lockedTask.Id))!.Description);
        Assert.Contains($"@attachment:{att.Id}", (await harness.TaskRepo.GetByIdAsync(inFlightTask.Id))!.Description);
        Assert.Contains($"@attachment:{att.Id}", (await harness.TaskRepo.GetByIdAsync(failingTask.Id))!.Description);

        var siblingAfter = await harness.TaskRepo.GetByIdAsync(siblingTask.Id);
        Assert.DoesNotContain($"@attachment:{att.Id}", siblingAfter!.Description);
        Assert.Equal("Clean up right now.", siblingAfter.Description);

        coordinatorMock.Verify(c => c.MarkWorkspaceContextDirty(harness.Workspace.Id), Times.Once);
    }

    [Fact]
    public async Task AttachmentUiService_ResolveCitedAttachmentChipsAsync_DoesNotCallIntakeGetAttachmentForMissingIds()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 79);
        var otherWs = await harness.WorkspaceRepo.CreateAsync(new Workspace
        {
            Id = 80,
            Name = "Other WS",
            RepositoryPath = harness.RepoDir,
            ExecutionMode = ExecutionMode.BareHost
        });

        var validAtt = await harness.AttachmentRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "local.md",
            Slug = "local",
            RelativePath = "001-local.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "local.md",
            Sha256 = new string('b', 64),
            SizeBytes = 10
        });
        var foreignAtt = await harness.AttachmentRepo.CreateAsync(new TaskAttachment
        {
            WorkspaceId = otherWs.Id,
            Title = "foreign.md",
            Slug = "foreign",
            RelativePath = "001-foreign.md",
            ContentKind = TaskAttachmentContentKind.PlainText,
            MediaType = "text/markdown",
            SourceKind = TaskAttachmentSourceKind.FileDrop,
            SourceRef = "foreign.md",
            Sha256 = new string('c', 64),
            SizeBytes = 10
        });

        var intakeMock = new Mock<IAttachmentIntakeService>();
        var uiService = new AttachmentUiService(
            intakeMock.Object,
            harness.AttachmentRepo,
            harness.TaskRepo,
            harness.WorkspaceRepo,
            harness.PathResolver,
            Mock.Of<IIdeationConversationCoordinator>(),
            Mock.Of<IVisionCapabilityUiService>(),
            Mock.Of<ITaskMarkdownSyncService>(),
            harness.LockProvider,
            NullLogger<AttachmentUiService>.Instance);

        var chips = await uiService.ResolveCitedAttachmentChipsAsync(
            harness.Workspace.Id,
            $"Check @attachment:{validAtt.Id}, missing @attachment:99999, and foreign @attachment:{foreignAtt.Id}");

        var single = Assert.Single(chips);
        Assert.Equal(validAtt.Id, single.AttachmentId);
        intakeMock.Verify(i => i.GetAttachmentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IdeationAndCreateTask_FileUpload_HandlesOpenReadStreamAndBatchLimitErrorsWithoutAbortingValidFiles()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 81);
        harness.WorkspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);

        var mockIdeation = new MockIdeationConversationCoordinator(harness.TaskRepo);
        var sessionState = TestServices.CreateIdeationSessionState(
            coordinator: mockIdeation,
            taskRepository: harness.TaskRepo,
            workflowRepository: harness.WorkflowRepo,
            attachmentRepository: harness.AttachmentRepo,
            attachmentUiService: harness.UiService);
        sessionState.BoundWorkspaceId = harness.Workspace.Id;

        RegisterHarnessServices(harness, wsState, sessionState, mockIdeation);
        var toastService = Services.GetRequiredService<ToastService>();

        // 1. Ideation.razor: OpenReadStream failure on first file does not crash or prevent valid second file; > max batch files shows error toast
        var ideationCut = Render<Ideation>();
        var ideationInput = ideationCut.FindComponent<InputFile>();

        await ideationCut.InvokeAsync(() => ideationInput.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs(
        [
            new FailingReadBrowserFile("oversized-ideation.md", "Stream exceeds max allowed size"),
            new ValidMemoryBrowserFile("ideation-valid.md", "# Ideation Valid")
        ])));

        ideationCut.WaitForAssertion(() =>
        {
            Assert.Contains("ideation-valid.md", ideationCut.Find(".composer-attachment-chips").TextContent);
            Assert.Contains(toastService.Toasts, t => t.Title == "Attachment Upload Failed" && t.Message.Contains("Stream exceeds max allowed size"));
        });

        var tooManyFiles = Enumerable.Range(1, AttachmentPresentationHelper.MaxBatchUploadFiles + 1)
            .Select(i => (IBrowserFile)new ValidMemoryBrowserFile($"batch-{i}.md", $"# Batch {i}"))
            .ToArray();
        var failedToastCountBeforeBatch = toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed");

        await ideationCut.InvokeAsync(() => ideationInput.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs(tooManyFiles)));
        ideationCut.WaitForAssertion(() =>
            Assert.True(toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed") > failedToastCountBeforeBatch));

        // 2. CreateTask.razor: OpenReadStream failure on first file does not crash or prevent valid second file; > max batch files shows error toast
        var createTaskCut = Render<CreateTask>();
        var createTaskInput = createTaskCut.FindComponent<InputFile>();
        var failedToastCountBeforeCreate = toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed");

        await createTaskCut.InvokeAsync(() => createTaskInput.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs(
        [
            new FailingReadBrowserFile("oversized-create.md", "Create stream failure"),
            new ValidMemoryBrowserFile("create-valid.md", "# Create Valid")
        ])));

        createTaskCut.WaitForAssertion(() =>
        {
            Assert.Contains("create-valid.md", createTaskCut.Find(".selected-attachments-chips").TextContent);
            Assert.Contains(toastService.Toasts, t => t.Title == "Attachment Upload Failed" && t.Message.Contains("Create stream failure"));
            Assert.True(toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed") > failedToastCountBeforeCreate);
        });

        var failedToastCountBeforeCreateBatch = toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed");
        await createTaskCut.InvokeAsync(() => createTaskInput.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs(tooManyFiles)));
        createTaskCut.WaitForAssertion(() =>
            Assert.True(toastService.Toasts.Count(t => t.Title == "Attachment Upload Failed") > failedToastCountBeforeCreateBatch));
    }

    private sealed class FailingReadBrowserFile(string name, string errorMessage) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => AttachmentConstants.MaxFileDropBytes + 1024;
        public string ContentType => "text/markdown";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            throw new IOException(errorMessage);
    }

    private sealed class ValidMemoryBrowserFile(string name, string content) : IBrowserFile
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(content);
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => _bytes.LongLength;
        public string ContentType => "text/markdown";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream(_bytes, writable: false);
    }

    private void RegisterHarnessServices(
        AttachmentUiTestHarness harness,
        WorkspaceStateService wsState,
        IdeationSessionState sessionState,
        MockIdeationConversationCoordinator mockCoordinator)
    {
        var depStore = new InMemoryTaskDependencyStore();
        var orchestrator = new MockTaskOrchestrator();
        var markdownSync = new MockTaskMarkdownSyncService();
        var mutationService = new TaskMutationService(
            harness.TaskRepo,
            harness.WorkspaceRepo,
            depStore,
            markdownSync,
            orchestrator,
            new Mock<IOperationsTelemetryService>().Object,
            mockCoordinator,
            NullLogger<TaskMutationService>.Instance);
        var reviewService = new TaskReviewService(
            harness.TaskRepo,
            harness.WorkspaceRepo,
            new WorkspaceLockProvider(),
            new Mock<IGitWorkspaceManager>().Object,
            new Mock<IGitHubPullRequestDeliveryService>().Object,
            orchestrator,
            markdownSync,
            NullLogger<TaskReviewService>.Instance);

        Services.AddSingleton(wsState);
        Services.AddSingleton<IWorkspaceRepository>(harness.WorkspaceRepo);
        Services.AddSingleton<ITaskRepository>(harness.TaskRepo);
        Services.AddSingleton<IWorkflowRepository>(harness.WorkflowRepo);
        Services.AddSingleton<ITaskAttachmentRepository>(harness.AttachmentRepo);
        Services.AddSingleton<IWorkspacePathResolver>(harness.PathResolver);
        Services.AddSingleton<IAttachmentIntakeService>(harness.IntakeService);
        Services.AddSingleton<IAttachmentUiService>(harness.UiService);
        Services.AddSingleton<IWorkspaceLockProvider>(harness.LockProvider);
        Services.AddSingleton<ITaskDependencyStore>(depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        Services.AddSingleton<ITaskMarkdownSyncService>(markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(orchestrator);
        Services.AddSingleton<ITaskMutationService>(mutationService);
        Services.AddSingleton<ITaskReviewService>(reviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(reviewService));
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        Services.AddSingleton(new ReviewCommentStateService());
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton(TestHubConnectionHelper.Create());
        Services.AddSingleton<IIdeationConversationCoordinator>(mockCoordinator);
        Services.AddSingleton(sessionState);
        Services.AddSingleton(new IdeationRefinementService(harness.TaskRepo));
        Services.AddSingleton<ICompanionTriageDraftStore>(new CompanionTriageDraftStore(TimeProvider.System));
        Services.AddSingleton(TestServices.CreateToastService());
        TestServices.AddDefaultWebUiServices(Services);
    }

    private async Task<AttachmentUiTestHarness> CreateAttachmentHarnessAsync(int workspaceId)
    {
        var wksDir = Path.Combine(_tempRoot, $"wks-{workspaceId}");
        var repoDir = Path.Combine(wksDir, "clone");
        var attachDir = Path.Combine(wksDir, AttachmentConstants.AttachmentsDirectoryName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(attachDir);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = workspaceId,
            Name = $"Workspace {workspaceId}",
            RepositoryPath = repoDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var workflowRepo = new InMemoryWorkflowRepository();

        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(p => p.GetAttachmentsPath(workspaceId)).Returns(attachDir);
        pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        pathResolverMock.Setup(p => p.GetWslUserName()).Returns("tester");

        var processRunnerMock = new Mock<IProcessRunner>();
        processRunnerMock.Setup(r => r.RunAsync(It.IsAny<ProcessStartSpec>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessExecutionResult(0, string.Empty, string.Empty));

        var intakeService = new AttachmentIntakeService(
            attachmentRepo,
            workspaceRepo,
            pathResolverMock.Object,
            processRunnerMock.Object,
            new Markitdown(new MarkitdownOptions { IncludeTitle = true, CleanHtml = true, FilterHiddenText = true }),
            NullLogger<AttachmentIntakeService>.Instance);

        var visionMock = new Mock<IVisionCapabilityUiService>();
        visionMock.Setup(v => v.EvaluateModelVisionAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(true, "test-vision", false, null));
        visionMock.Setup(v => v.EvaluateWorkspaceStageVisionAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<TaskDifficulty?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(true, "test-vision", false, null));
        visionMock.Setup(v => v.EvaluateWorkspaceTaskVisionAsync(It.IsAny<int>(), It.IsAny<TaskDifficulty>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisionCapabilityStatus(true, "test-vision", false, null));

        var lockProvider = new WorkspaceLockProvider();

        var uiService = new AttachmentUiService(
            intakeService,
            attachmentRepo,
            taskRepo,
            workspaceRepo,
            pathResolverMock.Object,
            Mock.Of<IIdeationConversationCoordinator>(),
            visionMock.Object,
            Mock.Of<ITaskMarkdownSyncService>(),
            lockProvider,
            NullLogger<AttachmentUiService>.Instance);

        return new AttachmentUiTestHarness(
            workspace,
            repoDir,
            attachDir,
            workspaceRepo,
            taskRepo,
            attachmentRepo,
            workflowRepo,
            pathResolverMock.Object,
            lockProvider,
            intakeService,
            uiService);
    }

    private static byte[] CreateValidPngBytes(uint width, uint height)
    {
        var bytes = new byte[32];
        byte[] pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        pngSig.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        bytes[12] = (byte)'I';
        bytes[13] = (byte)'H';
        bytes[14] = (byte)'D';
        bytes[15] = (byte)'R';
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        bytes[24] = 8; // bit depth
        bytes[25] = 2; // RGB
        return bytes;
    }

    private sealed record AttachmentUiTestHarness(
        Workspace Workspace,
        string RepoDir,
        string AttachmentsDir,
        InMemoryWorkspaceRepository WorkspaceRepo,
        InMemoryTaskRepository TaskRepo,
        InMemoryTaskAttachmentRepository AttachmentRepo,
        InMemoryWorkflowRepository WorkflowRepo,
        IWorkspacePathResolver PathResolver,
        IWorkspaceLockProvider LockProvider,
        AttachmentIntakeService IntakeService,
        AttachmentUiService UiService);
}
