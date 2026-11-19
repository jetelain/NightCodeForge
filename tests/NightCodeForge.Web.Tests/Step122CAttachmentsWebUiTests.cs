using System.Buffers.Binary;
using System.Net;
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
    public void AttachmentPresentationHelper_ExtractsCitationsFormatsStatusSizeSymlinkReachabilityAndFilters()
    {
        // 1. Message citation extraction
        const string msg = "Inspect C:\\dev\\poc\\router-spikes.md, `~/docs/guide.md`, and https://example.com/rfc along with @attachment:7.";
        var extraction = AttachmentPresentationHelper.ExtractMessageCitations(msg);
        Assert.Contains("C:\\dev\\poc\\router-spikes.md", extraction.HostPaths);
        Assert.Contains("~/docs/guide.md", extraction.HostPaths);
        Assert.Contains("https://example.com/rfc", extraction.Urls);
        Assert.Equal([7], extraction.CitedAttachmentIds);
        Assert.True(extraction.HasAny);

        // 2. Status, symlink, and reachability formatting
        Assert.Equal("stored", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Stored));
        Assert.Equal("symlinked", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Symlinked));
        Assert.Equal("converting", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Converting));
        Assert.Equal("failed", AttachmentPresentationHelper.FormatIntakeStatusLabel(AttachmentIntakeStatus.Failed));
        Assert.Equal("badge-teal", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Stored));
        Assert.Equal("badge-indigo", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Symlinked));
        Assert.Equal("badge-amber", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Converting));
        Assert.Equal("badge-crimson", AttachmentPresentationHelper.GetIntakeStatusBadgeClass(AttachmentIntakeStatus.Failed));

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
        Assert.Equal(@"🔗 C:\dev\poc\router-spikes.md", AttachmentPresentationHelper.FormatSymlinkBadge(symlinkAtt));
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

        // 5. Filtering by title, source ref, and #id
        var att1 = new TaskAttachment { Id = 1, Title = "OpenAI API Spec", Slug = "openai", SourceRef = "openai.pdf", SizeBytes = 100 };
        var att2 = symlinkAtt;
        var all = new[] { att1, att2 };
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "#2"), a => a.Id == 2);
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "router-spikes"), a => a.Id == 2);
        Assert.Single(AttachmentPresentationHelper.FilterAttachments(all, "OpenAI"), a => a.Id == 1);
        Assert.Equal(2, AttachmentPresentationHelper.FilterAttachments(all, "").Count);
    }

    [Fact]
    public async Task AttachmentUiService_CoordinatesIngestionCitationsPreviewReachabilityAndSafeDeletion()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 10);
        var hostFile = Path.Combine(_tempRoot, "host-spikes.md");
        await File.WriteAllTextAsync(hostFile, "# Host Spikes\nImportant routing rules.");

        // 1. Upload file drop + image file drop
        await using var mdStream = new MemoryStream(Encoding.UTF8.GetBytes("# Design Doc\nToken rotation spec."));
        var uploadedMd = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "design-doc.md", mdStream);

        await using var pngStream = new MemoryStream(CreateValidPngBytes(1280, 720));
        var uploadedImg = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "ui-mockup.png", pngStream);

        // 2. Host path symlink attach + URL capture + message citation ingestion (valid + missing)
        var missingHostPath = Path.Combine(_tempRoot, "does-not-exist.md");
        var observedTransitions = new List<(string Title, AttachmentIntakeStatus Status)>();
        var chips = await harness.UiService.IngestMessageCitationsAsync(
            harness.Workspace.Id,
            $"Check \"{hostFile}\", https://example.com/rfc, and \"{missingHostPath}\" plus @attachment:{uploadedMd.Id}",
            chip => observedTransitions.Add((chip.Title, chip.Status)));

        Assert.Contains(observedTransitions, t => t.Title == "host-spikes.md" && t.Status == AttachmentIntakeStatus.Converting);
        Assert.Contains(observedTransitions, t => t.Title == "host-spikes.md" && t.Status == AttachmentIntakeStatus.Symlinked);
        Assert.Contains(observedTransitions, t => t.Title == "does-not-exist.md" && t.Status == AttachmentIntakeStatus.Failed);
        Assert.Contains(observedTransitions, t => t.Status == AttachmentIntakeStatus.Stored && t.Title == "Mock Page");

        var symlinkChip = Assert.Single(chips, c => c.SourceRef == hostFile || c.IsSymlink && c.Title == "host-spikes.md");
        Assert.Equal(AttachmentIntakeStatus.Symlinked, symlinkChip.Status);
        Assert.NotNull(symlinkChip.AttachmentId);

        var urlChip = Assert.Single(chips, c => c.SourceRef == "https://example.com/rfc");
        Assert.Equal(AttachmentIntakeStatus.Stored, urlChip.Status);
        Assert.NotNull(urlChip.AttachmentId);

        var failedChip = Assert.Single(chips, c => c.Status == AttachmentIntakeStatus.Failed);
        Assert.False(string.IsNullOrWhiteSpace(failedChip.ErrorMessage));

        var citedChip = Assert.Single(chips, c => c.AttachmentId == uploadedMd.Id);
        Assert.Equal(AttachmentIntakeStatus.Stored, citedChip.Status);

        // 3. Preview loading (markdown HTML + image DataUri + symlink reachability)
        var mdPreview = await harness.UiService.LoadPreviewAsync(uploadedMd.Id);
        Assert.Contains("<h1", mdPreview.RenderedHtml);
        Assert.Contains("Token rotation spec.", mdPreview.RenderedHtml);
        Assert.True(mdPreview.IsTargetReachable);

        var imgPreview = await harness.UiService.LoadPreviewAsync(uploadedImg.Id);
        Assert.NotNull(imgPreview.ImageDataUri);
        Assert.StartsWith("data:image/png;base64,", imgPreview.ImageDataUri);
        Assert.Equal(1280, imgPreview.Attachment.WidthPx);
        Assert.Equal(720, imgPreview.Attachment.HeightPx);

        var symlinkPreviewOk = await harness.UiService.LoadPreviewAsync(symlinkChip.AttachmentId!.Value);
        Assert.True(symlinkPreviewOk.IsTargetReachable);
        Assert.Contains("Important routing rules.", symlinkPreviewOk.RenderedHtml);

        // Delete impact + safe deletion preserves host file
        var task = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Task Linking Host File",
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task.Id, [symlinkChip.AttachmentId.Value]);

        var impact = await harness.UiService.GetDeleteImpactAsync(symlinkChip.AttachmentId.Value);
        Assert.Single(impact.LinkedTasks, t => t.Id == task.Id);

        await harness.UiService.DeleteAttachmentAsync(symlinkChip.AttachmentId.Value);
        Assert.Null(await harness.AttachmentRepo.GetByIdAsync(symlinkChip.AttachmentId.Value));
        Assert.Empty(await harness.AttachmentRepo.GetByTaskIdAsync(task.Id));
        Assert.True(File.Exists(hostFile));

        // 4. Target Missing detection when host target disappears
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
    public async Task IdeationPage_SupportsDropZoneHostFileAttachMessageCitationsWorkspacePanelAndCiteInChat()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 30);
        harness.WorkspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);

        var sessionState = new IdeationSessionState { BoundWorkspaceId = harness.Workspace.Id };
        var mockIdeation = new MockIdeationService();
        mockIdeation.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Reviewed cited attachments."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Reviewed cited attachments.", []))
        ];

        var hostFile1 = Path.Combine(_tempRoot, "router-spikes.md");
        var hostFile2 = Path.Combine(_tempRoot, "cited-in-msg.md");
        await File.WriteAllTextAsync(hostFile1, "# Router Spikes\nSpike content.");
        await File.WriteAllTextAsync(hostFile2, "# Cited In Message\nMessage citation content.");

        RegisterHarnessServices(harness, wsState, sessionState, mockIdeation);

        var cut = Render<Ideation>();

        // 1. File drop via <InputFile>
        var inputFile = cut.FindComponent<InputFile>();
        inputFile.UploadFiles(
            InputFileContent.CreateFromText("# Drop Spec\nDropped markdown.", "drop-spec.md", contentType: "text/markdown"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("drop-spec.md", cut.Find(".composer-attachment-chips").TextContent);
            Assert.Contains("stored", cut.Find(".composer-attachment-chips").TextContent);
            Assert.Contains("drop-spec.md", cut.Find(".workspace-attachments-list").TextContent);
        });

        // 2. Attach Host File path input
        cut.Find("#ideation-host-file-input").Input(hostFile1);
        cut.Find("#btn-attach-host-file").Click();

        cut.WaitForAssertion(() =>
        {
            var chipsText = cut.Find(".composer-attachment-chips").TextContent;
            Assert.Contains("router-spikes.md", chipsText);
            Assert.Contains("symlinked", chipsText);
            Assert.Contains($"🔗 {VirtualMountEntry.ToDefaultWslPath(hostFile1)}", chipsText);
            Assert.Contains("router-spikes.md", cut.Find(".workspace-attachments-list").TextContent);
        });

        // 3. Cite in Chat from Workspace Attachments panel inserts @attachment:{id}
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
        });

        // Filter workspace attachments panel
        cut.Find("#ideation-attachment-search-input").Input("router-spikes");
        var filteredRows = cut.FindAll(".workspace-attachment-row");
        Assert.Single(filteredRows);
        Assert.Contains("router-spikes.md", filteredRows[0].TextContent);

        // Clear search filter and collapse/expand panel
        cut.Find("#ideation-attachment-search-input").Input(string.Empty);
        cut.Find("#btn-toggle-workspace-attachments").Click();
        Assert.Empty(cut.FindAll(".workspace-attachments-body"));
        cut.Find("#btn-toggle-workspace-attachments").Click();
        Assert.NotEmpty(cut.FindAll(".workspace-attachments-body"));

        // 4. Send message with cited host path and missing path -> creates message chips with live status
        var missingPath = Path.Combine(_tempRoot, "missing-cite.md");
        cut.Find("textarea.chat-textarea").Input($"Please review \"{hostFile2}\" and \"{missingPath}\"");
        cut.Find("button.send-btn").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(sessionState.IsGenerating);
            Assert.Equal(2, sessionState.Messages.Count);
            var msgChips = cut.FindAll(".message-attachment-chips .attachment-chip");
            Assert.Contains(msgChips, c => c.TextContent.Contains("cited-in-msg.md") && c.TextContent.Contains("symlinked"));
            Assert.Contains(msgChips, c => c.TextContent.Contains("missing-cite.md") && c.TextContent.Contains("failed"));
        });

        // 5. Clicking a conversation chip opens AttachmentPreviewDrawer
        await cut.InvokeAsync(() =>
        {
            var clickableMsgChip = cut.FindAll(".message-attachment-chips .attachment-chip")
                .First(c => c.TextContent.Contains("cited-in-msg.md"));
            clickableMsgChip.Click();
        });

        cut.WaitForAssertion(() =>
        {
            var drawer = cut.Find(".attachment-preview-drawer");
            Assert.Contains("cited-in-msg.md", drawer.TextContent);
            Assert.Contains("Message citation content.", drawer.InnerHtml);
            Assert.Contains("Reachable", cut.Find("#attachment-reachability-badge").TextContent);
        });

        // 6. Close preview, add proposal with linked attachment to backlog, and verify TaskAttachmentLinks
        await cut.InvokeAsync(() => cut.Find("#btn-close-attachment-preview").Click());
        Assert.Empty(cut.FindAll(".attachment-preview-drawer"));

        var dropRowId = int.Parse(
            cut.FindAll(".workspace-attachment-row")
                .First(r => r.TextContent.Contains("drop-spec.md"))
                .GetAttribute("data-attachment-id")!,
            System.Globalization.CultureInfo.InvariantCulture);

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

        // 7. Workspace Attachments panel Preview & Delete buttons open drawer and delete safely
        var citedRowId = cut.FindAll(".workspace-attachment-row")
            .First(r => r.TextContent.Contains("cited-in-msg.md"))
            .GetAttribute("data-attachment-id");
        await cut.InvokeAsync(() =>
            cut.Find($".workspace-attachment-row[data-attachment-id='{citedRowId}'] .attachment-preview-btn").Click());
        cut.WaitForAssertion(() => Assert.Contains("cited-in-msg.md", cut.Find(".attachment-preview-drawer").TextContent));
        await cut.InvokeAsync(() => cut.Find("#btn-close-attachment-preview").Click());

        await cut.InvokeAsync(() =>
            cut.Find($".workspace-attachment-row[data-attachment-id='{citedRowId}'] .attachment-delete-btn").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".attachment-delete-confirm-modal")));
        await cut.InvokeAsync(() => cut.Find("#btn-confirm-delete-attachment").Click());

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain("cited-in-msg.md", cut.Find(".workspace-attachments-list").TextContent));
        Assert.True(File.Exists(hostFile2));
    }

    [Fact]
    public async Task IdeationProposalCard_RendersClickableAttachmentChips_AndOpensPreviewDrawer()
    {
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 35);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("# API Spec\nEndpoints specification."));
        var att = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "api-spec.md", stream);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

        var proposal = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Implement Endpoints", $"Follow @attachment:{att.Id} guidelines."));

        var cut = Render<IdeationProposalCard>(p => p
            .Add(c => c.Proposal, proposal)
            .Add(c => c.WorkspaceAttachments, [att]));

        var chip = cut.Find(".proposal-attachment-chip");
        Assert.Contains($"#{att.Id}", chip.TextContent);
        Assert.Contains("api-spec.md", chip.TextContent);

        chip.Click();

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
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

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
            Assert.Contains($"🔗 {VirtualMountEntry.ToDefaultWslPath(hostFile)}", selectedChips);
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

        var task = await harness.TaskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = harness.Workspace.Id,
            Title = "Task With Linked Spec",
            Description = "Implement per attachment.",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        await harness.AttachmentRepo.SetTaskAttachmentLinksAsync(task.Id, [att.Id]);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

        // 1. TaskDetail.razor
        var detailCut = Render<TaskDetail>(p => p.Add(t => t.TaskId, task.Id));
        var detailChip = detailCut.Find(".task-attachments-section .task-attachment-chip");
        Assert.Contains($"#{att.Id}", detailChip.TextContent);
        Assert.Contains("arch-spec.md", detailChip.TextContent);

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

        var drawerChip = drawerCut.Find(".task-attachments-section .task-attachment-chip");
        Assert.Contains("arch-spec.md", drawerChip.TextContent);

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
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

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
            Assert.Equal("Reachable", symlinkCut.Find("#attachment-reachability-badge").TextContent.Trim());
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
        // 1. Unit assertions for RemoveAttachmentCitation & FilterAttachments/ExtractMessageCitations null-safety
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

        var unconfiguredIntakeMock = new Mock<IAttachmentIntakeService>();
        var fallbackExtracted = AttachmentPresentationHelper.ExtractMessageCitations(
            "Read https://example.com/spec and C:\\dev\\note.md",
            unconfiguredIntakeMock.Object);
        Assert.Contains("https://example.com/spec", fallbackExtracted.Urls);
        Assert.Contains("C:\\dev\\note.md", fallbackExtracted.HostPaths);

        // 2. Deleting a cited attachment from IdeationProposalCard standalone drawer strips citation and permits promotion
        var harness = await CreateAttachmentHarnessAsync(workspaceId: 70);
        await using var s1 = new MemoryStream(Encoding.UTF8.GetBytes("# Proposal Spec\nDetails."));
        var att1 = await harness.UiService.UploadFileAsync(harness.Workspace.Id, "proposal-spec.md", s1);

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

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

        // 1. IngestMessageCitationsAsync deduplicates duplicate citations resolving to the same AttachmentId
        var serviceChips = await harness.UiService.IngestMessageCitationsAsync(
            harness.Workspace.Id,
            $"Check \"{hostFile}\" and also @attachment:{existingAtt.Id}");
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
            content: $"Initial review of \"{hostFile}\"");

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(harness.Workspace);
        var sessionState = new IdeationSessionState();
        var mockIdeation = new MockIdeationService
        {
            StreamUpdatesToYield =
            [
                new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Done."),
                new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Done.", []))
            ]
        };
        RegisterHarnessServices(harness, wsState, sessionState, mockIdeation);

        var cut = Render<Ideation>();

        // Restored user turn displays attachment chip
        cut.WaitForAssertion(() =>
        {
            var initialChips = cut.FindAll(".message-attachment-chips .attachment-chip");
            Assert.Single(initialChips, c => c.TextContent.Contains($"#{existingAtt.Id}"));
        });

        // Attach same host file in composer AND cite both host path & @attachment:{id} in next message
        cut.Find("#ideation-host-file-input").Input(hostFile);
        cut.Find("#btn-attach-host-file").Click();
        cut.WaitForAssertion(() =>
            Assert.Contains("dedup-host.md", cut.Find(".composer-attachment-chips").TextContent));

        cut.Find("textarea.chat-textarea").Input($"Re-check \"{hostFile}\" and @attachment:{existingAtt.Id}");
        cut.Find("button.send-btn").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(sessionState.IsGenerating);
            var userMessages = sessionState.Messages.Where(m => m.Role == "user").ToList();
            Assert.Equal(2, userMessages.Count);
            Assert.Single(userMessages[1].AttachmentChips!, c => c.AttachmentId == existingAtt.Id);
        });
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

        var sessionState = new IdeationSessionState();
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
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

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
        RegisterHarnessServices(harness, wsState, new IdeationSessionState(), new MockIdeationService());

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

    private void RegisterHarnessServices(
        AttachmentUiTestHarness harness,
        WorkspaceStateService wsState,
        IdeationSessionState sessionState,
        MockIdeationService mockIdeation)
    {
        var depStore = new InMemoryTaskDependencyStore();
        var orchestrator = new MockTaskOrchestrator();
        var markdownSync = new MockTaskMarkdownSyncService();
        var mutationService = new TaskMutationService(
            harness.TaskRepo,
            harness.WorkspaceRepo,
            markdownSync,
            orchestrator,
            new Mock<IOperationsTelemetryService>().Object,
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
        Services.AddSingleton<IIdeationService>(mockIdeation);
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

        var intakeService = new TestableAttachmentIntakeService(
            attachmentRepo,
            workspaceRepo,
            pathResolverMock.Object,
            processRunnerMock.Object,
            new Markitdown(new MarkitdownOptions { IncludeTitle = true, CleanHtml = true, FilterHiddenText = true }),
            new HttpClient(new FakeHttpMessageHandler()),
            Options.Create(new SecurityOptions { AirGappedMode = false }),
            Options.Create(new AttachmentIntakeOptions()),
            NullLogger<AttachmentIntakeService>.Instance);

        var uiService = new AttachmentUiService(
            intakeService,
            attachmentRepo,
            taskRepo,
            workspaceRepo,
            pathResolverMock.Object,
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
        AttachmentIntakeService IntakeService,
        AttachmentUiService UiService);

    private sealed class TestableAttachmentIntakeService(
        ITaskAttachmentRepository attachmentRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkspacePathResolver pathResolver,
        IProcessRunner processRunner,
        IMarkitdown markitdown,
        HttpClient httpClient,
        IOptions<SecurityOptions> securityOptions,
        IOptions<AttachmentIntakeOptions> intakeOptions,
        Microsoft.Extensions.Logging.ILogger<AttachmentIntakeService> logger)
        : AttachmentIntakeService(
            attachmentRepository,
            workspaceRepository,
            pathResolver,
            processRunner,
            markitdown,
            httpClient,
            securityOptions,
            intakeOptions,
            logger)
    {
        protected override Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><title>Mock Page</title><body><h1>Hello</h1></body></html>", Encoding.UTF8, "text/html")
            };
            return Task.FromResult(resp);
        }
    }
}
