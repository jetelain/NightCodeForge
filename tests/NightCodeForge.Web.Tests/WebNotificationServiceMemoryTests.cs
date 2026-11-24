using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using NightCodeForge.Core.Memory;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WebNotificationServiceMemoryTests
{
    private const string ShowNotificationJs = "NightCodeForge.notifications.showNotification";

    private readonly ToastService _toastService = TestServices.CreateToastService();
    private readonly TestNavigationManager _nav = new("http://localhost/");
    private readonly Mock<IJSRuntime> _jsMock = new();
    private readonly OrchestratorHubClientConnection _hub;
    private readonly WebNotificationService _service;

    public WebNotificationServiceMemoryTests()
    {
        _hub = TestHubConnectionHelper.Create(nav: _nav);
        _jsMock.Setup(j => j.InvokeAsync<NotificationPreferences>("NightCodeForge.notifications.getPreferences", It.IsAny<object[]>()))
            .ReturnsAsync(new NotificationPreferences());
        _jsMock.Setup(j => j.InvokeAsync<bool>("NightCodeForge.notifications.isDocumentVisible", It.IsAny<object[]>()))
            .ReturnsAsync(false);
        _jsMock.Setup(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>())).ReturnsAsync(true);

        _service = new WebNotificationService(
            _hub,
            new InMemoryTaskRepository(),
            _toastService,
            _nav,
            _jsMock.Object,
            NullLogger<WebNotificationService>.Instance);
    }

    [Fact]
    public void ReceiveMemoryUpdated_Ideation_ShowsToast()
    {
        _hub.TriggerMemoryUpdated(1, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/ideation-vision.md", "Updated", "s");

        var toast = Assert.Single(_toastService.Toasts);
        Assert.Equal(MemoryUiConstants.ToastTitle, toast.Title);
        Assert.Equal("Ideation updated /workspace/memory/ideation-vision.md", toast.Message);
    }

    [Fact]
    public void ReceiveMemoryUpdated_Task_ShowsTaskAndStage()
    {
        _hub.TriggerMemoryUpdated(1, 42, "2", AgentMemoryConstants.ScopeSharedGitMetadataValue, ".agents/memory/architecture-decisions.md", "Created", "s");

        var toast = Assert.Single(_toastService.Toasts);
        Assert.Equal("Task #42 (Stage 2) created .agents/memory/architecture-decisions.md", toast.Message);
    }

    [Fact]
    public async Task ReceiveMemoryUpdated_PushDisabledByDefault_NoDesktopNotification()
    {
        _hub.TriggerMemoryUpdated(1, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Updated", "s");
        await Task.Delay(100);

        _jsMock.Verify(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task ReceiveMemoryUpdated_PushEnabledAndTabHidden_DispatchesTasklessNotification()
    {
        string? clickUrl = null;
        _jsMock.Setup(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>()))
            .Callback<string, object?[]?>((_, args) =>
            {
                if (args is { Length: >= 2 } && args[1] != null)
                {
                    var data = args[1]!.GetType().GetProperty("data")?.GetValue(args[1]);
                    clickUrl = data?.GetType().GetProperty("url")?.GetValue(data)?.ToString();
                }
            })
            .ReturnsAsync(true);

        await _service.SavePreferencesAsync(new NotificationPreferences { NotifyOnAgentMemory = true });

        _hub.TriggerMemoryUpdated(5, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Updated", "s");

        await WaitUntilAsync(() =>
        {
            try
            {
                _jsMock.Verify(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>()), Times.Once);
                return true;
            }
            catch (MockException)
            {
                return false;
            }
        });

        Assert.Equal("/w/5/memory?scope=local&path=a.md", clickUrl);
    }

    [Fact]
    public async Task TasklessDispatch_RespectsMasterToggle()
    {
        await _service.SavePreferencesAsync(new NotificationPreferences { MasterEnabled = false, NotifyOnAgentMemory = true });

        var dispatched = await _service.DispatchNotificationAsync(NotificationSubject.AgentMemory, "t", "b", "tag", "/x");

        Assert.False(dispatched);
    }

    [Fact]
    public async Task ReceiveMemoryUpdated_RapidMultiFileUpdates_DispatchDistinctTags()
    {
        var tags = new List<string>();
        _jsMock.Setup(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>()))
            .Callback<string, object?[]?>((_, args) =>
            {
                if (args is { Length: >= 2 } && args[1] != null)
                {
                    var tag = args[1]!.GetType().GetProperty("tag")?.GetValue(args[1])?.ToString();
                    if (tag != null)
                    {
                        tags.Add(tag);
                    }
                }
            })
            .ReturnsAsync(true);

        await _service.SavePreferencesAsync(new NotificationPreferences { NotifyOnAgentMemory = true });

        _hub.TriggerMemoryUpdated(5, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Updated", "s");
        _hub.TriggerMemoryUpdated(5, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/b.md", "Updated", "s");

        await WaitUntilAsync(() => tags.Count == 2);

        Assert.Equal(2, tags.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(tags, t => t.Contains("a.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tags, t => t.Contains("b.md", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReceiveMemoryUpdated_DeletedFile_PushNotificationLinksToMemoryRootWithoutFileParams()
    {
        string? clickUrl = null;
        _jsMock.Setup(j => j.InvokeAsync<bool>(ShowNotificationJs, It.IsAny<object[]>()))
            .Callback<string, object?[]?>((_, args) =>
            {
                if (args is { Length: >= 2 } && args[1] != null)
                {
                    var data = args[1]!.GetType().GetProperty("data")?.GetValue(args[1]);
                    clickUrl = data?.GetType().GetProperty("url")?.GetValue(data)?.ToString();
                }
            })
            .ReturnsAsync(true);

        await _service.SavePreferencesAsync(new NotificationPreferences { NotifyOnAgentMemory = true });

        _hub.TriggerMemoryUpdated(5, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Deleted", "s");

        await WaitUntilAsync(() => clickUrl != null);

        Assert.Equal("/w/5/memory", clickUrl);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromMemoryEvents()
    {
        await _service.DisposeAsync();

        _hub.TriggerMemoryUpdated(1, null, "", AgentMemoryConstants.ScopeLocalMetadataValue, "/workspace/memory/a.md", "Updated", "s");

        Assert.Empty(_toastService.Toasts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
