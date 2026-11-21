namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when storing, deleting, or reading credentials in the OS keyring fails unexpectedly.
/// </summary>
public class SecretStoreException : Exception
{
    public SecretStoreException(string message)
        : base(message)
    {
    }

    public SecretStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
