using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service interface for computing deterministic, culture-invariant 2D layouts for Directed Acyclic Graph (DAG) task visualizers.
/// </summary>
public interface IDagLayoutEngine
{
    /// <summary>
    /// Computes the node positions, ranking, and edge connection curves for the given tasks and dependency pairs.
    /// </summary>
    DagGraphLayout ComputeLayout(
        IReadOnlyList<ForgeTask> tasks,
        IReadOnlyList<(int TaskId, int DependsOnTaskId)> dependencies);
}

/// <summary>
/// Represents the computed graph layout including dimensions and SVG elements.
/// </summary>
public class DagGraphLayout
{
    public double Width { get; set; } = 920;
    public double Height { get; set; } = 480;
    public string ViewBox { get; set; } = "0 0 920 480";
    public List<DagLayoutNode> Nodes { get; set; } = new();
    public List<DagLayoutEdge> Edges { get; set; } = new();

    public string GetZoomedWidthInvariant(float zoomScale) =>
        FormattableString.Invariant($"{(int)Math.Max(320, Width * zoomScale)}px");

    public string GetZoomedHeightInvariant(float zoomScale) =>
        FormattableString.Invariant($"{(int)Math.Max(240, Height * zoomScale)}px");
}

/// <summary>
/// Represents a positioned task node in the SVG DAG visualization.
/// </summary>
public class DagLayoutNode
{
    public ForgeTask Task { get; set; } = default!;
    public double X { get; set; }
    public double Y { get; set; }
    public string Transform { get; set; } = string.Empty;
    public int Rank { get; set; }
    public bool IsBlocked { get; set; }
    public int BlockerCount { get; set; }
    public bool IsInFlight { get; set; }
    public bool IsReadyToExecute { get; set; }

    public string FillColor { get; set; } = "#162238";
    public string StrokeColor { get; set; } = "#6366f1";
    public string StatusTextColor { get; set; } = "#a5b4fc";
    public string StatusLabel { get; set; } = string.Empty;

    public string PriorityBg { get; set; } = "rgba(59, 130, 246, 0.3)";
    public string PriorityText { get; set; } = "#93c5fd";

    public string TruncatedTitle { get; set; } = string.Empty;
}

/// <summary>
/// Represents a directed edge connecting a prerequisite task to its dependent task.
/// </summary>
public class DagLayoutEdge
{
    public int SourceTaskId { get; set; }
    public int TargetTaskId { get; set; }
    public double SourceX { get; set; }
    public double SourceY { get; set; }
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public bool IsCompleted { get; set; }

    public string PathData { get; set; } = string.Empty;
    public string StrokeColor { get; set; } = "#6366f1";
    public string ArrowPoints { get; set; } = string.Empty;
}
