using Pmad.LocalApi.Security;

namespace NightCodeForge.Web.Endpoints;

/// <summary>
/// Endpoint filter enforcing local API key authentication (<c>Authorization: Bearer</c> or <c>X-Api-Key</c>)
/// on all <c>/api/companion/*</c> endpoints via <see cref="IApiKeyStore"/>.
/// </summary>
public sealed class CompanionApiKeyEndpointFilter : IEndpointFilter
{
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string WwwAuthenticateHeaderValue = "Bearer";
    public const string UnauthorizedErrorMessage = "Unauthorized: valid NightCodeForge API key required.";

    private readonly IApiKeyStore _apiKeyStore;

    /// <summary>
    /// Initializes a new instance of <see cref="CompanionApiKeyEndpointFilter"/>.
    /// </summary>
    public CompanionApiKeyEndpointFilter(IApiKeyStore apiKeyStore)
    {
        ArgumentNullException.ThrowIfNull(apiKeyStore);
        _apiKeyStore = apiKeyStore;
    }

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var headers = context.HttpContext.Request.Headers;
        var authorizationHeader = headers.Authorization.FirstOrDefault();
        var xApiKeyHeader = headers[ApiKeyHeaderName].FirstOrDefault();

        var presentedKey = ApiKeyHeaderParser.ExtractKey(authorizationHeader, xApiKeyHeader);
        if (!_apiKeyStore.ValidateKey(presentedKey))
        {
            context.HttpContext.Response.Headers.WWWAuthenticate = WwwAuthenticateHeaderValue;
            return Results.Json(
                new CompanionErrorResponse(UnauthorizedErrorMessage),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
