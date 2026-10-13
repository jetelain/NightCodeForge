namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Exception thrown when a circular dependency is detected in the DAG.
/// </summary>
public class CircularDependencyException : Exception
{
    public IReadOnlyList<int> CyclePath { get; }

    public CircularDependencyException(string message) : base(message)
    {
        CyclePath = Array.Empty<int>();
    }

    public CircularDependencyException(string message, IEnumerable<int> cyclePath) : base(message)
    {
        CyclePath = cyclePath.ToList().AsReadOnly();
    }
}
