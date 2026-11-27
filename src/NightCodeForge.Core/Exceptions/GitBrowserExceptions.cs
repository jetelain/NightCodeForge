namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Base exception for read-only Git repository browser failures.
/// </summary>
public class GitRepositoryBrowserException : Exception
{
    public GitRepositoryBrowserException(string message)
        : base(message)
    {
    }

    public GitRepositoryBrowserException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when the target workspace cannot be found.
/// </summary>
public sealed class GitBrowserWorkspaceNotFoundException : GitRepositoryBrowserException
{
    public int WorkspaceId { get; }

    public GitBrowserWorkspaceNotFoundException(int workspaceId, string? message = null)
        : base(message ?? $"Workspace #{workspaceId} was not found.")
    {
        WorkspaceId = workspaceId;
    }
}

/// <summary>
/// Thrown when no accessible Git repository exists for the workspace.
/// </summary>
public sealed class GitBrowserRepositoryNotFoundException : GitRepositoryBrowserException
{
    public int WorkspaceId { get; }

    public GitBrowserRepositoryNotFoundException(int workspaceId, string? message = null)
        : base(message ?? $"No accessible Git repository found for workspace #{workspaceId}.")
    {
        WorkspaceId = workspaceId;
    }
}

/// <summary>
/// Thrown when a requested Git ref or repository-relative path fails security validation.
/// </summary>
public sealed class GitBrowserInvalidInputException : GitRepositoryBrowserException
{
    public GitBrowserInvalidInputException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a requested Git ref cannot be resolved even after task fallback lookup.
/// </summary>
public sealed class GitBrowserRefNotFoundException : GitRepositoryBrowserException
{
    public string RequestedRef { get; }

    public GitBrowserRefNotFoundException(string requestedRef, string? message = null)
        : base(message ?? $"Git ref '{requestedRef}' could not be resolved.")
    {
        RequestedRef = requestedRef;
    }
}

/// <summary>
/// Thrown when a requested file or directory path does not exist at the resolved Git ref.
/// </summary>
public sealed class GitBrowserPathNotFoundException : GitRepositoryBrowserException
{
    public string RequestedPath { get; }

    public GitBrowserPathNotFoundException(string requestedPath, string? message = null)
        : base(message ?? $"Path '{requestedPath}' was not found at the selected Git ref.")
    {
        RequestedPath = requestedPath;
    }
}
