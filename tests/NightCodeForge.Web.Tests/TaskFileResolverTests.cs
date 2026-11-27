using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskFileResolverTests : BunitContext
{
    private const int WorkspaceId = 9;

    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly Mock<IGitRepositoryBrowserService> _gitBrowserMock = new();
    private readonly Mock<ITaskMarkdownSyncService> _syncServiceMock = new();
    private readonly Mock<IWorkspacePathResolver> _pathResolverMock = new();

    public TaskFileResolverTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        _syncServiceMock.Setup(s => s.ParseMarkdown(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string content, string? _) =>
            {
                var real = new TaskMarkdownSyncService(
                    new Mock<IServiceScopeFactory>().Object,
                    _pathResolverMock.Object,
                    NullLogger<TaskMarkdownSyncService>.Instance);
                return real.ParseMarkdown(content, null);
            });
    }

    private TaskFileResolverService CreateService() => new(
        _workspaceRepo,
        _taskRepo,
        _gitBrowserMock.Object,
        _syncServiceMock.Object,
        _pathResolverMock.Object,
        NullLogger<TaskFileResolverService>.Instance);

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("docs/tasks/nested/task.md")]
    [InlineData("src/Program.cs")]
    [InlineData("/docs/tasks/foo.md")]
    [InlineData("docs/tasks/.hidden.md")]
    public async Task ResolveAsync_InvalidPaths_ReturnsInvalidPath(string path)
    {
        var service = CreateService();
        var result = await service.ResolveAsync(WorkspaceId, path, "main", null);

        Assert.Equal(TaskFileResolutionOutcome.InvalidPath, result.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_FrontmatterIdInGit_ResolvesTaskWithinSameWorkspaceOnly()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace { Name = "WS", RepositoryPath = "/repos/ws", DefaultBaseBranch = "main" });
        var task = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Implement Links" });
        var otherWsTask = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id + 1, Title = "Foreign Task" });

        var resolved = new GitResolvedRef("main", "sha123", null);
        _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, "main", "docs/tasks/2026-11-26-implement-links.md", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved, "docs/tasks/2026-11-26-implement-links.md", "2026-11-26-implement-links.md",
                GitFileContentKind.Text, 64, $"---\nid: {task.Id}\ntitle: Implement Links\n---\n# Body", 5,
                null, null, null, null, null));

        var service = CreateService();
        var hit = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-26-implement-links.md", "main", null);
        Assert.Equal(TaskFileResolutionOutcome.TaskFound, hit.Outcome);
        Assert.Equal(task.Id, hit.TaskId);

        // Frontmatter pointing to another workspace must NOT cross workspace boundary
        _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, "main", "docs/tasks/2026-11-26-other.md", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved, "docs/tasks/2026-11-26-other.md", "2026-11-26-other.md",
                GitFileContentKind.Text, 64, $"---\nid: {otherWsTask.Id}\ntitle: Unrelated\n---\n# Body", 5,
                null, null, null, null, null));

        var crossWs = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-26-other.md", "main", null);
        Assert.Equal(TaskFileResolutionOutcome.FallbackToFileViewer, crossWs.Outcome);
    }

    [Fact]
    public async Task ResolveAsync_UncommittedHostDiskTaskFile_ResolvesByFrontmatter_WhenNotInGit()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "ncf-resolver-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tasksDir = Path.Combine(tempRepo, "docs", "tasks");
            Directory.CreateDirectory(tasksDir);

            var ws = await _workspaceRepo.CreateAsync(new Workspace { Name = "DiskWS", RepositoryPath = tempRepo, DefaultBaseBranch = "main" });
            var task = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Uncommitted Task" });

            await File.WriteAllTextAsync(
                Path.Combine(tasksDir, "2026-11-27-uncommitted-task.md"),
                $"---\nid: {task.Id}\ntitle: Uncommitted Task\n---\n# Spec");

            _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, "main", "docs/tasks/2026-11-27-uncommitted-task.md", null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GitBrowserPathNotFoundException("docs/tasks/2026-11-27-uncommitted-task.md"));

            var service = CreateService();
            var result = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-27-uncommitted-task.md", "main", null);

            Assert.Equal(TaskFileResolutionOutcome.TaskFound, result.Outcome);
            Assert.Equal(task.Id, result.TaskId);
        }
        finally
        {
            if (Directory.Exists(tempRepo))
            {
                Directory.Delete(tempRepo, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToSlugDateAndArchiveMatching_AndToFileViewerWhenNoDbTask()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace { Name = "SlugWS", RepositoryPath = "/missing/repo", DefaultBaseBranch = "main" });
        var activeTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Build Link Builder",
            Status = TaskOverallStatus.Open,
            CreatedAt = new DateTime(2026, 11, 26, 10, 0, 0, DateTimeKind.Utc)
        });
        var archivedTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Build Link Builder",
            Status = TaskOverallStatus.Closed,
            CreatedAt = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc)
        });

        _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserPathNotFoundException("not found"));

        var service = CreateService();

        var activeResult = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-26-build-link-builder.md", "main", null);
        Assert.Equal(TaskFileResolutionOutcome.TaskFound, activeResult.Outcome);
        Assert.Equal(activeTask.Id, activeResult.TaskId);

        var archivedResult = await service.ResolveAsync(ws.Id, "docs/tasks/archive/2026-11-20-build-link-builder.md", "main", null);
        Assert.Equal(TaskFileResolutionOutcome.TaskFound, archivedResult.Outcome);
        Assert.Equal(archivedTask.Id, archivedResult.TaskId);

        // External/un-ingested markdown existing in Git falls back to FileViewer
        var resolved = new GitResolvedRef("feature/task-77", "deadbeef", null);
        _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, "feature/task-77", "docs/tasks/2026-11-27-external-only.md", 77, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitPathInspectionResult(
                resolved, "docs/tasks/2026-11-27-external-only.md", "2026-11-27-external-only.md",
                GitFileContentKind.Text, 20, "# External spec", 1, null, null, null, null, null));

        var fileFallback = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-27-external-only.md", "feature/task-77", 77);
        Assert.Equal(TaskFileResolutionOutcome.FallbackToFileViewer, fileFallback.Outcome);
        Assert.Equal("feature/task-77", fileFallback.ResolvedRef);

        var missing = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-27-completely-missing.md", "main", null);
        Assert.Equal(TaskFileResolutionOutcome.NotFound, missing.Outcome);
    }

    [Fact]
    public void TaskByFilePage_RedirectsToTaskOrFiles_AndShowsNotFoundWithBacklogLink()
    {
        var resolverMock = new Mock<ITaskFileResolverService>();
        resolverMock.Setup(r => r.ResolveAsync(WorkspaceId, "docs/tasks/a.md", "main", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskFileResolutionResult(TaskFileResolutionOutcome.TaskFound, 42, "docs/tasks/a.md", "main", null, null));
        resolverMock.Setup(r => r.ResolveAsync(WorkspaceId, "docs/tasks/b.md", "main", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskFileResolutionResult(TaskFileResolutionOutcome.FallbackToFileViewer, null, "docs/tasks/b.md", "main", 7, null));
        resolverMock.Setup(r => r.ResolveAsync(WorkspaceId, "docs/tasks/c.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskFileResolutionResult(TaskFileResolutionOutcome.NotFound, null, "docs/tasks/c.md", "main", null, "Not found."));

        // 1. Task found -> redirects to /w/9/tasks/42
        using (var ctx1 = CreateTaskByFileTestContext(resolverMock.Object))
        {
            var nav1 = ctx1.Services.GetRequiredService<NavigationManager>();
            nav1.NavigateTo($"http://localhost/w/{WorkspaceId}/tasks/by-file?path=docs%2Ftasks%2Fa.md&ref=main");
            ctx1.Render<TaskByFile>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
            Assert.EndsWith($"/w/{WorkspaceId}/tasks/42", nav1.Uri);
        }

        // 2. Git file fallback -> redirects to /w/9/files?ref=main&path=docs%2Ftasks%2Fb.md&taskId=7
        using (var ctx2 = CreateTaskByFileTestContext(resolverMock.Object))
        {
            var nav2 = ctx2.Services.GetRequiredService<NavigationManager>();
            nav2.NavigateTo($"http://localhost/w/{WorkspaceId}/tasks/by-file?path=docs%2Ftasks%2Fb.md&ref=main&taskId=7");
            ctx2.Render<TaskByFile>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
            Assert.EndsWith($"/w/{WorkspaceId}/files?ref=main&path=docs%2Ftasks%2Fb.md&taskId=7", nav2.Uri);
        }

        // 3. Not found -> renders inline card with backlog link
        using (var ctx3 = CreateTaskByFileTestContext(resolverMock.Object))
        {
            var nav3 = ctx3.Services.GetRequiredService<NavigationManager>();
            nav3.NavigateTo($"http://localhost/w/{WorkspaceId}/tasks/by-file?path=docs%2Ftasks%2Fc.md");
            var notFoundCut = ctx3.Render<TaskByFile>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
            Assert.NotNull(notFoundCut.Find("#task-by-file-not-found"));
            Assert.Equal($"/w/{WorkspaceId}/backlog", notFoundCut.Find("#task-by-file-backlog-link").GetAttribute("href"));
        }

        // 4. Workspace-less -> renders WorkspaceEmptyStateHero
        using var emptyCtx = new BunitContext();
        emptyCtx.Services.AddSingleton(new WorkspaceStateService());
        emptyCtx.Services.AddSingleton(resolverMock.Object);
        var emptyCut = emptyCtx.Render<TaskByFile>();
        Assert.NotEmpty(emptyCut.FindAll(".workspace-empty-hero"));
    }

    [Fact]
    public async Task AttachmentUiService_LoadPreviewAsync_RendersContextAwareLinks()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ncf-att-preview-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tempFile, "See [mem](/workspace/memory/notes.md) and [code](src/Foo.cs:10).");

            var ws = await _workspaceRepo.CreateAsync(new Workspace
            {
                Name = "AttWS",
                RepositoryPath = "/repos/att",
                DefaultBaseBranch = "develop"
            });

            var attachment = new TaskAttachment
            {
                Id = 55,
                WorkspaceId = ws.Id,
                Title = "notes.md",
                SourceRef = tempFile,
                RelativePath = Path.GetFileName(tempFile),
                MarkdownPath = tempFile,
                ContentKind = TaskAttachmentContentKind.PlainText
            };

            _pathResolverMock.Setup(p => p.GetAttachmentsPath(ws.Id)).Returns(Path.GetTempPath());

            var attRepoMock = new Mock<ITaskAttachmentRepository>();
            attRepoMock.Setup(r => r.GetByIdAsync(55, It.IsAny<CancellationToken>())).ReturnsAsync(attachment);
            attRepoMock.Setup(r => r.GetLinkedTasksAsync(55, It.IsAny<CancellationToken>())).ReturnsAsync([]);

            var uiService = new AttachmentUiService(
                new Mock<IAttachmentIntakeService>().Object,
                attRepoMock.Object,
                _taskRepo,
                _workspaceRepo,
                _pathResolverMock.Object,
                new Mock<IIdeationConversationCoordinator>().Object,
                new Mock<IVisionCapabilityUiService>().Object,
                _syncServiceMock.Object,
                new NightCodeForge.Core.Concurrency.WorkspaceLockProvider(),
                NullLogger<AttachmentUiService>.Instance);

            var preview = await uiService.LoadPreviewAsync(55);

            Assert.Contains($"href=\"/w/{ws.Id}/memory?scope=local&amp;path=notes.md\"", preview.RenderedHtml);
            Assert.Contains($"href=\"/w/{ws.Id}/files?ref=develop&amp;path=src%2FFoo.cs#L10\"", preview.RenderedHtml);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenFileExistsInGitWithoutTextContent_DoesNotReadUncommittedDiskFile()
    {
        var tempRepo = Path.Combine(Path.GetTempPath(), "ncf-resolver-git-exists-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tasksDir = Path.Combine(tempRepo, "docs", "tasks");
            Directory.CreateDirectory(tasksDir);

            var ws = await _workspaceRepo.CreateAsync(new Workspace { Name = "GitExistsWS", RepositoryPath = tempRepo, DefaultBaseBranch = "main" });
            var task = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = ws.Id, Title = "Different Task" });

            await File.WriteAllTextAsync(
                Path.Combine(tasksDir, "2026-11-27-symlink-task.md"),
                $"---\nid: {task.Id}\ntitle: Different Task\n---\n# Stale disk file");

            var resolved = new GitResolvedRef("main", "sha123", null);
            _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, "main", "docs/tasks/2026-11-27-symlink-task.md", null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitPathInspectionResult(
                    resolved, "docs/tasks/2026-11-27-symlink-task.md", "2026-11-27-symlink-task.md",
                    GitFileContentKind.Symlink, 12, null, null, "../other.md", null, null, null, null));

            var service = CreateService();
            var result = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-27-symlink-task.md", "main", null);

            Assert.Equal(TaskFileResolutionOutcome.FallbackToFileViewer, result.Outcome);
        }
        finally
        {
            if (Directory.Exists(tempRepo))
            {
                Directory.Delete(tempRepo, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ResolveAsync_SlugDateMatching_HandlesUnspecifiedUtcCreatedAtWithoutLocalTimezoneShift()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace { Name = "TzWS", RepositoryPath = "/missing/repo", DefaultBaseBranch = "main" });

        // Older date (Nov 25) has newer UpdatedAt so only exact date match (Nov 26) can make nov26Task win.
        var nov25Task = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Timezone Edge Task",
            Status = TaskOverallStatus.Open,
            CreatedAt = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Unspecified),
            UpdatedAt = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc)
        });
        var nov26Task = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Timezone Edge Task",
            Status = TaskOverallStatus.Open,
            CreatedAt = new DateTime(2026, 11, 26, 0, 5, 0, DateTimeKind.Unspecified),
            UpdatedAt = new DateTime(2026, 11, 26, 0, 5, 0, DateTimeKind.Utc)
        });

        _gitBrowserMock.Setup(g => g.InspectPathAsync(ws.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitBrowserPathNotFoundException("not found"));

        var service = CreateService();
        var result = await service.ResolveAsync(ws.Id, "docs/tasks/2026-11-26-timezone-edge-task.md", "main", null);

        Assert.Equal(TaskFileResolutionOutcome.TaskFound, result.Outcome);
        Assert.Equal(nov26Task.Id, result.TaskId);
        Assert.NotEqual(nov25Task.Id, result.TaskId);
    }

    [Fact]
    public async Task ResolveAsync_InvalidFileNameCharacters_ReturnsInvalidPath()
    {
        var service = CreateService();
        var result = await service.ResolveAsync(WorkspaceId, "docs/tasks/a:b.md", "main", null);

        Assert.Equal(TaskFileResolutionOutcome.InvalidPath, result.Outcome);
    }

    [Fact]
    public async Task TaskByFilePage_ColdLoadWithoutWorkspace_ReactsWhenWorkspaceStateActivates()
    {
        var resolverMock = new Mock<ITaskFileResolverService>();
        resolverMock.Setup(r => r.ResolveAsync(WorkspaceId, "docs/tasks/a.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskFileResolutionResult(TaskFileResolutionOutcome.TaskFound, 42, "docs/tasks/a.md", "main", null, null));

        using var ctx = new BunitContext();
        var wsState = new WorkspaceStateService();
        ctx.Services.AddSingleton(wsState);
        ctx.Services.AddSingleton(resolverMock.Object);

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/tasks/by-file?path=docs%2Ftasks%2Fa.md");

        var cut = ctx.Render<TaskByFile>();
        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));

        await cut.InvokeAsync(() => wsState.SetActiveWorkspace(new Workspace
        {
            Id = WorkspaceId,
            Name = "WS",
            RepositoryPath = "/repos/ws",
            DefaultBaseBranch = "main"
        }));

        cut.WaitForAssertion(() => Assert.EndsWith($"/w/{WorkspaceId}/tasks/42", nav.Uri));
    }

    [Fact]
    public void TaskByFilePage_ExplicitWorkspaceId_DoesNotFallBackToDifferentActiveWorkspace()
    {
        var resolverMock = new Mock<ITaskFileResolverService>();
        resolverMock.Setup(r => r.ResolveAsync(999, "docs/tasks/a.md", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskFileResolutionResult(TaskFileResolutionOutcome.WorkspaceNotFound, null, "docs/tasks/a.md", null, null, "Workspace #999 was not found."));

        using var ctx = CreateTaskByFileTestContext(resolverMock.Object);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/w/999/tasks/by-file?path=docs%2Ftasks%2Fa.md");

        var cut = ctx.Render<TaskByFile>(p => p.Add(x => x.WorkspaceId, 999));

        Assert.Contains("Workspace #999 was not found.", cut.Find("#task-by-file-not-found").TextContent);
        resolverMock.Verify(r => r.ResolveAsync(999, "docs/tasks/a.md", null, null, It.IsAny<CancellationToken>()), Times.Once);
        resolverMock.Verify(r => r.ResolveAsync(WorkspaceId, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TaskByFilePage_DisposedWhileResolving_DoesNotRedirectOnCompletion()
    {
        var tcs = new TaskCompletionSource<TaskFileResolutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolverMock = new Mock<ITaskFileResolverService>();
        resolverMock.Setup(r => r.ResolveAsync(WorkspaceId, "docs/tasks/a.md", null, null, It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        NavigationManager nav;
        using (var ctx = CreateTaskByFileTestContext(resolverMock.Object))
        {
            nav = ctx.Services.GetRequiredService<NavigationManager>();
            nav.NavigateTo($"http://localhost/w/{WorkspaceId}/tasks/by-file?path=docs%2Ftasks%2Fa.md");
            ctx.Render<TaskByFile>(p => p.Add(x => x.WorkspaceId, WorkspaceId));
        }

        tcs.SetResult(new TaskFileResolutionResult(TaskFileResolutionOutcome.TaskFound, 42, "docs/tasks/a.md", "main", null, null));
        Assert.DoesNotContain($"/w/{WorkspaceId}/tasks/42", nav.Uri);
    }

    private static BunitContext CreateTaskByFileTestContext(ITaskFileResolverService resolver)
    {
        var ctx = new BunitContext();
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(new Workspace
        {
            Id = WorkspaceId,
            Name = "WS",
            RepositoryPath = "/repos/ws",
            DefaultBaseBranch = "main"
        });
        ctx.Services.AddSingleton(wsState);
        ctx.Services.AddSingleton(resolver);
        return ctx;
    }
}
