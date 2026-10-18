using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class ToastServiceTests
{
    [Fact]
    public void InitialState_ShouldHaveEmptyToasts()
    {
        var service = TestServices.CreateToastService();

        Assert.Empty(service.Toasts);
    }

    [Fact]
    public void Show_ShouldAddToastAndNotify()
    {
        var service = TestServices.CreateToastService();
        var eventFired = false;
        service.OnToastsChanged += () => eventFired = true;

        service.Show("Title A", "Message A", ToastLevel.Success);

        Assert.Single(service.Toasts);
        var toast = service.Toasts[0];
        Assert.Equal("Title A", toast.Title);
        Assert.Equal("Message A", toast.Message);
        Assert.Equal(ToastLevel.Success, toast.Level);
        Assert.NotEqual(Guid.Empty, toast.Id);
        Assert.True(eventFired);
    }

    [Fact]
    public void Remove_ShouldRemoveMatchingToastAndNotify()
    {
        var service = TestServices.CreateToastService();
        service.Show("Notice 1", "Content 1", ToastLevel.Info);
        service.Show("Notice 2", "Content 2", ToastLevel.Warning);

        Assert.Equal(2, service.Toasts.Count);
        var firstId = service.Toasts[0].Id;
        var secondId = service.Toasts[1].Id;

        var eventFired = false;
        service.OnToastsChanged += () => eventFired = true;

        service.Remove(firstId);

        Assert.Single(service.Toasts);
        Assert.Equal(secondId, service.Toasts[0].Id);
        Assert.True(eventFired);
    }

    [Fact]
    public void Remove_WithNonExistentId_ShouldNotThrow()
    {
        var service = TestServices.CreateToastService();
        service.Show("Notice", "Content", ToastLevel.Info);

        service.Remove(Guid.NewGuid());

        Assert.Single(service.Toasts);
    }
}
