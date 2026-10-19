using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class KanbanCardPriorityEditorTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly MockDevQueueService _devQueueService;
    private readonly ToastService _toastService;

    public KanbanCardPriorityEditorTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _devQueueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public void KanbanCard_ClickingPriorityBadge_OpensPopover()
    {
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Quick triage task",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task));

        Assert.Empty(cut.FindAll(".priority-popover"));

        var badgeBtn = cut.Find(".priority-popover-btn");
        badgeBtn.Click();

        Assert.NotEmpty(cut.FindAll(".priority-popover"));
        var items = cut.FindAll(".priority-menu-item");
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public void KanbanCard_SelectingPriority_UpdatesPriorityAndFiresCallback()
    {
        var task = new ForgeTask
        {
            Id = 43,
            Title = "Quick reprioritize task",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };

        (ForgeTask? updatedTask, int newP) callbackResult = (null, 0);

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.OnPriorityChanged, (args) => { callbackResult = (args.Task, args.NewPriority); }));

        var badgeBtn = cut.Find(".priority-popover-btn");
        badgeBtn.Click();

        // Click P1 Critical in popover
        var p1Item = cut.FindAll(".priority-menu-item").First(b => b.TextContent.Contains("P1"));
        p1Item.Click();

        Assert.Equal(TaskPriority.Critical, task.Priority);
        Assert.Equal(43, callbackResult.updatedTask?.Id);
        Assert.Equal(TaskPriority.Critical, callbackResult.newP);

        // Popover should be closed after selection
        Assert.Empty(cut.FindAll(".priority-popover"));
    }

    [Fact]
    public async Task KanbanCard_WhenMarkdownSyncThrows_LogsWarningWithoutFailingPriorityUpdate()
    {
        var task = new ForgeTask
        {
            Id = 44,
            WorkspaceId = 1,
            Title = "Quick reprioritize with sync error",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var tempDir = Path.GetTempPath();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(new Workspace { Id = 1, Name = "Test WS", RepositoryPath = tempDir });
        Services.AddSingleton<IWorkspaceRepository>(workspaceRepo);

        var mockSync = new Mock<ITaskMarkdownSyncService>();
        mockSync.Setup(s => s.SyncTaskToDiskAsync(It.IsAny<ForgeTask>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Simulated disk error"));
        Services.AddSingleton(mockSync.Object);

        var mockLogger = new Mock<ILogger<KanbanCard>>();
        Services.AddSingleton(mockLogger.Object);

        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task));

        var badgeBtn = cut.Find(".priority-popover-btn");
        badgeBtn.Click();

        var p2Item = cut.FindAll(".priority-menu-item").First(b => b.TextContent.Contains("P2"));
        p2Item.Click();

        Assert.Equal(TaskPriority.High, task.Priority);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to sync markdown")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
