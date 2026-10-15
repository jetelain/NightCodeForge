using Bunit;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ToolActivityStripTests : BunitContext
{
    [Fact]
    public void ToolActivityStrip_WhenEmpty_RendersIdleMessage()
    {
        var cut = Render<ToolActivityStrip>();

        Assert.Contains("No active tool invocations", cut.Markup);
    }

    [Fact]
    public void ToolActivityStrip_WhenToolRunning_RendersRunningPillAndStopwatch()
    {
        var cut = Render<ToolActivityStrip>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.HandleToolExecution(10, "bash", "dotnet test", "Running", null);
        });

        var pill = cut.Find(".tool-call-pill.running");
        Assert.NotNull(pill);
        Assert.Contains("bash", pill.TextContent);
        Assert.Contains("s...", pill.TextContent);
        Assert.NotEmpty(pill.QuerySelectorAll(".spinner-pulse"));
    }

    [Fact]
    public void ToolActivityStrip_WhenToolCompleted_RendersDurationBadge()
    {
        var cut = Render<ToolActivityStrip>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.HandleToolExecution(10, "read_file", "path/to/code.cs", "Running", null);
            cut.Instance.HandleToolExecution(10, "read_file", "path/to/code.cs", "Completed", 1450);
        });

        var pill = cut.Find(".tool-call-pill.completed");
        Assert.NotNull(pill);
        Assert.Contains("read_file", pill.TextContent);
        Assert.Contains("✓", pill.TextContent);
        Assert.Contains("1.45s", pill.TextContent);
    }

    [Fact]
    public void ToolActivityStrip_WhenMultipleToolsExecuted_RendersAllPills()
    {
        var cut = Render<ToolActivityStrip>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.HandleToolExecution(10, "bash", "git checkout -b feature/test", "Completed", 250);
            cut.Instance.HandleToolExecution(10, "edit", "Fix compiler warning", "Completed", 80);
            cut.Instance.HandleToolExecution(10, "ask", "Clarify specification", "Running", null);
        });

        var pills = cut.FindAll(".tool-call-pill");
        Assert.Equal(3, pills.Count);
        Assert.Contains("ask", pills[0].TextContent);
        Assert.Contains("edit", pills[1].TextContent);
        Assert.Contains("bash", pills[2].TextContent);
    }
}
