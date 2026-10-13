namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Directed Acyclic Graph (DAG) engine for managing issue execution order, cycle detection, and readiness.
/// </summary>
public interface IDependencyResolutionEngine
{
    /// <summary>
    /// Checks whether the given set of dependencies contains a circular dependency.
    /// </summary>
    bool HasCycle(int workspaceId, IEnumerable<(int IssueId, int DependsOnIssueId)> dependencies);

    /// <summary>
    /// Validates that dependencies do not contain cycles, throwing <see cref="CircularDependencyException"/> if detected.
    /// </summary>
    void ValidateNoCycles(int workspaceId, IEnumerable<(int IssueId, int DependsOnIssueId)> dependencies);

    /// <summary>
    /// Computes the topological execution order of issues for a workspace based on stored dependencies.
    /// </summary>
    IReadOnlyList<int> GetTopologicalOrder(int workspaceId);

    /// <summary>
    /// Computes the topological execution order for an explicit collection of dependencies.
    /// </summary>
    IReadOnlyList<int> GetTopologicalOrder(IEnumerable<(int IssueId, int DependsOnIssueId)> dependencies);

    /// <summary>
    /// Determines if an issue is ready to be executed (i.e. all its prerequisites are completed).
    /// </summary>
    bool IsReadyForExecution(int issueId);

    /// <summary>
    /// Asynchronously determines if an issue is ready to be executed.
    /// </summary>
    Task<bool> IsReadyForExecutionAsync(int issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves dependent issues unblocked by the completion of a prerequisite issue, transitioning them to Pending.
    /// </summary>
    Task<IReadOnlyList<int>> ResolveCascadeUnblockedIssuesAsync(int completedIssueId);
}
