namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Exception thrown when a POSIX path must be mapped to a host-accessible WSL UNC path
/// but no default WSL distribution could be determined.
/// </summary>
public class WslDistributionUnavailableException : Exception
{
    public string PosixPath { get; }

    public WslDistributionUnavailableException(string posixPath)
        : base($"Cannot resolve POSIX path '{posixPath}' to a host-accessible path: no default WSL distribution is available.")
    {
        PosixPath = posixPath;
    }
}
