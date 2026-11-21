namespace NightCodeForge.Installer.Core.Exceptions;

/// <summary>
/// Base domain exception for failures raised by <c>NightCodeForge.Installer</c> services.
/// </summary>
public class InstallerException : Exception
{
    public InstallerException(string message)
        : base(message)
    {
    }

    public InstallerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when host, WSL2, Linux, or macOS prerequisite provisioning fails.
/// </summary>
public sealed class ProvisioningException : InstallerException
{
    public ProvisioningException(string message)
        : base(message)
    {
    }

    public ProvisioningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when resolving or downloading a component release archive fails.
/// </summary>
public sealed class ReleaseDownloadException : InstallerException
{
    public ReleaseDownloadException(string message)
        : base(message)
    {
    }

    public ReleaseDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when an archive fails SHA-256 integrity verification.
/// </summary>
public sealed class ChecksumVerificationException : InstallerException
{
    public ChecksumVerificationException(
        string assetName,
        string expectedSha256,
        string actualSha256)
        : base($"SHA-256 verification failed for '{assetName}'. Expected '{expectedSha256}', got '{actualSha256}'.")
    {
        AssetName = assetName;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    public string AssetName { get; }
    public string ExpectedSha256 { get; }
    public string ActualSha256 { get; }
}

/// <summary>
/// Raised when an air-gapped bundle directory or archive layout is missing or invalid.
/// </summary>
public sealed class BundleValidationException : InstallerException
{
    public BundleValidationException(string message)
        : base(message)
    {
    }

    public BundleValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when installing, updating, or removing a companion NuGet global tool fails.
/// </summary>
public sealed class NuGetToolProvisioningException : InstallerException
{
    public NuGetToolProvisioningException(string message)
        : base(message)
    {
    }

    public NuGetToolProvisioningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when uninstallation fails to stop processes, clean OS registrations, or remove files.
/// </summary>
public sealed class UninstallException : InstallerException
{
    public UninstallException(string message)
        : base(message)
    {
    }

    public UninstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
