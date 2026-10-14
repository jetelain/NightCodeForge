using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Implements DAG resolution, topological ordering, 3-color DFS cycle detection, and cascade unblocking.
/// </summary>
public class DependencyResolutionEngine : IDependencyResolutionEngine
{
    private readonly ITaskDependencyStore _store;

    public DependencyResolutionEngine(ITaskDependencyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool HasCycle(int workspaceId, IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies)
    {
        return TryFindCycle(dependencies, out _);
    }

    public void ValidateNoCycles(int workspaceId, IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies)
    {
        if (TryFindCycle(dependencies, out var cyclePath))
        {
            var pathStr = string.Join(" -> ", cyclePath);
            throw new CircularDependencyException($"Circular dependency detected in workspace {workspaceId}: {pathStr}", cyclePath);
        }
    }

    public IReadOnlyList<int> GetTopologicalOrder(int workspaceId)
    {
        var dependencies = _store.GetDependenciesForWorkspaceAsync(workspaceId).GetAwaiter().GetResult();
        return GetTopologicalOrder(dependencies);
    }

    public IReadOnlyList<int> GetTopologicalOrder(IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies)
    {
        var depList = dependencies.ToList();
        ValidateNoCycles(0, depList);

        // Build execution graph: edge U -> V means U must execute before V (V depends on U)
        var allNodes = new HashSet<int>();
        var adj = new Dictionary<int, List<int>>();
        var inDegree = new Dictionary<int, int>();

        foreach (var (taskId, dependsOnId) in depList)
        {
            allNodes.Add(taskId);
            allNodes.Add(dependsOnId);

            if (!adj.ContainsKey(dependsOnId)) adj[dependsOnId] = new List<int>();
            if (!adj.ContainsKey(taskId)) adj[taskId] = new List<int>();

            adj[dependsOnId].Add(taskId);
        }

        foreach (var node in allNodes)
        {
            inDegree[node] = 0;
        }

        foreach (var kvp in adj)
        {
            foreach (var target in kvp.Value)
            {
                inDegree[target]++;
            }
        }

        // Kahn's algorithm
        var queue = new PriorityQueue<int, int>(); // Use deterministic order (smallest ID first)
        foreach (var node in allNodes.Where(n => inDegree[n] == 0))
        {
            queue.Enqueue(node, node);
        }

        var result = new List<int>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(current);

            if (adj.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                    {
                        queue.Enqueue(neighbor, neighbor);
                    }
                }
            }
        }

        if (result.Count < allNodes.Count)
        {
            throw new CircularDependencyException("Unable to resolve topological order due to a cycle.");
        }

        return result.AsReadOnly();
    }

    public bool IsReadyForExecution(int taskId)
    {
        return IsReadyForExecutionAsync(taskId).GetAwaiter().GetResult();
    }

    public async Task<bool> IsReadyForExecutionAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var status = await _store.GetTaskStatusAsync(taskId, cancellationToken);
        if (status != TaskOverallStatus.Open && status != TaskOverallStatus.Reviewed && status != TaskOverallStatus.Implemented)
        {
            return false;
        }

        var isWaiting = await _store.IsTaskWaitingForHumanAsync(taskId, cancellationToken);
        if (isWaiting)
        {
            return false;
        }

        var prerequisites = await _store.GetPrerequisiteTaskIdsAsync(taskId, cancellationToken);
        if (prerequisites.Count == 0)
        {
            return true;
        }

        foreach (var prereqId in prerequisites)
        {
            var prereqStatus = await _store.GetTaskStatusAsync(prereqId, cancellationToken);
            if (prereqStatus != TaskOverallStatus.Closed)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<IReadOnlyList<int>> ResolveCascadeUnblockedTasksAsync(int completedTaskId)
    {
        var dependentTaskIds = await _store.GetDependentTaskIdsAsync(completedTaskId);
        var unblocked = new List<int>();

        foreach (var depId in dependentTaskIds)
        {
            var depStatus = await _store.GetTaskStatusAsync(depId);
            if (depStatus == TaskOverallStatus.Open && await IsReadyForExecutionAsync(depId))
            {
                unblocked.Add(depId);
            }
        }

        return unblocked.AsReadOnly();
    }

    private static bool TryFindCycle(IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies, out List<int> cyclePath)
    {
        cyclePath = new List<int>();
        var graph = new Dictionary<int, List<int>>();
        var allNodes = new HashSet<int>();

        foreach (var (taskId, dependsOnId) in dependencies)
        {
            allNodes.Add(taskId);
            allNodes.Add(dependsOnId);

            if (!graph.ContainsKey(taskId))
            {
                graph[taskId] = new List<int>();
            }
            graph[taskId].Add(dependsOnId);
        }

        // Colors: 0 = White (unvisited), 1 = Gray (visiting), 2 = Black (visited)
        var colors = new Dictionary<int, int>();
        var parent = new Dictionary<int, int>();
        foreach (var node in allNodes)
        {
            colors[node] = 0;
        }

        foreach (var node in allNodes)
        {
            if (colors[node] == 0)
            {
                if (DfsVisit(node, graph, colors, parent, out var cycleStart, out var cycleEnd))
                {
                    // Reconstruct cycle path
                    var path = new List<int> { cycleStart };
                    var curr = cycleEnd;
                    while (curr != cycleStart && parent.ContainsKey(curr))
                    {
                        path.Add(curr);
                        curr = parent[curr];
                    }
                    path.Add(cycleStart);
                    path.Reverse();
                    cyclePath = path;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool DfsVisit(
        int u,
        Dictionary<int, List<int>> graph,
        Dictionary<int, int> colors,
        Dictionary<int, int> parent,
        out int cycleStart,
        out int cycleEnd)
    {
        colors[u] = 1; // Gray

        if (graph.TryGetValue(u, out var neighbors))
        {
            foreach (var v in neighbors)
            {
                if (colors.TryGetValue(v, out var color))
                {
                    if (color == 1) // Gray neighbor -> cycle found!
                    {
                        cycleStart = v;
                        cycleEnd = u;
                        return true;
                    }
                    if (color == 0) // White neighbor
                    {
                        parent[v] = u;
                        if (DfsVisit(v, graph, colors, parent, out cycleStart, out cycleEnd))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        colors[u] = 2; // Black
        cycleStart = -1;
        cycleEnd = -1;
        return false;
    }
}
