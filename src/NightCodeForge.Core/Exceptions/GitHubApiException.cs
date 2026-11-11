using System.Net;

namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a GitHub REST API request fails with a non-success HTTP status code.
/// </summary>
public class GitHubApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public GitHubApiException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public GitHubApiException(HttpStatusCode statusCode, string message, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public GitHubApiException(HttpStatusCode statusCode, string message, string? responseBody, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

