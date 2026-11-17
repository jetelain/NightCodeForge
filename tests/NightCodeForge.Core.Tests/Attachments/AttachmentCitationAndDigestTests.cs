using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests.Attachments;

public class AttachmentCitationAndDigestTests
{
    [Fact]
    public void ExtractAttachmentIds_ExtractsVariousValidForms_AndDeduplicatesInOrder()
    {
        var text = "Review @attachment:12, compare with @attachment:#4, check @attachment:007 and @attachment:12 again at the end @attachment:99.";

        var ids = AttachmentCitationParser.ExtractAttachmentIds(text);

        Assert.Equal([12, 4, 7, 99], ids);
        Assert.Equal(ids, AttachmentCitationParser.Parse(text));
        Assert.Equal(ids, AttachmentCitationParser.ParseAttachmentIds(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Email user@attachment:12 should not match")]
    [InlineData("Zero id @attachment:0 or @attachment:00 is ignored")]
    [InlineData("Negative @attachment:-3 is ignored")]
    [InlineData("Non-numeric @attachment:abc or @attachment:12a should not match")]
    [InlineData("Attached as prefix my_attachment:5 is ignored")]
    public void ExtractAttachmentIds_IgnoresInvalidOrFalsePositiveForms(string? input)
    {
        var ids = AttachmentCitationParser.ExtractAttachmentIds(input);

        Assert.Empty(ids);
    }

    [Fact]
    public void UnionAttachmentIds_UnionsExplicitAndCitedIds_WithoutDuplicates()
    {
        var explicitIds = new[] { 3, 1, 3 };
        var description = "Uses @attachment:1 spec and @attachment:#5 mockup.";

        var union = AttachmentCitationParser.UnionAttachmentIds(explicitIds, description);

        Assert.Equal([3, 1, 5], union);
    }

    [Fact]
    public async Task ValidateAndResolveWorkspaceIdsAsync_ReturnsSortedIdsForValidSameWorkspaceAttachments()
    {
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var a1 = await attachmentRepo.CreateAsync(CreateAttachment(id: 0, workspaceId: 10, title: "Spec", relPath: "1-spec.pdf", mdPath: "1-spec.pdf.md", kind: TaskAttachmentContentKind.RichDocument));
        var a2 = await attachmentRepo.CreateAsync(CreateAttachment(id: 0, workspaceId: 10, title: "Logo", relPath: "2-logo.png", mdPath: null, kind: TaskAttachmentContentKind.Image, width: 640, height: 480));

        var resolved = await AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(
            attachmentRepo,
            workspaceId: 10,
            explicitIds: [a2.Id],
            description: $"See @attachment:{a1.Id} and @attachment:{a2.Id}.");

        Assert.Equal([a1.Id, a2.Id], resolved);
    }

    [Fact]
    public async Task ValidateAndResolveWorkspaceIdsAsync_ThrowsAttachmentNotFoundException_WhenMissingNonPositiveOrForeignWorkspace()
    {
        var taskRepo = new InMemoryTaskRepository();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

        var foreignAttachment = await attachmentRepo.CreateAsync(
            CreateAttachment(id: 0, workspaceId: 99, title: "Foreign", relPath: "1-foreign.md", mdPath: null, kind: TaskAttachmentContentKind.PlainText));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(attachmentRepo, 10, [9999], "none"));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(attachmentRepo, 10, null, "See @attachment:9999"));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(attachmentRepo, 10, [0], "none"));

        await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(attachmentRepo, 10, [-2], "none"));

        var foreignEx = await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
            AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(attachmentRepo, 10, null, $"Cross workspace @attachment:{foreignAttachment.Id}"));
        Assert.Equal(foreignAttachment.Id, foreignEx.AttachmentId);
        Assert.Equal(10, foreignEx.WorkspaceId);
    }

    [Fact]
    public void DigestFormatter_ReturnsEmptyWhenNullOrEmpty()
    {
        Assert.Equal(string.Empty, AttachmentDigestFormatter.FormatWorkspaceAttachmentsDigest(null));
        Assert.Equal(string.Empty, AttachmentDigestFormatter.FormatWorkspaceAttachmentsDigest(Array.Empty<TaskAttachment>()));
        Assert.Equal(string.Empty, AttachmentDigestFormatter.FormatTaskAttachmentsDigest(null));
        Assert.Equal(string.Empty, AttachmentDigestFormatter.FormatTaskAttachmentsDigest(Array.Empty<TaskAttachment>()));
    }

    [Fact]
    public void DigestFormatter_ResolvesVirtualPathsAndSerializesCompactEntriesSortedById()
    {
        var image = CreateAttachment(id: 8, workspaceId: 1, title: "Architecture Diagram", relPath: "8-arch.png", mdPath: null, kind: TaskAttachmentContentKind.Image, width: 1280, height: 720, mediaType: "image/png");
        var richDoc = CreateAttachment(id: 2, workspaceId: 1, title: "PRD PDF", relPath: "2-prd.pdf", mdPath: "2-prd.pdf.md", kind: TaskAttachmentContentKind.RichDocument, mediaType: "application/pdf", isSymlink: true);
        var plainText = CreateAttachment(id: 5, workspaceId: 1, title: "Notes", relPath: "5-notes.txt", mdPath: null, kind: TaskAttachmentContentKind.PlainText, mediaType: "text/plain");

        Assert.Equal("/workspace/attachments/2-prd.pdf.md", AttachmentDigestFormatter.ResolveVirtualAttachmentPath(richDoc));
        Assert.Equal("/workspace/attachments/8-arch.png", AttachmentDigestFormatter.ResolveVirtualAttachmentPath(image));
        Assert.Equal("/workspace/attachments/5-notes.txt", AttachmentDigestFormatter.ResolveVirtualAttachmentPath(plainText));

        var workspaceDigest = AttachmentDigestFormatter.FormatWorkspaceAttachmentsDigest([image, richDoc, plainText]);
        Assert.StartsWith("<workspace_attachments>", workspaceDigest);
        Assert.EndsWith("</workspace_attachments>", workspaceDigest);
        Assert.Contains("@attachment:{id}", workspaceDigest);

        var lines = workspaceDigest.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = lines
            .Where(l => l.StartsWith('{'))
            .Select(l => JsonSerializer.Deserialize<AttachmentDigestEntry>(l)!)
            .ToList();

        Assert.Equal([2, 5, 8], entries.Select(e => e.Id).ToArray());

        Assert.Equal("/workspace/attachments/2-prd.pdf.md", entries[0].Path);
        Assert.Equal(TaskAttachmentContentKind.RichDocument, entries[0].ContentKind);
        Assert.True(entries[0].IsSymlink);
        Assert.Null(entries[0].Dimensions);

        Assert.Equal("/workspace/attachments/5-notes.txt", entries[1].Path);
        Assert.Equal(TaskAttachmentContentKind.PlainText, entries[1].ContentKind);
        Assert.False(entries[1].IsSymlink);

        Assert.Equal("/workspace/attachments/8-arch.png", entries[2].Path);
        Assert.Equal("1280x720", entries[2].Dimensions);

        var taskDigest = AttachmentDigestFormatter.FormatTaskAttachmentsDigest([richDoc]);
        Assert.StartsWith("<task_attachments>", taskDigest);
        Assert.EndsWith("</task_attachments>", taskDigest);
        Assert.Contains("\"/workspace/attachments/2-prd.pdf.md\"", taskDigest);
    }

    [Fact]
    public async Task TaskOrchestrator_PersistsAttachmentLinksFromPendingProposals_AndRethrowsAttachmentNotFoundException()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ncf-orch-att-" + Guid.NewGuid().ToString("N"));
        var repoDir = Path.Combine(tempRoot, "clone");
        Directory.CreateDirectory(repoDir);

        try
        {
            var fakeGit = new FakeGitWorkspaceManager();
            var builder = new TestOrchestratorBuilder
            {
                GitWorkspaceManager = fakeGit,
                JobQueue = new FakeTaskJobQueue()
            };
            var taskRepo = (InMemoryTaskRepository)builder.TaskRepository;
            var wsRepo = (InMemoryWorkspaceRepository)builder.WorkspaceRepository;
            var depStore = (InMemoryTaskDependencyStore)builder.DependencyStore;
            var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);

            var workspace = await wsRepo.CreateAsync(new Workspace
            {
                Name = "orch-ws",
                RepositoryPath = repoDir,
                Ext4MirrorPath = repoDir,
                DefaultBaseBranch = "main",
                AgentProfiles =
                [
                    new AgentProfile
                    {
                        Id = StandardStageIds.TaskReview,
                        Name = "Review",
                        HarnessId = "crucible",
                        ModelName = "test-model",
                        Mode = AgentMode.Full,
                        PermittedTools = DefaultAgentTools.Full
                    }
                ]
            });

            var att1 = await attachmentRepo.CreateAsync(CreateAttachment(0, workspace.Id, "Spec 1", "1-spec.md", null, TaskAttachmentContentKind.PlainText));
            var att2 = await attachmentRepo.CreateAsync(CreateAttachment(0, workspace.Id, "Spec 2", "2-spec.md", null, TaskAttachmentContentKind.PlainText));

            IReadOnlyList<TaskProposal>? proposalsToEmit =
            [
                new("Child Task With Cited Attachments", $"See @attachment:{att1.Id}", attachmentIds: [att2.Id])
            ];
            var harnessMock = new Mock<IAgentHarness>();
            harnessMock.SetupGet(h => h.HarnessId).Returns("crucible");
            harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var currentProposals = proposalsToEmit;
                    proposalsToEmit = null;
                    return AgentRunResult.Completed(new AgentRunMetrics(50, 10, 10, 20), summary: "done", proposals: currentProposals);
                });
            builder.HarnessRegistry = new HarnessRegistry([harnessMock.Object]);

            var parentTask = await taskRepo.CreateAsync(new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Analyze specs",
                Description = "Parent discovery task",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main"
            });
            depStore.AddTask(parentTask.Id, workspace.Id, TaskOverallStatus.Open);

            var reviewStage = TestWorkflowPipelines.CreateReviewStage();
            builder.PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([reviewStage]));

            var wp = builder.BuildWorkspacePreparer();
            var mc = builder.BuildMergeAndCascadeCoordinator(wp);
            var lm = builder.BuildLifecycleMutationCoordinator(wp, mc);
            var orchestrator = new TaskOrchestrator(
                taskRepo,
                wp,
                mc,
                lm,
                builder.HarnessRegistry,
                builder.Streamer,
                builder.OperationsControlService,
                builder.TelemetryService,
                attachmentRepo,
                NullLogger<TaskOrchestrator>.Instance);

            await orchestrator.ProcessTaskAsync(parentTask.Id);

            var allTasks = await taskRepo.GetByWorkspaceIdAsync(workspace.Id);
            var suggested = Assert.Single(allTasks, t => t.Title == "Child Task With Cited Attachments");
            var linked = await attachmentRepo.GetByTaskIdAsync(suggested.Id);
            Assert.Equal([att1.Id, att2.Id], linked.Select(a => a.Id).OrderBy(id => id).ToArray());

            // Unknown @attachment in PendingProposals fails fast with AttachmentNotFoundException
            proposalsToEmit = [new("Broken Child", "Cites @attachment:999999")];
            var badProposalTask = await taskRepo.CreateAsync(new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Bad proposal parent",
                Description = "Parent",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main"
            });
            depStore.AddTask(badProposalTask.Id, workspace.Id, TaskOverallStatus.Open);

            await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
                orchestrator.ProcessTaskAsync(badProposalTask.Id));

            // Post-stage markdown sync AttachmentNotFoundException propagates un-wrapped (not wrapped in TaskMarkdownSyncException)
            proposalsToEmit = null;
            var syncMock = new Mock<ITaskMarkdownSyncService>();
            syncMock.Setup(s => s.SyncAllTasksFromDiskAsync(
                    It.IsAny<Workspace>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new AttachmentNotFoundException(777, workspace.Id));
            builder.MarkdownSyncService = syncMock.Object;

            var wp2 = builder.BuildWorkspacePreparer();
            var mc2 = builder.BuildMergeAndCascadeCoordinator(wp2);
            var lm2 = builder.BuildLifecycleMutationCoordinator(wp2, mc2);
            var orch2 = new TaskOrchestrator(
                taskRepo,
                wp2,
                mc2,
                lm2,
                builder.HarnessRegistry,
                builder.Streamer,
                builder.OperationsControlService,
                builder.TelemetryService,
                attachmentRepo,
                NullLogger<TaskOrchestrator>.Instance);

            var syncFailTask = await taskRepo.CreateAsync(new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Post stage sync fail",
                Description = "Parent",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main"
            });
            depStore.AddTask(syncFailTask.Id, workspace.Id, TaskOverallStatus.Open);
            var resolvedWorktree = await fakeGit.EnsureWorktreeAsync(workspace, syncFailTask.Id, "main");
            Directory.CreateDirectory(resolvedWorktree);

            await Assert.ThrowsAsync<AttachmentNotFoundException>(() =>
                orch2.ProcessTaskAsync(syncFailTask.Id));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static TaskAttachment CreateAttachment(
        int id,
        int workspaceId,
        string title,
        string relPath,
        string? mdPath,
        TaskAttachmentContentKind kind,
        int? width = null,
        int? height = null,
        string mediaType = "text/plain",
        bool isSymlink = false) =>
        new()
        {
            Id = id,
            WorkspaceId = workspaceId,
            Title = title,
            Slug = title.ToLowerInvariant().Replace(' ', '-'),
            RelativePath = relPath,
            MarkdownPath = mdPath,
            ContentKind = kind,
            MediaType = mediaType,
            WidthPx = width,
            HeightPx = height,
            SourceKind = isSymlink ? TaskAttachmentSourceKind.HostPath : TaskAttachmentSourceKind.FileDrop,
            SourceRef = isSymlink ? "/mnt/c/docs/" + relPath : relPath,
            IsSymlink = isSymlink,
            Sha256 = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            SizeBytes = 128,
            CreatedAt = DateTime.UtcNow
        };
}
