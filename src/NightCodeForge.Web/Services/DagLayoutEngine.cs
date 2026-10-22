using System.Globalization;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Default implementation of IDagLayoutEngine.
/// Performs topological ranking, node placement, and culture-invariant SVG path generation.
/// </summary>
public class DagLayoutEngine : IDagLayoutEngine
{
    public const double NodeWidth = 220;
    public const double NodeHeight = 76;
    public const double ColSpacing = 280;
    public const double RowSpacing = 100;
    public const double PadLeft = 40;
    public const double PadTop = 40;

    public DagGraphLayout ComputeLayout(
        IReadOnlyList<ForgeTask> tasks,
        IReadOnlyList<(int TaskId, int DependsOnTaskId)> dependencies)
    {
        var layout = new DagGraphLayout();
        if (tasks == null || tasks.Count == 0)
        {
            return layout;
        }

        var taskMap = tasks.ToDictionary(t => t.Id);

        // Filter valid dependencies where both endpoints exist in active workspace
        var validDeps = (dependencies ?? Array.Empty<(int, int)>())
            .Where(d => taskMap.ContainsKey(d.TaskId) && taskMap.ContainsKey(d.DependsOnTaskId))
            .ToList();

        // Build prereq map: TaskId -> List of DependsOnTaskId
        // Build dependent map: DependsOnTaskId -> List of TaskId
        var prereqMap = new Dictionary<int, List<int>>();
        var dependentMap = new Dictionary<int, List<int>>();
        foreach (var task in tasks)
        {
            prereqMap[task.Id] = new List<int>();
            dependentMap[task.Id] = new List<int>();
        }

        foreach (var (taskId, dependsOnId) in validDeps)
        {
            prereqMap[taskId].Add(dependsOnId);
            dependentMap[dependsOnId].Add(taskId);
        }

        // Identify connected tasks (tasks that have prerequisites or dependents)
        var connectedTaskIds = new HashSet<int>(
            tasks.Where(t => prereqMap[t.Id].Count > 0 || dependentMap[t.Id].Count > 0)
                 .Select(t => t.Id));

        var connectedTasks = tasks.Where(t => connectedTaskIds.Contains(t.Id)).ToList();
        var independentTasks = tasks.Where(t => !connectedTaskIds.Contains(t.Id)).ToList();

        // Compute rank per node: Rank = max(prereqs rank) + 1, root = 0
        var ranks = new Dictionary<int, int>();

        int GetRank(int id, HashSet<int> visiting)
        {
            if (ranks.TryGetValue(id, out var r)) return r;
            if (visiting.Contains(id)) return 0; // Cycle safety

            visiting.Add(id);
            int maxPrereqRank = -1;

            if (prereqMap.TryGetValue(id, out var prereqs))
            {
                foreach (var p in prereqs)
                {
                    var pr = GetRank(p, visiting);
                    if (pr > maxPrereqRank) maxPrereqRank = pr;
                }
            }

            visiting.Remove(id);
            var computed = maxPrereqRank + 1;
            ranks[id] = computed;
            return computed;
        }

        foreach (var task in connectedTasks)
        {
            GetRank(task.Id, new HashSet<int>());
        }

        var nodePositions = new Dictionary<int, (double X, double Y)>();
        double currentMaxX = PadLeft;
        double currentMaxY = PadTop;

        int maxConnectedRank = 0;
        int maxConnectedRowCount = 0;

        // 1. Layout connected tasks by rank
        if (connectedTasks.Count > 0)
        {
            var rankGroups = connectedTasks
                .GroupBy(t => ranks[t.Id])
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in rankGroups)
            {
                var rank = group.Key;
                if (rank > maxConnectedRank) maxConnectedRank = rank;

                var items = group.OrderBy(t => t.Priority).ThenBy(t => t.Id).ToList();
                if (items.Count > maxConnectedRowCount) maxConnectedRowCount = items.Count;

                for (int i = 0; i < items.Count; i++)
                {
                    var t = items[i];
                    var x = PadLeft + rank * ColSpacing;
                    var y = PadTop + i * RowSpacing;
                    nodePositions[t.Id] = (x, y);

                    if (x + NodeWidth > currentMaxX) currentMaxX = x + NodeWidth;
                    if (y + NodeHeight > currentMaxY) currentMaxY = y + NodeHeight;

                    var node = CreateLayoutNode(t, x, y, rank, prereqMap[t.Id], taskMap);
                    layout.Nodes.Add(node);
                }
            }
        }

        // 2. Layout independent tasks in a neat, balanced multi-column grid
        if (independentTasks.Count > 0)
        {
            double baseIndependentY;
            int independentCols;

            if (connectedTasks.Count > 0)
            {
                // Place below connected pipelines with comfortable section spacing
                baseIndependentY = PadTop + (maxConnectedRowCount * RowSpacing) + 40;
                independentCols = Math.Max(3, maxConnectedRank + 1);
            }
            else
            {
                // Entire workspace has independent tasks (no dependencies defined yet)
                baseIndependentY = PadTop;
                independentCols = Math.Min(4, Math.Max(2, (int)Math.Ceiling(Math.Sqrt(independentTasks.Count * 1.5))));
            }

            var orderedIndependent = independentTasks.OrderBy(t => t.Priority).ThenBy(t => t.Id).ToList();
            for (int i = 0; i < orderedIndependent.Count; i++)
            {
                var t = orderedIndependent[i];
                var col = i % independentCols;
                var row = i / independentCols;

                var x = PadLeft + col * ColSpacing;
                var y = baseIndependentY + row * RowSpacing;
                nodePositions[t.Id] = (x, y);

                if (x + NodeWidth > currentMaxX) currentMaxX = x + NodeWidth;
                if (y + NodeHeight > currentMaxY) currentMaxY = y + NodeHeight;

                var node = CreateLayoutNode(t, x, y, 0, Array.Empty<int>(), taskMap);
                layout.Nodes.Add(node);
            }
        }

        layout.Width = Math.Max(960, currentMaxX + PadLeft);
        layout.Height = Math.Max(520, currentMaxY + PadTop);
        layout.ViewBox = FormattableString.Invariant($"0 0 {layout.Width:0.##} {layout.Height:0.##}");

        // 3. Layout edges: (DependsOnId -> TaskId) since DependsOnId precedes TaskId
        foreach (var (taskId, dependsOnId) in validDeps)
        {
            if (nodePositions.TryGetValue(dependsOnId, out var src) && nodePositions.TryGetValue(taskId, out var dst))
            {
                var srcTask = taskMap[dependsOnId];
                var isCompleted = srcTask.Status == TaskOverallStatus.Closed;
                var strokeColor = isCompleted ? "#10b981" : "#6366f1";

                var sourceX = src.X + NodeWidth;
                var sourceY = src.Y + NodeHeight / 2.0;
                var targetX = dst.X;
                var targetY = dst.Y + NodeHeight / 2.0;

                string pathData;
                if (targetX > sourceX)
                {
                    var cpOffset = Math.Max(35.0, (targetX - sourceX) * 0.45);
                    pathData = FormattableString.Invariant(
                        $"M {sourceX:0.##} {sourceY:0.##} C {sourceX + cpOffset:0.##} {sourceY:0.##}, {targetX - cpOffset:0.##} {targetY:0.##}, {targetX:0.##} {targetY:0.##}");
                }
                else
                {
                    // Loops around for reverse or same-column edges
                    var cpOffset = 45.0;
                    pathData = FormattableString.Invariant(
                        $"M {sourceX:0.##} {sourceY:0.##} C {sourceX + cpOffset:0.##} {sourceY + 30:0.##}, {targetX - cpOffset:0.##} {targetY - 30:0.##}, {targetX:0.##} {targetY:0.##}");
                }

                // Culture-invariant arrowhead polygon pointing to (targetX, targetY)
                var p1 = FormattableString.Invariant($"{targetX:0.##},{targetY:0.##}");
                var p2 = FormattableString.Invariant($"{targetX - 8:0.##},{targetY - 4.5:0.##}");
                var p3 = FormattableString.Invariant($"{targetX - 8:0.##},{targetY + 4.5:0.##}");
                var arrowPoints = $"{p1} {p2} {p3}";

                layout.Edges.Add(new DagLayoutEdge
                {
                    SourceTaskId = dependsOnId,
                    TargetTaskId = taskId,
                    SourceX = sourceX,
                    SourceY = sourceY,
                    TargetX = targetX,
                    TargetY = targetY,
                    IsCompleted = isCompleted,
                    PathData = pathData,
                    StrokeColor = strokeColor,
                    ArrowPoints = arrowPoints
                });
            }
        }

        return layout;
    }

    private static DagLayoutNode CreateLayoutNode(
        ForgeTask task,
        double x,
        double y,
        int rank,
        IReadOnlyList<int> prereqIds,
        Dictionary<int, ForgeTask> taskMap)
    {
        var blockers = prereqIds.Count(p => taskMap.TryGetValue(p, out var pt) && pt.Status != TaskOverallStatus.Closed);
        var isInFlight = IsInFlight(task);
        var isBlocked = task.Status == TaskOverallStatus.Open && !isInFlight && blockers > 0;
        var isReadyToExecute = task.Status == TaskOverallStatus.Open && !isInFlight && blockers == 0;

        return new DagLayoutNode
        {
            Task = task,
            X = x,
            Y = y,
            Transform = FormattableString.Invariant($"translate({x:0.##}, {y:0.##})"),
            Rank = rank,
            IsBlocked = isBlocked,
            BlockerCount = blockers,
            IsInFlight = isInFlight,
            IsReadyToExecute = isReadyToExecute,
            FillColor = GetNodeFillColor(task, isBlocked, isInFlight),
            StrokeColor = GetNodeStrokeColor(task, isBlocked, isInFlight),
            StatusTextColor = GetStatusTextColor(task, isInFlight),
            StatusLabel = GetStatusLabel(task, isBlocked, isReadyToExecute, blockers),
            PriorityBg = GetPriorityBg(task.Priority),
            PriorityText = GetPriorityText(task.Priority),
            TruncatedTitle = TruncateText(task.Title, 26)
        };
    }

    public static bool IsInFlight(ForgeTask task) => BacklogStatusClassifier.IsInFlight(task);

    public static string GetNodeFillColor(ForgeTask task, bool isBlocked, bool isInFlight)
    {
        if (isBlocked) return "#241e2b";
        if (isInFlight) return "#2e1b4d";
        return task.Status switch
        {
            TaskOverallStatus.Suggested => "#2a2416",
            TaskOverallStatus.Open => "#162238",
            TaskOverallStatus.Ready => "#133331",
            TaskOverallStatus.Closed => "#123324",
            TaskOverallStatus.Rejected or TaskOverallStatus.Failed => "#2a151b",
            _ => "#181b24"
        };
    }

    public static string GetNodeStrokeColor(ForgeTask task, bool isBlocked, bool isInFlight)
    {
        if (isBlocked) return "#f59e0b";
        if (isInFlight) return "#a855f7";
        return task.Status switch
        {
            TaskOverallStatus.Suggested => "#eab308",
            TaskOverallStatus.Open => "#6366f1",
            TaskOverallStatus.Ready => "#14b8a6",
            TaskOverallStatus.Closed => "#10b981",
            TaskOverallStatus.Rejected or TaskOverallStatus.Failed => "#ef4444",
            _ => "#2d3345"
        };
    }

    public static string GetStatusTextColor(ForgeTask task, bool isInFlight)
    {
        if (isInFlight) return "#d8b4fe";
        return task.Status switch
        {
            TaskOverallStatus.Suggested => "#fde047",
            TaskOverallStatus.Open => "#a5b4fc",
            TaskOverallStatus.Ready => "#5eead4",
            TaskOverallStatus.Closed => "#6ee7b7",
            TaskOverallStatus.Rejected or TaskOverallStatus.Failed => "#fca5a5",
            _ => "#94a3b8"
        };
    }

    public static string GetStatusLabel(ForgeTask task, bool isBlocked, bool isReadyToExecute, int blockerCount)
    {
        if (task.IsWaitingForHuman) return "⚠️ Waiting Human";
        if (isBlocked) return $"🔒 Blocked ({blockerCount})";
        if (isReadyToExecute) return "🟢 Ready";
        if (task.Status == TaskOverallStatus.Open && task.HasExecutionRun && !string.IsNullOrWhiteSpace(task.CurrentStageId))
        {
            return $"In-Flight: {task.CurrentStageId}";
        }
        return task.Status switch
        {
            TaskOverallStatus.Suggested => "💡 Suggested",
            TaskOverallStatus.Reviewed => "Stage 1: Reviewed",
            TaskOverallStatus.Implemented => "Stage 3: Implemented",
            TaskOverallStatus.Ready => "🔍 Ready for Review",
            TaskOverallStatus.Closed => "✅ Closed",
            TaskOverallStatus.Rejected => "🗑️ Rejected",
            TaskOverallStatus.Failed => "❌ Failed",
            _ => task.Status.ToString()
        };
    }

    public static string GetPriorityBg(int priority) => priority switch
    {
        TaskPriority.Critical => "rgba(239, 68, 68, 0.35)",
        TaskPriority.High => "rgba(249, 115, 22, 0.35)",
        TaskPriority.Normal => "rgba(59, 130, 246, 0.35)",
        TaskPriority.Low => "rgba(100, 116, 139, 0.3)",
        _ => "rgba(100, 116, 139, 0.25)"
    };

    public static string GetPriorityText(int priority) => priority switch
    {
        TaskPriority.Critical => "#fca5a5",
        TaskPriority.High => "#fed7aa",
        TaskPriority.Normal => "#93c5fd",
        TaskPriority.Low => "#cbd5e1",
        _ => "#94a3b8"
    };

    public static string TruncateText(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
        return text.Substring(0, max).TrimEnd() + "…";
    }
}
