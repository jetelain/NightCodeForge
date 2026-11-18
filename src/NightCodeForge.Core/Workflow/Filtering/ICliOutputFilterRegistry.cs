namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Resolves an appropriate <see cref="ICliOutputFilter"/> for a shell command line.
/// </summary>
public interface ICliOutputFilterRegistry
{
    /// <summary>
    /// Returns the first registered filter matching <paramref name="command"/>, or <c>null</c>.
    /// </summary>
    ICliOutputFilter? GetFilter(string command);
}
