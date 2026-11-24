using System.Net;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when an operation against a remote VCS hosting provider fails.
/// </summary>
public class VcsHostingProviderException : Exception
{
    public VcsHostingProviderType? ProviderType { get; }
    public HttpStatusCode? StatusCode { get; }

    public VcsHostingProviderException(string message)
        : base(message)
    {
    }

    public VcsHostingProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public VcsHostingProviderException(
        VcsHostingProviderType providerType,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderType = providerType;
        StatusCode = statusCode;
    }
}
