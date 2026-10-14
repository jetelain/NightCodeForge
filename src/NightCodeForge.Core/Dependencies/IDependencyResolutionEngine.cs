namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Directed Acyclic Graph (DAG) engine for managing task execution order, cycle detection, and readiness.
/// </summary>
public interface IDependencyResolutionEngine
{
    /// <summary>
    /// Checks whether the given set of dependencies contains a circular dependency.
    /// </summary>
    bool HasCycle(int workspaceId, IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies);

    /// <summary>
    /// Validates that dependencies do not contain cycles, throwing <see cref="CircularDependencyException"/> if detected.
    /// </summary>
    void ValidateNoCycles(int workspaceId, IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies);

    /// <summary>
    /// Computes the topological execution order of tasks for a workspace based on stored dependencies.
    /// </summary>
    IReadOnlyList<int> GetTopologicalOrder(int workspaceId);

    /// <summary>
    /// Computes the topological execution order for an explicit collection of dependencies.
    /// </summary>
    IReadOnlyList<int> GetTopologicalOrder(IEnumerable<(int TaskId, int DependsOnTaskId)> dependencies);

    /// <summary>
    /// Determines if a task is ready to be executed (i.e. all its prerequisites are completed).
    /// </summary>
    bool IsReadyForExecution(int taskId);

    /// <summary>
    /// Asynchronously determines if a task is ready to be executed.
    /// </summary>
    Task<bool> IsReadyForExecutionAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves dependent tasks unblocked by the completion of a prerequisite task, transitioning them to Pending.
    /// </summary>
    Task<IReadOnlyList<int>> ResolveCascadeUnblockedTasksAsync(int completedTaskId);
}
