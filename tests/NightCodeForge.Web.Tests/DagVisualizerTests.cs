using Bunit;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Backlog;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DagVisualizerTests : BunitContext
{
    [Fact]
    public void WhenNoTasks_RendersEmptyState()
    {
        var cut = Render<DagVisualizer>();

        Assert.NotEmpty(cut.FindAll(".dag-empty-state"));
        Assert.Contains("No Tasks in Active Workspace", cut.Find(".dag-empty-state").TextContent);
    }

    [Fact]
    public void WhenTasksAndDependencies_RendersSvgNodesAndEdges()
    {
        var task1 = new ForgeTask { Id = 1, Title = "Foundation Architecture", Status = TaskOverallStatus.Closed };
        var task2 = new ForgeTask { Id = 2, Title = "Feature Implementation", Status = TaskOverallStatus.Open };
        var task3 = new ForgeTask { Id = 3, Title = "Documentation Update", Status = TaskOverallStatus.Open };

        var tasks = new List<ForgeTask> { task1, task2, task3 };
        var deps = new List<(int TaskId, int DependsOnTaskId)>
        {
            (TaskId: 2, DependsOnTaskId: 1), // 2 depends on 1 (1 is Closed -> satisfied edge)
            (TaskId: 3, DependsOnTaskId: 2)  // 3 depends on 2 (2 is Open -> pending edge)
        };

        var cut = Render<DagVisualizer>(p => p
            .Add(v => v.Tasks, tasks)
            .Add(v => v.Dependencies, deps));

        // SVG element present
        var svg = cut.Find("svg.dag-svg");
        Assert.NotNull(svg);

        // Nodes present
        var nodeGroups = cut.FindAll(".dag-node-group");
        Assert.Equal(3, nodeGroups.Count);

        // Edges present
        var edges = cut.FindAll("path.dag-edge");
        Assert.Equal(2, edges.Count);

        // First edge is completed (source task 1 is Closed)
        Assert.Contains("edge-completed", edges[0].ClassName);

        // Second edge is pending (source task 2 is Open)
        Assert.Contains("edge-pending", edges[1].ClassName);
    }

    [Fact]
    public void ZoomControls_AdjustZoomPercentage()
    {
        var tasks = new List<ForgeTask> { new ForgeTask { Id = 1, Title = "Item" } };

        var cut = Render<DagVisualizer>(p => p
            .Add(v => v.Tasks, tasks));

        Assert.Contains("100%", cut.Find(".zoom-level-text").TextContent);

        // Click Zoom In (+)
        var zoomInBtn = cut.FindAll(".zoom-controls button").First(b => b.TextContent == "+");
        zoomInBtn.Click();

        Assert.Contains("115%", cut.Find(".zoom-level-text").TextContent);

        // Click Reset
        var resetBtn = cut.FindAll(".zoom-controls button").First(b => b.TextContent == "Reset");
        resetBtn.Click();

        Assert.Contains("100%", cut.Find(".zoom-level-text").TextContent);
    }

    [Fact]
    public void NodeClick_InvokesOnTaskSelected()
    {
        var task = new ForgeTask { Id = 5, Title = "Selected Task" };
        var tasks = new List<ForgeTask> { task };

        ForgeTask? selected = null;
        var cut = Render<DagVisualizer>(p => p
            .Add(v => v.Tasks, tasks)
            .Add(v => v.OnTaskSelected, t => selected = t));

        var node = cut.Find(".dag-node-group");
        node.Click();

        Assert.NotNull(selected);
        Assert.Equal(5, selected.Id);
    }

    [Fact]
    public void WhenTasksRendered_PriorityPillHasCorrectColors()
    {
        var tasks = new List<ForgeTask>
        {
            new() { Id = 1, Title = "Critical Task", Priority = TaskPriority.Critical },
            new() { Id = 2, Title = "High Task", Priority = TaskPriority.High },
            new() { Id = 3, Title = "Normal Task", Priority = TaskPriority.Normal },
            new() { Id = 4, Title = "Low Task", Priority = TaskPriority.Low },
            new() { Id = 5, Title = "Trivial Task", Priority = TaskPriority.Trivial }
        };

        var cut = Render<DagVisualizer>(p => p.Add(v => v.Tasks, tasks));

        var textElements = cut.FindAll("text");
        Assert.Contains(textElements, t => t.TextContent == "P1" && t.GetAttribute("fill") == "#fca5a5");
        Assert.Contains(textElements, t => t.TextContent == "P2" && t.GetAttribute("fill") == "#fed7aa");
        Assert.Contains(textElements, t => t.TextContent == "P3" && t.GetAttribute("fill") == "#93c5fd");
        Assert.Contains(textElements, t => t.TextContent == "P4" && t.GetAttribute("fill") == "#cbd5e1");
        Assert.Contains(textElements, t => t.TextContent == "P5" && t.GetAttribute("fill") == "#94a3b8");
    }
}
