namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when local Git repository discovery or path resolution fails.
/// </summary>
public class LocalRepositoryDiscoveryException : Exception
{
    public LocalRepositoryDiscoveryException(string message)
        : base(message)
    {
    }

    public LocalRepositoryDiscoveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
