namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when GitHub authentication fails, an OAuth token is expired and cannot be refreshed,
/// or when no valid token is found in the dedicated token store.
/// </summary>
public class GitHubAuthenticationException : Exception
{
    public GitHubAuthenticationException(string message)
        : base(message)
    {
    }

    public GitHubAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

