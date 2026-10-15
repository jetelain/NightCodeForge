using Bunit;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TerminalConsoleTests : BunitContext
{
    public TerminalConsoleTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void TerminalConsole_WhenEmpty_RendersEmptyPlaceholder()
    {
        var cut = Render<TerminalConsole>();

        Assert.Contains("No logs recorded yet", cut.Find(".terminal-empty").TextContent);
    }

    [Fact]
    public void TerminalConsole_RendersFormattedLogLines()
    {
        var initialLogs = new List<TerminalConsole.TerminalLog>
        {
            new(10, "Stage1", "Information", "Task initialized successfully", new DateTime(2026, 10, 15, 12, 0, 0, 123)),
            new(10, "Stage2", "Warning", "Compiler warning CS8618 detected", new DateTime(2026, 10, 15, 12, 0, 1, 456)),
            new(10, "Stage3", "Error", "Test assertion failed", new DateTime(2026, 10, 15, 12, 0, 2, 789))
        };

        var cut = Render<TerminalConsole>(p => p.Add(c => c.InitialLogs, initialLogs));

        var lines = cut.FindAll(".log-line");
        Assert.Equal(3, lines.Count);

        Assert.Contains("[12:00:00.123]", lines[0].TextContent);
        Assert.Contains("[Stage1]", lines[0].TextContent);
        Assert.Contains("[Information]", lines[0].TextContent);
        Assert.Contains("Task initialized successfully", lines[0].TextContent);

        Assert.Contains("[Warning]", lines[1].TextContent);
        Assert.Contains("[Error]", lines[2].TextContent);
    }

    [Fact]
    public void TerminalConsole_FiltersByLevel()
    {
        var initialLogs = new List<TerminalConsole.TerminalLog>
        {
            new(10, "Stage1", "Information", "Info message", DateTime.UtcNow),
            new(10, "Stage2", "Warning", "Warning message", DateTime.UtcNow),
            new(10, "Stage3", "Error", "Error message", DateTime.UtcNow)
        };

        var cut = Render<TerminalConsole>(p => p.Add(c => c.InitialLogs, initialLogs));

        // Select Error
        var select = cut.Find("select");
        select.Change("Error");

        var lines = cut.FindAll(".log-line");
        Assert.Single(lines);
        Assert.Contains("Error message", lines[0].TextContent);
    }

    [Fact]
    public void TerminalConsole_FiltersBySearchText()
    {
        var initialLogs = new List<TerminalConsole.TerminalLog>
        {
            new(10, "Stage1", "Information", "Running git status", DateTime.UtcNow),
            new(10, "Stage2", "Information", "Compiling Roslyn workspace", DateTime.UtcNow)
        };

        var cut = Render<TerminalConsole>(p => p.Add(c => c.InitialLogs, initialLogs));

        var input = cut.Find("input[placeholder='Search logs...']");
        input.Input("Roslyn");

        var lines = cut.FindAll(".log-line");
        Assert.Single(lines);
        Assert.Contains("Compiling Roslyn workspace", lines[0].TextContent);
    }

    [Fact]
    public void TerminalConsole_ClearButton_RemovesAllLogs()
    {
        var initialLogs = new List<TerminalConsole.TerminalLog>
        {
            new(10, "Stage1", "Information", "Log 1", DateTime.UtcNow),
            new(10, "Stage2", "Information", "Log 2", DateTime.UtcNow)
        };

        var cut = Render<TerminalConsole>(p => p.Add(c => c.InitialLogs, initialLogs));
        Assert.Equal(2, cut.FindAll(".log-line").Count);

        var clearBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Clear");
        clearBtn.Click();

        Assert.Empty(cut.FindAll(".log-line"));
        Assert.NotEmpty(cut.FindAll(".terminal-empty"));
    }

    [Fact]
    public void TerminalConsole_ToggleAutoScroll_SwitchesButtonText()
    {
        var cut = Render<TerminalConsole>();

        var autoScrollBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Scroll"));
        Assert.Contains("Auto-Scroll ON", autoScrollBtn.TextContent);

        autoScrollBtn.Click();
        var refreshedBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Scroll"));
        Assert.Contains("Auto-Scroll OFF", refreshedBtn.TextContent);
    }

    [Fact]
    public void TerminalConsole_HighThroughputIngestion_Handles500LinesSmoothly()
    {
        var cut = Render<TerminalConsole>();

        cut.InvokeAsync(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                cut.Instance.AddLog(10, "Stage3", "Information", $"High-throughput line #{i}", DateTime.UtcNow);
            }
        });

        Assert.Equal(500, cut.FindAll(".log-line").Count);
    }
}
